using HoldTheLine.Rules.Cards;
using HoldTheLine.Rules.Geometry;
using HoldTheLine.Rules.State;

namespace HoldTheLine.Rules.Engine.Actions;

/// <summary>echo_order (薪火回响·门德, docs/21 §3.1): passive marker (trigger == first_kindle_order_each_turn)
/// — your first 薪炎 damage order each turn may be recast once while this unit is on board.</summary>
internal sealed class EchoOrderAction : EffectActionBase
{
    public override string Name => "echo_order";

    public override string? ValidateCard(EffectSpec spec, CardDefinition card) =>
        spec.Trigger != "first_kindle_order_each_turn"
            ? $"Card '{card.Id}': echo_order is only for the 薪火回响 marker."
            : null;

    public override void Execute(ResolutionContext ctx, UnitInstance? source, int ownerSeat, EffectSpec spec,
        IReadOnlyList<UnitInstance> targets, Cell? targetCell, int amount, int? secondaryTargetUnitId)
    {
        // Marker — resolved in the Resolver's order pipeline: ctx.HasFirstKindleCopier reads it, and
        // Resolver.ResolveOrder performs the re-aimed recast (EchoRecast/EchoTarget*). RunTrigger is
        // never called with the first_kindle_order_each_turn trigger, so this never executes.
    }

    public override double Score(EffectScoreArgs a) => 1; // never scored: the marker trigger isn't play/battlecry
}
