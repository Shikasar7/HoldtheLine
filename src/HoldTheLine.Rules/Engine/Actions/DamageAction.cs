using HoldTheLine.Rules.Ai;
using HoldTheLine.Rules.Cards;
using HoldTheLine.Rules.Geometry;
using HoldTheLine.Rules.State;

namespace HoldTheLine.Rules.Engine.Actions;

/// <summary>damage: plain effect damage to each resolved target.</summary>
internal sealed class DamageAction : EffectActionBase
{
    public override string Name => "damage";

    public override void Execute(ResolutionContext ctx, UnitInstance? source, int ownerSeat, EffectSpec spec,
        IReadOnlyList<UnitInstance> targets, Cell? targetCell, int amount, int? secondaryTargetUnitId)
    {
        // effectDamage: 架设 victims take +1 from effect damage (the 焰克械 counter interface,
        // docs/06 §4) — the +1 now lives inside DamageUnit/DamageMath, not at call sites.
        foreach (var t in targets)
            ctx.DamageUnit(t, amount, school: spec.School, effectDamage: true);
    }

    public override double Score(EffectScoreArgs a) => ScoreDamageLike(a, sear: false);

    /// <summary>Shared damage/sear pricing (GreedyAi's old joint "damage"/"sear" case). 灼蚀 (sear):
    /// same shape as damage, but ignores 坚守 — so it scores full value vs HoldFast prey.</summary>
    internal static double ScoreDamageLike(EffectScoreArgs a, bool sear)
    {
        var s = a.State;
        var e = a.Spec;
        int seat = a.Seat;
        switch (e.Target)
        {
            case "target_unit":
            case "target_unit_own_half":
                if (a.Target == null) return 0;
                return a.TargetIsEnemy ? GreedyAi.DamageValue(s, seat, a.Target, a.EffectAmount, sear, e.School) : -100;
            case "column_enemies":
                return GreedyAi.SumDamage(s, seat, s.Units.Where(u => u.OwnerSeat != seat && GreedyAi.InCol(u, a.TargetCell)), a.EffectAmount, sear, e.School);
            case "row_enemies":
                return GreedyAi.SumDamage(s, seat, s.Units.Where(u => u.OwnerSeat != seat && GreedyAi.InRow(u, a.TargetCell)), a.EffectAmount, sear, e.School);
            case "cell_cross_all":
                return a.TargetCell is { } cc ? GreedyAi.SumDamage(s, seat, GreedyAi.CrossUnits(s, cc), a.EffectAmount, sear, e.School) : 0;
            case "unit_cross_all":
                return a.Target == null ? 0 : GreedyAi.SumDamage(s, seat, GreedyAi.CrossUnits(s, a.Target.Cell), a.EffectAmount, sear, e.School);
            case "adjacent_enemies":
                return 1.5; // source-relative on-cast/deathrattle — small flat credit
            default:
                return 1;
        }
    }
}
