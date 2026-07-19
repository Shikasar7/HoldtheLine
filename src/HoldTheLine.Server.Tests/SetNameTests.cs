using HoldTheLine.Net.Client;
using HoldTheLine.Net.Protocol;
using HoldTheLine.Rules;
using HoldTheLine.Server.Data;
using Xunit;

namespace HoldTheLine.Server.Tests;

/// <summary>docs/16: set_name renames the display name in place — it applies to the connection, persists to
/// the identity row, and re-pushes the profile. No reconnect, no protocol bump.</summary>
public class SetNameTests
{
    private static Hello Hello(string guest, string name) => new()
    {
        GuestId = guest,
        Secret = "secret-" + guest,
        Name = name,
        ProtocolVersion = ProtocolConstants.ProtocolVersion,
        RulesVersion = RulesInfo.Version,
    };

    private static TaskCompletionSource<T> Tcs<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task Set_name_updates_profile_and_persists_to_the_row()
    {
        await using var server = await RunningServer.StartAsync();
        await using var c = new GameServerClient(new WebSocketTransport());

        // The initial hello pushes a profile named "Alice"; wait for the one triggered by set_name.
        var renamed = Tcs<Profile>();
        c.MessageReceived += m => { if (m is Profile p && p.Name == "新名字") renamed.TrySetResult(p); };

        await c.ConnectAsync(server.Ws, Hello("gA", "Alice"));
        await c.SendAsync(new SetName { Name = "  新名字  " }); // trimmed server-side

        var profile = await renamed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("新名字", profile.Name);
        Assert.Equal("新名字", server.Service<AccountStore>().Find("gA")!.Name); // durably written to the account row
    }

    [Fact]
    public async Task Set_name_rejects_a_blank_name()
    {
        await using var server = await RunningServer.StartAsync();
        await using var c = new GameServerClient(new WebSocketTransport());

        var err = Tcs<string>();
        c.MessageReceived += m => { if (m is ErrorMsg e) err.TrySetResult(e.Code); };

        await c.ConnectAsync(server.Ws, Hello("gA", "Alice"));
        await c.SendAsync(new SetName { Name = "   " });

        Assert.Equal("invalid_name", await err.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }
}
