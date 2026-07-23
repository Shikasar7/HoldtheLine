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

    public static async Task<RunningServer> StartAsync(int? disconnectGraceSeconds = null, int? turnSeconds = null, string? commandLogDir = null, string? dbPath = null, bool mulliganEnabled = false, int? mulliganSeconds = null, string? minClientVersion = null, bool enforceMinClientVersion = false, bool devCheatsEnabled = false, bool devCheatsAllowRanked = false)
    {
        // Mulligan defaults OFF for tests so the pre-mulligan turn-flow suites stay unchanged; the dedicated
        // mulligan flow tests opt in. Production ServerOptions defaults it ON.
        var opts = new ServerOptions { Urls = "http://127.0.0.1:0", MulliganEnabled = mulliganEnabled };
        if (disconnectGraceSeconds is { } g) opts.DisconnectGraceSeconds = g;
        if (turnSeconds is { } t) opts.TurnSeconds = t;
        if (mulliganSeconds is { } ms) opts.MulliganSeconds = ms;
        opts.CommandLogDir = commandLogDir;
        opts.DbPath = dbPath; // null → private in-memory db (isolated per server); a path persists across restarts
        opts.MinClientVersion = minClientVersion;               // docs/15 §2 soft update-gate (null = disabled)
        opts.EnforceMinClientVersion = enforceMinClientVersion; // false = log-only, true = hard-reject
        opts.DevCheatsEnabled = devCheatsEnabled;               // 开发者测试修改器 (dev-only); off in production
        opts.DevCheatsAllowRanked = devCheatsAllowRanked;       // 排位需额外显式开启，避免误伤正式天梯
        var app = ServerApp.Build(opts);
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
