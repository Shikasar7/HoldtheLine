using HoldTheLine.Rules.Cards;
using HoldTheLine.Rules.Geometry;
using HoldTheLine.Rules.State;

namespace HoldTheLine.Rules.Engine.Actions;

/// <summary>sear (灼蚀): effect damage that ignores 坚守 reduction.</summary>
internal sealed class SearAction : EffectActionBase
{
    public override string Name => "sear";

    public override void Execute(ResolutionContext ctx, UnitInstance? source, int ownerSeat, EffectSpec spec,
        IReadOnlyList<UnitInstance> targets, Cell? targetCell, int amount, int? secondaryTargetUnitId)
    {
        // 灼蚀 (docs/10 §6#2): effect damage that ignores 坚守 reduction — the 教团→铁壁 answer
        // (v2.1 遗留#1: HoldFast otherwise eats the 教团's 1-2pt chip damage whole). 持盾 still
        // absorbs; 架设's +1 effect-damage clause still stacks (灼蚀 is effect damage too — effectDamage).
        foreach (var t in targets)
            ctx.DamageUnit(t, amount, ignoreHoldFast: true, school: spec.School, effectDamage: true);
    }

    public override double Score(EffectScoreArgs a) => DamageAction.ScoreDamageLike(a, sear: true);
}
