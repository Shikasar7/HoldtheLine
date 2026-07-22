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
        // ctx.BuffUnit routes a 炮台 buff into its External layer + recompute (survives module swaps, docs/20 §S4)
        // and mutates every other unit's panel directly; it emits the UnitBuffedEvent.
        foreach (var t in targets)
            ctx.BuffUnit(t, spec.Atk, spec.Hp);
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
            case "friendly_turret": // docs/20: 齿轮工长/护炮班组 buff the turret — worth it only when one is in play.
                return s.Units.Any(u => u.OwnerSeat == seat && u.Turret is { IsShadow: false }) ? (e.Atk + e.Hp) * 2 : 0;
            default:
                return 1;
        }
    }
}
