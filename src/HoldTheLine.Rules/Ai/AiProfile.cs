namespace HoldTheLine.Rules.Ai;

public enum AiLevel { Easy, Normal, Hard }

/// <summary>
/// The single tuning surface for the three vs-AI difficulty tiers (docs/12 C2). The simulator's
/// <c>easy</c>/<c>fast</c> policies reference the very same constants, so difficulty is defined once —
/// change a number here and both the live host and the balance sim move together.
/// </summary>
public sealed record AiProfile
{
    public required AiLevel Level { get; init; }
    public double Epsilon { get; init; }        // chance of playing a random legal command on a turn (0 = never)
    public bool UseSearch { get; init; }        // false → GreedyAi
    public int SearchTopK { get; init; }
    public int SearchRollout { get; init; }
    public bool MulliganKeepAll { get; init; }  // true → keep the whole opening hand

    public static readonly AiProfile Easy = new()
    {
        Level = AiLevel.Easy,
        Epsilon = 0.22,
        UseSearch = false,
        MulliganKeepAll = true,
    };

    public static readonly AiProfile Normal = new()
    {
        Level = AiLevel.Normal,
        UseSearch = true,
        SearchTopK = 2,
        SearchRollout = 4,
    };

    public static readonly AiProfile Hard = new()
    {
        Level = AiLevel.Hard,
        UseSearch = true,
        SearchTopK = 4,
        SearchRollout = 8, // == SearchAi's current defaults, so Hard is behaviour-identical to today's vs-AI
    };

    public static AiProfile For(AiLevel level) => level switch
    {
        AiLevel.Easy => Easy,
        AiLevel.Normal => Normal,
        _ => Hard,
    };
}
