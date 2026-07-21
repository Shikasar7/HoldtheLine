using HoldTheLine.Rules.Cards;
using HoldTheLine.Rules.Commands;
using HoldTheLine.Rules.Engine;
using HoldTheLine.Rules.Geometry;
using HoldTheLine.Rules.State;

namespace HoldTheLine.Rules.Ai;

/// <summary>
/// One-ply heuristic AI shared by the simulator and the in-game opponent (plan §6/P4). Scores every
/// legal command by its immediate value — kill &gt; trade up &gt; push toward the enemy home row — and
/// takes the best. Crude but symmetric: it punishes obvious blunders and beats a careless human,
/// which is all the prototype needs. Deterministic given the state (tie-breaks seed off the state).
///
/// X2.1 taught it the new-faction primitives (docs/06 §5.2): battlecry damage aims at enemies, 贯穿
/// counts the line hit, cell/cross AOEs subtract friendly-fire, 消灭 only sacrifices cheap/dying bodies,
/// and any order is worth more while on-cast engines (ally_order_played) sit in play. The second batch
/// (docs/10 §6#4): 灼蚀 (sear) scores full value vs 坚守, self_moved makes a move worth its payoff, and
/// buffing all_ally_emplacements scales with the turrets already down.
/// </summary>
public static class GreedyAi
{
    /// <summary>Enumerates legal commands and returns the best. Never null while it is the seat's turn.</summary>
    public static Command Pick(GameState state, CardDatabase db, LeaderDatabase leaders)
    {
        var legal = CommandEnumerator.LegalCommands(state, db, leaders);
        return Pick(state, db, leaders, legal);
    }

    /// <summary>Back-compat overload without leader data — leader skills fall back to a flat score.</summary>
    public static Command Pick(GameState state, CardDatabase db, IReadOnlyList<Command> legal)
        => Pick(state, db, LeaderDatabase.Empty, legal);

    public static Command Pick(GameState state, CardDatabase db, LeaderDatabase leaders, IReadOnlyList<Command> legal)
    {
        if (legal.Count == 0)
            return new EndTurnCommand { Seat = state.ActiveSeat };

        // Deterministic tie-break jitter: varies per state so repeated picks in one turn don't lock up.
        ulong seed = (ulong)(state.TurnNumber * 7919L + state.EventSequence * 31L + state.Units.Count + 1);
        var rng = new DeterministicRng(seed);

        Command best = legal[^1]; // EndTurn is always last
        double bestScore = double.NegativeInfinity;
        foreach (var c in legal)
        {
            double score = Score(state, db, leaders, c) + rng.NextInt(100) * 0.001;
            if (score > bestScore) { bestScore = score; best = c; }
        }
        return best;
    }

