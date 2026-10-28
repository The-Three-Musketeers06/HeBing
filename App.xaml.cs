using System.Windows;
using System.Windows.Threading;
using AiVisionStudio.Views;
using System.IO;

namespace AiVisionStudio;

/// <summary>应用入口：先显示启动画面，加载完成后打开主窗口。</summary>
public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnCrash;

        // 批量验证模式：--verify <目录> [输出文件]
        var argv = e.Args;
        var verifyIdx = Array.IndexOf(argv, "--verify");
        if (verifyIdx >= 0 && verifyIdx + 1 < argv.Length)
        {
            var dir = argv[verifyIdx + 1];
            var outFile = verifyIdx + 2 < argv.Length ? argv[verifyIdx + 2] : "verify.txt";
            Services.VerifyRunner.Run(dir, outFile);
            Shutdown(0);
            return;
        }

        var settingsError=Services.StudioSettings.Load();if(settingsError!=null)StartupLog(settingsError);
        var splash = new SplashWindow();
        splash.Show();

        try
        {
            StartupLog("读取工具定义");
            splash.SetProgress(0, 3, "读取工具定义");
            await Dispatcher.Yield(DispatcherPriority.Render);
            await Task.Run(() => Models.DriverCatalog.Load(Path.Combine(AppContext.BaseDirectory, "Assets", "driver_defs.json")))
                .WaitAsync(TimeSpan.FromSeconds(20));
            StartupLog("创建主窗口");
            splash.SetProgress(1, 3, "创建主窗口");
            await Dispatcher.Yield(DispatcherPriority.Render);
            var main = new MainWindow();
            MainWindow = main;
            splash.SetProgress(2, 3, "显示工作区");
            main.Show();
            StartupLog("主窗口已就绪；计算内核按运行请求启动");
            splash.SetProgress(3, 3, "就绪");
            splash.Close();
        }
        catch (Exception error)
        {
            StartupLog(error.ToString());
            MessageBox.Show(splash, "启动失败：" + error.Message + "\n详细信息已写入 UserData/startup.log。", "ES-Vision-Studio", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private static void StartupLog(string message)
    {
        try
        {
            var directory = Path.Combine(AppContext.BaseDirectory, "UserData"); Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "startup.log"), $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private static void OnCrash(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var nl = Environment.NewLine;
        try
        {
            var path = System.IO.Path.Combine(AppContext.BaseDirectory, "crash.log");
            System.IO.File.AppendAllText(path,
                "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + e.Exception + nl + nl);
        }
        catch { /* 忽略 */ }
        e.Handled = true;
        MessageBox.Show("发生异常：" + e.Exception.Message, "ES-Vision-Studio", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
