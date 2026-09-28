using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using BotOrNot.Avalonia.Services;
using BotOrNot.Avalonia.ViewModels;
using BotOrNot.Avalonia.Views;

namespace BotOrNot.UITests.Tests;

[TestFixture]
public sealed class NativeBenchmarkVisualTests
{
    [AvaloniaTest]
    public void ReplayGrid_IsResolvedFromTheLibraryViewNameScope()
    {
        using var viewModel = new LibraryViewModel(_ => { }, settingsService: new SettingsService(Path.Combine(Path.GetTempPath(), $"native-ui-{Guid.NewGuid():N}.json")));
        var view = new LibraryView { DataContext = viewModel };
        var window = new Window { Content = view, Width = 900, Height = 600 };
        try
        {
            window.Show();
            window.UpdateLayout();
            Assert.That(NativeLibraryVisuals.FindReplayGrid(window), Is.Not.Null);
        }
        finally
        {
            window.Close();
        }
    }

    [Test]
    public void AvaloniaCompositionAcknowledgementContract_IsPresent()
    {
        Assert.That(NativeCompositionContract.ValidationError, Is.Null);
    }

    [Test]
    public void NativePixelDiversity_RejectsBlackAndAcceptsRenderedVariation()
    {
        Assert.That(NativePixelInspector.HasMeaningfulDiversity(new byte[64]), Is.False);
        Assert.That(NativePixelInspector.HasMeaningfulDiversity([0, 0, 0, 255, 1, 0, 0, 255]), Is.True);
    }
}
