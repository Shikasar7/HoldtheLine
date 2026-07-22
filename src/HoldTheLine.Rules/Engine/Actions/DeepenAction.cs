using HoldTheLine.Rules.Cards;
using HoldTheLine.Rules.Geometry;
using HoldTheLine.Rules.State;

namespace HoldTheLine.Rules.Engine.Actions;

/// <summary>deepen (docs/21 §1.3): a passive 引导者 marker (trigger == channel) — +Amount 薪炎 damage
/// when this unit channels. Read via EffectEngine.ChannelEffectAmount in the Resolver's order
/// pipeline; RunTrigger never dispatches a channel effect, so Execute is unreachable.</summary>
internal sealed class DeepenAction : EffectActionBase
{
    public override string Name => "deepen";

    public override string? ValidateCard(EffectSpec spec, CardDefinition card) =>
        spec.Trigger != "channel"
            ? $"Card '{card.Id}': 'deepen' is only valid on a 'channel' marker."
            : null;

    public override void Execute(ResolutionContext ctx, UnitInstance? source, int ownerSeat, EffectSpec spec,
        IReadOnlyList<UnitInstance> targets, Cell? targetCell, int amount, int? secondaryTargetUnitId) =>
        // Validation pins deepen to trigger 'channel', and RunTrigger is never called with that trigger.
        throw new InvalidOperationException("'deepen' is a passive channel marker — read via ChannelEffectAmount, never executed.");

    public override double Score(EffectScoreArgs a) => 1; // never scored: channel markers aren't play/battlecry effects
}
