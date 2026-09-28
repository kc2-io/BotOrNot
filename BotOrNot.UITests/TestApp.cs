using global::Avalonia;
using global::Avalonia.Controls.ApplicationLifetimes;
using global::Avalonia.Controls;
using global::Avalonia.Headless;
using global::Avalonia.Styling;
using global::Avalonia.Themes.Fluent;

[assembly: AvaloniaTestApplication(typeof(BotOrNot.UITests.TestAppBuilder))]

namespace BotOrNot.UITests;

public class TestApp : Application
{
    public override void Initialize()
    {
        Resources.ThemeDictionaries[ThemeVariant.Light] = new ResourceDictionary
        {
            ["ThemeMarker"] = "light"
        };
        Resources.ThemeDictionaries[ThemeVariant.Dark] = new ResourceDictionary
        {
            ["ThemeMarker"] = "dark"
        };
        Styles.Add(new FluentTheme());
        Styles.Add(new global::Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://BotOrNot.UITests"))
        {
            Source = new Uri("avares://Avalonia.Controls.DataGrid/Themes/Fluent.xaml")
        });
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = global::Avalonia.Controls.ShutdownMode.OnExplicitShutdown;
        }

        base.OnFrameworkInitializationCompleted();
    }
}

public class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<TestApp>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
