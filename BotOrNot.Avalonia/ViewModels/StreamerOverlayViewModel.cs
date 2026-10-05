using System.Globalization;
using System.Reactive;
using BotOrNot.Avalonia.Services;
using ReactiveUI;

namespace BotOrNot.Avalonia.ViewModels;

public sealed class StreamerOverlayViewModel : ReactiveObject, IDisposable
{
    private readonly ISettingsService _settings;
    private readonly IStreamerOverlayServer _server;
    private readonly StreamerOverlayState _state;
    private readonly Action<bool> _setRefreshActive;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private bool _disposed;
    private bool _isEnabled;
    private bool _isBusy;
    private int _port;
    private string _portText;
    private string? _message;

    public StreamerOverlayViewModel(ISettingsService settings, StreamerOverlayState state,
        Action<bool> setRefreshActive, IStreamerOverlayServer? server = null)
    {
        _settings = settings;
        _state = state;
        _setRefreshActive = setRefreshActive;
        _server = server ?? new StreamerOverlayServer();
        var saved = settings.Load();
        _port = saved.StreamerOverlayPort is >= 1024 and <= 65535
            ? saved.StreamerOverlayPort : AppSettings.DefaultStreamerOverlayPort;
        _portText = _port.ToString(CultureInfo.InvariantCulture);
        ToggleCommand = ReactiveCommand.CreateFromTask(() => SetEnabledAsync(!IsEnabled),
            this.WhenAnyValue(x => x.IsBusy, busy => !busy));
        ApplyPortCommand = ReactiveCommand.Create(ApplyPort,
            this.WhenAnyValue(x => x.IsEnabled, x => x.IsBusy, (enabled, busy) => !enabled && !busy));
        Initialization = saved.StreamerOverlayEnabled ? SetEnabledAsync(true) : Task.CompletedTask;
    }

    public Task Initialization { get; }
    public Task Shutdown { get; private set; } = Task.CompletedTask;
    public ReactiveCommand<Unit, Unit> ToggleCommand { get; }
    public ReactiveCommand<Unit, Unit> ApplyPortCommand { get; }

    public bool IsEnabled
    {
        get => _isEnabled;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isEnabled, value);
            this.RaisePropertyChanged(nameof(ToggleText));
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isBusy, value);
            this.RaisePropertyChanged(nameof(CanEditPort));
            this.RaisePropertyChanged(nameof(CanUseUrl));
        }
    }

    public string ToggleText => IsEnabled ? "Disable browser overlay" : "Enable browser overlay";
    public bool CanEditPort => !IsEnabled && !IsBusy;
    public bool CanUseUrl => IsEnabled && !IsBusy;
    public string Url => $"http://127.0.0.1:{_port}/";
    public string PortText
    {
        get => _portText;
        set => this.RaiseAndSetIfChanged(ref _portText, value);
    }
    public string? Message
    {
        get => _message;
        private set => this.RaiseAndSetIfChanged(ref _message, value);
    }

    public void ReportMessage(string message) => Message = message;

    private void ApplyPort()
    {
        if (_disposed || !CanEditPort) return;
        if (!int.TryParse(PortText, NumberStyles.None, CultureInfo.InvariantCulture, out var port) ||
            port is < 1024 or > 65535)
        {
            Message = "Overlay port must be a whole number from 1024 to 65535.";
            return;
        }
        _port = port;
        PortText = port.ToString(CultureInfo.InvariantCulture);
        _settings.Update(settings => settings.StreamerOverlayPort = port);
        this.RaisePropertyChanged(nameof(Url));
        Message = null;
    }

    private async Task SetEnabledAsync(bool enabled)
    {
        if (_disposed) return;
        IsBusy = true;
        Message = null;
        await _lifecycle.WaitAsync();
        try
        {
            if (_disposed || IsEnabled == enabled) return;
            if (enabled)
            {
                try
                {
                    await _server.StartAsync(_port, _state);
                    if (_disposed) return;
                }
                catch
                {
                    Message = $"Could not start the overlay on port {_port}. The port may be in use. Apply another port and try again.";
                    _settings.Update(settings => settings.StreamerOverlayEnabled = false);
                    return;
                }
            }
            else
                await _server.StopAsync();

            IsEnabled = enabled;
            _setRefreshActive(enabled);
            _settings.Update(settings => settings.StreamerOverlayEnabled = enabled);
        }
        catch
        {
            Message = "Could not stop the browser overlay. Restart BotOrNot before enabling it again.";
        }
        finally
        {
            _lifecycle.Release();
            IsBusy = false;
            this.RaisePropertyChanged(nameof(CanEditPort));
            this.RaisePropertyChanged(nameof(CanUseUrl));
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _setRefreshActive(false);
        Shutdown = ShutdownAsync();
    }

    private async Task ShutdownAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try { await _server.DisposeAsync().ConfigureAwait(false); }
        catch { /* Process exit still releases the listener if shutdown fails. */ }
        finally { _lifecycle.Release(); }
    }
}
