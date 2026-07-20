namespace HoldTheLine.Rules.Engine;

/// <summary>
/// Everything needed to (re)create a match deterministically. Config + command log = full replay
/// (hard constraint #4, plan §3.1).
/// </summary>
public sealed record MatchConfig
{
    public required ulong Seed { get; init; }
    public required IReadOnlyList<string> Deck0 { get; init; }
    public required IReadOnlyList<string> Deck1 { get; init; }
    public int FirstSeat { get; init; }
    public string Leader0 { get; init; } = "";
    public string Leader1 { get; init; } = "";
    public int LeaderHp { get; init; } = 25;
    public int OpeningHandFirst { get; init; } = 4;
    /// <summary>Second player draws one extra card on top of the first player's opening hand,
    /// plus the coin below. Balance patch #3 (Rules 0.8.1): dropped +2→+1 (6→5) — with the coin,
    /// going-second win rate had grown too high; the earlier +2 over-compensated the seat.</summary>
    public int OpeningHandSecond { get; init; } = 5;
    /// <summary>军令硬币 given to the second player. Empty string = no coin.</summary>
    public string CoinCardId { get; init; } = "neutral_coin";
    /// <summary>Enforce constructed-deck rules (30 cards, rarity caps). Off by default so tests and sims can use small decks.</summary>
    public bool ValidateDecks { get; init; }

    /// <summary>Run the 起手重抽 (mulligan) phase before the first turn (docs/11). **Off by default**: an
    /// old command log's config JSON has no such field → deserializes to false → its replay is byte-for-byte
    /// unchanged. Production entry points (MatchSession / BattleScene / Sim) set it explicitly.</summary>
    public bool MulliganEnabled { get; init; }
}
