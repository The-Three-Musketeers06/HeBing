using AiVisionStudio.Algorithm.Lua;

namespace AiVisionStudio.Algorithm.Impl;

/// <summary>Manual-defined typed values, geometry and graphics. No calculation is substituted with defaults.</summary>
internal static class LuaManualFunctions
{
    public sealed class MatrixValue(int rows,int columns,IEnumerable<object> data):List<object>(data)
    {public int Rows{get;}=rows;public int Columns{get;}=columns;public IReadOnlyList<object> Data=>this;}
    private static double Number(object value)
    {
        if(value is not double and not float and not int and not long)throw new ArgumentException("需要数值参数");
        var n=Convert.ToDouble(value);if(!double.IsFinite(n))throw new ArgumentException("数值参数必须有限");return n;
    }
    private static int Integer(object value,int minimum,int maximum)
    {var n=Number(value);if(n<minimum||n>maximum||n!=Math.Truncate(n))throw new ArgumentException($"整数参数须在 {minimum}..{maximum}");return (int)n;}
    private static int View(object value)=>Integer(value is string text&&double.TryParse(text,System.Globalization.NumberStyles.Integer,System.Globalization.CultureInfo.InvariantCulture,out var index)?index:value,0,31);
    private static string Name(object value)=>value is string s&&!string.IsNullOrWhiteSpace(s)?s:throw new ArgumentException("输出名称不能为空");
    private static object Typed(string kind,object value)=>kind switch{
        "Int"=>(object)Integer(value,int.MinValue,int.MaxValue),"Float"=>Number(value),
        "Bool"=>value is bool b?b:throw new ArgumentException("需要布尔值"),"String"=>value is string s?s:throw new ArgumentException("需要字符串"),_=>throw new ArgumentException("未知值类型")};
    private static List<object> Array(object value)
    {
        if(value is not LuaTable table||table.Map.Count!=table.Length)throw new ArgumentException("需要从 1 开始的连续数组");
        return Enumerable.Range(1,table.Length).Select(i=>table.Get((double)i)).ToList();
    }
    public static void Install(OpContext ctx,ParamBag p,LuaEnv env)
    {
        foreach(var kind in new[]{"Int","Float","Bool","String"})
        {
            var k=kind;
            foreach(var array in new[]{false,true})foreach(var global in new[]{false,true})
            {
                var isArray=array;var isGlobal=global;var function="Set"+k+(array?"Array":"")+(global?"Variable":"");
                env.Set(function,new LuaFunc(a=>{
                    if(a.Length!=2)throw new ArgumentException(function+" 需要名称和值");var name=Name(a[0]);
                    // Earlier ES projects published arbitrary tables through scalar output setters.
                    // Retain that output contract; the explicit Array functions enforce element types.
                    if(!isArray&&!isGlobal&&a[1] is LuaTable legacyTable){ctx.Vars.Set(name,legacyTable);return null;}
                    object value=isArray?Array(a[1]).Select(v=>Typed(k,v)).ToList():k=="String"&&a[1] is double or int?Convert.ToString(a[1],System.Globalization.CultureInfo.InvariantCulture):Typed(k,a[1]);
                    if(isGlobal){ctx.Globals.Set(name,value);env.Set(name,LuaLite.From(value));}else ctx.Vars.Set(name,value);return null;
                }));
            }
        }
        env.Set("GetOutput",new LuaFunc(a=>{
            if(a.Length!=1)throw new ArgumentException("GetOutput 需要算子名");var name=Name(a[0]);
            var id=ctx.NamedOutputSources.GetValueOrDefault(name)??name;
            if(!ctx.OpOutputs.TryGetValue(id,out var output))throw new InvalidOperationException("算子输出尚不可用："+name);
            return LuaLite.From(output.All);
        }));
        env.Set("SetMatrix",new LuaFunc(a=>{
            if(a.Length!=4)throw new ArgumentException("SetMatrix 需要名称、行数、列数和数据数组");var rows=Integer(a[1],1,4096);var cols=Integer(a[2],1,4096);var data=Array(a[3]).Select(v=>(object)Number(v)).ToList();
            if((long)rows*cols!=data.Count)throw new ArgumentException("矩阵数据长度与行列数不一致");ctx.Vars.Set(Name(a[0]),new MatrixValue(rows,cols,data));return null;
        }));
        foreach(var dimension in new[]{1,2,3})foreach(var shape in new[]{"Point","Line","Segment","Circle","Ring","Window","CircleWindow"})
        {
            if(dimension==3&&(shape is "Window" or "CircleWindow"))continue;
            var dims=dimension;var kind=shape;RegisterGeometry(kind+dims+"D",kind.ToLowerInvariant()+dims+"d",a=>Geometry(kind,dims,a));
        }
        RegisterGeometry("Plane","plane",a=>{
            Count(a,6);var pos=Point(a,0,3);var normal=Point(a,3,3);NonZero(normal);return new(){["Position"]=pos,["Normal"]=normal};
        });
        RegisterGeometry("Box","box",a=>{Count(a,6);return new(){["Start"]=Point(a,0,3),["End"]=Point(a,3,3)};});
        RegisterGeometry("OrientedBox","orientedbox",a=>{Count(a,9);if(a.Skip(3).Take(3).Any(n=>n<0))throw new ArgumentException("盒子尺寸不能为负");return new(){["Center"]=Point(a,0,3),["Size"]=Point(a,3,3),["Rotation"]=Point(a,6,3)};});
        RegisterGeometry("Cylinder","cylinder",a=>{Count(a,7);if(a[6]<=0)throw new ArgumentException("圆柱半径必须为正");if(Enumerable.Range(0,3).All(i=>a[i]==a[i+3]))throw new ArgumentException("圆柱轴线不能为零");return new(){["Bottom"]=Point(a,0,3),["Top"]=Point(a,3,3),["Radius"]=a[6]};});
        env.Set("SetPose",new LuaFunc(a=>{
            if(a.Length!=8)throw new ArgumentException("SetPose 需要名称、XYZ 和四元数 WXYZ");var values=a.Skip(1).Select(Number).ToArray();var scale=values.Skip(3).Max(Math.Abs);if(scale==0)throw new ArgumentException("四元数不能为零");for(int i=3;i<7;i++)values[i]/=scale;var norm=Math.Sqrt(values.Skip(3).Sum(n=>n*n));
            var q=new Dictionary<string,object>{{"W",values[3]/norm},{"X",values[4]/norm},{"Y",values[5]/norm},{"Z",values[6]/norm}};
            ctx.Vars.Set(Name(a[0]),new Dictionary<string,object>{{"Type","pose"},{"Position",Point(values,0,3)},{"Quaternion",q},{"Rotation",q}});return null;
        }));
        for(int dimension=1;dimension<=3;dimension++)
        {
            var dims=dimension;var coordinates=dims==3?3:2;
            env.Set("DrawText"+dims+"D",new LuaFunc(a=>{
                if(a.Length!=coordinates+4)throw new ArgumentException("文本参数数量无效");var view=View(a[0]);var color=Name(a[1]);var point=Point(a.Skip(2).Take(coordinates).Select(Number).ToArray(),0,dims);var size=Number(a[^2]);if(size<=0)throw new ArgumentException("字号必须为正");
                var value=a[^1] as string??throw new ArgumentException("文本内容必须为字符串");
                DetectionOverlay2D.Add(ctx,p,"Lua","text"+dims+"d",new(){["Position"]=point,["Text"]=value,["_color"]=color,["_size"]=size},view:view);
                var texts=ctx.Vars.Get("Draw.Texts") as List<object>??new();texts.Add(value);ctx.Vars.Set("Draw.Texts",texts);return null;
            }));
            env.Set("DrawPolygon"+dims+"D",new LuaFunc(a=>{
                if(a.Length!=3)throw new ArgumentException("多边形需要 IM、颜色和点数组");var points=Array(a[2]).Select(v=>{
                    if(v is not LuaTable point)throw new ArgumentException("多边形点必须为坐标表");var axes=dims==1?new[]{"X","Z"}:dims==2?new[]{"X","Y"}:new[]{"X","Y","Z"};
                    return (object)axes.ToDictionary(axis=>axis,axis=>(object)Number(point.Get(axis)??point.Get(axis.ToLowerInvariant())));
                }).ToList();if(points.Count<2)throw new ArgumentException("多边形至少需要两个点");
                DetectionOverlay2D.Add(ctx,p,"Lua","polygon"+dims+"d",new(){["Points"]=points,["_color"]=Name(a[1])},view:View(a[0]));return null;
            }));
        }
        void RegisterGeometry(string suffix,string type,Func<double[],Dictionary<string,object>> build)
        {
            env.Set("Set"+suffix,new LuaFunc(a=>{
                if(a.Length<2)throw new ArgumentException("缺少几何参数");var value=build(a.Skip(1).Select(Number).ToArray());var name=Name(a[0]);value["Type"]=type;value["_type"]=type;ctx.Vars.Set(name,value);
                // Preserve the existing cylinder marker used by legacy ES examples.
                if(suffix=="Cylinder"){ctx.NativeOverlays.RemoveAll(o=>o.Type==type&&o.Values.GetValueOrDefault("_name")?.ToString()==name);value["_name"]=name;DetectionOverlay2D.Add(ctx,p,"Lua",type,value,view:0);}return null;
            }));
            env.Set("Draw"+suffix,new LuaFunc(a=>{
                if(a.Length<3)throw new ArgumentException("缺少绘制参数");var value=build(a.Skip(2).Select(Number).ToArray());value["_color"]=Name(a[1]);var view=View(a[0]);
                if(type=="box"){
                    var start=(Dictionary<string,object>)value["Start"];var end=(Dictionary<string,object>)value["End"];
                    var center=new[]{"X","Y","Z"}.ToDictionary(axis=>axis,axis=>(object)(((double)start[axis]+(double)end[axis])/2));
                    var size=new[]{"X","Y","Z"}.ToDictionary(axis=>axis,axis=>(object)Math.Abs((double)end[axis]-(double)start[axis]));
                    DetectionOverlay2D.Add(ctx,p,"Lua","orientedbox",new(){["Center"]=center,["Size"]=size,["Rotation"]=Point(new double[3],0,3),["_color"]=value["_color"]},view:view);
                }
                else DetectionOverlay2D.Add(ctx,p,"Lua",type,value,view:view);return null;
            }));
        }
    }
    private static Dictionary<string,object> Geometry(string kind,int dimension,double[] a)
    {
        int dims=dimension==3?3:2;var result=new Dictionary<string,object>();
        switch(kind)
        {
            case "Point":Count(a,dims);return Point(a,0,dimension);
            case "Line":Count(a,dims*2);result["Position"]=Point(a,0,dimension);var v=Point(a,dims,dimension);NonZero(v);result["Direction"]=v;break;
            case "Segment":case "Window":Count(a,dims*2);result["Start"]=Point(a,0,dimension);result["End"]=Point(a,dims,dimension);break;
            case "Circle":case "CircleWindow":case "Ring":
                int radii=kind=="Ring"?2:1;Count(a,dims+radii+(dimension==3?3:0));
                result["Center"]=Point(a,0,dimension);if(a[dims]<0||kind!="Ring"&&a[dims]==0)throw new ArgumentException("圆半径无效");
                if(kind=="Ring"){if(a[dims+1]<=a[dims])throw new ArgumentException("外半径必须大于内半径");result["Inner Radius"]=a[dims];result["Outer Radius"]=a[dims+1];result["Outter Radius"]=a[dims+1];}else result["Radius"]=a[dims];
                if(dimension==3)result["Rotation"]=Point(a,dims+radii,3);break;
            default:throw new ArgumentException("未知几何类型");
        }
        return result;
    }
    private static void Count(double[] values,int count){if(values.Length!=count)throw new ArgumentException("几何需要 "+count+" 个数值");}
    private static Dictionary<string,object> Point(double[] a,int start,int dims)
    {var axes=dims==1?new[]{"X","Z"}:dims==2?new[]{"X","Y"}:new[]{"X","Y","Z"};return axes.Select((axis,i)=>(axis,i)).ToDictionary(v=>v.axis,v=>(object)a[start+v.i]);}
    private static void NonZero(Dictionary<string,object> vector){if(vector.Values.All(v=>(double)v==0))throw new ArgumentException("几何方向不能为零");}
}
