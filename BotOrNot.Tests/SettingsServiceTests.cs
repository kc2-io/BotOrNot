using BotOrNot.Avalonia.Services;

namespace BotOrNot.Tests;

[TestFixture]
public sealed class SettingsServiceTests
{
    private readonly List<string> _tempPaths = new();

    private string GetTempPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"botornot-settings-test-{Guid.NewGuid():N}.json");
        _tempPaths.Add(path);
        return path;
    }

    [Test]
    public void Update_PreservesReplayDirectory_WhenChangingTheme()
    {
        var path = GetTempPath();
        var service = new SettingsService(path);
        service.Save(new AppSettings
        {
            Theme = ThemePreference.System,
            ReplayDirectory = @"C:\Replays"
        });

        service.Update(settings => settings.Theme = ThemePreference.Dark);

        var saved = service.Load();
        Assert.That(saved.Theme, Is.EqualTo(ThemePreference.Dark));
        Assert.That(saved.ReplayDirectory, Is.EqualTo(@"C:\Replays"),
            "Cycling the theme must not overwrite the saved replay directory.");
    }

    [Test]
    public void Update_PreservesOtherSettings_WhenChangingTheme()
    {
        var path = GetTempPath();
        var service = new SettingsService(path);
        service.Save(new AppSettings
        {
            Theme = ThemePreference.Light,
            ReplayDirectory = @"D:\Fortnite",
            ReplayScanLimit = 100,
            LibraryAutoRefreshEnabled = false,
            LibraryAutoRefreshMinutes = 30
        });

        service.Update(settings => settings.Theme = ThemePreference.System);

        var saved = service.Load();
        Assert.That(saved.Theme, Is.EqualTo(ThemePreference.System));
        Assert.That(saved.ReplayDirectory, Is.EqualTo(@"D:\Fortnite"));
        Assert.That(saved.ReplayScanLimit, Is.EqualTo(100));
        Assert.That(saved.LibraryAutoRefreshEnabled, Is.False);
        Assert.That(saved.LibraryAutoRefreshMinutes, Is.EqualTo(30));
    }

    [TearDown]
    public void TearDown()
    {
        foreach (var path in _tempPaths)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
        _tempPaths.Clear();
    }
}
