using HoldTheLine.Rules.Cards;
using HoldTheLine.Rules.Geometry;
using HoldTheLine.Rules.State;

namespace HoldTheLine.Rules.Engine.Actions;

/// <summary>recall_order (火种循环): return up to Amount orders from the graveyard to hand.</summary>
internal sealed class RecallOrderAction : EffectActionBase
{
    public override string Name => "recall_order";

    public override void Execute(ResolutionContext ctx, UnitInstance? source, int ownerSeat, EffectSpec spec,
        IReadOnlyList<UnitInstance> targets, Cell? targetCell, int amount, int? secondaryTargetUnitId)
    {
        ctx.RecallOrders(ownerSeat, spec.Amount);
    }

    public override double Score(EffectScoreArgs a)
    {
        // Worth a draw per order actually available in our graveyard; nothing to recall = dead text.
        var db = a.Db;
        int available = a.State.Player(a.Seat).Graveyard.Count(id => db.Get(id).Type == CardType.Order);
        return Math.Min(a.Spec.Amount, available) * 2;
    }
}
