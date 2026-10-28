namespace AiVisionStudio.Algorithm.Impl;

public sealed class CreateGeometry2DOp:IOperator
{
    public string Driver=>"2D Create Geometry";
    public bool Run(OpContext ctx,ParamBag p)
    {
        DetectionOverlay2D.Begin(ctx,Driver);var total=0;
        foreach(var (name,type) in new[]{("Rect","rect2d"),("Circle","circle2d"),("Point","point2d"),("Segment","segment2d")})
        {
            var results=new List<object>();
            if(p.Bool("Enable "+name+" Geometry")&&p.Group(name+" Geometry")?.GetValueOrDefault(type+" array") is IEnumerable<object> values)
                foreach(var shape in values.OfType<IReadOnlyDictionary<string,object>>())
                {
                    var item=new Dictionary<string,object>(shape);results.Add(item);total++;
                    if(p.Bool(name+" Result"))DetectionOverlay2D.Add(ctx,p,Driver,type,item,name+" Result Display");
                }
            ctx.Vars.Set(name+" Result",results);
        }
        ctx.Log($"创建 2D 几何：{total} 项",false);return total>0;
    }
}
