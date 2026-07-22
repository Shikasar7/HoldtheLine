using HoldTheLine.Rules.Cards;
using HoldTheLine.Rules.Geometry;
using HoldTheLine.Rules.State;

namespace HoldTheLine.Rules.Engine.Actions;

/// <summary>heal: restore up to Amount HP on each resolved target (capped at MaxHp inside HealUnit).</summary>
internal sealed class HealAction : EffectActionBase
{
    public override string Name => "heal";

    public override void Execute(ResolutionContext ctx, UnitInstance? source, int ownerSeat, EffectSpec spec,
        IReadOnlyList<UnitInstance> targets, Cell? targetCell, int amount, int? secondaryTargetUnitId)
    {
        foreach (var t in targets)
            ctx.HealUnit(t, spec.Amount);
    }

    public override double Score(EffectScoreArgs a)
    {
        var e = a.Spec;
        if (e.Target is "target_unit" or "target_unit_ally")
        {
            if (a.Target == null) return 0;
            if (!a.TargetIsAlly) return -50;
            return Math.Min(a.Target.MaxHp - a.Target.CurrentHp, e.Amount) * 1.2;
        }
        return 1;
    }
}
