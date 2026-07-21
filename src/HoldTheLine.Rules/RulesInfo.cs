namespace HoldTheLine.Rules;

public static class RulesInfo
{
    /// <summary>Ruleset version. Bump on any change to resolution semantics or serialized shapes.</summary>
    /// <remarks>0.2.0 (2026-07-17): new-faction primitives — Manhattan 射程 + no line-blocking, 架设/贯穿,
    /// destroy, ally_order_played, and the row/cross/column-ally selectors (docs/06 §3, docs/07 X0).
    /// 0.3.0 (2026-07-18): second-batch primitives — 灼蚀 (sear, ignores 坚守), self_moved trigger,
    /// all_ally_emplacements selector; 80 new cards, pool 83→163 (docs/10).
    /// 0.4.0 (2026-07-18): 起手重抽 (mulligan) — new phase/command/events + MatchConfig.MulliganEnabled
    /// (default off, old logs replay unchanged); protocol v3→v4 (docs/11).
    /// 0.4.1 (2026-07-18): 重新部署 (Mobilized) — granted transient keyword lets an 架设 unit move once this
    /// turn; repurposes 校准指令 from a redundant range-1 grant (docs/10 §11).
    /// 0.5.0 (2026-07-19): token orders (军令硬币) are removed from the game after use instead of entering
    /// the graveyard (recall_order can no longer fish the coin back); hand limit 10 → 9.
    /// 0.6.0 (2026-07-19): balance patch #1 — 践踏 reworked to melee splash (adjacent to target, friend+foe,
    /// OccupyCellOnKill ignored); 围猎 flank bonus +1 → +2; self_moved ATK gains capped at 2/unit/turn;
    /// amount_max random magnitude for damage/sear (灼痕烙印 2-3); iron_vow leader skill 筑垒(守护) → 授盾(持盾).
    /// 0.7.0 (2026-07-20): drawing/gaining a card into a full hand now sends it to the graveyard instead of
    /// burning it out of the game (recyclable by 火种循环); reverses the 0.5.0 "overdraw burns" wording.
    /// 0.8.0 (2026-07-20): keyword rework — the old 守护/Guard (must-be-attacked-first) is renamed 嘲讽/Taunt;
    /// two new keywords: 福泽/Blessing (adjacent allies take 1 less damage) and 守护/Guardian (adjacent allies'
    /// damage redirects to this unit, resolved through its own reductions). UnitDamagedEvent gains GuardRedirect.
    /// Card rebalance: 壁垒射手 3/2, 越线者死 3费4伤, 磐石巨像/移动要塞 taunt→guardian, 磐石哨卫 +guardian,
    /// 薇兰蒂 亡语→福泽, 尖桩拒马/装甲路桩 lose 架设.
    /// 0.8.1 (2026-07-20): balance patch #3 — second player's opening draw cut +2→+1 (6→5 cards); the coin
    /// stays. With the coin, going-second win rate had climbed too high, so the extra card is rolled back.
    /// Old command logs keep their serialized OpeningHandSecond, so replays are unchanged.
    /// 0.9.0 (2026-07-21): balance patch #4 — 教团法术改版 + 秘密体系 (docs/21). Large engine expansion:
    /// EffectSpec gains school (physical | spell.kindle) + 锚/引导 positioning (anchor/anchor_range) +
    /// target_side/min_order_cost/secret_kind; new actions damage_scatter/amplify_next/place_smoke/place_trap/
    /// stat_transfer/sacrifice_equip/add_secret + channel-marker deepen/discount + echo_order; new keywords
    /// Rooted/MoltenSword/KindleImmune/SpellWard + Hidden enabled (潜行). GameState gains 蓄能/秘密区/格子状态
    /// (烟幕/陷阱)/成长/薪火回响 tracking; PlayerCommand gains ChannelerUnitId/SecondaryTargetUnitId/
    /// SacrificeEntityIds (additive — old logs replay unchanged). PlayerView adds SpellCharge/SecretCount/
    /// CellStates with server-authority redaction of hidden traps + secret contents. 7 new cards, pool 163→170.
    /// DataHash changes — client and server must ship this same data.
    /// 0.9.1 (2026-07-21): 薪火回响·门德 recast rework + tuning. 门德's 薪火回响 no longer auto-copies the first
    /// 薪炎 order at the same target; instead PlayCardCommand gains EchoRecast/EchoTargetUnitId/EchoTargetCell
    /// (additive — old logs replay unchanged) so the caster RE-AIMS the once-per-turn recast at a target of their
    /// choice, and may 空放/取消 (EchoRecast=false). CommandEnumerator forks echo variants only in 门德 games.
    /// Card tuning (DataHash changes): 熔剑祭士 hp 4→6, 焰术学徒 2/1→1/3, all 引导 order texts reworded to
    /// "需友方随从引导(施法距离 N)". Client + server must ship this same data.</remarks>
    public const string Version = "0.9.1";

    /// <summary>压力潮汐 start round, forwarded from the (internal) <see cref="Engine.TurnFlow"/> so the client
    /// HUD can show the tide countdown (docs/17) without hardcoding 8 and drifting from the rule. Read-only
    /// mirror — the single source of truth stays <c>TurnFlow.PressureTideStartRound</c>.</summary>
    public const int PressureTideStartRound = Engine.TurnFlow.PressureTideStartRound;

    /// <summary>压力潮汐 per-turn bleed ceiling (补丁#4), mirrored so the client HUD's tide prediction caps the
    /// same way the rule does. Single source of truth stays <c>TurnFlow.PressureTideMaxAmount</c>.</summary>
    public const int PressureTideMaxAmount = Engine.TurnFlow.PressureTideMaxAmount;
}