    private static double Score(GameState s, CardDatabase db, LeaderDatabase leaders, Command c)
    {
        switch (c)
        {
            case EndTurnCommand:
                return 0.05;

            case AttackCommand a:
                return ScoreAttack(s, a);

            case MoveUnitCommand m:
            {
                var unit = s.FindUnit(m.UnitEntityId)!;
                int enemyHome = BoardGeometry.EnemyHomeRow(m.Seat);
                int progress = Math.Abs(unit.Cell.Row - enemyHome) - Math.Abs(m.To.Row - enemyHome);
                double score = progress * 1.5 + 0.2;
                if (unit.HasKeyword(Keyword.Garrison) && unit.Cell.Row == BoardGeometry.HomeRow(m.Seat) && m.To.Row != unit.Cell.Row)
                    score -= 4; // leaving the home row drops the garrison bonus

                // docs/21 §5: steer clear of a REVEALED trap (unrevealed ones are invisible to the AI — fair)
                // and of smoke (a unit that ends there can't attack). Unrevealed traps aren't in the AI's view.
                if (s.CellStates.Any(cs => cs.Kind == "trap" && cs.Revealed && cs.Cell == m.To))
                    score -= 6;
                if (s.IsSmoked(m.To))
                    score -= 3;

                // 压力潮汐 awareness: once the tide is live (or one round out), being our ONLY unit in
                // the enemy half is worth real leader HP — crossing in stops the bleed, retreating
                // restarts it. Without this the bots race the tide instead of fighting (X2.1 gap).
                int round = (s.TurnNumber + 1) / 2;
                if (round >= TurnFlow.PressureTideStartRound - 1)
                {
                    bool othersPress = s.Units.Any(u =>
                        u.OwnerSeat == m.Seat && u.EntityId != unit.EntityId && !BoardGeometry.InOwnHalf(m.Seat, u.Cell));
                    if (!othersPress)
                    {
                        double tideWorth = Math.Min(10, 4 + (round - TurnFlow.PressureTideStartRound) * 2);
                        bool inEnemyHalfNow = !BoardGeometry.InOwnHalf(m.Seat, unit.Cell);
                        bool inEnemyHalfAfter = !BoardGeometry.InOwnHalf(m.Seat, m.To);
                        if (!inEnemyHalfNow && inEnemyHalfAfter) score += tideWorth;
                        else if (inEnemyHalfNow && !inEnemyHalfAfter) score -= tideWorth;
                    }
                }

                // self_moved (游群): moving IS the payoff. A per-move self-buff makes the step worth more;
                // a move-ping is worth the enemies it will hit from the DESTINATION cell (where it fires).
                foreach (var e in db.Get(unit.CardId).Effects.Where(e => e.Trigger == "self_moved"))
                    score += e.Action switch
                    {
                        // Atk gains stop at the per-turn cap (0.6.0) — a capped-out step is worth only its Hp part.
                        "buff" when e.Target == "self" =>
                            ((unit.SelfMovedAtkGainsThisTurn >= Engine.Resolver.SelfMovedAtkGainCap ? 0 : e.Atk) + e.Hp) * 1.2,
                        ("damage" or "sear") when e.Target == "adjacent_enemies" =>
                            BoardGeometry.AdjacentCells(m.To).Select(s.UnitAt)
                                .Count(x => x != null && x.OwnerSeat != m.Seat) * e.Amount * 1.5,
                        _ => 0.5,
                    };
                return score;
            }

            case UseLeaderSkillCommand ls:
                return ScoreLeaderSkill(s, db, leaders, ls);

            case PlayCardCommand p:
            {
                var def = db.Get(s.Player(p.Seat).Hand.First(h => h.EntityId == p.CardEntityId).CardId);
                return def.Type == CardType.Unit ? ScoreDeploy(s, db, p, def) : ScoreOrder(s, db, p, def);
            }

            default:
                return -1000; // Concede — never
        }
    }

    private static double ScoreAttack(GameState s, AttackCommand a)
    {
        var attacker = s.FindUnit(a.AttackerEntityId)!;
        if (a.TargetLeader)
            return 1000 + attacker.Atk * 10;

        var target = s.FindUnit(a.TargetUnitId!.Value)!;
        bool melee = !attacker.HasKeyword(Keyword.Range) || attacker.KeywordValue(Keyword.Range) == 0;

        int myDmg = EstimateDamage(s, attacker, target, melee);
        bool kills = myDmg >= target.CurrentHp;

        // Retaliation lands whenever the target can reach the attacker's cell (matches Resolver.ReachesCell):
        // melee attackers are always in reach; ranged ones only when inside the target's range/adjacency.
        bool retaliated = !attacker.HasKeyword(Keyword.CheapShot) && target.Atk > 0 && Reaches(target, attacker.Cell);
        int retDmg = retaliated ? EstimateDamage(s, target, attacker, melee: true) : 0;
        bool iDie = retDmg >= attacker.CurrentHp;

        double score = myDmg * 2
            + (kills ? UnitValue(target) * 3 : 0)
            - retDmg * 1.5
            - (iDie ? UnitValue(attacker) * 3 : 0);

        // 贯穿: a straight-line ranged shot also strikes the cell directly behind the target (friend or foe).
        if (attacker.HasKeyword(Keyword.Pierce) && !melee
            && BoardGeometry.StepBeyond(attacker.Cell, target.Cell) is { } behind
            && BoardGeometry.IsInside(behind) && s.UnitAt(behind) is { } bu)
        {
            score += DamageValue(s, a.Seat, bu, attacker.Atk);
        }
        return score;
    }

