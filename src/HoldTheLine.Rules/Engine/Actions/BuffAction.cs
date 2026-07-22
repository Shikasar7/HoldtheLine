using HoldTheLine.Rules.Ai;
using HoldTheLine.Rules.Cards;
using HoldTheLine.Rules.Geometry;
using HoldTheLine.Rules.State;

namespace HoldTheLine.Rules.Engine.Actions;

/// <summary>buff: permanent Atk/Hp deltas on each resolved target.</summary>
internal sealed class BuffAction : EffectActionBase
{
    public override string Name => "buff";

    public override void Execute(ResolutionContext ctx, UnitInstance? source, int ownerSeat, EffectSpec spec,
        IReadOnlyList<UnitInstance> targets, Cell? targetCell, int amount, int? secondaryTargetUnitId)
    {
        foreach (var t in targets)
        {
            t.Atk += spec.Atk;
            t.MaxHp += spec.Hp;
            t.CurrentHp += spec.Hp;
            ctx.Emit(new Events.UnitBuffedEvent
            {
                UnitEntityId = t.EntityId,
                AtkDelta = spec.Atk,
                HpDelta = spec.Hp,
                NewAtk = t.Atk,
                NewHp = t.CurrentHp,
            });
        }
    }

    public override double Score(EffectScoreArgs a)
    {
        var s = a.State;
        var e = a.Spec;
        int seat = a.Seat;
        switch (e.Target)
        {
            case "target_unit":
            case "target_unit_ally":
                if (a.Target == null) return 0;
                return a.TargetIsAlly ? 2 + a.Cost : -100;
            case "adjacent_allies":
                return 2;
            case "allies_home_row":
            case "all_allies":
            {
                int n = e.Target == "all_allies"
                    ? s.Units.Count(u => u.OwnerSeat == seat)
                    : s.Units.Count(u => u.OwnerSeat == seat && u.Cell.Row == BoardGeometry.HomeRow(seat));
                return n * (e.Atk + e.Hp) * 1.5;
            }
            case "column_allies":
                return s.Units.Count(u => u.OwnerSeat == seat && GreedyAi.InCol(u, a.TargetCell)) * (e.Atk + e.Hp) * 1.5;
            case "all_ally_emplacements": // 匠会 阵地 payoff: value scales with turrets already bolted down.
                return s.Units.Count(u => u.OwnerSeat == seat && u.HasKeyword(Keyword.Emplacement)) * (e.Atk + e.Hp) * 1.5;
            default:
                return 1;
        }
    }
}
