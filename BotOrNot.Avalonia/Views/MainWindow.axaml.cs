using Avalonia.Controls;
using BotOrNot.Avalonia.ViewModels;

namespace BotOrNot.Avalonia.Views;

public partial class MainWindow : Window
{
    public MainWindow()
        : this(new AppViewModel())
    {
    }

    public MainWindow(AppViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    protected override void OnClosed(EventArgs e)
    {
        (DataContext as IDisposable)?.Dispose();
        base.OnClosed(e);
    }
}
