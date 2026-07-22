using HoldTheLine.Rules.Cards;
using HoldTheLine.Rules.Geometry;
using HoldTheLine.Rules.State;

namespace HoldTheLine.Rules.Engine.Actions;

/// <summary>mirror_module (镜像工坊, docs/20 §S9b): copy one in-装 module (无视同名唯一) into a free slot. A RESOLVER-
/// DRIVEN marker — the copy target rides on the command's TargetModuleCardId, which the effect pipeline never sees,
/// so <see cref="Resolver"/>.ResolveMirrorWorks does the work (like add_secret / 献祭). Execute is never reached.</summary>
internal sealed class MirrorModuleAction : EffectActionBase
{
    public override string Name => "mirror_module";

    public override string? ValidateCard(EffectSpec spec, CardDefinition card) =>
        card.Type != CardType.Order || spec.Trigger != "play" || spec.Target != "none"
            ? $"Card '{card.Id}': mirror_module must be an order play with target none."
            : null;

    public override void Execute(ResolutionContext ctx, UnitInstance? source, int ownerSeat, EffectSpec spec,
        IReadOnlyList<UnitInstance> targets, Cell? targetCell, int amount, int? secondaryTargetUnitId)
    {
        // Resolver-driven marker — see Resolver.ResolveMirrorWorks. No-op if ever reached by RunTrigger.
    }

    public override double Score(EffectScoreArgs a) =>
        a.State.Units.Any(u => u.OwnerSeat == a.Seat && u.Turret is { IsShadow: false }) ? 5 : 0;
}
