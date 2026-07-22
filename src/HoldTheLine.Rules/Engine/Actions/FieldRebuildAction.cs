using HoldTheLine.Rules.Cards;
using HoldTheLine.Rules.Geometry;
using HoldTheLine.Rules.State;

namespace HoldTheLine.Rules.Engine.Actions;

/// <summary>field_rebuild (战地重构, docs/20 §S8): install 2 RANDOM history modules not currently in装 into the
/// turret's free slots. A RESOLVER-DRIVEN marker — the random draw over the history pool + free-slot legality live
/// in <see cref="Resolver"/>.ResolveFieldRebuild (like add_secret). Execute is never reached. Order play, target none.</summary>
internal sealed class FieldRebuildAction : EffectActionBase
{
    public override string Name => "field_rebuild";

    public override string? ValidateCard(EffectSpec spec, CardDefinition card) =>
        card.Type != CardType.Order || spec.Trigger != "play" || spec.Target != "none"
            ? $"Card '{card.Id}': field_rebuild must be an order play with target none."
            : null;

    public override void Execute(ResolutionContext ctx, UnitInstance? source, int ownerSeat, EffectSpec spec,
        IReadOnlyList<UnitInstance> targets, Cell? targetCell, int amount, int? secondaryTargetUnitId)
    {
        // Resolver-driven marker — see Resolver.ResolveFieldRebuild. No-op if ever reached by RunTrigger.
    }

    public override double Score(EffectScoreArgs a)
    {
        // Value scales with how much material the history pool holds that isn't already installed.
        var turret = a.State.Units.FirstOrDefault(u => u.OwnerSeat == a.Seat && u.Turret is { IsShadow: false });
        if (turret?.Turret is not { } t)
            return 0;
        int pool = a.State.Player(a.Seat).InstalledHistory.Count(id => !t.Modules.Contains(id));
        return Math.Min(pool, 2) * 3;
    }
}
