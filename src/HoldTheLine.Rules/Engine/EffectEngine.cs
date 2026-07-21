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
    /// <param name="spellDamageBonus">加深/蓄能/引导 amplification (docs/21 §1.3) added to each 薪炎 (spell.*)
    /// damage/sear instance in this run. 0 for every path except a channeled 薪炎 order.</param>
    public static void RunTrigger(
        ResolutionContext ctx,
        UnitInstance? source,
        int ownerSeat,
        IReadOnlyList<EffectSpec> effects,
        string trigger,
        int? targetUnitId,
        Cell? targetCell = null,
        int spellDamageBonus = 0)
    {
        foreach (var spec in effects)
        {
            if (spec.Trigger != trigger)
                continue;
            Run(ctx, source, ownerSeat, spec, targetUnitId, targetCell, spellDamageBonus);
        }
        ctx.ProcessDeaths();
    }

    /// <summary>Sum of a 引导者's channel-marker bonus of the given action (deepen/discount); 0 if it has none.
    /// Data-driven off the channeler's card, so 引导者差异化 stays in the card table (docs/21 §1.2).</summary>
    public static int ChannelEffectAmount(CardDatabase db, UnitInstance channeler, string action) =>
        db.Get(channeler.CardId).Effects
            .Where(e => e.Trigger == "channel" && e.Action == action)
            .Sum(e => e.Amount);

    /// <summary>Whether this order carries a 薪炎 (spell.*) damage/sear play effect — the trigger for 蓄能
    /// consumption and 晚祷领唱's cost discount (docs/21 §1.3).</summary>
    public static bool IsKindleDamageOrder(CardDefinition def) =>
        def.Type == CardType.Order && def.Effects.Any(e => e.Trigger == "play" && e.IsSpellDamage);

    /// <summary>Pre-validation used before paying costs: do the effect's declared targets exist and satisfy filters?</summary>
    /// <param name="allowFizzleWhenNoTarget">先上随从再判战吼: when true (unit deploy / battlecry), a required
    /// unit target that has NO legal candidate on the board is waved through — the unit still deploys and the
    /// battlecry simply fizzles. A legal target still makes the choice mandatory. Orders / leader skills pass
    /// false: a targeted order with no target is a wasted card and stays illegal (docs/07, GDD battlecry rule).</param>
    /// <param name="anchorCenter">The 锚/引导 range origin (docs/21 §1.2): the deploy cell for a 锚 battlecry,
    /// the channeler's cell for a 引导 order. Null when the effect carries no anchor — then no range gate applies.</param>
    public static RuleError? ValidateTargets(
        ResolutionContext ctx,
        int ownerSeat,
        IReadOnlyList<EffectSpec> effects,
        string trigger,
        int? targetUnitId,
        Cell? targetCell,
        bool allowFizzleWhenNoTarget = false,
        Cell? anchorCenter = null)
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
                    if (allowFizzleWhenNoTarget && !AnyLegalUnitTarget(ctx.State, ownerSeat, spec, anchorCenter))
                        continue;
                    return new RuleError(RuleErrorCode.InvalidTarget, "This effect requires a unit target.");
                }
                var target = ctx.State.FindUnit(targetUnitId.Value);
                if (target is null)
                    return new RuleError(RuleErrorCode.UnknownEntity, $"Target unit {targetUnitId.Value} does not exist.");
                // Single source of truth for the owner/half + 锚/引导 range filters — shared with
                // AnyLegalUnitTarget so "may I aim here?" and "does a legal target exist?" can never drift.
                if (!IsLegalUnitTarget(ownerSeat, spec, target, anchorCenter))
                    return new RuleError(RuleErrorCode.InvalidTarget, $"That unit is not a legal target for a '{spec.Target}' effect.");
            }

            if (spec.NeedsCellTarget)
            {
                if (targetCell is null)
                    return new RuleError(RuleErrorCode.InvalidTarget, "This effect requires a target cell.");
                // 引导 落点格 (行/列以落点格计算) must sit within the channeler's reach.
                if (spec.HasAnchorRange && anchorCenter is { } cc
                    && BoardGeometry.StepDistance(cc, targetCell.Value) > spec.AnchorRange)
                    return new RuleError(RuleErrorCode.InvalidTarget, "落点格超出引导者射程。");
            }
        }
        return null;
    }

    /// <summary>True when the card's battlecry FORCES a unit target — some needsUnit battlecry spec has a legal
    /// target on the board. The enumerator uses this to skip the (would-be-pruned) bare-deploy candidate, sparing
    /// a full resolver dry-run per free home cell in the AI search loop.</summary>
    internal static bool BattlecryTargetMandatory(GameState state, int ownerSeat, IReadOnlyList<EffectSpec> effects) =>
        // 锚 (self anchor) battlecries are excluded: their mandatory-ness depends on the deploy cell (which
        // in-range targets exist there), unknown at this per-card call — so the enumerator always offers the
        // bare deploy and the resolver prunes it per cell. Non-anchored battlecries keep the cheap skip.
        effects.Any(e => e.Trigger == "battlecry" && e.NeedsUnitTarget && !e.IsSelfAnchor
                         && AnyLegalUnitTarget(state, ownerSeat, e, anchorCenter: null));

    /// <summary>Does at least one on-board unit satisfy this spec's unit-target filter? Shares <see
    /// cref="IsLegalUnitTarget"/> with ValidateTargets, so the two can never disagree about "no legal target".</summary>
    private static bool AnyLegalUnitTarget(GameState state, int ownerSeat, EffectSpec spec, Cell? anchorCenter) =>
        state.Units.Any(u => IsLegalUnitTarget(ownerSeat, spec, u, anchorCenter));

    private static bool IsLegalUnitTarget(int ownerSeat, EffectSpec spec, UnitInstance u, Cell? anchorCenter)
    {
        bool ownerOk = spec.Target switch
        {
            "target_unit_ally" => u.OwnerSeat == ownerSeat,
            "target_unit_own_half" => u.OwnerSeat != ownerSeat && BoardGeometry.InOwnHalf(ownerSeat, u.Cell),
            // target_unit / unit_cross_all: no owner/half filter — any unit qualifies.
            _ => true,
        };
        if (!ownerOk)
            return false;
        // 锚/引导 range gate: a self/channel unit-target effect requires the target within reach of the
        // anchor centre (deploy cell for 锚, channeler cell for 引导). No centre → gate not evaluated here.
        if (spec.HasAnchorRange && anchorCenter is { } c
            && BoardGeometry.StepDistance(c, u.Cell) > spec.AnchorRange)
            return false;
        return true;
    }

    private static void Run(ResolutionContext ctx, UnitInstance? source, int ownerSeat, EffectSpec spec, int? targetUnitId, Cell? targetCell, int spellDamageBonus = 0)
    {
        var targets = ResolveTargets(ctx, source, ownerSeat, spec.Target, targetUnitId, targetCell);

        // amount_max: a random magnitude in [Amount, AmountMax], rolled ONCE per effect (not per target)
        // on the match Rng so replays stay deterministic (灼痕烙印's 2-或-3).
        int amount = spec.AmountMax > spec.Amount
            ? spec.Amount + ctx.State.Rng.NextInt(spec.AmountMax - spec.Amount + 1)
            : spec.Amount;

        // 加深/蓄能/引导 (docs/21 §1.3): amplify 薪炎 (spell.*) damage/sear only; physical is untouched.
        if (spec.IsSpellDamage)
            amount += spellDamageBonus;

        switch (spec.Action)
        {
            case "amplify_next":
                // 蓄能 N: bank a bonus for the seat's next 薪炎 order (焰跃术士).
                ctx.AddSpellCharge(ownerSeat, spec.Amount);
                break;

            // deepen / discount are passive 引导者 markers (trigger == channel) — the amplify pipeline reads
            // them via ChannelEffectAmount; RunTrigger never dispatches a channel effect, so they never land here.

            case "damage":
                // 架设 second clause: bolted-down units cannot dodge incoming barrages — they take
                // +1 from EFFECT damage (orders, skills, battlecries; never from attacks). This is
                // the 焰克械 counter interface (docs/06 §4): spell factions crack static formations.
                foreach (var t in targets)
                    ctx.DamageUnit(t, amount + (t.HasKeyword(Keyword.Emplacement) ? 1 : 0));
                break;

            case "damage_scatter":
                // 燔火 (docs/21 §3.1): fire `amount` missiles of 1 薪炎 damage, each at a RANDOM live enemy minion
                // (re-rolled per missile among survivors, 炉石奥术飞弹 semantics). The roll is on the match Rng so
                // replays are deterministic. 加深/蓄能 already folded into `amount` above (+1 missile per point).
                for (int i = 0; i < amount; i++)
                {
                    var live = ctx.State.Units.Where(u => u.OwnerSeat != ownerSeat && u.CurrentHp > 0).ToList();
                    if (live.Count == 0)
                        break;
                    var victim = live[ctx.State.Rng.NextInt(live.Count)];
                    ctx.DamageUnit(victim, 1 + (victim.HasKeyword(Keyword.Emplacement) ? 1 : 0));
                }
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

            case "all_enemies":
                // 燎原 (docs/21 §3.1): every enemy minion. Units order → deterministic replay.
                return ctx.State.Units
                    .Where(u => u.OwnerSeat != ownerSeat)
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
