using HoldTheLine.Rules.Cards;
using HoldTheLine.Rules.Geometry;
using HoldTheLine.Rules.State;

namespace HoldTheLine.Rules.Engine.Actions;

/// <summary>place_trap (烬火陷阱, docs/21 §1.7): bury a hidden trap on the chosen cell.</summary>
internal sealed class PlaceTrapAction : EffectActionBase
{
    public override string Name => "place_trap";

    public override string? ValidateCard(EffectSpec spec, CardDefinition card) =>
        spec.Target != "cell"
            ? $"Card '{card.Id}': place_trap needs target 'cell'."
            : null;

    public override void Execute(ResolutionContext ctx, UnitInstance? source, int ownerSeat, EffectSpec spec,
        IReadOnlyList<UnitInstance> targets, Cell? targetCell, int amount, int? secondaryTargetUnitId)
    {
        // 烬火陷阱 (docs/21 §1.7): bury a hidden trap; placement legality is pre-checked in the resolver
        // (Resolver.PlaceTrapLegality — empty in-board cell, not the enemy home row, not already trapped).
        if (targetCell is { } trapCell)
            ctx.PlaceTrap(ownerSeat, trapCell);
    }

    public override double Score(EffectScoreArgs a) => 2; // board-control setup
}
