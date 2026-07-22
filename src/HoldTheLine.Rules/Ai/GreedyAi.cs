using HoldTheLine.Rules.Cards;
using HoldTheLine.Rules.Commands;
using HoldTheLine.Rules.Engine;
using HoldTheLine.Rules.Engine.Actions;
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

                // S16 (docs/20): the turret is the faction's whole bankroll — never march it forward for
                // generic "progress", and never nominate it as the tide body (班组 cross, the gun stays).
                // It only repositions to bring more enemies into its own firing arc.
                if (unit.Turret is { IsShadow: false })
                {
                    int reach = unit.HasKeyword(Keyword.Range) ? unit.KeywordValue(Keyword.Range) : 1;
                    int inRangeNow = s.Units.Count(u => u.OwnerSeat != m.Seat && BoardGeometry.StepDistance(unit.Cell, u.Cell) <= reach);
                    int inRangeAfter = s.Units.Count(u => u.OwnerSeat != m.Seat && BoardGeometry.StepDistance(m.To, u.Cell) <= reach);
                    double tScore = (inRangeAfter - inRangeNow) * 1.0 - 0.3;
                    if (s.CellStates.Any(cs => cs.Kind == "trap" && cs.Revealed && cs.Cell == m.To))
                        tScore -= 6;
                    if (s.IsSmoked(m.To))
                        tScore -= 3;
                    return tScore;
                }

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
                return def.Type switch
                {
                    CardType.Unit => ScoreDeploy(s, db, p, def),
                    CardType.Equipment => ScoreInstallModule(s, db, p, def), // 掘世匠会 装配 (docs/20)
                    _ => ScoreOrder(s, db, p, def),
                };
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
        // Attack damage — no 架设 +1 (effectDamage false), physical school.
        if (attacker.HasKeyword(Keyword.Pierce) && !melee
            && BoardGeometry.StepBeyond(attacker.Cell, target.Cell) is { } behind
            && BoardGeometry.IsInside(behind) && s.UnitAt(behind) is { } bu)
        {
            score += OutcomeValue(a.Seat, DamageMath.Predict(s, bu, attacker.Atk));
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

    /// <summary>掘世匠会 装配 (docs/20 §S16): value a module by its stat/keyword contribution to the turret, minus the
    /// value of any件 it 顶替. Keeps the AI investing in the turret instead of ending the turn (S16-d 呆滞局零出现).</summary>
    private static double ScoreInstallModule(GameState s, CardDatabase db, PlayCardCommand p, CardDefinition def)
    {
        var turret = s.Units.FirstOrDefault(u => u.OwnerSeat == p.Seat && u.Turret is { IsShadow: false });
        if (turret is null || def.Module is not { } m)
            return -1; // no turret → the enumerator should never offer this
        double v = 3 + m.Atk * 1.5 + m.Hp + m.Range * 1.2 + m.Move * 0.8;
        if (m.GrantKeywords.Contains(Keyword.Pierce)) v += 1.5;
        v += m.OnHit switch { "blast" => 2.5, "frag" or "split" => 1.5, "concussion" => 1.0, _ => 0 };
        v += m.Lifesteal switch { "half" => 2, "fixed" => 1, _ => 0 };
        if (m.ExtraAttacks > 0) v += turret.Atk * 1.2;  // 快装 doubles the turret's output
        if (m.Deathrattle == "failsafe_pod") v += 2;    // insurance against a lost investment
        // S16-b: 模块价值 × 炮台存活预期 — a gun about to die is a bad place for more attack; hp件 first.
        if (turret.CurrentHp <= 2 && m.Hp == 0) v -= 2.5;
        if (turret.CurrentHp <= 2 && m.Hp > 0) v += 2;
        // 顶替 (docs/20 §S2): scrapping a decent module is a real cost.
        if (p.ReplacedModuleCardId is { } scrap && db.Get(scrap).Module is { } old)
            v -= old.Atk * 1.5 + old.Hp + old.Range * 1.2;
        return v;
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

    /// <summary>Value of one effect resolved with the given targets, from the acting seat's perspective.
    /// Per-action pricing lives on the action handlers (Engine/Actions, docs/22 D1); this computes the
    /// shared inputs and dispatches. An unregistered action throws (was a silent default-1): load-time
    /// validation guarantees every data-borne action is registered, so reaching one here is a bug.</summary>
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

        return EffectActionRegistry.Get(e.Action)
            .Score(new EffectScoreArgs(s, db, seat, e, target, targetIsEnemy, targetIsAlly, targetCell, cost, effectAmount));
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

    // internal (not private): shared with the per-action score handlers in Engine/Actions (docs/22 D1).
    internal static double SacrificeValue(CardDatabase db, UnitInstance ally)
    {
        // Losing the body is a cost; a deathrattle payoff (cinder moth ping, avenger buff, phoenix) offsets it.
        double v = -UnitValue(ally);
        if (ally.CurrentHp <= 1) v += 2; // already almost dead — cheap to spend
        if (db.Get(ally.CardId).Effects.Any(e => e.Trigger == "deathrattle")) v += 3; // deathrattle payoff
        return v;
    }

    internal static double SumDamage(GameState s, int seat, IEnumerable<UnitInstance> victims, int amount, bool sear = false, string school = "physical") =>
        victims.Sum(u => DamageValue(s, seat, u, amount, sear, school));

    /// <summary>Signed value of dealing <paramref name="amount"/> of EFFECT damage to <paramref name="victim"/>,
    /// scored on where it actually lands via the engine's own <see cref="DamageMath.Predict"/> (架设 +1, 免疫薪炎,
    /// 守护 redirect, 坚守, 福泽, 持盾): enemies good, friendly fire bad. Replaces the old hand-mirrored
    /// EffectDamage, which ignored 福泽 and 守护 and so systematically overvalued hitting blessed/guarded units.</summary>
    internal static double DamageValue(GameState s, int seat, UnitInstance victim, int amount, bool sear = false, string school = "physical") =>
        OutcomeValue(seat, DamageMath.Predict(s, victim, amount, ignoreHoldFast: sear, school: school, effectDamage: true));

    /// <summary>Score a predicted landing chain from <paramref name="seat"/>'s perspective: damage dealt plus a
    /// kill bonus, signed per ACTUAL recipient (a 守护 redirect onto an ally's guardian is still friendly fire).</summary>
    private static double OutcomeValue(int seat, List<DamageOutcome> outcomes)
    {
        double v = 0;
        foreach (var o in outcomes)
        {
            double worth = o.Amount * 2
                + (o.Kind == DamageOutcomeKind.HpLoss && o.Amount >= o.Victim.CurrentHp ? UnitValue(o.Victim) * 3 : 0);
            v += o.Victim.OwnerSeat == seat ? -worth : worth;
        }
        return v;
    }

    internal static IEnumerable<UnitInstance> CrossUnits(GameState s, Cell center)
    {
        var cells = new HashSet<Cell>(BoardGeometry.AdjacentCells(center)) { center };
        return s.Units.Where(u => cells.Contains(u.Cell));
    }

    internal static bool InCol(UnitInstance u, Cell? cell) => cell is { } c && u.Cell.Col == c.Col;
    internal static bool InRow(UnitInstance u, Cell? cell) => cell is { } c && u.Cell.Row == c.Row;

    private static bool Reaches(UnitInstance u, Cell cell)
    {
        int r = u.HasKeyword(Keyword.Range) ? u.KeywordValue(Keyword.Range) : 0;
        return r == 0 ? BoardGeometry.AreAdjacent(u.Cell, cell) : BoardGeometry.StepDistance(u.Cell, cell) <= r;
    }

    // Attack-damage estimate: pack-tactics +2 on flanked prey, then the engine's own reduction chain via
    // DamageMath.Predict (守护 redirect spares the target, 坚守/福泽 shave, 持盾 absorbs). Returns the damage
    // the TARGET itself takes — a redirected hit counts 0 here. No 架设 +1: attacks are not effect damage.
    private static int EstimateDamage(GameState s, UnitInstance attacker, UnitInstance target, bool melee)
    {
        int dmg = attacker.Atk;
        if (melee && attacker.HasKeyword(Keyword.PackTactics)
            && BoardGeometry.AdjacentCells(target.Cell).Select(s.UnitAt)
                .Any(u => u != null && u.OwnerSeat == attacker.OwnerSeat && u.EntityId != attacker.EntityId))
            dmg += 2;
        return DamageMath.Predict(s, target, dmg)
            .Where(o => o.Victim.EntityId == target.EntityId)
            .Sum(o => o.Amount);
    }

    // S16-c (docs/20): a live turret is worth its panel PLUS the module investment riding on it —
    // killing it erases every installed件, so both sides must price that in (集火炮台加分).
    private static double UnitValue(UnitInstance u) =>
        u.Atk * 1.5 + u.CurrentHp + (u.Turret is { IsShadow: false } t ? 2 + t.Modules.Count * 1.5 : 0);
}
