using Avalonia;

namespace THRM.Avalonia;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Any(static arg => string.Equals(arg, "--self-check", StringComparison.OrdinalIgnoreCase)))
        {
            IpcProtocolSelfCheck.Run();
            FanCurveEdit.SelfCheck();
            TemperatureHistoryPreview.SelfCheck();
            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .UseWaylandWithFallback()
            .LogToTrace();
}
