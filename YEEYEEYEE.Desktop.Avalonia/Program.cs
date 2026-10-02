using Avalonia;

namespace YEEYEEYEE.Desktop.Avalonia;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogFatal(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => LogFatal(e.Exception);
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception error)
        {
            LogFatal(error);
            throw;
        }
    }

    private static void LogFatal(Exception? error)
    {
        if (error is null) return;
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "startup-error.log");
            File.AppendAllText(path, $"[{DateTimeOffset.Now:O}]\n{error}\n\n");
        }
        catch
        {
            // 日志写入失败时不影响原始异常抛出
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
