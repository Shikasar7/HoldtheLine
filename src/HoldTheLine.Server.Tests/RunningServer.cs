using HoldTheLine.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace HoldTheLine.Server.Tests;

/// <summary>Boots a real server on an ephemeral loopback port and exposes its <c>/ws</c> URI, so
/// integration tests drive it over a genuine WebSocket (not an in-memory TestServer) — the same
/// path production runs.</summary>
public sealed class RunningServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    public Uri Ws { get; }

    private RunningServer(WebApplication app, Uri ws)
    {
        _app = app;
        Ws = ws;
    }

    public static async Task<RunningServer> StartAsync()
    {
        var app = ServerApp.Build(new ServerOptions { Urls = "http://127.0.0.1:0" });
        await app.StartAsync();

        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
        var http = addresses.First(); // e.g. http://127.0.0.1:49732
        var ws = new Uri(http.Replace("http://", "ws://").Replace("https://", "wss://").TrimEnd('/') + "/ws");
        return new RunningServer(app, ws);
    }

    /// <summary>Resolve a server-side singleton (RoomManager, GameContent) for white-box assertions.</summary>
    public T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