    private static double ScoreDeploy(GameState s, CardDatabase db, PlayCardCommand p, CardDefinition def)
    {
        double score = 4 + def.Cost * 2; // board presence
        if (def.HasKeyword(Keyword.Emplacement))
            score -= 0.5; // slight discount: can't reposition/dodge the tide — but the supermodel body is the point

        foreach (var e in def.Effects.Where(e => e.Trigger == "battlecry"))
            score += ScoreEffect(s, db, p.Seat, e, p.TargetUnitId, p.TargetCell, def.Cost);
        return score;
    }

    private static double ScoreOrder(GameState s, CardDatabase db, PlayCardCommand p, CardDefinition def)
    {
        double score = 0;
        int deepen = 0;
        int manaSaved = 0;
        if (p.ChannelerUnitId is { } channelerId && s.FindUnit(channelerId) is { } channeler
            && channeler.OwnerSeat == p.Seat && EffectEngine.IsKindleDamageOrder(def))
        {
            // Score the complete play, not just its target. Different channelers can change both the
            // resolved damage and the mana left for the rest of the turn.
            deepen = EffectEngine.ChannelEffectAmount(db, channeler, "deepen");
            int discount = EffectEngine.ChannelEffectAmount(db, channeler, "discount");
            manaSaved = discount > 0 ? def.Cost - Math.Max(1, def.Cost - discount) : 0;
        }
        foreach (var e in def.Effects.Where(e => e.Trigger == "play"))
            score += ScoreEffect(s, db, p.Seat, e, p.TargetUnitId, p.TargetCell, def.Cost, deepen);

        // One saved mana is meaningfully better than deterministic tie jitter, while damage remains
        // the dominant consideration when a deepen channeler changes a kill breakpoint.
        score += manaSaved * 1.25;

        // 教团: any order also fires every friendly on-cast engine, so it is worth more while they are out.
        score += OnCastEngineBonus(s, db, p.Seat);
        return score;
    }

    private static double ScoreLeaderSkill(GameState s, CardDatabase db, LeaderDatabase leaders, UseLeaderSkillCommand ls)
    {
        if (!leaders.TryGet(s.Player(ls.Seat).LeaderId, out var leader))
        {
            var t0 = ls.TargetUnitId is { } id0 ? s.FindUnit(id0) : null;
            return t0 != null && t0.OwnerSeat != ls.Seat ? -100 : 0.8; // no leader data (sim back-compat)
        }

        double score = 0.3; // small base — spending mana on the skill
        foreach (var e in leader.SkillEffects.Where(e => e.Trigger == "leader_skill"))
            score += ScoreEffect(s, db, ls.Seat, e, ls.TargetUnitId, ls.TargetCell, cost: 2);
        return score;
    }

