using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using BotOrNot.Avalonia.Services;
using BotOrNot.Avalonia.ViewModels;
using BotOrNot.Avalonia.Views;
using BotOrNot.Core.Models;
using BotOrNot.Core.Services;

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
            var benchmark = NativeBenchmarkLaunch.Current;
            var settingsService = benchmark is null
                ? new SettingsService()
                : CreateBenchmarkSettings(benchmark.Config);
            var themeService = new ThemeService(settingsService, this);
            themeService.ApplySavedTheme();
            var viewModel = benchmark is null
                ? new AppViewModel(settingsService, themeService)
                : new AppViewModel(settingsService, themeService,
                    new ReplayCacheService(cachePath: benchmark.Config.CachePath),
                    () => new ReplayScanOptions { MaxConcurrency = benchmark.Config.MaxConcurrency }, benchmark);
            var window = new MainWindow(viewModel);
            desktop.MainWindow = window;
            benchmark?.Attach(window, desktop, viewModel.LibraryPage);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static SettingsService CreateBenchmarkSettings(NativeBenchmarkConfig config)
    {
        var settings = new SettingsService(config.SettingsPath);
        settings.Save(new AppSettings
        {
            ReplayDirectory = config.ReplayDirectory,
            ReplayScanLimit = config.ScanLimit,
            Theme = ThemePreference.Dark
        });
        var saved = settings.Load();
        if (!string.Equals(saved.ReplayDirectory, config.ReplayDirectory, StringComparison.Ordinal) ||
            saved.ReplayScanLimit != config.ScanLimit)
            throw new InvalidOperationException("The isolated native benchmark settings file could not be saved.");
        return settings;
    }
}
