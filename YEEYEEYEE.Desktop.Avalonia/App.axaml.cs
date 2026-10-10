using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace YEEYEEYEE.Desktop.Avalonia;

public partial class App : Application
{
    internal ComfyUiNativeWebViewService? ComfyUiWebViewService { get; private set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            ComfyUiWebViewService = ComfyUiNativeWebViewService.Start();
            desktop.ShutdownRequested += (_, _) => ComfyUiWebViewService.Dispose();
            desktop.MainWindow = new MainWindow(desktop.Args);
            if (desktop.Args is ["--verify-comfy", var url, var output])
                Dispatcher.UIThread.Post(async () => await ComfyUiRuntimeVerification.RunAsync(ComfyUiWebViewService, url, output));
        }

        base.OnFrameworkInitializationCompleted();
    }
}
