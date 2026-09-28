using BotOrNot.Avalonia.Services;

namespace BotOrNot.Tests;

[TestFixture]
public sealed class SettingsServiceTests
{
    private string _directory = null!;
    private string _settingsPath = null!;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"botornot-settings-{Guid.NewGuid():N}");
        _settingsPath = Path.Combine(_directory, "settings.json");
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Test]
    public void Load_MissingOrCorruptFile_ReturnsSafeDefaults()
    {
        var service = new SettingsService(_settingsPath);

        var missing = service.Load();
        Assert.Multiple(() =>
        {
            Assert.That(missing.Theme, Is.EqualTo(ThemePreference.System));
            Assert.That(missing.ReplayScanLimit, Is.EqualTo(AppSettings.DefaultReplayScanLimit));
            Assert.That(missing.LibraryAutoRefreshEnabled, Is.True);
            Assert.That(missing.LibraryAutoRefreshMinutes, Is.EqualTo(10));
        });

        Directory.CreateDirectory(_directory);
        File.WriteAllText(_settingsPath, "not json");

        var corrupt = service.Load();
        Assert.Multiple(() =>
        {
            Assert.That(corrupt.Theme, Is.EqualTo(ThemePreference.System));
            Assert.That(corrupt.ReplayScanLimit, Is.EqualTo(AppSettings.DefaultReplayScanLimit));
            Assert.That(corrupt.LibraryAutoRefreshEnabled, Is.True);
            Assert.That(corrupt.LibraryAutoRefreshMinutes, Is.EqualTo(10));
        });
    }

    [Test]
    public void Update_PreservesOtherAndUnknownSettings()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(
            _settingsPath,
            """
            {
              "Theme": "Dark",
              "ReplayDirectory": "C:\\replays",
              "ReplayScanLimit": 75,
              "FutureSetting": { "Enabled": true }
            }
            """);
        var service = new SettingsService(_settingsPath);

        service.Update(settings => settings.Theme = ThemePreference.Light);

        var persisted = service.Load();
        Assert.Multiple(() =>
        {
            Assert.That(persisted.Theme, Is.EqualTo(ThemePreference.Light));
            Assert.That(persisted.ReplayDirectory, Is.EqualTo("C:\\replays"));
            Assert.That(persisted.ReplayScanLimit, Is.EqualTo(75));
            Assert.That(persisted.AdditionalSettings, Does.ContainKey("FutureSetting"));
            Assert.That(persisted.AdditionalSettings!["FutureSetting"].GetProperty("Enabled").GetBoolean(), Is.True);
        });
    }

    [Test]
    public void Load_InvalidPersistedValues_NormalizesToDefaults()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(_settingsPath, """{ "Theme": 999, "ReplayScanLimit": 0 }""");

        var settings = new SettingsService(_settingsPath).Load();

        Assert.Multiple(() =>
        {
            Assert.That(settings.Theme, Is.EqualTo(ThemePreference.System));
            Assert.That(settings.ReplayScanLimit, Is.EqualTo(AppSettings.DefaultReplayScanLimit));
        });
    }

    [Test]
    public void ConcurrentUpdates_DoNotDiscardUnrelatedSettings()
    {
        var service = new SettingsService(_settingsPath);
        service.Save(new AppSettings { ReplayDirectory = "C:\\replays", ReplayScanLimit = 50 });

        Parallel.Invoke(
            () => service.Update(settings => settings.Theme = ThemePreference.Dark),
            () => service.Update(settings => settings.ReplayScanLimit = 100));

        var persisted = service.Load();
        Assert.Multiple(() =>
        {
            Assert.That(persisted.Theme, Is.EqualTo(ThemePreference.Dark));
            Assert.That(persisted.ReplayDirectory, Is.EqualTo("C:\\replays"));
            Assert.That(persisted.ReplayScanLimit, Is.EqualTo(100));
        });
    }

    [Test]
    public void AutoRefreshPreferences_RoundTripAndNormalizeInvalidIntervals()
    {
        var service = new SettingsService(_settingsPath);
        service.Update(settings =>
        {
            settings.LibraryAutoRefreshEnabled = false;
            settings.LibraryAutoRefreshMinutes = 27;
        });

        Assert.Multiple(() =>
        {
            Assert.That(service.Load().LibraryAutoRefreshEnabled, Is.False);
            Assert.That(service.Load().LibraryAutoRefreshMinutes, Is.EqualTo(27));
        });

        foreach (var invalid in new[] { 0, -1, 1441, int.MaxValue })
        {
            File.WriteAllText(_settingsPath,
                $$"""{ "LibraryAutoRefreshEnabled": false, "LibraryAutoRefreshMinutes": {{invalid}} }""");
            var loaded = service.Load();
            Assert.That(loaded.LibraryAutoRefreshEnabled, Is.False);
            Assert.That(loaded.LibraryAutoRefreshMinutes, Is.EqualTo(10));
        }
    }
}
