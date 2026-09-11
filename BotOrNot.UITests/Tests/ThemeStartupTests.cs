using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Styling;
using BotOrNot.Avalonia.Services;
using BotOrNot.Avalonia.ViewModels;
using BotOrNot.Core.Models;

namespace BotOrNot.UITests.Tests;

[TestFixture]
public sealed class ThemeStartupTests
{
    private string _directory = null!;
    private string _settingsPath = null!;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"botornot-theme-{Guid.NewGuid():N}");
        _settingsPath = Path.Combine(_directory, "settings.json");
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [AvaloniaTest]
    public void StoredDarkTheme_AppliesBeforeLibraryAndPersistsThroughNavigation()
    {
        var settings = new SettingsService(_settingsPath);
        settings.Save(new AppSettings
        {
            Theme = ThemePreference.Dark,
            ReplayDirectory = "C:\\replays",
            ReplayScanLimit = 75
        });
        var theme = new ThemeService(settings, Application.Current);

        var appViewModel = new AppViewModel(settings, theme);

        Assert.Multiple(() =>
        {
            Assert.That(appViewModel.CurrentPage, Is.SameAs(appViewModel.LibraryPage));
            Assert.That(Application.Current!.RequestedThemeVariant, Is.EqualTo(ThemeVariant.Dark));
            Assert.That(GetThemeMarker(ThemeVariant.Dark), Is.EqualTo("dark"));
        });

        NavigateToMatch(appViewModel);
        var match = (MainWindowViewModel)appViewModel.CurrentPage;
        match.CycleThemeCommand.Execute().Subscribe();

        Assert.Multiple(() =>
        {
            Assert.That(Application.Current!.RequestedThemeVariant, Is.EqualTo(ThemeVariant.Default));
            Assert.That(settings.Load().ReplayDirectory, Is.EqualTo("C:\\replays"));
            Assert.That(settings.Load().ReplayScanLimit, Is.EqualTo(75));
        });

        match.BackCommand.Execute().Subscribe();
        Assert.That(appViewModel.CurrentPage, Is.SameAs(appViewModel.LibraryPage));
        Assert.That(Application.Current!.RequestedThemeVariant, Is.EqualTo(ThemeVariant.Default));

        var restartedTheme = new ThemeService(new SettingsService(_settingsPath), Application.Current);
        restartedTheme.ApplySavedTheme();
        Assert.That(restartedTheme.CurrentTheme, Is.EqualTo(ThemePreference.System));
    }

    [AvaloniaTest]
    public void StoredLightTheme_AppliesBeforeLibrary()
    {
        var settings = new SettingsService(_settingsPath);
        settings.Save(new AppSettings { Theme = ThemePreference.Light });

        var appViewModel = new AppViewModel(settings, new ThemeService(settings, Application.Current));

        Assert.Multiple(() =>
        {
            Assert.That(appViewModel.CurrentPage, Is.SameAs(appViewModel.LibraryPage));
            Assert.That(Application.Current!.RequestedThemeVariant, Is.EqualTo(ThemeVariant.Light));
            Assert.That(GetThemeMarker(ThemeVariant.Light), Is.EqualTo("light"));
        });
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

    private static string GetThemeMarker(ThemeVariant theme)
    {
        var found = Application.Current!.TryFindResource("ThemeMarker", theme, out var resource);
        Assert.That(found, Is.True);
        return (string)resource!;
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
