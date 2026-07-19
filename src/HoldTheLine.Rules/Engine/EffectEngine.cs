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
    /// <param name="allowFizzleWhenNoTarget">先上随从再判战吼: when true (unit deploy / battlecry), a required
    /// unit target that has NO legal candidate on the board is waved through — the unit still deploys and the
    /// battlecry simply fizzles. A legal target still makes the choice mandatory. Orders / leader skills pass
    /// false: a targeted order with no target is a wasted card and stays illegal (docs/07, GDD battlecry rule).</param>
    public static RuleError? ValidateTargets(
        ResolutionContext ctx,
        int ownerSeat,
        IReadOnlyList<EffectSpec> effects,
        string trigger,
        int? targetUnitId,
        Cell? targetCell,
        bool allowFizzleWhenNoTarget = false)
    {
        foreach (var spec in effects)
        {
            if (spec.Trigger != trigger)
                continue;

            if (spec.NeedsUnitTarget)
            {
                if (targetUnitId is null)
                {
                    // Fizzle only when the board offers no legal target for THIS effect; otherwise the
                    // player must still pick one (an empty command can't skip an answerable battlecry).
                    if (allowFizzleWhenNoTarget && !AnyLegalUnitTarget(ctx.State, ownerSeat, spec))
                        continue;
                    return new RuleError(RuleErrorCode.InvalidTarget, "This effect requires a unit target.");
                }
                var target = ctx.State.FindUnit(targetUnitId.Value);
                if (target is null)
                    return new RuleError(RuleErrorCode.UnknownEntity, $"Target unit {targetUnitId.Value} does not exist.");
                // Single source of truth for the owner/half filters — shared with AnyLegalUnitTarget so the
                // "may I aim here?" and "does a legal target exist?" questions can never drift apart.
                if (!IsLegalUnitTarget(ownerSeat, spec, target))
                    return new RuleError(RuleErrorCode.InvalidTarget, $"That unit is not a legal target for a '{spec.Target}' effect.");
            }

            if (spec.NeedsCellTarget && targetCell is null)
                return new RuleError(RuleErrorCode.InvalidTarget, "This effect requires a target cell.");
        }
        return null;
    }

    /// <summary>True when the card's battlecry FORCES a unit target — some needsUnit battlecry spec has a legal
    /// target on the board. The enumerator uses this to skip the (would-be-pruned) bare-deploy candidate, sparing
    /// a full resolver dry-run per free home cell in the AI search loop.</summary>
    internal static bool BattlecryTargetMandatory(GameState state, int ownerSeat, IReadOnlyList<EffectSpec> effects) =>
        effects.Any(e => e.Trigger == "battlecry" && e.NeedsUnitTarget && AnyLegalUnitTarget(state, ownerSeat, e));

    /// <summary>Does at least one on-board unit satisfy this spec's unit-target filter? Shares <see
    /// cref="IsLegalUnitTarget"/> with ValidateTargets, so the two can never disagree about "no legal target".</summary>
    private static bool AnyLegalUnitTarget(GameState state, int ownerSeat, EffectSpec spec) =>
        state.Units.Any(u => IsLegalUnitTarget(ownerSeat, spec, u));

    private static bool IsLegalUnitTarget(int ownerSeat, EffectSpec spec, UnitInstance u) => spec.Target switch
    {
        "target_unit_ally" => u.OwnerSeat == ownerSeat,
        "target_unit_own_half" => u.OwnerSeat != ownerSeat && BoardGeometry.InOwnHalf(ownerSeat, u.Cell),
        // target_unit / unit_cross_all: no owner/half filter — any unit qualifies.
        _ => true,
    };

    private static void Run(ResolutionContext ctx, UnitInstance? source, int ownerSeat, EffectSpec spec, int? targetUnitId, Cell? targetCell)
    {
        var targets = ResolveTargets(ctx, source, ownerSeat, spec.Target, targetUnitId, targetCell);

        // amount_max: a random magnitude in [Amount, AmountMax], rolled ONCE per effect (not per target)
        // on the match Rng so replays stay deterministic (灼痕烙印's 2-或-3).
        int amount = spec.AmountMax > spec.Amount
            ? spec.Amount + ctx.State.Rng.NextInt(spec.AmountMax - spec.Amount + 1)
            : spec.Amount;

        switch (spec.Action)
        {
            case "damage":
                // 架设 second clause: bolted-down units cannot dodge incoming barrages — they take
                // +1 from EFFECT damage (orders, skills, battlecries; never from attacks). This is
                // the 焰克械 counter interface (docs/06 §4): spell factions crack static formations.
                foreach (var t in targets)
                    ctx.DamageUnit(t, amount + (t.HasKeyword(Keyword.Emplacement) ? 1 : 0));
                break;

            case "sear":
                // 灼蚀 (docs/10 §6#2): effect damage that ignores 坚守 reduction — the 教团→铁壁 answer
                // (v2.1 遗留#1: HoldFast otherwise eats the 教团's 1-2pt chip damage whole). 持盾 still
                // absorbs; 架设's +1 effect-damage clause still stacks (灼蚀 is effect damage too).
                foreach (var t in targets)
                    ctx.DamageUnit(t, amount + (t.HasKeyword(Keyword.Emplacement) ? 1 : 0), ignoreHoldFast: true);
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

            case "boost_range":
                // 加农校准: +Amount range, ADDITIVE onto whatever range the unit already has (docs/00 §3 —
                // restores the GDD "射程加法叠加" original). KeywordValue is a max across grants, so raising
                // it means granting (current range + Amount): a melee unit → range Amount, a range-2 unit → 2+Amount.
                foreach (var t in targets)
                    ctx.GrantKeyword(t, Keyword.Range, t.KeywordValue(Keyword.Range) + spec.Amount, spec.Duration, ownerSeat);
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

            case "all_ally_emplacements":
                // 匠会 阵地 payoff (docs/10 §6#3): every friendly 架设 unit — turrets you have bolted down.
                return ctx.State.Units
                    .Where(u => u.OwnerSeat == ownerSeat && u.HasKeyword(Keyword.Emplacement))
                    .ToList();

            default:
                throw new InvalidOperationException($"Unknown effect target '{target}'.");
        }
    }
}