    /// <summary>Value of one effect resolved with the given targets, from the acting seat's perspective.</summary>
    private static double ScoreEffect(GameState s, CardDatabase db, int seat, EffectSpec e, int? targetUnitId, Cell? targetCell,
        int cost, int spellDamageBonus = 0)
    {
        var target = targetUnitId is { } id ? s.FindUnit(id) : null;
        bool targetIsEnemy = target != null && target.OwnerSeat != seat;
        bool targetIsAlly = target != null && target.OwnerSeat == seat;
        int effectAmount = e.Amount + (e.IsSpellDamage ? spellDamageBonus : 0);

        // 双模式 (docs/21 §1.8): an effect gated to the wrong side won't fire — score it 0 so 焰鞭's two halves
        // don't cancel (its enemy-damage effect must not read as friendly-fire in the friendly-transfer mode).
        if ((e.TargetSide == "enemy" && targetIsAlly) || (e.TargetSide == "ally" && targetIsEnemy))
            return 0;

        switch (e.Action)
        {
            case "damage":
            case "sear": // 灼蚀: same shape as damage, but ignores 坚守 — so it scores full value vs HoldFast prey.
            {
                bool sear = e.Action == "sear";
                switch (e.Target)
                {
                    case "target_unit":
                    case "target_unit_own_half":
                        if (target == null) return 0;
                        return targetIsEnemy ? DamageValue(s, seat, target, effectAmount, sear) : -100;
                    case "column_enemies":
                        return SumDamage(s, seat, s.Units.Where(u => u.OwnerSeat != seat && InCol(u, targetCell)), effectAmount, sear);
                    case "row_enemies":
                        return SumDamage(s, seat, s.Units.Where(u => u.OwnerSeat != seat && InRow(u, targetCell)), effectAmount, sear);
                    case "cell_cross_all":
                        return targetCell is { } cc ? SumDamage(s, seat, CrossUnits(s, cc), effectAmount, sear) : 0;
                    case "unit_cross_all":
                        return target == null ? 0 : SumDamage(s, seat, CrossUnits(s, target.Cell), effectAmount, sear);
                    case "adjacent_enemies":
                        return 1.5; // source-relative on-cast/deathrattle — small flat credit
                    default:
                        return 1;
                }
            }

            case "destroy":
                if (target == null || !targetIsAlly) return -100;
                return SacrificeValue(db, target); // 献祭: only cheap/dying/deathrattle bodies score positive

            case "buff":
                switch (e.Target)
                {
                    case "target_unit":
                    case "target_unit_ally":
                        if (target == null) return 0;
                        return targetIsAlly ? 2 + cost : -100;
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
                        return s.Units.Count(u => u.OwnerSeat == seat && InCol(u, targetCell)) * (e.Atk + e.Hp) * 1.5;
                    case "all_ally_emplacements": // 匠会 阵地 payoff: value scales with turrets already bolted down.
                        return s.Units.Count(u => u.OwnerSeat == seat && u.HasKeyword(Keyword.Emplacement)) * (e.Atk + e.Hp) * 1.5;
                    default:
                        return 1;
                }

            case "heal":
                if (e.Target is "target_unit" or "target_unit_ally")
                {
                    if (target == null) return 0;
                    if (!targetIsAlly) return -50;
                    return Math.Min(target.MaxHp - target.CurrentHp, e.Amount) * 1.2;
                }
                return 1;

            case "grant_keyword":
            case "move_bonus":
            case "boost_range": // 加农校准: +range on an ally — reach from safety, worth a small buff
                if (e.Target is "target_unit" or "target_unit_ally")
                {
                    if (target == null) return 0;
                    if (!targetIsAlly) return -100;
                    // 重新部署 (Mobilized) only matters on an 架设 unit — repositioning a bolted-down turret;
                    // granting it to a mobile unit is inert, so don't waste the card there.
                    if (e.GrantKeyword == Keyword.Mobilized)
                        return target.HasKeyword(Keyword.Emplacement) ? 2 + cost : 0.2;
                    return 2 + cost;
                }
                return 1;

            case "summon":
                return 3 + cost;
            case "draw":
                return e.Amount * 2;
            case "recall_order":
            {
                // Worth a draw per order actually available in our graveyard; nothing to recall = dead text.
                int available = s.Player(seat).Graveyard.Count(id => db.Get(id).Type == CardType.Order);
                return Math.Min(e.Amount, available) * 2;
            }
            case "gain_mana":
                return 0.5;

            // ---- docs/21 §5 new-mechanic scoring (simple heuristics) ----
            case "damage_scatter": // 燔火: `amount` missiles of 1 at random enemies — worth ~ per-missile enemy value.
            {
                int enemies = s.Units.Count(u => u.OwnerSeat != seat);
                return enemies == 0 ? 0 : Math.Min(effectAmount, enemies * 3) * 1.5;
            }
            case "stat_transfer": // 焰鞭 friendly mode: only worth it on a cheap/dying/deathrattle body (SacrificeValue guards it).
                return target == null || !targetIsAlly ? 0 : SacrificeValue(db, target) + 1.5;
            case "place_smoke":
                return 2;   // tempo denial (区内不能攻击/反击)
            case "place_trap":
                return 2;   // board-control setup
            case "add_secret":
                return 2;   // deterrent, constant EV (docs/21 §5 — no game-theory this patch)
            case "amplify_next":
                return 1.5; // banks a bigger 薪炎 order next turn
            case "sacrifice_equip":
                return 2;   // the 熔岩巨剑 payoff (the discard cost is not enumerated this patch)
            default:
                return 1;
        }
    }

