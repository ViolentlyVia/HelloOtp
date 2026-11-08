using System.Windows;
using System.Windows.Threading;

namespace HelloOtp;

public partial class App : Application
{
    private Mutex? _instance;
    private AppController? _controller;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instance = new Mutex(initiallyOwned: true, @"Local\HelloOtp.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUnhandledException;
        Theme.Apply();
        _controller = new AppController();
        _controller.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _controller?.Stop();
        base.OnExit(e);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Fail closed: anything unexpected locks every window holding secrets.
        e.Handled = true;
        _controller?.LockAll();
        MessageBox.Show(e.Exception.Message, "HelloOtp", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
