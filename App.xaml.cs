using System.Windows;
using System.Windows.Threading;
using MusicRecorder.Core;

namespace MusicRecorder;

public partial class App : Application
{
    private Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("未处理的后台异常", args.ExceptionObject as Exception);

        if (e.Args.Any(a => a.StartsWith("--selftest", StringComparison.OrdinalIgnoreCase) ||
                            a.StartsWith("--e2e", StringComparison.OrdinalIgnoreCase) ||
                            a.StartsWith("--gentest", StringComparison.OrdinalIgnoreCase) ||
                            a.StartsWith("--fakeplayer", StringComparison.OrdinalIgnoreCase) ||
                            a.StartsWith("--pause", StringComparison.OrdinalIgnoreCase) ||
                            a.StartsWith("--diag", StringComparison.OrdinalIgnoreCase) ||
                            a.StartsWith("--compare", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(Task.Run(() => SelfTest.Run(e.Args)).GetAwaiter().GetResult());
            return;
        }

        _singleInstance = new Mutex(true, @"Local\MusicRecorder.SingleInstance", out var created);
        if (!created)
        {
            MessageBox.Show("音乐内录工具已经在运行中。", "MusicRecorder", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(0);
            return;
        }

        Log.Info("程序启动");
        new MainWindow().Show();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("界面线程未处理异常", e.Exception);
        MessageBox.Show($"发生未处理的错误：\r\n{e.Exception.Message}\r\n\r\n日志：{Log.LogDirectory}",
            "MusicRecorder", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
