using HoldTheLine.Rules.Cards;
using HoldTheLine.Rules.Geometry;
using HoldTheLine.Rules.State;

namespace HoldTheLine.Rules.Engine;

/// <summary>
/// Executes data-driven EffectSpecs. All card/leader effects flow through here so new mechanics
/// never leak special cases into the resolver. Shared state mutations live on ResolutionContext.
/// </summary>
internal static class EffectEngine
{
    /// <param name="source">The unit the effect originates from; null for orders and leader skills.</param>
    /// <param name="targetUnitId">Explicit unit target from the command (target == target_unit*).</param>
    /// <param name="targetCell">Explicit cell target from the command (spatial selectors, e.g. column_enemies).</param>
    public static void RunTrigger(
        ResolutionContext ctx,
        UnitInstance? source,
        int ownerSeat,
        IReadOnlyList<EffectSpec> effects,
        string trigger,
        int? targetUnitId,
        Cell? targetCell = null)
    {
        foreach (var spec in effects)
        {
            if (spec.Trigger != trigger)
                continue;
            Run(ctx, source, ownerSeat, spec, targetUnitId, targetCell);
        }
        ctx.ProcessDeaths();
    }

    /// <summary>Pre-validation used before paying costs: do the effect's declared targets exist and satisfy filters?</summary>
    public static RuleError? ValidateTargets(
        ResolutionContext ctx,
        int ownerSeat,
        IReadOnlyList<EffectSpec> effects,
        string trigger,
        int? targetUnitId,
        Cell? targetCell)
    {
        foreach (var spec in effects)
        {
            if (spec.Trigger != trigger)
                continue;

            if (spec.NeedsUnitTarget)
            {
                if (targetUnitId is null)
                    return new RuleError(RuleErrorCode.InvalidTarget, "This effect requires a unit target.");
                var target = ctx.State.FindUnit(targetUnitId.Value);
                if (target is null)
                    return new RuleError(RuleErrorCode.UnknownEntity, $"Target unit {targetUnitId.Value} does not exist.");
                if (spec.Target == "target_unit_own_half")
                {
                    if (target.OwnerSeat == ownerSeat)
                        return new RuleError(RuleErrorCode.InvalidTarget, "This targets an enemy unit.");
                    if (!BoardGeometry.InOwnHalf(ownerSeat, target.Cell))
                        return new RuleError(RuleErrorCode.InvalidTarget, "Target must be in your half of the board.");
                }
                if (spec.Target == "target_unit_ally" && target.OwnerSeat != ownerSeat)
                    return new RuleError(RuleErrorCode.InvalidTarget, "This targets a friendly unit.");
            }

            if (spec.NeedsCellTarget && targetCell is null)
                return new RuleError(RuleErrorCode.InvalidTarget, "This effect requires a target cell.");
        }
        return null;
    }

    private static void Run(ResolutionContext ctx, UnitInstance? source, int ownerSeat, EffectSpec spec, int? targetUnitId, Cell? targetCell)
    {
        var targets = ResolveTargets(ctx, source, ownerSeat, spec.Target, targetUnitId, targetCell);

        switch (spec.Action)
        {
            case "damage":
                // 架设 second clause: bolted-down units cannot dodge incoming barrages — they take
                // +1 from EFFECT damage (orders, skills, battlecries; never from attacks). This is
                // the 焰克械 counter interface (docs/06 §4): spell factions crack static formations.
                foreach (var t in targets)
                    ctx.DamageUnit(t, spec.Amount + (t.HasKeyword(Keyword.Emplacement) ? 1 : 0));
                break;

            case "destroy":
                // 献祭/消灭: straight to the death sweep — bypasses DamageUnit, so 持盾/坚守 don't save it;
                // 亡语 still fires (via ProcessDeaths). No new event — the sweep emits UnitDiedEvent.
                foreach (var t in targets)
                    ctx.DestroyUnit(t);
                break;

            case "heal":
                foreach (var t in targets)
                    ctx.HealUnit(t, spec.Amount);
                break;

            case "buff":
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
                break;

            case "grant_keyword":
                foreach (var t in targets)
                    ctx.GrantKeyword(t, spec.GrantKeyword!.Value, spec.GrantKeywordValue, spec.Duration, ownerSeat);
                break;

            case "move_bonus":
                foreach (var t in targets)
                    ctx.AddMoveBonus(t, spec.Amount);
                break;

            case "summon":
                ctx.SummonUnits(ownerSeat, spec.SummonCardId!, spec.Amount);
                break;

            case "draw":
                ctx.DrawCards(ownerSeat, spec.Amount);
                break;

            case "recall_order":
                ctx.RecallOrders(ownerSeat, spec.Amount);
                break;

            case "gain_mana":
                ctx.GainMana(ownerSeat, spec.Amount);
                break;

            default:
                // CardDatabase / LeaderDatabase validation guarantees this is unreachable; stay loud.
                throw new InvalidOperationException($"Unknown effect action '{spec.Action}'.");
        }
    }

