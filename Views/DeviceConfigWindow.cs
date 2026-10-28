using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using AiVisionStudio.Services;

namespace AiVisionStudio.Views;

public sealed class DeviceConfigWindow : Window
{
    public ObservableCollection<DeviceProfile> Devices {get;}=new();
    private readonly MainViewModel _vm;
    private readonly object _root;
    private readonly ListBox _list=new(){DisplayMemberPath="Name",MinWidth=145};
    private readonly Grid _editor=new();
    private readonly ScrollViewer _details=new(){VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,Visibility=Visibility.Collapsed};
    private readonly TextBlock _empty=new(){Text="从左侧目录点击 ＋ 新增设备\n选择已配置设备后编辑连接与采集参数",TextWrapping=TextWrapping.Wrap,FontSize=15,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center,Margin=new(20)};
    private readonly TextBlock _status=new(){TextWrapping=TextWrapping.Wrap,Margin=new(8)};
    private readonly TextBox _request=new(){Text="PING\n",AcceptsReturn=true,MinHeight=45};
    private readonly TextBox _response=new(){IsReadOnly=true,AcceptsReturn=true,MinHeight=70,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
    private readonly TextBox _registerValue=new(){Text="0",Width=90};
    private readonly List<(FrameworkElement View,string[] Protocols)> _sections=new();
    private CancellationTokenSource _cancel;
    private bool _busy,_closeAfter;
    public DeviceConfigWindow(MainViewModel vm)
    {
        _vm=vm;_root=vm.SelectedProgram?.Root;Title="设备配置 · ES-Vision-Studio";Width=980;Height=760;MinWidth=600;MinHeight=420;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty,"Bg");SetResourceReference(ForegroundProperty,"Text");FontFamily=new("Microsoft YaHei UI");FontSize=12;
        WindowScreenBehavior.Attach(this);
        foreach(var device in vm.ReadDevices())Devices.Add(device);
        var root=new DockPanel{Margin=new(12)};root.SetResourceReference(Panel.BackgroundProperty,"Bg");Content=root;
        var actions=new WrapPanel{Margin=new(0,0,0,8)};DockPanel.SetDock(actions,Dock.Top);root.Children.Add(actions);
        void AddAction(string label,Action action){var button=new Button{Content=label,Padding=new(10,5,10,5),Margin=new(0,0,6,4)};button.Click+=(_,_)=>Guard(action);actions.Children.Add(button);}
        AddAction("新增设备",()=>{Ready();var name="设备";int n=1;while(Devices.Any(d=>d.Name==name+n))n++;var item=new DeviceProfile{Name=name+n};Devices.Add(item);_list.SelectedItem=item;});
        AddAction("删除设备",()=>{Ready();if(_list.SelectedItem is DeviceProfile item)Devices.Remove(item);});
        AddAction("应用配置",Apply);
        AddAction("保存工程",()=>{Apply();vm.TrySave();});
        AddAction("加入当前程序",()=>{Apply();vm.AddDeviceOperation(_list.SelectedItem as DeviceProfile??throw new ArgumentException("请选择设备"));_status.Text=vm.Status;});
        AddAction("连接 / 启动",()=>Start(async(d,t)=>_status.Text=await DeviceService.TestAsync(d,DirectoryPath(),t,vm.DeviceRuntime)));
        AddAction("断开设备",()=>{if(_list.SelectedItem is DeviceProfile p){vm.DeviceRuntime.Stop(p.Id);_status.Text="设备已断开";}});
        AddAction("采集到 IM",()=>Start(async(d,t)=>{await vm.AcquireDeviceAsync(d,t);_status.Text=vm.Status;}));
        AddAction("取消操作",()=>_cancel?.Cancel());
        DockPanel.SetDock(_status,Dock.Bottom);root.Children.Add(_status);
        var body=new Grid();body.ColumnDefinitions.Add(new(){Width=new(1,GridUnitType.Star)});body.ColumnDefinitions.Add(new(){Width=new(3,GridUnitType.Star)});root.Children.Add(body);
        _list.ItemsSource=Devices;_list.Margin=new(0,0,10,0);
        var left=new Grid{Margin=new(0,0,6,0)};left.RowDefinitions.Add(new(){Height=new(2,GridUnitType.Star)});left.RowDefinitions.Add(new(){Height=new(1,GridUnitType.Star)});body.Children.Add(left);
        var catalog=new TreeView{BorderThickness=new(0),Background=System.Windows.Media.Brushes.Transparent};left.Children.Add(catalog);
        void Catalog(string group,params string[] protocols)
        {
            var branch=new TreeViewItem{Header=group,IsExpanded=group is "相机" or "通信",FontWeight=FontWeights.SemiBold};catalog.Items.Add(branch);
            foreach(var protocol in protocols)
            {
                var row=new DockPanel{Margin=new(0,4,0,4)};var add=new Button{Content="＋",Padding=new(4,0,4,0),Margin=new(8,0,0,0),ToolTip="新增 "+protocol};DockPanel.SetDock(add,Dock.Right);row.Children.Add(add);row.Children.Add(new TextBlock{Text=protocol,FontWeight=FontWeights.Normal,VerticalAlignment=VerticalAlignment.Center});branch.Items.Add(new TreeViewItem{Header=row});
                add.Click+=(_,_)=>Guard(()=>{Ready();var n=1;while(Devices.Any(d=>d.Name==protocol+n))n++;var item=new DeviceProfile{Name=protocol+n,Category=group,Protocol=protocol,Host=protocol.Contains("服务器")?"0.0.0.0":"127.0.0.1",Port=protocol.Contains("Modbus")?502:5000};Devices.Add(item);_list.SelectedItem=item;});
            }
        }
        Catalog("通信","串口","TCP 客户端","TCP 服务器","UDP");Catalog("I/O","虚拟 I/O");Catalog("Modbus","Modbus RTU 主站","Modbus RTU 从站","Modbus TCP","Modbus TCP 服务器");Catalog("PLC",PlcSdkBridge.ProtocolTypes.Keys.ToArray());Catalog("相机","海康 MVS","USB / DirectShow","图像文件","点云 PCD","SDK 适配器");
        var configured=new DockPanel{Margin=new(0,8,0,0)};Grid.SetRow(configured,1);left.Children.Add(configured);var label=new TextBlock{Text="已配置设备",Margin=new(3,0,0,5),FontWeight=FontWeights.SemiBold};DockPanel.SetDock(label,Dock.Top);configured.Children.Add(label);configured.Children.Add(_list);
        _list.SelectionChanged+=(_,_)=>{_editor.DataContext=_list.SelectedItem;_details.Visibility=_list.SelectedItem==null?Visibility.Collapsed:Visibility.Visible;_empty.Visibility=_list.SelectedItem==null?Visibility.Visible:Visibility.Collapsed;RefreshSections();};
        _details.Content=_editor;Grid.SetColumn(_details,1);body.Children.Add(_details);Grid.SetColumn(_empty,1);body.Children.Add(_empty);
        var fields=new StackPanel{Margin=new(2,0,8,8)};_editor.Children.Add(fields);
        void Field(Panel host,string label,string property,object[] choices=null,bool multiline=false)
        {
            var row=new Grid{Margin=new(0,4,0,4)};row.ColumnDefinitions.Add(new(){Width=new(130)});row.ColumnDefinitions.Add(new(){Width=new(1,GridUnitType.Star)});host.Children.Add(row);
            row.Children.Add(new TextBlock{Text=label,VerticalAlignment=VerticalAlignment.Center,TextWrapping=TextWrapping.Wrap});
            Control input=choices==null?new TextBox{AcceptsReturn=multiline,MinHeight=multiline?65:28,VerticalScrollBarVisibility=multiline?ScrollBarVisibility.Auto:ScrollBarVisibility.Hidden}:new ComboBox{ItemsSource=choices};
            input.SetBinding(choices==null?TextBox.TextProperty:ComboBox.SelectedItemProperty,new Binding(property){Mode=BindingMode.TwoWay,UpdateSourceTrigger=UpdateSourceTrigger.PropertyChanged,ValidatesOnExceptions=true});
            Grid.SetColumn(input,1);row.Children.Add(input);
            if(property==nameof(DeviceProfile.Protocol)&&input is ComboBox combo)combo.SelectionChanged+=(_,_)=>Dispatcher.BeginInvoke(RefreshSections);
        }
        StackPanel Section(string title,params string[] protocols)
        {
            var content=new StackPanel{Margin=new(8)};var group=new GroupBox{Header=title,Content=content,Margin=new(0,8,0,0)};fields.Children.Add(group);_sections.Add((group,protocols));return content;
        }
        Field(fields,"名称",nameof(DeviceProfile.Name));Field(fields,"设备分类",nameof(DeviceProfile.Category),DeviceProfile.Categories);
        Field(fields,"接入协议",nameof(DeviceProfile.Protocol),DeviceProfile.Protocols);
        var enabled=new CheckBox{Content="启用设备",Margin=new(130,5,0,5)};enabled.SetBinding(CheckBox.IsCheckedProperty,new Binding(nameof(DeviceProfile.Enabled)){Mode=BindingMode.TwoWay});fields.Children.Add(enabled);
        Field(fields,"输出图像槽位 (0–31)",nameof(DeviceProfile.ImageSlot));Field(fields,"超时 (ms)",nameof(DeviceProfile.TimeoutMs));
        var files=Section("文件采集","图像文件","点云 PCD");Field(files,"文件路径",nameof(DeviceProfile.File));
        var browse=new Button{Content="浏览文件…",HorizontalAlignment=HorizontalAlignment.Right,Margin=new(0,4,0,0)};files.Children.Add(browse);
        browse.Click+=(_,_)=>{var dialog=new Microsoft.Win32.OpenFileDialog{Filter="图像或点云|*.png;*.jpg;*.bmp;*.tif;*.tiff;*.pcd|所有文件|*.*"};if(dialog.ShowDialog(this)==true&&_list.SelectedItem is DeviceProfile profile){profile.File=dialog.FileName;_editor.DataContext=null;_editor.DataContext=profile;}};
        Field(Section("USB 相机","USB / DirectShow"),"相机索引",nameof(DeviceProfile.CameraIndex));
        var hik=Section("海康 MVS 相机","海康 MVS");Field(hik,"相机序列号",nameof(DeviceProfile.SerialNumber));Field(hik,"备用相机索引",nameof(DeviceProfile.CameraIndex));Field(hik,"触发模式",nameof(DeviceProfile.TriggerMode),new object[]{"连续","软触发","外触发"});Field(hik,"外触发源",nameof(DeviceProfile.TriggerSource));Field(hik,"曝光 (μs，0保持)",nameof(DeviceProfile.ExposureUs));
        var cameras=new ComboBox{DisplayMemberPath="Model",MinHeight=28};hik.Children.Add(cameras);var enumerate=new Button{Content="搜索海康相机",HorizontalAlignment=HorizontalAlignment.Right,Margin=new(0,4,0,0)};hik.Children.Add(enumerate);
        enumerate.Click+=(_,_)=>Start(async(d,t)=>{var found=await Task.Run(HikMvsService.Enumerate,t);cameras.ItemsSource=found;_status.Text=$"海康 MVS：发现 {found.Count} 台相机";});
        cameras.SelectionChanged+=(_,_)=>{if(cameras.SelectedItem is HikCameraInfo selected&&_list.SelectedItem is DeviceProfile profile){profile.SerialNumber=selected.Serial;_editor.DataContext=null;_editor.DataContext=profile;_status.Text=$"{selected.Model} / {selected.Serial} / {selected.Address}";}};
        var network=Section("网络地址",["TCP 客户端","TCP 服务器","UDP","Modbus TCP","Modbus TCP 服务器",..PlcSdkBridge.ProtocolTypes.Keys]);Field(network,"IP / 主机名",nameof(DeviceProfile.Host));Field(network,"端口",nameof(DeviceProfile.Port));
        var plc=Section("寄存器 / 点位",["Modbus TCP","Modbus TCP 服务器","Modbus RTU 主站","Modbus RTU 从站","虚拟 I/O",..PlcSdkBridge.ProtocolTypes.Keys]);Field(plc,"单元号",nameof(DeviceProfile.UnitId));Field(plc,"零起始地址",nameof(DeviceProfile.Register));Field(plc,"读取数量",nameof(DeviceProfile.RegisterCount));
        var writePanel=new WrapPanel();plc.Children.Add(writePanel);writePanel.Children.Add(_registerValue);var write=new Button{Content="写单寄存器",Margin=new(8,0,0,0)};writePanel.Children.Add(write);
        write.Click+=(_,_)=>Guard(()=>{if(!ushort.TryParse(_registerValue.Text,out var value))throw new ArgumentException("写入值须为 0..65535");Start(async(d,t)=>{await DeviceService.WriteRegisterAsync(d,value,t,vm.DeviceRuntime,DirectoryPath());_status.Text="寄存器写入已确认";});});
        var read=new Button{Content="读取寄存器 / 点位",Margin=new(0,4,0,0)};plc.Children.Add(read);read.Click+=(_,_)=>Start(async(d,t)=>_status.Text=string.Join(", ",await DeviceService.ReadRegistersAsync(d,t,vm.DeviceRuntime,DirectoryPath())));
        var serial=Section("串口参数","串口","Modbus RTU 主站","Modbus RTU 从站");Field(serial,"COM 端口",nameof(DeviceProfile.ComPort));Field(serial,"波特率",nameof(DeviceProfile.BaudRate));Field(serial,"数据位",nameof(DeviceProfile.DataBits));Field(serial,"校验位",nameof(DeviceProfile.Parity),new object[]{"n","e","o","m","s"});Field(serial,"停止位",nameof(DeviceProfile.StopBits));
        var plcSdk=Section("PLC 驱动",PlcSdkBridge.ProtocolTypes.Keys.ToArray());Field(plcSdk,"驱动 DLL 路径",nameof(DeviceProfile.AssemblyPath));Field(plcSdk,"驱动类名（可选）",nameof(DeviceProfile.TypeName));Field(plcSdk,"PLC 地址",nameof(DeviceProfile.Address));Field(plcSdk,"协议参数 (JSON)",nameof(DeviceProfile.OptionsJson),multiline:true);
        plcSdk.Children.Add(new TextBlock{Text="此组通过已安装的 HSL 通信驱动访问 PLC，需对应版本与授权；缺少驱动会明确报错。协议参数按设备型号配置。",TextWrapping=TextWrapping.Wrap,Margin=new(0,5,0,0)});
        var sdk=Section("厂家 SDK 适配器","SDK 适配器");Field(sdk,"适配器 DLL 路径",nameof(DeviceProfile.AssemblyPath));Field(sdk,"完整驱动类名",nameof(DeviceProfile.TypeName));Field(sdk,"SDK 参数 (JSON)",nameof(DeviceProfile.OptionsJson),multiline:true);
        sdk.Children.Add(new TextBlock{Text="适配器须实现 IVisionDeviceAdapter，并通过厂家 SDK 返回真实图像或点云。未安装驱动时会提示具体错误。",TextWrapping=TextWrapping.Wrap,Margin=new(0,5,0,0)});
        var exchange=Section("收发测试（UTF-8）","TCP 客户端","TCP 服务器","UDP","串口");Field(exchange,"TCP 结束符",nameof(DeviceProfile.Terminator));exchange.Children.Add(_request);var send=new Button{Content="发送并接收",HorizontalAlignment=HorizontalAlignment.Right,Margin=new(0,5,0,5)};exchange.Children.Add(send);exchange.Children.Add(_response);
        send.Click+=(_,_)=>Guard(()=>Start(async(d,t)=>_response.Text=Encoding.UTF8.GetString(await DeviceService.ExchangeAsync(d,Encoding.UTF8.GetBytes(_request.Text.Replace("\\r","\r").Replace("\\n","\n")),t,vm.DeviceRuntime))));
        Closing+=(_,e)=>{if(_busy){e.Cancel=true;_closeAfter=true;_cancel?.Cancel();}};
        Closed+=(_,_)=>_cancel?.Dispose();
        if(Devices.Count>0)_list.SelectedIndex=0;else _status.Text="新增相机、PLC 或通信设备，应用后保存到当前工程。";
    }
    private string DirectoryPath()=>string.IsNullOrEmpty(_vm.ProjectPath)?AppContext.BaseDirectory:Path.GetDirectoryName(Path.GetFullPath(_vm.ProjectPath))!;
    private void RefreshSections(){var protocol=(_list.SelectedItem as DeviceProfile)?.Protocol;foreach(var section in _sections)section.View.Visibility=section.Protocols.Contains(protocol)?Visibility.Visible:Visibility.Collapsed;}
    private void Ready()
    {if(_busy)throw new InvalidOperationException("请等待当前操作完成");if(!_vm.CanEdit)throw new InvalidOperationException("请先停止工程运行");if(!ReferenceEquals(_root,_vm.SelectedProgram?.Root))throw new InvalidOperationException("工程已切换，请关闭后重新打开设备配置");KeyboardClear();if(HasErrors(_editor))throw new ArgumentException("请修正参数中的无效输入");}
    private static void KeyboardClear()=>System.Windows.Input.Keyboard.ClearFocus();
    private static bool HasErrors(DependencyObject value){if(Validation.GetHasError(value))return true;for(int i=0;i<System.Windows.Media.VisualTreeHelper.GetChildrenCount(value);i++)if(HasErrors(System.Windows.Media.VisualTreeHelper.GetChild(value,i)))return true;return false;}
    private void Apply(){Ready();_vm.ApplyDevices(Devices);_status.Text=_vm.Status;}
    private void Guard(Action action){try{action();}catch(Exception ex){_status.Text=ex.Message;}}
    private async void Start(Func<DeviceProfile,CancellationToken,Task> action)
    {
        if(_busy){_status.Text="请等待当前操作完成";return;}
        try
        {
            Ready();var profile=(_list.SelectedItem as DeviceProfile)?.Copy()??throw new ArgumentException("请选择设备");profile.Validate();
            _busy=true;_editor.IsEnabled=false;_list.IsEnabled=false;_cancel=new();_status.Text="正在执行…";
            await action(profile,_cancel.Token);
        }
        catch(OperationCanceledException){_status.Text="操作已取消或超时";}
        catch(Exception ex){_status.Text=ex.Message;}
        finally{_busy=false;_editor.IsEnabled=true;_list.IsEnabled=true;_cancel?.Dispose();_cancel=null;if(_closeAfter)Close();}
    }
}
