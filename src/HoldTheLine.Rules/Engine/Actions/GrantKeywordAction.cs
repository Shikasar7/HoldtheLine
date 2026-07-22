using HoldTheLine.Rules.Cards;
using HoldTheLine.Rules.Geometry;
using HoldTheLine.Rules.State;

namespace HoldTheLine.Rules.Engine.Actions;

/// <summary>grant_keyword: grant a keyword (with optional value/duration) to each resolved target.</summary>
internal sealed class GrantKeywordAction : EffectActionBase
{
    public override string Name => "grant_keyword";

    public override string? ValidateCard(EffectSpec spec, CardDefinition card)
    {
        if (spec.GrantKeyword is null)
            return $"Card '{card.Id}': grant_keyword needs a 'keyword'.";
        if (spec.GrantKeyword is Keyword.Swift or Keyword.Range && spec.GrantKeywordValue < 1)
            return $"Card '{card.Id}': granting {spec.GrantKeyword} needs keyword_value >= 1.";
        return null;
    }

    public override void Execute(ResolutionContext ctx, UnitInstance? source, int ownerSeat, EffectSpec spec,
        IReadOnlyList<UnitInstance> targets, Cell? targetCell, int amount, int? secondaryTargetUnitId)
    {
        foreach (var t in targets)
            ctx.GrantKeyword(t, spec.GrantKeyword!.Value, spec.GrantKeywordValue, spec.Duration, ownerSeat);
    }

    public override double Score(EffectScoreArgs a) => ScoreFriendlyReceiver(a);

    /// <summary>Shared grant_keyword/move_bonus/boost_range pricing (GreedyAi's old joint case).</summary>
    internal static double ScoreFriendlyReceiver(EffectScoreArgs a)
    {
        var e = a.Spec;
        if (e.Target is "target_unit" or "target_unit_ally")
        {
            if (a.Target == null) return 0;
            if (!a.TargetIsAlly) return -100;
            // 重新部署 (Mobilized) only matters on an 架设 unit — repositioning a bolted-down turret;
            // granting it to a mobile unit is inert, so don't waste the card there.
            if (e.GrantKeyword == Keyword.Mobilized)
                return a.Target.HasKeyword(Keyword.Emplacement) ? 2 + a.Cost : 0.2;
            return 2 + a.Cost;
        }
        return 1;
    }
}
