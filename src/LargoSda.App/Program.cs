using Avalonia;

namespace LargoSda.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (Updater.TryApplyMode(args)) return;
        Updater.Cleanup();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
