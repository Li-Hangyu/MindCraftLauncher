using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using MindCraftLauncher.App.ViewModels;
using MindCraftLauncher.Core;
using MindCraftLauncher.Core.Common;

namespace MindCraftLauncher.App;

public partial class App : Application
{
    public static LauncherServices Services { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 兜底：任何没被捕获的 UI 线程异常都记日志 + 提示，而不是直接闪退
        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            WriteCrash("AppDomain", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            WriteCrash("Task", args.Exception);
            args.SetObserved();
        };

        try
        {
            // 组合根：界面只拿到这一个服务对象，内部逻辑换实现时不影响界面
            Services = LauncherServices.CreateDefault();

            var window = new MainWindow { DataContext = new MainViewModel(Services) };
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            var log = WriteCrash("Startup", ex);
            var reason = ex is UnauthorizedAccessException
                ? "系统拒绝了文件或目录访问。"
                : "启动阶段发生了未处理异常。";
            MessageBox.Show(
                "启动器初始化失败。\n\n" + ex.Message +
                "\n\n异常类型：" + ex.GetType().FullName +
                "\n\n" + reason +
                "\n\n详细信息已写入：\n" + log +
                "\n\n请根据上面的异常类型和消息处理，不要把该错误默认当作配置目录权限问题。",
                "Mind Craft Launcher 启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void OnDispatcherException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var log = WriteCrash("UI", e.Exception);
        e.Handled = true;   // 保住进程，让用户至少能保存现场 / 继续操作

        MessageBox.Show(
            "操作过程中出现了一个未处理的错误：\n\n" + e.Exception.Message +
            "\n\n已记录到日志：\n" + log,
            "出错了", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>把异常写进 logs 目录，返回文件路径；写不进去也不能再抛。</summary>
    private static string WriteCrash(string kind, Exception? ex)
    {
        string logDirectory;
        try
        {
            logDirectory = Path.Combine(GameLocation.LauncherDataRoot(), "logs");
        }
        catch
        {
            logDirectory = Path.Combine(Path.GetTempPath(), "MindCraftLauncher", "logs");
        }

        var path = Path.Combine(logDirectory, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        try
        {
            Directory.CreateDirectory(logDirectory);
            var sb = new StringBuilder();
            sb.AppendLine($"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"类型：{kind}");
            sb.AppendLine($"版本：{typeof(App).Assembly.GetName().Version}");
            sb.AppendLine($"系统：{Environment.OSVersion}｜.NET {Environment.Version}");
            sb.AppendLine(new string('-', 60));
            sb.AppendLine(ex?.ToString() ?? "（没有异常对象）");
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        }
        catch
        {
            // 日志写不进去就算了，绝不能因为记日志再崩一次
        }
        return path;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 关闭陶瓦联机时顺手停掉组网进程，避免残留虚拟网卡
        try { Services?.Dispose(); } catch { }
        base.OnExit(e);
    }
}
