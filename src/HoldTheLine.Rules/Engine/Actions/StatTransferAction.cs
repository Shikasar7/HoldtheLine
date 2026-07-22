using HoldTheLine.Rules.Ai;
using HoldTheLine.Rules.Cards;
using HoldTheLine.Rules.Geometry;
using HoldTheLine.Rules.State;

namespace HoldTheLine.Rules.Engine.Actions;

/// <summary>stat_transfer (焰鞭 friendly mode, docs/21 §1.8): destroy the primary (ally) target and
/// add its current atk/hp to the 二段目标.</summary>
internal sealed class StatTransferAction : EffectActionBase
{
    public override string Name => "stat_transfer";

    public override string? ValidateCard(EffectSpec spec, CardDefinition card) =>
        !spec.NeedsUnitTarget
            ? $"Card '{card.Id}': stat_transfer (焰鞭) needs a unit target."
            : null;

    public override void Execute(ResolutionContext ctx, UnitInstance? source, int ownerSeat, EffectSpec spec,
        IReadOnlyList<UnitInstance> targets, Cell? targetCell, int amount, int? secondaryTargetUnitId)
    {
        // 焰鞭 friendly mode (docs/21 §1.8): consume the primary ally (A) and add its CURRENT atk/hp to
        // the 二段目标 (B). Secondary validity is pre-checked in the resolver; guard again defensively.
        if (targets.Count > 0 && secondaryTargetUnitId is { } bId
            && ctx.State.FindUnit(bId) is { } b && b.OwnerSeat == ownerSeat && b.EntityId != targets[0].EntityId)
        {
            var a = targets[0];
            int atk = a.Atk, hp = a.CurrentHp;
            ctx.Emit(new Events.StatTransferredEvent { FromUnitId = a.EntityId, ToUnitId = b.EntityId, Atk = atk, Hp = hp });
            ctx.DestroyUnit(a); // 消灭 A — deathrattle fires via ProcessDeaths
            b.Atk += atk;
            b.MaxHp += hp;
            b.CurrentHp += hp;
            ctx.Emit(new Events.UnitBuffedEvent { UnitEntityId = b.EntityId, AtkDelta = atk, HpDelta = hp, NewAtk = b.Atk, NewHp = b.CurrentHp });
        }
    }

    // 焰鞭 friendly mode: only worth it on a cheap/dying/deathrattle body (SacrificeValue guards it).
    public override double Score(EffectScoreArgs a) =>
        a.Target == null || !a.TargetIsAlly ? 0 : GreedyAi.SacrificeValue(a.Db, a.Target) + 1.5;
}
