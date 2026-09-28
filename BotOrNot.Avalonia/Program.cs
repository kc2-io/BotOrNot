using Avalonia;
using Avalonia.ReactiveUI;
using BotOrNot.Avalonia.Services;

namespace BotOrNot.Avalonia;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Length == 2 && string.Equals(args[0], "--benchmark-config", StringComparison.Ordinal))
        {
            NativeBenchmarkLaunch.Configure(args[1]);
            BuildAvaloniaApp().StartWithClassicDesktopLifetime([]);
            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace()
            .UseReactiveUI();
}
