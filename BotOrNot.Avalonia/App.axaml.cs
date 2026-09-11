using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using BotOrNot.Avalonia.Services;
using BotOrNot.Avalonia.ViewModels;
using BotOrNot.Avalonia.Views;

namespace BotOrNot.Avalonia;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var settingsService = new SettingsService();
            var themeService = new ThemeService(settingsService, this);
            themeService.ApplySavedTheme();
            desktop.MainWindow = new MainWindow(new AppViewModel(settingsService, themeService));
        }

        base.OnFrameworkInitializationCompleted();
    }
}
