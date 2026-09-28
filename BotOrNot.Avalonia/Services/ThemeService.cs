using Avalonia;
using Avalonia.Styling;

namespace BotOrNot.Avalonia.Services;

public interface IThemeService
{
    ThemePreference CurrentTheme { get; }
    void ApplySavedTheme();
    void CycleTheme();
}

public sealed class ThemeService : IThemeService
{
    private readonly ISettingsService _settingsService;
    private readonly Application? _application;

    public ThemeService(ISettingsService settingsService, Application? application = null)
    {
        _settingsService = settingsService;
        _application = application;
    }

    public ThemePreference CurrentTheme { get; private set; } = ThemePreference.System;

    public void ApplySavedTheme()
    {
        CurrentTheme = _settingsService.Load().Theme;
        ApplyTheme();
    }

    public void CycleTheme()
    {
        CurrentTheme = CurrentTheme switch
        {
            ThemePreference.System => ThemePreference.Light,
            ThemePreference.Light => ThemePreference.Dark,
            _ => ThemePreference.System
        };

        _settingsService.Update(settings => settings.Theme = CurrentTheme);
        ApplyTheme();
    }

    public static ThemeVariant ToThemeVariant(ThemePreference preference) => preference switch
    {
        ThemePreference.Light => ThemeVariant.Light,
        ThemePreference.Dark => ThemeVariant.Dark,
        _ => ThemeVariant.Default
    };

    private void ApplyTheme()
    {
        (_application ?? Application.Current)?.RequestedThemeVariant = ToThemeVariant(CurrentTheme);
    }
}
