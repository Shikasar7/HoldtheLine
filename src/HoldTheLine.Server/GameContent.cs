using HoldTheLine.Rules.Cards;

namespace HoldTheLine.Server;

/// <summary>
/// The single source of card/leader/deck data, loaded once at startup from the SAME
/// <c>game/data</c> JSON the client and simulator use — no server-side copy of the card table
/// (plan §5.1). Immutable and shared across all matches.
/// </summary>
public sealed class GameContent
{
    public CardDatabase Cards { get; }
    public LeaderDatabase Leaders { get; }
    public IReadOnlyList<DeckList> Decks { get; }

    /// <summary>Content fingerprint of this data (M3 B0), compared against the client's hello to reject a
    /// client shipping different card data. See <see cref="HoldTheLine.Net.DataHash"/>.</summary>
    public string DataHash { get; }

    private GameContent(CardDatabase cards, LeaderDatabase leaders, IReadOnlyList<DeckList> decks)
    {
        Cards = cards;
        Leaders = leaders;
        Decks = decks;
        DataHash = HoldTheLine.Net.DataHash.Compute(cards, leaders, decks);
    }

    public static GameContent Load(string? explicitRoot = null)
    {
        string root = explicitRoot ?? DataRoot.Find();
        var cards = CardDatabase.LoadFromDirectory(Path.Combine(root, "cards"));
        var leaders = LeaderDatabase.LoadFromDirectory(Path.Combine(root, "leaders"));
        var decks = DeckLibrary.LoadFromDirectory(Path.Combine(root, "decks"));
        return new GameContent(cards, leaders, decks);
    }

    public DeckList? FindDeck(string id) => Decks.FirstOrDefault(d => d.Id == id);
}

/// <summary>Locates <c>game/data</c> by walking up from the running binary (mirrors HoldTheLine.Sim).</summary>
public static class DataRoot
{
    public static string Find()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "game", "data");
            if (Directory.Exists(Path.Combine(candidate, "cards")))
                return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("game/data not found above " + AppContext.BaseDirectory);
    }
}
