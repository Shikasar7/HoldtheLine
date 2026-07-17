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
        builder.Services.AddSingleton<RoomManager>();

        var app = builder.Build();
        app.UseWebSockets();

        app.MapGet("/healthz", () => Results.Text("ok"));
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
        var loggerFactory = ctx.RequestServices.GetRequiredService<ILoggerFactory>();

        using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
        var conn = new ClientConnection(socket, loggerFactory.CreateLogger<ClientConnection>());
        await conn.RunAsync(rooms, content, opts, ctx.RequestAborted);
    }
}
