using System.Reactive.Linq;
using System.Reflection;
using BotOrNot.Core.Models;
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

    public AppViewModel()
    {
        LibraryPage = new LibraryViewModel(NavigateToMatch);
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
        var matchVm = new MainWindowViewModel(() => CurrentPage = LibraryPage);
        CurrentPage = matchVm;
        matchVm.LoadReplayCommand.Execute(summary.FilePath).Subscribe();
    }
}
