using HoldTheLine.Rules.Cards;
using HoldTheLine.Rules.Geometry;
using HoldTheLine.Rules.State;

namespace HoldTheLine.Rules.Engine.Actions;

/// <summary>summon_shadow_turret (docs/20 §S15): 维尔达's battlecry — if a friendly 工造炮台 is in play and Vela has
/// an adjacent empty cell, place a 影子炮台 there (a runtime snapshot of the real turret, 满血/突袭, gone at turn end).
/// 战吼落空 when 炮台不在场 or 相邻无空格 (与现有战吼无合法目标先例一致). Unit battlecry, target none.</summary>
internal sealed class SummonShadowTurretAction : EffectActionBase
{
    public override string Name => "summon_shadow_turret";

    public override string? ValidateCard(EffectSpec spec, CardDefinition card) =>
        card.Type != CardType.Unit || spec.Trigger != "battlecry" || spec.Target != "none"
            ? $"Card '{card.Id}': summon_shadow_turret must be a unit battlecry with target none."
            : null;

    public override void Execute(ResolutionContext ctx, UnitInstance? source, int ownerSeat, EffectSpec spec,
        IReadOnlyList<UnitInstance> targets, Cell? targetCell, int amount, int? secondaryTargetUnitId)
    {
        if (source is null)
            return;
        var turret = ctx.FriendlyTurret(ownerSeat);
        if (turret is null)
            return; // 炮台不在场 → 落空
        Cell? spot = null;
        foreach (var c in BoardGeometry.AdjacentCells(source.Cell))
            if (ctx.State.UnitAt(c) is null) { spot = c; break; }
        if (spot is null)
            return; // 相邻无空格 → 落空
        ctx.SummonShadowTurret(ownerSeat, turret, spot.Value);
    }

    // 一回合双炮齐射 — worth roughly the whole turret again, but only when there IS a turret to copy (S15).
    public override double Score(EffectScoreArgs a) =>
        a.State.Units.Any(u => u.OwnerSeat == a.Seat && u.Turret is { IsShadow: false }) ? 8 : 0;
}
