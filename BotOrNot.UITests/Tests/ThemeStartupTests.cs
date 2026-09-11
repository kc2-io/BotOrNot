using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Styling;
using BotOrNot.Avalonia.Services;
using BotOrNot.Avalonia.ViewModels;
using BotOrNot.Core.Models;
using BotOrNot.Core.Services;

namespace BotOrNot.UITests.Tests;

[TestFixture]
public sealed class ThemeStartupTests
{
    private string _directory = null!;
    private string _settingsPath = null!;
    private ThemeVariant? _originalRequestedThemeVariant;
    private Window? _themeWindow;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"botornot-theme-{Guid.NewGuid():N}");
        _settingsPath = Path.Combine(_directory, "settings.json");
        _originalRequestedThemeVariant = Application.Current!.RequestedThemeVariant;
    }

    [TearDown]
    public void TearDown()
    {
        _themeWindow?.Close();
        Application.Current!.RequestedThemeVariant = _originalRequestedThemeVariant;

        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [AvaloniaTest]
    public void StoredDarkTheme_AppliesBeforeLibraryAndPersistsThroughNavigation()
    {
        var settings = new SettingsService(_settingsPath);
        settings.Save(new AppSettings { Theme = ThemePreference.Dark });
        var theme = new ThemeService(settings, Application.Current);

        var appViewModel = new AppViewModel(settings, theme);
        var themeControl = ShowThemeHost();

        Assert.Multiple(() =>
        {
            Assert.That(appViewModel.CurrentPage, Is.SameAs(appViewModel.LibraryPage));
            Assert.That(Application.Current!.RequestedThemeVariant, Is.EqualTo(ThemeVariant.Dark));
            Assert.That(themeControl.ActualThemeVariant, Is.EqualTo(ThemeVariant.Dark));
            Assert.That(GetThemeMarker(themeControl), Is.EqualTo("dark"));
        });
    }

    [AvaloniaTest]
    public void StoredLightTheme_UpdatesActiveResourceAndPreservesSettingsThroughNavigation()
    {
        var settings = new SettingsService(_settingsPath);
        settings.Save(new AppSettings
        {
            Theme = ThemePreference.Light,
            ReplayDirectory = "C:\\replays",
            ReplayScanLimit = 75
        });
        var theme = new ThemeService(settings, Application.Current);

        var appViewModel = new AppViewModel(settings, theme, new StubReplayCacheService());
        var themeControl = ShowThemeHost();

        Assert.Multiple(() =>
        {
            Assert.That(appViewModel.CurrentPage, Is.SameAs(appViewModel.LibraryPage));
            Assert.That(Application.Current!.RequestedThemeVariant, Is.EqualTo(ThemeVariant.Light));
            Assert.That(themeControl.ActualThemeVariant, Is.EqualTo(ThemeVariant.Light));
            Assert.That(GetThemeMarker(themeControl), Is.EqualTo("light"));
        });

        NavigateToMatch(appViewModel);
        var match = (MainWindowViewModel)appViewModel.CurrentPage;
        match.CycleThemeCommand.Execute().Subscribe();

        Assert.Multiple(() =>
        {
            Assert.That(Application.Current!.RequestedThemeVariant, Is.EqualTo(ThemeVariant.Dark));
            Assert.That(themeControl.ActualThemeVariant, Is.EqualTo(ThemeVariant.Dark));
            Assert.That(GetThemeMarker(themeControl), Is.EqualTo("dark"));
            Assert.That(settings.Load().ReplayDirectory, Is.EqualTo("C:\\replays"));
            Assert.That(settings.Load().ReplayScanLimit, Is.EqualTo(75));
        });

        match.BackCommand.Execute().Subscribe();
        Assert.That(appViewModel.CurrentPage, Is.SameAs(appViewModel.LibraryPage));
        Assert.That(Application.Current!.RequestedThemeVariant, Is.EqualTo(ThemeVariant.Dark));

        var restartedTheme = new ThemeService(new SettingsService(_settingsPath), Application.Current);
        restartedTheme.ApplySavedTheme();
        Assert.That(restartedTheme.CurrentTheme, Is.EqualTo(ThemePreference.Dark));
    }

    [AvaloniaTest]
    public void SystemTheme_RequestsDefaultVariant()
    {
        var settings = new SettingsService(_settingsPath);
        settings.Save(new AppSettings { Theme = ThemePreference.System });

        var theme = new ThemeService(settings, Application.Current);
        theme.ApplySavedTheme();

        Assert.Multiple(() =>
        {
            Assert.That(theme.CurrentTheme, Is.EqualTo(ThemePreference.System));
            Assert.That(Application.Current!.RequestedThemeVariant, Is.EqualTo(ThemeVariant.Default));
        });
    }

    private Control ShowThemeHost()
    {
        var control = new Border();
        _themeWindow = new Window { Content = control };
        _themeWindow.Show();
        return control;
    }

    private static string GetThemeMarker(Control control)
    {
        var found = control.TryFindResource("ThemeMarker", control.ActualThemeVariant, out var resource);
        Assert.That(found, Is.True);
        return (string)resource!;
    }

    private sealed class StubReplayCacheService : IReplayCacheService
    {
        public Task<IReadOnlyList<ReplaySummary>> GetSummariesAsync(
            string directory,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ReplaySummary>>(Array.Empty<ReplaySummary>());
    }

    private static void NavigateToMatch(AppViewModel appViewModel)
    {
        var navigate = typeof(AppViewModel).GetMethod(
            "NavigateToMatch",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        navigate.Invoke(appViewModel,
        [
            new ReplaySummary { FilePath = Path.Combine(Path.GetTempPath(), "missing.replay") }
        ]);
    }
}
