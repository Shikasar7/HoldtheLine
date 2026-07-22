using HoldTheLine.Rules.Cards;
using HoldTheLine.Rules.Geometry;
using HoldTheLine.Rules.State;

namespace HoldTheLine.Rules.Engine.Actions;

/// <summary>sacrifice_equip (熔剑祭士, docs/21 §3.2): battlecry marker — sacrifice 2 hand orders to
/// equip the 熔岩巨剑.</summary>
internal sealed class SacrificeEquipAction : EffectActionBase
{
    public override string Name => "sacrifice_equip";

    public override string? ValidateCard(EffectSpec spec, CardDefinition card) =>
        card.Type != CardType.Unit || spec.Trigger != "battlecry" || spec.Target != "none"
            ? $"Card '{card.Id}': sacrifice_equip (熔剑祭士) is a targetless unit battlecry."
            : null;

    public override void Execute(ResolutionContext ctx, UnitInstance? source, int ownerSeat, EffectSpec spec,
        IReadOnlyList<UnitInstance> targets, Cell? targetCell, int amount, int? secondaryTargetUnitId)
    {
        // 熔剑祭士 (docs/21 §3.2): a marker — resolved in the Resolver's deploy pipeline
        // (ctx.TrySacrificeEquip: it needs the command's SacrificeEntityIds + hand access),
        // so RunTrigger sees nothing to do here.
    }

    public override double Score(EffectScoreArgs a) => 2; // the 熔岩巨剑 payoff (the discard cost is not enumerated this patch)
}
