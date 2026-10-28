using YamlDotNet.RepresentationModel;

namespace AiVisionStudio.Services;

public partial class MainViewModel
{
    public ProgramModel CreateProgram(string name)
    {
        RequireProgramEdit(name);
        var template=SelectedProgram??Programs[0];
        var model=new ProgramModel{Name=name.Trim(),Root=template.Root,RecipePath=template.RecipePath,
            Definition=new YamlMappingNode(),Raw=new YamlMappingNode{{"order",new YamlSequenceNode()},{"define",new YamlMappingNode()}}};
        model.Variables.AddRange(template.Variables);
        BeforeEdit();Programs.Add(model);SelectedProgram=model;AfterEdit();Status="已新建程序："+model.Name;return model;
    }
    public void RenameProgram(ProgramModel program,string name)
    {
        if(program==null||!Programs.Contains(program))throw new ArgumentException("请选择程序");
        if(program.Name==name?.Trim())return;
        RequireProgramEdit(name);
        var old=program.Name;BeforeEdit();program.Name=name.Trim();
        foreach(var op in Programs.SelectMany(p=>p.Operations))
        {
            if(op.ParametersRaw is not YamlMappingNode root||HmiYamlGet(root,"value") is not YamlMappingNode values)continue;
            if(op.Driver=="Call"&&HmiYamlGet(values,"Target") is YamlScalarNode target&&target.Value==old)target.Value=program.Name;
            if(op.Driver=="Parallel Call")foreach(var group in values.Children.Values.OfType<YamlMappingNode>())
                if(HmiYamlGet(group,"Program") is YamlScalarNode targetName&&targetName.Value==old)targetName.Value=program.Name;
        }
        RenameVisualProgram(old, program.Name);

        AfterEdit();
        LoadParams();

        Status = "程序已重命名：" + program.Name;
    }
    public void SetDefaultProgram(ProgramModel program)
    {
        RequireManagementEdit();if(program==null||!Programs.Contains(program))throw new ArgumentException("请选择程序");
        if(program.IsDefault)return;if(!ApplyParameters())throw new InvalidOperationException("请先修正无效参数");
        BeforeEdit();foreach(var item in Programs)item.IsDefault=ReferenceEquals(item,program);AfterEdit();Status="默认程序："+program.Name;
    }
    public void DeleteProgram(ProgramModel program)
    {
        RequireManagementEdit();if(program==null||!Programs.Contains(program))throw new ArgumentException("请选择程序");
        if(Programs.Count==1)throw new InvalidOperationException("工程至少需要保留一个程序");
        foreach(var source in Programs.Where(p=>p!=program))foreach(var op in source.Operations)
        {
            var bag=Algorithm.ParamBag.FromYaml(op.ParametersRaw as YamlNode);
            if(op.Driver=="Call"&&bag.Str("Target")==program.Name||op.Driver=="Parallel Call"&&Enumerable.Range(1,bag.Int("Target Count")).Any(i=>Equals(bag.Group("Target"+i)?.GetValueOrDefault("Program"),program.Name)))
                throw new InvalidOperationException($"程序仍被 {source.Name} / {op.Name} 调用，请先解除调用");
            if(bag.Bindings.Values.Any(b=>program.Operations.Any(o=>o.Uuid==b.Uuid)))throw new InvalidOperationException($"程序输出仍被 {source.Name} / {op.Name} 引用");
        }
        if(!ApplyParameters())throw new InvalidOperationException("请先修正无效参数");
        BeforeEdit();Programs.Remove(program);if(program.IsDefault)Programs[0].IsDefault=true;
        if(SelectedProgram==program)SelectedProgram=Programs.FirstOrDefault(p=>p.IsDefault)??Programs[0];
        AfterEdit();Status="已删除程序："+program.Name;
    }
    private void RequireProgramEdit(string name)
    {
        RequireManagementEdit();name=name?.Trim();
        if(string.IsNullOrWhiteSpace(name)||name.Length>80||name.Any(char.IsControl))throw new ArgumentException("程序名称须为 1–80 个可见字符");
        if(Programs.Any(p=>p.Name.Equals(name,StringComparison.OrdinalIgnoreCase)))throw new ArgumentException("程序名称已存在："+name);
        if(!ApplyParameters())throw new InvalidOperationException("请先修正无效参数");
    }
}
