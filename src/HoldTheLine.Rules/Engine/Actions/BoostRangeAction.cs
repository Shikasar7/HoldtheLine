using HoldTheLine.Rules.Cards;
using HoldTheLine.Rules.Geometry;
using HoldTheLine.Rules.State;

namespace HoldTheLine.Rules.Engine.Actions;

/// <summary>boost_range (加农校准): additive +Amount range on each resolved target.</summary>
internal sealed class BoostRangeAction : EffectActionBase
{
    public override string Name => "boost_range";

    public override void Execute(ResolutionContext ctx, UnitInstance? source, int ownerSeat, EffectSpec spec,
        IReadOnlyList<UnitInstance> targets, Cell? targetCell, int amount, int? secondaryTargetUnitId)
    {
        // 加农校准: +Amount range, ADDITIVE onto whatever range the unit already has (docs/00 §3 —
        // restores the GDD "射程加法叠加" original). KeywordValue is a max across grants, so raising
        // it means granting (current range + Amount): a melee unit → range Amount, a range-2 unit → 2+Amount.
        foreach (var t in targets)
            ctx.GrantKeyword(t, Keyword.Range, t.KeywordValue(Keyword.Range) + spec.Amount, spec.Duration, ownerSeat);
    }

    // 加农校准: +range on an ally — reach from safety, worth a small buff.
    public override double Score(EffectScoreArgs a) => GrantKeywordAction.ScoreFriendlyReceiver(a);
}
