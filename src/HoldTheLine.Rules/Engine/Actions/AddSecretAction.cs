using HoldTheLine.Rules.Cards;
using HoldTheLine.Rules.Geometry;
using HoldTheLine.Rules.State;

namespace HoldTheLine.Rules.Engine.Actions;

/// <summary>add_secret (秘密, docs/21 §1.7): set a face-down reactive order in your 秘密区 (焰誓反制).</summary>
internal sealed class AddSecretAction : EffectActionBase
{
    public override string Name => "add_secret";

    public override string? ValidateCard(EffectSpec spec, CardDefinition card) =>
        card.Type != CardType.Order || spec.SecretKind is null || !EffectSpec.KnownSecretKinds.Contains(spec.SecretKind)
            ? $"Card '{card.Id}': add_secret (秘密) needs an order and a known secret_kind."
            : null;

    public override void Execute(ResolutionContext ctx, UnitInstance? source, int ownerSeat, EffectSpec spec,
        IReadOnlyList<UnitInstance> targets, Cell? targetCell, int amount, int? secondaryTargetUnitId)
    {
        // Marker — resolved in the Resolver's order pipeline: ResolveOrder sets the card face-down
        // (ctx.AddSecret) INSTEAD of running its play effects, so this never executes; the reactive
        // payload later fires via ctx.TryTriggerCounterSecret when an enemy order selects your minion.
    }

    public override double Score(EffectScoreArgs a) => 2; // deterrent, constant EV (docs/21 §5 — no game-theory this patch)
}
