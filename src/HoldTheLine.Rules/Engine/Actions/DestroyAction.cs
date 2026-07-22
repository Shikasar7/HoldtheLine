using HoldTheLine.Rules.Ai;
using HoldTheLine.Rules.Cards;
using HoldTheLine.Rules.Geometry;
using HoldTheLine.Rules.State;

namespace HoldTheLine.Rules.Engine.Actions;

/// <summary>destroy (献祭/消灭): kill outright, bypassing the damage pipeline.</summary>
internal sealed class DestroyAction : EffectActionBase
{
    public override string Name => "destroy";

    public override void Execute(ResolutionContext ctx, UnitInstance? source, int ownerSeat, EffectSpec spec,
        IReadOnlyList<UnitInstance> targets, Cell? targetCell, int amount, int? secondaryTargetUnitId)
    {
        // 献祭/消灭: straight to the death sweep — bypasses DamageUnit, so 持盾/坚守 don't save it;
        // 亡语 still fires (via ProcessDeaths). No new event — the sweep emits UnitDiedEvent.
        foreach (var t in targets)
            ctx.DestroyUnit(t);
    }

    public override double Score(EffectScoreArgs a)
    {
        if (a.Target == null || !a.TargetIsAlly) return -100;
        return GreedyAi.SacrificeValue(a.Db, a.Target); // 献祭: only cheap/dying/deathrattle bodies score positive
    }
}
