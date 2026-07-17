using HoldTheLine.Net;
using HoldTheLine.Server;
using Xunit;

namespace HoldTheLine.Server.Tests;

/// <summary>M3 B0: the card-data fingerprint is stable for identical data and sensitive to any change,
/// so a client shipping different card values is caught at the handshake.</summary>
public class DataHashTests
{
    [Fact]
    public void Hash_is_deterministic_for_the_same_data()
    {
        var content = GameContent.Load();
        var again = DataHash.Compute(content.Cards, content.Leaders, content.Decks);
        Assert.Equal(content.DataHash, again);
        Assert.Matches("^[0-9a-f]{64}$", content.DataHash); // hex sha-256
    }

    [Fact]
    public void Hash_changes_when_the_data_changes()
    {
        var content = GameContent.Load();
        Assert.NotEmpty(content.Decks);

        // Drop one deck: the normalized serialization differs, so the hash must differ.
        var fewer = content.Decks.Take(content.Decks.Count - 1).ToList();
        var mutated = DataHash.Compute(content.Cards, content.Leaders, fewer);
        Assert.NotEqual(content.DataHash, mutated);
    }
}
