using HoldTheLine.Net.Client;
using HoldTheLine.Net.Protocol;
using HoldTheLine.Rules;
using Xunit;

namespace HoldTheLine.Server.Tests;

/// <summary>M3 B0 acceptance: the data-hash gate, persistent-identity register/restore + secret
/// verification, the Profile push, and survival across a server restart (file db).</summary>
public class IdentityHandshakeTests
{
    private static Hello Hello(string guest, string? secret, string name, string? dataHash) => new()
    {
        GuestId = guest,
        Secret = secret,
        Name = name,
        ProtocolVersion = ProtocolConstants.ProtocolVersion,
        RulesVersion = RulesInfo.Version,
        DataHash = dataHash,
    };

    /// <summary>Send a hello over a raw socket and return the first server frame (ErrorMsg or HelloOk).</summary>
    private static async Task<ServerMessage?> FirstReply(Uri ws, Hello hello)
    {
        await using var t = new WebSocketTransport();
        await t.ConnectAsync(ws, CancellationToken.None);
        await t.SendTextAsync(ProtocolJson.Encode(hello), CancellationToken.None);
        var reply = await t.ReceiveTextAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        return reply is null ? null : ProtocolJson.TryDecodeServer(reply);
    }

    [Fact]
    public async Task Mismatched_data_hash_is_rejected()
    {
        await using var server = await RunningServer.StartAsync();
        var reply = await FirstReply(server.Ws, Hello("g", "s", "Alice", dataHash: "deadbeef"));
        var err = Assert.IsType<ErrorMsg>(reply);
        Assert.Equal("data_mismatch", err.Code);
    }

    [Fact]
    public async Task Matching_data_hash_passes_and_pushes_profile()
    {
        await using var server = await RunningServer.StartAsync();
        var serverHash = server.Service<GameContent>().DataHash;

        await using var client = new GameServerClient(new WebSocketTransport());
        var profile = Tcs<Profile>();
        client.MessageReceived += m => { if (m is Profile p) profile.TrySetResult(p); };

        await client.ConnectAsync(server.Ws, Hello("g1", "s1", "Alice", serverHash));
        var p = await profile.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Alice", p.Name);
        Assert.Equal("unlock_all", p.CollectionMode);
    }

    [Fact]
    public async Task Wrong_secret_for_a_known_guest_is_rejected()
    {
        await using var server = await RunningServer.StartAsync();

        // Register g1 with the right secret.
        await using (var first = new GameServerClient(new WebSocketTransport()))
            await first.ConnectAsync(server.Ws, Hello("g1", "correct", "Alice", null));

        // A different device claiming g1 with the wrong secret is turned away.
        var reply = await FirstReply(server.Ws, Hello("g1", "WRONG", "Mallory", null));
        var err = Assert.IsType<ErrorMsg>(reply);
        Assert.Equal("bad_identity", err.Code);
    }

    [Fact]
    public async Task Identity_survives_a_server_restart()
    {
        var dbPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"htl-test-{Guid.NewGuid():N}.db");
        try
        {
            await using (var server1 = await RunningServer.StartAsync(dbPath: dbPath))
                await using (var c = new GameServerClient(new WebSocketTransport()))
                    await c.ConnectAsync(server1.Ws, Hello("g1", "s1", "Alice", null)); // registers into the file

            // Fresh server on the same file: the account is still there.
            await using var server2 = await RunningServer.StartAsync(dbPath: dbPath);

            var wrong = await FirstReply(server2.Ws, Hello("g1", "nope", "x", null));
            Assert.Equal("bad_identity", Assert.IsType<ErrorMsg>(wrong).Code); // remembered secret rejects imposters

            await using var ok = new GameServerClient(new WebSocketTransport());
            var okReply = await ok.ConnectAsync(server2.Ws, Hello("g1", "s1", "Alice", null)); // correct secret restores
            Assert.NotNull(okReply);
        }
        finally
        {
            foreach (var f in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
                try { if (System.IO.File.Exists(f)) System.IO.File.Delete(f); } catch { /* best-effort temp cleanup */ }
        }
    }

    private static TaskCompletionSource<T> Tcs<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