    private static List<UnitInstance> ResolveTargets(
        ResolutionContext ctx, UnitInstance? source, int ownerSeat, string target, int? targetUnitId, Cell? targetCell)
    {
        switch (target)
        {
            case "none":
                return [];

            case "self":
                return source is null ? [] : [source];

            case "target_unit":
            case "target_unit_own_half":
            case "target_unit_ally":
                var explicitTarget = targetUnitId is null ? null : ctx.State.FindUnit(targetUnitId.Value);
                return explicitTarget is null ? [] : [explicitTarget];

            case "adjacent_allies":
            case "adjacent_enemies":
                if (source is null)
                    return [];
                bool allies = target == "adjacent_allies";
                return BoardGeometry.AdjacentCells(source.Cell)
                    .Select(ctx.State.UnitAt)
                    .Where(u => u != null && (u.OwnerSeat == source.OwnerSeat) == allies)
                    .Select(u => u!)
                    .ToList();

            case "column_enemies":
                if (targetCell is null)
                    return [];
                return ctx.State.Units
                    .Where(u => u.OwnerSeat != ownerSeat && u.Cell.Col == targetCell.Value.Col)
                    .ToList();

            case "row_enemies":
                if (targetCell is null)
                    return [];
                return ctx.State.Units
                    .Where(u => u.OwnerSeat != ownerSeat && u.Cell.Row == targetCell.Value.Row)
                    .ToList();

            case "column_allies":
                if (targetCell is null)
                    return [];
                return ctx.State.Units
                    .Where(u => u.OwnerSeat == ownerSeat && u.Cell.Col == targetCell.Value.Col)
                    .ToList();

            case "cell_cross_all":
                if (targetCell is null)
                    return [];
                // 十字模板: the target cell plus its four orthogonal neighbours, BOTH sides (含友方).
                // Edge/corner cells self-clip because AdjacentCells only yields in-board neighbours.
                var cross = new HashSet<Cell>(BoardGeometry.AdjacentCells(targetCell.Value)) { targetCell.Value };
                return ctx.State.Units.Where(u => cross.Contains(u.Cell)).ToList(); // Units order → deterministic

            case "unit_cross_all":
                // Same 十字 template, but centred on a chosen unit's cell — the deploy command already carries
                // a unit target, so a unit's battlecry can aim without a second cell field (docs/07 pyroclast).
                var centre = targetUnitId is null ? null : ctx.State.FindUnit(targetUnitId.Value);
                if (centre is null)
                    return [];
                var unitCross = new HashSet<Cell>(BoardGeometry.AdjacentCells(centre.Cell)) { centre.Cell };
                return ctx.State.Units.Where(u => unitCross.Contains(u.Cell)).ToList();

            case "allies_home_row":
                int homeRow = BoardGeometry.HomeRow(ownerSeat);
                return ctx.State.Units
                    .Where(u => u.OwnerSeat == ownerSeat && u.Cell.Row == homeRow)
                    .ToList();

            case "all_allies":
                return ctx.State.Units
                    .Where(u => u.OwnerSeat == ownerSeat)
                    .ToList();

            default:
                throw new InvalidOperationException($"Unknown effect target '{target}'.");
        }
    }
}
