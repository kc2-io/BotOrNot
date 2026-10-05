using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BotOrNot.Avalonia.Services;

public interface IStreamerOverlayServer : IAsyncDisposable
{
    Task StartAsync(int port, StreamerOverlayState state);
    Task StopAsync();
}

public sealed class StreamerOverlayServer : IStreamerOverlayServer
{
    private WebApplication? _application;

    public async Task StartAsync(int port, StreamerOverlayState state)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1024);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        if (_application is not null)
            throw new InvalidOperationException("The overlay is already running.");

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = [], EnvironmentName = Environments.Production,
            ContentRootPath = AppContext.BaseDirectory
        });
        // Desktop settings own this listener. Do not inherit appsettings.json or ASPNETCORE_URLS.
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(2));
        builder.Services.AddSingleton<IHostLifetime, OverlayHostLifetime>();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Listen(IPAddress.Loopback, port);
        });
        var assets = new Dictionary<string, (string Content, string Type)>(StringComparer.Ordinal)
        {
            ["/"] = (ReadAsset("index.html"), "text/html; charset=utf-8"),
            ["/overlay.css"] = (ReadAsset("overlay.css"), "text/css; charset=utf-8"),
            ["/overlay.js"] = (ReadAsset("overlay.js"), "text/javascript; charset=utf-8")
        };
        var app = builder.Build();
        app.Run(async context =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Content-Security-Policy"] =
                "default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; frame-ancestors 'none'";

            if (context.Request.Host.Host != "127.0.0.1" || context.Request.Host.Port != port)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            if (!HttpMethods.IsGet(context.Request.Method))
            {
                context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                context.Response.Headers.Allow = "GET";
                return;
            }
            var path = context.Request.Path.Value ?? "/";
            if (path == "/api/snapshot")
                await context.Response.WriteAsJsonAsync(state.Current, context.RequestAborted);
            else if (assets.TryGetValue(path, out var asset))
            {
                context.Response.ContentType = asset.Type;
                await context.Response.WriteAsync(asset.Content, context.RequestAborted);
            }
            else
                context.Response.StatusCode = StatusCodes.Status404NotFound;
        });

        try
        {
            await app.StartAsync();
            _application = app;
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
    }

    public async Task StopAsync()
    {
        var app = _application;
        _application = null;
        if (app is null) return;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await app.StopAsync(timeout.Token);
        }
        finally { await app.DisposeAsync(); }
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    private static string ReadAsset(string name)
    {
        using var stream = typeof(StreamerOverlayServer).Assembly
            .GetManifestResourceStream($"BotOrNot.StreamerOverlay.{name}")
            ?? throw new InvalidOperationException($"Missing overlay asset: {name}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    // The desktop lifetime owns shutdown, not ConsoleLifetime's process-level handlers.
    private sealed class OverlayHostLifetime : IHostLifetime
    {
        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
