using HoldTheLine.Net;
using HoldTheLine.Net.Client;
using HoldTheLine.Net.Protocol;
using HoldTheLine.Rules;
using Xunit;

namespace HoldTheLine.Server.Tests;

/// <summary>
/// docs/15 §2: the soft client-version gate. A configured MinClientVersion only bites when
/// EnforceMinClientVersion is on (the deliberate soft-launch stance is log-and-allow); when it does, an
/// outdated hello is rejected with "client_outdated", and <see cref="GameServerClient.ConnectAsync"/>
/// surfaces that as a <see cref="HandshakeRejectedException"/> rather than an opaque hello timeout.
/// </summary>
public class UpdateGateTests
{
    private static Hello HelloAt(string? clientVersion) => new()
    {
        GuestId = "",
        Name = "tester",
        ProtocolVersion = ProtocolConstants.ProtocolVersion,
        RulesVersion = RulesInfo.Version,
        ClientVersion = clientVersion,
    };

    [Fact]
    public async Task Outdated_client_is_allowed_when_enforcement_off()
    {
        await using var server = await RunningServer.StartAsync(minClientVersion: "1.0.0", enforceMinClientVersion: false);
        await using var c = new GameServerClient(new WebSocketTransport());

        var ok = await c.ConnectAsync(server.Ws, HelloAt("0.1.0")); // below min, but log-only → connects
        Assert.NotNull(ok);
        Assert.Equal(ConnectionState.Connected, c.State);
    }

    [Fact]
    public async Task Outdated_client_is_rejected_when_enforcement_on()
    {
        await using var server = await RunningServer.StartAsync(minClientVersion: "1.0.0", enforceMinClientVersion: true);
        await using var c = new GameServerClient(new WebSocketTransport());

        var ex = await Assert.ThrowsAsync<HandshakeRejectedException>(
            () => c.ConnectAsync(server.Ws, HelloAt("0.9.9")));
        Assert.Equal("client_outdated", ex.Code);
    }

    [Fact]
    public async Task Missing_client_version_counts_as_zero_and_is_rejected_when_enforced()
    {
        await using var server = await RunningServer.StartAsync(minClientVersion: "0.1.0", enforceMinClientVersion: true);
        await using var c = new GameServerClient(new WebSocketTransport());

        var ex = await Assert.ThrowsAsync<HandshakeRejectedException>(
            () => c.ConnectAsync(server.Ws, HelloAt(null))); // no version → treated as 0.0.0 < 0.1.0
        Assert.Equal("client_outdated", ex.Code);
    }

    [Fact]
    public async Task Up_to_date_client_connects_under_enforcement()
    {
        await using var server = await RunningServer.StartAsync(minClientVersion: "0.1.0", enforceMinClientVersion: true);
        await using var c = new GameServerClient(new WebSocketTransport());

        var ok = await c.ConnectAsync(server.Ws, HelloAt("0.1.0")); // equal to min → allowed
        Assert.NotNull(ok);
        Assert.Equal(ConnectionState.Connected, c.State);
    }

    [Fact]
    public async Task No_gate_configured_ignores_client_version()
    {
        await using var server = await RunningServer.StartAsync(); // MinClientVersion null → gate off
        await using var c = new GameServerClient(new WebSocketTransport());

        var ok = await c.ConnectAsync(server.Ws, HelloAt("0.0.1"));
        Assert.NotNull(ok);
    }

    [Theory]
    [InlineData("0.1.0", "0.2.0", true)]
    [InlineData("0.2.0", "0.1.0", false)]
    [InlineData("1.0.0", "1.0.0", false)]
    [InlineData(null, "0.0.1", true)]     // missing → 0.0.0, older than anything positive
    [InlineData("0.1.0", "0.1.0-rc1", false)] // pre-release suffix ignored → equal cores
    [InlineData("1.2.3", "1.10.0", true)]  // numeric compare, not lexical (10 > 2)
    public void SemVer_IsOlder(string? version, string? minimum, bool expected) =>
        Assert.Equal(expected, SemVer.IsOlder(version, minimum));
}
