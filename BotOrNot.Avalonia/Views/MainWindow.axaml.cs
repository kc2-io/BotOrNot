using Avalonia.Controls;
using BotOrNot.Avalonia.ViewModels;

namespace BotOrNot.Avalonia.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new AppViewModel();
    }
}