    private static double OnCastEngineBonus(GameState s, CardDatabase db, int seat)
    {
        double bonus = 0;
        foreach (var u in s.Units.Where(u => u.OwnerSeat == seat))
            foreach (var e in db.Get(u.CardId).Effects.Where(e => e.Trigger == "ally_order_played"))
                bonus += e.Action switch
                {
                    "buff" => (e.Atk + e.Hp) * 1.2,
                    ("damage" or "sear") when e.Target == "adjacent_enemies" =>
                        BoardGeometry.AdjacentCells(u.Cell).Select(s.UnitAt)
                            .Count(x => x != null && x.OwnerSeat != seat) * e.Amount * 1.5,
                    "recall_order" => 1.5, // 奥菲兰: a free order back each cast — steady value engine
                    _ => 1.0,
                };
        return bonus;
    }

    // ---- helpers ----

    private static double SacrificeValue(CardDatabase db, UnitInstance ally)
    {
        // Losing the body is a cost; a deathrattle payoff (cinder moth ping, avenger buff, phoenix) offsets it.
        double v = -UnitValue(ally);
        if (ally.CurrentHp <= 1) v += 2; // already almost dead — cheap to spend
        if (db.Get(ally.CardId).Effects.Any(e => e.Trigger == "deathrattle")) v += 3; // deathrattle payoff
        return v;
    }

    private static double SumDamage(GameState s, int seat, IEnumerable<UnitInstance> victims, int amount, bool sear = false) =>
        victims.Sum(u => DamageValue(s, seat, u, amount, sear));

    /// <summary>Signed value of dealing <paramref name="amount"/> to <paramref name="victim"/>: enemies good, friendly fire bad.</summary>
    private static double DamageValue(GameState s, int seat, UnitInstance victim, int amount, bool sear = false)
    {
        int dmg = EffectDamage(victim, amount, sear);
        double v = dmg * 2 + (dmg >= victim.CurrentHp ? UnitValue(victim) * 3 : 0);
        return victim.OwnerSeat == seat ? -v : v;
    }

    private static int EffectDamage(UnitInstance victim, int amount, bool ignoreHoldFast = false)
    {
        if (victim.ShieldActive) return 0;
        int dmg = amount;
        if (victim.HasKeyword(Keyword.Emplacement)) dmg += 1; // bolted down — effect damage +1
        if (!ignoreHoldFast && victim.HasKeyword(Keyword.HoldFast) && !victim.MovedThisRound) dmg -= 1; // 灼蚀 skips this
        return Math.Max(0, dmg);
    }

    private static IEnumerable<UnitInstance> CrossUnits(GameState s, Cell center)
    {
        var cells = new HashSet<Cell>(BoardGeometry.AdjacentCells(center)) { center };
        return s.Units.Where(u => cells.Contains(u.Cell));
    }

    private static bool InCol(UnitInstance u, Cell? cell) => cell is { } c && u.Cell.Col == c.Col;
    private static bool InRow(UnitInstance u, Cell? cell) => cell is { } c && u.Cell.Row == c.Row;

    private static bool Reaches(UnitInstance u, Cell cell)
    {
        int r = u.HasKeyword(Keyword.Range) ? u.KeywordValue(Keyword.Range) : 0;
        return r == 0 ? BoardGeometry.AreAdjacent(u.Cell, cell) : BoardGeometry.StepDistance(u.Cell, cell) <= r;
    }

    // Mirrors the resolver's damage pipeline closely enough for one-ply scoring:
    // shield eats the whole instance, hold-fast -1 while stationary, pack-tactics +1 on flanked prey.
    private static int EstimateDamage(GameState s, UnitInstance attacker, UnitInstance target, bool melee)
    {
        if (target.ShieldActive)
            return 0;
        int dmg = attacker.Atk;
        if (melee && attacker.HasKeyword(Keyword.PackTactics)
            && BoardGeometry.AdjacentCells(target.Cell).Select(s.UnitAt)
                .Any(u => u != null && u.OwnerSeat == attacker.OwnerSeat && u.EntityId != attacker.EntityId))
            dmg += 2;
        if (target.HasKeyword(Keyword.HoldFast) && !target.MovedThisRound)
            dmg -= 1;
        return Math.Max(0, dmg);
    }

    private static double UnitValue(UnitInstance u) => u.Atk * 1.5 + u.CurrentHp;
}
