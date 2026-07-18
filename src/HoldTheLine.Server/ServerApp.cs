using HoldTheLine.Server.Data;
using HoldTheLine.Server.Rooms;

namespace HoldTheLine.Server;

/// <summary>
/// Composition root, factored out of Program so tests can boot a real server on an ephemeral port
/// (<c>http://127.0.0.1:0</c>) and drive it over a genuine WebSocket. Endpoints: <c>/healthz</c>
/// and the <c>/ws</c> upgrade that hands each socket to a <see cref="ClientConnection"/>.
/// </summary>
public static class ServerApp
{
    public static WebApplication Build(ServerOptions options)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(options.Urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(GameContent.Load(options.DataRoot));
        builder.Services.AddSingleton(new Db(options.DbPath));
        builder.Services.AddSingleton<AccountStore>();
        builder.Services.AddSingleton<DeckStore>();
        builder.Services.AddSingleton<CollectionStore>();
        builder.Services.AddSingleton<LadderStore>();
        builder.Services.AddSingleton<DeckSource>();
        builder.Services.AddSingleton<RoomManager>();
        builder.Services.AddSingleton<QueueManager>();
        builder.Services.AddSingleton<ServerStats>();
        builder.Services.AddSingleton<AuthThrottle>();

        // Daily online backups (docs/12 B2) — only with a real file db AND a target dir (never for the
        // in-memory db that tests / throwaway runs use).
        if (!string.IsNullOrWhiteSpace(options.BackupDir)
            && !string.IsNullOrWhiteSpace(options.DbPath) && options.DbPath != ":memory:")
            builder.Services.AddHostedService<BackupService>();

        var app = builder.Build();
        app.UseWebSockets();

        // /healthz is JSON (M3 B4): a glance tells you the Beta's live load.
        app.MapGet("/healthz", (RoomManager rooms, QueueManager queue, LadderStore ladder, ServerStats stats) =>
        {
            long since = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero).ToUnixTimeSeconds();
            return Results.Json(new
            {
                status = "ok",
                connections = stats.Connections,
                matches = rooms.ActiveMatchCount,
                queue = queue.Count,
                rankedToday = ladder.MatchesSince(since),
            });
        });
        app.Map("/ws", HandleWebSocketAsync);
        return app;
    }

    private static async Task HandleWebSocketAsync(HttpContext ctx)
    {
        if (!ctx.WebSockets.IsWebSocketRequest)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var rooms = ctx.RequestServices.GetRequiredService<RoomManager>();
        var content = ctx.RequestServices.GetRequiredService<GameContent>();
        var opts = ctx.RequestServices.GetRequiredService<ServerOptions>();
        var accounts = ctx.RequestServices.GetRequiredService<AccountStore>();
        var decks = ctx.RequestServices.GetRequiredService<DeckStore>();
        var collection = ctx.RequestServices.GetRequiredService<CollectionStore>();
        var ladder = ctx.RequestServices.GetRequiredService<LadderStore>();
        var queue = ctx.RequestServices.GetRequiredService<QueueManager>();
        var stats = ctx.RequestServices.GetRequiredService<ServerStats>();
        var throttle = ctx.RequestServices.GetRequiredService<AuthThrottle>();
        var loggerFactory = ctx.RequestServices.GetRequiredService<ILoggerFactory>();

        string remoteIp = ctx.Connection.RemoteIpAddress?.ToString() ?? "?";
        using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
        var conn = new ClientConnection(socket, loggerFactory.CreateLogger<ClientConnection>(), remoteIp);
        stats.Connected();
        try
        {
            await conn.RunAsync(rooms, content, opts, accounts, decks, collection, ladder, queue, throttle, ctx.RequestAborted);
        }
        finally
        {
            stats.Disconnected();
        }
    }
}
