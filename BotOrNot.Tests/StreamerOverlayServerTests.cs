using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using BotOrNot.Avalonia.Services;
using BotOrNot.Core.Models;

namespace BotOrNot.Tests;

[TestFixture]
public sealed class StreamerOverlayServerTests
{
    [Test]
    public async Task ServesEmbeddedOverlayAndSanitizedSnapshot_RejectsOtherRoutesMethodsAndHosts()
    {
        var state = new StreamerOverlayState();
        state.Configure(true, true, 1);
        state.CompleteScan([new ReplaySummary
        {
            FileName = "private-file.replay", FilePath = "/private/replays/private-file.replay",
            OwnerName = "private-owner", Opponents = [new() { StableId = "private-id", Name = "private-name" }],
            Kills = 5, BotKills = 2, Placement = "1", PlayerCount = 100, BotCount = 60
        }], 50, 80, 1, 0, DateTimeOffset.UtcNow);
        var port = FreePort();
        await using var server = new StreamerOverlayServer();
        await server.StartAsync(port, state);
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

        var page = await client.GetAsync("/");
        Assert.That(page.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(page.Content.Headers.ContentType?.MediaType, Is.EqualTo("text/html"));
        Assert.That(await page.Content.ReadAsStringAsync(), Does.Contain("LIBRARY OVERVIEW"));
        Assert.That(await client.GetStringAsync("/overlay.css"), Does.Contain("background: transparent"));
        Assert.That(await client.GetStringAsync("/overlay.js"), Does.Contain("/api/snapshot"));

        var response = await client.GetAsync("/api/snapshot");
        var json = await response.Content.ReadAsStringAsync();
        Assert.That(response.Headers.CacheControl?.NoStore, Is.True);
        Assert.That(response.Headers.Contains("Access-Control-Allow-Origin"), Is.False);
        Assert.That(json, Does.Not.Contain("private-"));
        using var payload = JsonDocument.Parse(json);
        var snapshot = payload.RootElement.GetProperty("snapshot");
        Assert.That(snapshot.GetProperty("statistics").GetProperty("matches").GetInt32(), Is.EqualTo(1));
        Assert.That(snapshot.GetProperty("recentMatches")[0].GetProperty("playerKills").GetInt32(), Is.EqualTo(3));
        Assert.That((await client.GetAsync("/replay-cache.json")).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That((await client.PostAsync("/api/snapshot", null)).StatusCode, Is.EqualTo(HttpStatusCode.MethodNotAllowed));
        using var wrongHost = new HttpRequestMessage(HttpMethod.Get, "/api/snapshot");
        wrongHost.Headers.Host = $"example.com:{port}";
        Assert.That((await client.SendAsync(wrongHost)).StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        await server.StopAsync();
        // Stop releases the port and the same URL can be used on restart.
        await server.StartAsync(port, state);
        Assert.That((await client.GetAsync("/api/snapshot")).StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task OccupiedPort_FailsWithoutChoosingAnotherUrl_AndCanRetry()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        await using var server = new StreamerOverlayServer();
        Assert.That(async () => await server.StartAsync(port, new()), Throws.Exception);
        listener.Stop();
        await server.StartAsync(port, new());
    }

    [Test]
    public void Snapshot_PreservesUnknownsAndLastResults_ThenClearsOnConfirmedEmptyScan()
    {
        var state = new StreamerOverlayState();
        state.CompleteScan([new ReplaySummary { Kills = null }, new ReplaySummary { Kills = 4, Placement = "1" }],
            50, 2, 2, 0, DateTimeOffset.UtcNow);
        var last = state.Current.Snapshot;
        Assert.That(last!.Statistics.AverageKills, Is.EqualTo(4));
        Assert.That(last.Statistics.WinRate, Is.EqualTo(50));
        Assert.That(last.RecentMatches[0].Kills, Is.Null);
        state.BeginScan();
        Assert.That(state.Current.Snapshot, Is.SameAs(last));
        state.FailScan();
        Assert.That(state.Current.Snapshot, Is.SameAs(last));
        state.CompleteScan([], 50, 2, 2, 2, DateTimeOffset.UtcNow);
        Assert.That(state.Current.Snapshot, Is.SameAs(last));
        Assert.That(state.Current.UpdatesDelayed, Is.True);
        state.CompleteScan([], 50, 0, 0, 0, DateTimeOffset.UtcNow);
        Assert.That(state.Current.Snapshot!.Statistics.Matches, Is.Zero);
        Assert.That(state.Current.UpdatesDelayed, Is.False);
    }

    public static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
