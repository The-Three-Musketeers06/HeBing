using System.Globalization;
using System.IO;
using System.Text.Json;

namespace AiVisionStudio.Algorithm.Impl;

/// <summary>Source-owned pin segmentation, recipe-window measurement and tolerance checks.</summary>
public sealed class PinMeasurementOp : IOperator
{
    public string Driver { get; }
    public PinMeasurementOp(bool createRecipe=false)=>Driver=createRecipe?"PinRecipe":"PinMatch";
    private sealed record Pin(List<int> Indices,double X,double Y,double Z)
    {public double SideX,SideY,SideZ;}
    private static double Number(IReadOnlyDictionary<string,object> map,string key,double? fallback=null)
    {
        if(map!=null&&map.TryGetValue(key,out var v)&&v!=null&&double.TryParse(Convert.ToString(v,CultureInfo.InvariantCulture),NumberStyles.Float,CultureInfo.InvariantCulture,out var n)&&double.IsFinite(n))return n;
        return fallback??throw new ArgumentException("Pin 配方/参数缺少有效数值："+key);
    }
    private static bool Flag(IReadOnlyDictionary<string,object> map,string key,bool fallback=false)=>map?.TryGetValue(key,out var v)==true?ControlValues.Bool(v):fallback;
    private static string Text(IReadOnlyDictionary<string,object> map,string key,string fallback)=>map?.TryGetValue(key,out var v)==true?v?.ToString()??fallback:fallback;
    public bool Run(OpContext ctx,ParamBag p)
    {
        p.Context??=ctx;int input=p.Int("IM In");var cloud=ctx.Clouds.Get(input);
        if(cloud==null||cloud.Count==0)throw new ArgumentException("Pin 检测输入点云为空");
        var selected=CloudRegionSelector.Select(cloud,p);if(selected.Count==0)throw new ArgumentException("Pin 检测区域为空");
        var settings=p.Group("Pin Settings");var options=p.Group("Output Options");
        var compare=Driver=="PinMatch"&&Flag(options,"Recipe Compare");var height=Flag(options,"Measure Height");var interval=Flag(options,"Measure Interval");
        var minimum=Number(settings,"Minimum Number of Pin Points",50);
        if(minimum<1||minimum>int.MaxValue||minimum!=Math.Truncate(minimum))throw new ArgumentException("Pin 最少点数必须是正整数");
        var transform=CloudRegionSelector.CreateReferenceTransform(p.Group("Reference"));
        var local=new PointCloud();for(int i=0;i<cloud.Count;i++){var q=transform(cloud.X[i],cloud.Y[i],cloud.Z[i]);local.Add((float)q.X,(float)q.Y,(float)q.Z);}
        List<Dictionary<string,object>> recipe=null;List<List<int>> groups;
        if(compare)
        {
            recipe=RecipeRuntime.Read(ctx.ResolvePath(Text(options,"Recipe File","")));
            if(recipe.Count==0)throw new ArgumentException("Pin 配方为空");
            double length=Number(settings,"Length",1),width=Number(settings,"Width",1),depth=Number(settings,"Height",1);
            if(Math.Min(length,Math.Min(width,depth))<=0)throw new ArgumentException("Pin 搜索盒尺寸必须为正");
            groups=new();
            foreach(var row in recipe)
            {
                ctx.CancellationToken.ThrowIfCancellationRequested();
                var x=Number(row,"PinPositionX");var y=Number(row,"PinPositionY");var z=Number(row,"PinHeight");
                var group=selected.Where(i=>Math.Abs(local.X[i]-x)<=length/2&&Math.Abs(local.Y[i]-y)<=width/2&&Math.Abs(local.Z[i]-z)<=depth/2).ToList();
                if(group.Count<minimum)throw new ArgumentException($"Pin {groups.Count+1} 搜索盒只有 {group.Count} 个点，少于 {minimum}");
                groups.Add(group);
            }
            if(groups.SelectMany(g=>g).Distinct().Count()!=groups.Sum(g=>g.Count))throw new ArgumentException("Pin 配方搜索盒重叠并引用相同点，请调整搜索盒尺寸");
        }
        else groups=CloudComponents.Find(local,selected,Number(settings,"Minimum Distance Tolerance of Pins",.5),ctx.CancellationToken).Where(g=>g.Count>=minimum).ToList();
        if(groups.Count==0)throw new ArgumentException("提取 Pin 针失败，请检查点间距和最少点数");
        var pins=groups.Select(indices=>{
            var zs=indices.Select(i=>(double)local.Z[i]).OrderBy(z=>z).ToArray();
            var removal=Text(options,"Outlier Removal Method","None");
            if(height&&removal!="None")
            {
                if(removal!="Percentile trimming")throw new ArgumentException("Pin 高度滤波方式不支持："+removal);
                var trim=C3.Sub(options,"Percentile Trimming");double low=Number(trim,"Percent to Trim from Bottom",10),high=Number(trim,"Percent to Trim from Top",10);
                if(low<0||high<0||low>50||high>50||low+high>=100)throw new ArgumentException("Pin 高度去除百分比无效");
                zs=zs.Skip((int)(zs.Length*low/100)).Take(zs.Length-(int)(zs.Length*low/100)-(int)(zs.Length*high/100)).ToArray();
            }
            double Quantile(double percent){if(percent<0||percent>100)throw new ArgumentException("百分位须在 0..100");double i=(zs.Length-1)*percent/100;return zs[(int)i]*(1-i+(int)i)+zs[(int)Math.Ceiling(i)]*(i-(int)i);}
            var average=zs.Average();
            var z=height?Text(options,"Height Mode","Median") switch {
                "Minimum"=>zs[0],"Maximum"=>zs[^1],"Mean"=>zs.Average(),"Median"=>Quantile(50),"Percentile"=>Quantile(Number(options,"Percentile",10)),
                "Standard deviation"=>Math.Sqrt(zs.Average(z=>Math.Pow(z-average,2))),_=>throw new ArgumentException("未知 Pin 高度模式")
            }:zs.Average();
            return new Pin(indices,indices.Average(i=>(double)local.X[i]),indices.Average(i=>(double)local.Y[i]),z);
        }).ToList();
        if(!compare)pins=Sort(pins,Text(settings,"Sort Mode","+X,-Y"),p.GroupDbl("Sort Settings","Offset",1));
        if(interval)
        {
            if(pins.Count<2)throw new ArgumentException("Pin 间隔测量至少需要两个 Pin");
            foreach(var pin in pins){var next=pins.Where(q=>!ReferenceEquals(q,pin)).MinBy(q=>Math.Pow(pin.X-q.X,2)+Math.Pow(pin.Y-q.Y,2)+Math.Pow(pin.Z-q.Z,2));pin.SideX=Math.Abs(pin.X-next.X);pin.SideY=Math.Abs(pin.Y-next.Y);pin.SideZ=Math.Abs(pin.Z-next.Z);}
        }
        var results=new List<object>();var pinIds=new List<object>();var keys=new List<object>();
        for(int i=0;i<pins.Count;i++)
        {
            var pin=pins[i];var row=recipe?[i];var ok=true;
            if(compare)
            {
                bool Within(string field,double measured)
                {
                    // Supply is the compensation applied to the measured value.
                    var target=Number(row,field);var low=Number(row,field+"Min");var high=Number(row,field+"Max");
                    if(low<0||high<0)throw new ArgumentException("Pin 配方公差不能为负");
                    var corrected=measured+Number(row,field+"Supply",0);return corrected>=target-low&&corrected<=target+high;
                }
                ok=Within("PinPositionX",pin.X)&Within("PinPositionY",pin.Y);
                if(height)ok&=Within("PinHeight",pin.Z);
                if(interval){ok&=Within("PinSideX",pin.SideX)&Within("PinSideY",pin.SideY);if(height)ok&=Within(row.ContainsKey("PinSideHeight")?"PinSideHeight":"PinSideHight",pin.SideZ);}
            }
            pinIds.Add(compare?Number(row,"pin"):i+1);keys.Add(compare?Number(row,"key",i):i);
            results.Add(ok?" OK":" NG");
        }
        DetectionOverlay2D.Begin(ctx,Driver);
        void Output(string key,IEnumerable<object> values)=>ctx.Vars.Set(key,values.ToList());
        Output("pin",pinIds);Output("key",keys);Output("pin_position_x",pins.Select(q=>(object)q.X));Output("pin_position_y",pins.Select(q=>(object)q.Y));
        if(height)Output("pin_position_height",pins.Select(q=>(object)q.Z));
        if(interval){Output("pin_position_side_x",pins.Select(q=>(object)q.SideX));Output("pin_position_side_y",pins.Select(q=>(object)q.SideY));if(height)Output("pin_position_side_height",pins.Select(q=>(object)q.SideZ));}
        if(compare)
        {
            Output("pin_result",results);
            if(Flag(options,"Measure Result"))
            {
                var result=results.All(r=>Equals(r," OK"));ctx.Vars.Set("Result",result);
                // The manual requires a global measurement result, but does not define
                // its name. Keep it separate for each operation; generic names such as
                // pin/key must remain ordinary outputs, otherwise independent parallel
                // measurements overwrite user variables or fail their atomic commit.
                var prefix=string.IsNullOrWhiteSpace(ctx.OperationUuid)?null:$"PinMatch.{ctx.OperationUuid}.";
                if(prefix!=null)
                {
                    ctx.Globals.Set(prefix+"Result",result);
                    ctx.Globals.Set(prefix+"pin_result",results.ToList());
                }
                ctx.Vars.Set("Warning",true);
                ctx.Vars.Set("Message",prefix==null
                    ?"自有内核：直接调用未提供算子 UUID，测量结果仅保存为算子输出；未写入通用全局变量。"
                    :$"自有内核：全局测量结果保存为 {prefix}Result 和 {prefix}pin_result；该命名规则未与原版校准。");
            }
        }
        ctx.Clouds.SetPointSets(input,pins.Select(q=>new PointSet3D(cloud,q.Indices)).ToArray());
        for(int i=0;i<pins.Count;i++)
        {
            var pin=pins[i];var lines=new List<string>();
            if(p.Bool("Display Pin Index"))lines.Add("Pin "+pinIds[i]);
            void Label(string setting,string name,double value){if(p.Bool(setting))lines.Add(name+value.ToString("0.000",CultureInfo.InvariantCulture));}
            Label("Display Pin Position X","X=",pin.X);Label("Display Pin Position Y","Y=",pin.Y);
            if(height)Label("Display Pin Position Height","H=",pin.Z);
            if(interval){Label("Display Pin Position Side X","ΔX=",pin.SideX);Label("Display Pin Position Side Y","ΔY=",pin.SideY);if(height)Label("Display Pin Position Side Height","ΔH=",pin.SideZ);}
            if(compare&&p.Bool("Display Pin Result"))lines.Add((string)results[i]);
            var center=pin.Indices;var world=CreateRoiArrayOp.Point(center.Average(j=>(double)cloud.X[j]),center.Average(j=>(double)cloud.Y[j]),center.Average(j=>(double)cloud.Z[j]));
            if(lines.Count>0)ctx.Texts.Add(new(){Owner=string.IsNullOrEmpty(ctx.OperationUuid)?Driver:ctx.OperationUuid,ViewIndex=input,X=(double)world["X"],Y=(double)world["Y"],Z=(double)world["Z"],Text=string.Join("\n",lines),Color=compare&&!Equals(results[i]," OK")?"red":"green",Size=12,SizeIsPixels=true});
            if(compare&&p.Bool("Display Box"))DetectionOverlay2D.Add(ctx,p,Driver,"orientedbox",new(){["Center"]=world,["Size"]=CreateRoiArrayOp.Point(Number(settings,"Length",1),Number(settings,"Width",1),Number(settings,"Height",1)),["Rotation"]=CreateRoiArrayOp.Point(p.GroupDbl("Reference","X Angle"),p.GroupDbl("Reference","Y Angle"),p.GroupDbl("Reference","Z Angle"))},view:input);
        }
        cloud.DisplayIndices=p.Bool("Pointset Cut")&&p.Bool("Use Region")?selected.ToArray():null;
        if(Driver=="PinRecipe")
        {
            var rows=pins.Select((pin,i)=>{
                var row=new Dictionary<string,object>{["pin"]=i+1,["key"]=i};
                void Field(string name,double value,double tolerance){row[name]=value;row[name+"Min"]=tolerance;row[name+"Max"]=tolerance;row[name+"Supply"]=0;}
                if(Flag(options,"Measure Position",true)){Field("PinPositionX",pin.X,.5);Field("PinPositionY",pin.Y,.5);}
                if(height)Field("PinHeight",pin.Z,.5);
                if(interval){Field("PinSideX",pin.SideX,.3);Field("PinSideY",pin.SideY,.3);if(height)Field("PinSideHeight",pin.SideZ,.3);}
                return row;
            }).ToArray();
            var path=ctx.ResolveOutputPath(p.Str("File"))??throw new ArgumentException("Pin 配方输出路径为空");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllText(path,JsonSerializer.Serialize(new{recipe=rows},new JsonSerializerOptions{WriteIndented=true}));
        }
        ctx.Log($"Pin 检测：{pins.Count} 个，{(compare?results.Count(r=>Equals(r," OK"))+" 个合格":"未启用配方比对")}",false);
        return true;
    }
    private static List<Pin> Sort(List<Pin> pins,string mode,double offset)
    {
        var axes=mode.Split(',');if(axes.Length!=2||axes.Any(a=>a is not("+X" or "-X" or "+Y" or "-Y"))||axes[0][1]==axes[1][1]||!double.IsFinite(offset)||offset<0)throw new ArgumentException("Pin 排序方式/偏移无效");
        double Axis(Pin q,string axis)=>(axis[1]=='X'?q.X:q.Y)*(axis[0]=='-'?-1:1);
        var pending=pins.OrderBy(q=>Axis(q,axes[1])).ToList();var sorted=new List<Pin>();
        while(pending.Count>0){var start=Axis(pending[0],axes[1]);var row=pending.TakeWhile(q=>Axis(q,axes[1])-start<=offset).ToList();sorted.AddRange(row.OrderBy(q=>Axis(q,axes[0])));pending.RemoveRange(0,row.Count);}
        return sorted;
    }
}
