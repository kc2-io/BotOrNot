using System.Reactive.Linq;
using System.Reflection;
using BotOrNot.Avalonia.Services;
using BotOrNot.Core.Models;
using BotOrNot.Core.Services;
using ReactiveUI;

namespace BotOrNot.Avalonia.ViewModels;

public class AppViewModel : ReactiveObject
{
    private static readonly string AppVersion = (Assembly.GetExecutingAssembly()
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0")
        .Split('+')[0];

    private static readonly string BaseTitle = $"Bot or Not? v{AppVersion}";

    private ReactiveObject _currentPage;
    private string _windowTitle = BaseTitle;

    private readonly IThemeService _themeService;

    public AppViewModel(
        ISettingsService? settingsService = null,
        IThemeService? themeService = null,
        IReplayCacheService? cacheService = null)
    {
        settingsService ??= new SettingsService();
        _themeService = themeService ?? new ThemeService(settingsService);
        _themeService.ApplySavedTheme();

        LibraryPage = new LibraryViewModel(NavigateToMatch, cacheService, settingsService);
        _currentPage = LibraryPage;

        // Keep window title in sync with the active page
        this.WhenAnyValue(x => x.CurrentPage)
            .Select(page => page is MainWindowViewModel vm
                ? vm.WhenAnyValue(v => v.WindowTitle)
                : Observable.Return(BaseTitle))
            .Switch()
            .Subscribe(title => WindowTitle = title);
    }

    public LibraryViewModel LibraryPage { get; }

    public ReactiveObject CurrentPage
    {
        get => _currentPage;
        private set => this.RaiseAndSetIfChanged(ref _currentPage, value);
    }

    public string WindowTitle
    {
        get => _windowTitle;
        private set => this.RaiseAndSetIfChanged(ref _windowTitle, value);
    }

    private void NavigateToMatch(ReplaySummary summary)
    {
        var matchVm = new MainWindowViewModel(() => CurrentPage = LibraryPage, themeService: _themeService);
        CurrentPage = matchVm;
        matchVm.LoadReplayCommand.Execute(summary.FilePath).Subscribe();
    }
}
