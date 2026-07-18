using HoldTheLine.Rules.Cards;
using HoldTheLine.Rules.Commands;
using HoldTheLine.Rules.State;

namespace HoldTheLine.Rules.Ai;

/// <summary>
/// Heuristic 起手重抽 pick (docs/11 §7): swap out the cards you can't afford early. v1 = replace every
/// hand card costing ≥ <see cref="DefaultCostThreshold"/>, keep the rest. Shared by the vs-AI host, the
/// server's fallback bot, and the simulator. Mulligan is a hidden-information decision, so there is nothing
/// to search — it is deliberately a plain rule rather than a lookahead (kept separate from GreedyAi/SearchAi).
/// </summary>
public static class MulliganAi
{
    public const int DefaultCostThreshold = 5;

    public static MulliganCommand Pick(GameState state, CardDatabase db, int seat, int costThreshold = DefaultCostThreshold)
    {
        var replaced = state.Player(seat).Hand
            .Where(c => db.Get(c.CardId).Cost >= costThreshold)
            .Select(c => c.EntityId)
            .ToList();
        return new MulliganCommand { Seat = seat, ReplacedEntityIds = replaced };
    }
}
