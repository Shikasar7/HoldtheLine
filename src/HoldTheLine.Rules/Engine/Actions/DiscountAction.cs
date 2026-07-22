using HoldTheLine.Rules.Cards;
using HoldTheLine.Rules.Geometry;
using HoldTheLine.Rules.State;

namespace HoldTheLine.Rules.Engine.Actions;

/// <summary>discount (docs/21 §1.3): a passive 引导者 marker (trigger == channel) — -Amount order cost
/// when this unit channels (晚祷领唱). Read via EffectEngine.ChannelEffectAmount in the Resolver's
/// EffectiveCost; RunTrigger never dispatches a channel effect, so Execute is unreachable.</summary>
internal sealed class DiscountAction : EffectActionBase
{
    public override string Name => "discount";

    public override string? ValidateCard(EffectSpec spec, CardDefinition card) =>
        spec.Trigger != "channel"
            ? $"Card '{card.Id}': 'discount' is only valid on a 'channel' marker."
            : null;

    public override void Execute(ResolutionContext ctx, UnitInstance? source, int ownerSeat, EffectSpec spec,
        IReadOnlyList<UnitInstance> targets, Cell? targetCell, int amount, int? secondaryTargetUnitId) =>
        // Validation pins discount to trigger 'channel', and RunTrigger is never called with that trigger.
        throw new InvalidOperationException("'discount' is a passive channel marker — read via ChannelEffectAmount, never executed.");

    public override double Score(EffectScoreArgs a) => 1; // never scored: channel markers aren't play/battlecry effects
}
