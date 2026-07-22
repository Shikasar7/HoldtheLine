using HoldTheLine.Rules.Cards;
using HoldTheLine.Rules.Geometry;
using HoldTheLine.Rules.State;

namespace HoldTheLine.Rules.Engine.Actions;

/// <summary>draw: the owner draws Amount cards.</summary>
internal sealed class DrawAction : EffectActionBase
{
    public override string Name => "draw";

    public override void Execute(ResolutionContext ctx, UnitInstance? source, int ownerSeat, EffectSpec spec,
        IReadOnlyList<UnitInstance> targets, Cell? targetCell, int amount, int? secondaryTargetUnitId)
    {
        ctx.DrawCards(ownerSeat, spec.Amount);
    }

    public override double Score(EffectScoreArgs a) => a.Spec.Amount * 2;
}
