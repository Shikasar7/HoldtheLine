using HoldTheLine.Rules.Cards;
using HoldTheLine.Rules.Geometry;
using HoldTheLine.Rules.State;

namespace HoldTheLine.Rules.Engine.Actions;

/// <summary>place_turret (docs/20 §1.1): 领袖 铸炮 — puts the唯一 工造炮台 on a chosen empty home-row cell.
/// Uniqueness / emptiness / home-row are enforced by <see cref="Resolver"/> (PlaceTurretLegality) before this
/// runs — so 场上已有你的炮台时按钮置灰 — leaving Execute to delegate to <see cref="ResolutionContext.PlaceTurret"/>.
/// leader_skill only.</summary>
internal sealed class PlaceTurretAction : EffectActionBase
{
    public override string Name => "place_turret";

    // Only ever borne by a leader (validated in LeaderDatabase). A CARD carrying it is a data error.
    public override string? ValidateCard(EffectSpec spec, CardDefinition card) =>
        $"Card '{card.Id}': place_turret is a leader skill, not a card effect.";

    public override void Execute(ResolutionContext ctx, UnitInstance? source, int ownerSeat, EffectSpec spec,
        IReadOnlyList<UnitInstance> targets, Cell? targetCell, int amount, int? secondaryTargetUnitId)
    {
        if (targetCell is { } cell)
            ctx.PlaceTurret(ownerSeat, cell);
    }

    // S16-a: 开局尽早铸炮 — the turret is the whole faction, so building it is top priority. A second placement is
    // illegal (uniqueness), so this is only ever scored when the seat has no turret; a flat high value suffices.
    public override double Score(EffectScoreArgs a) => 25;
}
