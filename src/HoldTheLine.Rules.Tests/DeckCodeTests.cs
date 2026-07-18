using HoldTheLine.Rules.Cards;
using Xunit;

namespace HoldTheLine.Rules.Tests;

public class DeckCodeTests
{
    private const string Rules = "0.4.1";
    private const string Hash = "a1b2c3d4e5f6a7b8"; // 64-hex would be longer; Encode only stores the first 8

    private static IReadOnlyList<string> SampleCards() =>
        Enumerable.Range(0, 30).Select(i => $"card_{i % 7}").ToList();

    [Fact]
    public void Roundtrip_PreservesLeaderCardsRulesHash()
    {
        var cards = SampleCards();
        var code = DeckCode.Encode("leader_iron", cards, Rules, Hash);
        Assert.StartsWith(DeckCode.Prefix, code);

        var (err, payload) = DeckCode.Decode(code);
        Assert.Equal(DeckCodeError.None, err);
        Assert.NotNull(payload);
        Assert.Equal("leader_iron", payload!.Leader);
        Assert.Equal(cards, payload.Cards);
        Assert.Equal(Rules, payload.Rules);
        Assert.Equal(Hash[..8], payload.Hash);
    }

    [Fact]
    public void TamperedMiddle_YieldsBadFormat()
    {
        var code = DeckCode.Encode("leader_iron", SampleCards(), Rules, Hash);
        // Flip a character in the payload body (past the prefix); Deflate/json must fail, captured as BadFormat.
        int mid = (DeckCode.Prefix.Length + code.Length) / 2;
        char swap = code[mid] == 'A' ? 'B' : 'A';
        var tampered = code[..mid] + swap + code[(mid + 1)..];

        var (err, payload) = DeckCode.Decode(tampered);
        Assert.Equal(DeckCodeError.BadFormat, err);
        Assert.Null(payload);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a deck code")]
    [InlineData("HTL1-@@@not-base64@@@")]
    [InlineData("XYZ1-abcdef")]
    public void NoPrefixOrGarbage_YieldsBadFormat(string code)
    {
        var (err, payload) = DeckCode.Decode(code);
        Assert.Equal(DeckCodeError.BadFormat, err);
        Assert.Null(payload);
    }

    [Fact]
    public void HandCraftedV2_YieldsUnsupportedVersion()
    {
        // Build a well-formed pipeline payload whose version is 2 (a future format this build can't read).
        var code = DeckCode.Pack("""{"v":2,"rules":"0.4.1","hash":"a1b2c3d4","leader":"x","cards":[]}""");
        var (err, payload) = DeckCode.Decode(code);
        Assert.Equal(DeckCodeError.UnsupportedVersion, err);
        Assert.Null(payload);
    }

    [Fact]
    public void Check_RulesMismatch_YieldsDataMismatch()
    {
        var p = new DeckCodePayload("0.4.0", "a1b2c3d4", "x", []);
        Assert.Equal(DeckCodeError.DataMismatch, DeckCode.Check(p, "0.4.1", "a1b2c3d4ffffffff"));
    }

    [Fact]
    public void Check_HashPrefixMismatch_YieldsDataMismatch()
    {
        var p = new DeckCodePayload("0.4.1", "deadbeef", "x", []);
        Assert.Equal(DeckCodeError.DataMismatch, DeckCode.Check(p, "0.4.1", "a1b2c3d4ffffffff"));
    }

    [Fact]
    public void Check_AllMatch_YieldsNone()
    {
        var p = new DeckCodePayload("0.4.1", "a1b2c3d4", "x", []);
        Assert.Equal(DeckCodeError.None, DeckCode.Check(p, "0.4.1", "a1b2c3d4ffffffff"));
    }
}
