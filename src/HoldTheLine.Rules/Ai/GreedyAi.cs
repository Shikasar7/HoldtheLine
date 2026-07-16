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
/// </summary>
public static class GreedyAi
{
    /// <summary>Enumerates legal commands and returns the best. Never null while it is the seat's turn.</summary>
    public static Command Pick(GameState state, CardDatabase db, LeaderDatabase leaders)
    {
        var legal = CommandEnumerator.LegalCommands(state, db, leaders);
        return Pick(state, db, legal);
    }

    public static Command Pick(GameState state, CardDatabase db, IReadOnlyList<Command> legal)
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
            double score = Score(state, db, c) + rng.NextInt(100) * 0.001;
            if (score > bestScore) { bestScore = score; best = c; }
        }
        return best;
    }

    private static double Score(GameState s, CardDatabase db, Command c)
    {
        switch (c)
        {
            case EndTurnCommand:
                return 0.05;

            case AttackCommand a:
            {
                var attacker = s.FindUnit(a.AttackerEntityId)!;
                if (a.TargetLeader)
                    return 1000 + attacker.Atk * 10;

                var target = s.FindUnit(a.TargetUnitId!.Value)!;
                bool melee = !attacker.HasKeyword(Keyword.Range) || attacker.KeywordValue(Keyword.Range) == 0;

                int myDmg = EstimateDamage(s, attacker, target, melee);
                bool kills = myDmg >= target.CurrentHp;
                int retDmg = melee && !attacker.HasKeyword(Keyword.CheapShot) && target.Atk > 0
                    ? EstimateDamage(s, target, attacker, melee: true)
                    : 0;
                bool iDie = retDmg >= attacker.CurrentHp;

                return myDmg * 2
                    + (kills ? UnitValue(target) * 3 : 0)
                    - retDmg * 1.5
                    - (iDie ? UnitValue(attacker) * 3 : 0);
            }

            case MoveUnitCommand m:
            {
                var unit = s.FindUnit(m.UnitEntityId)!;
                int enemyHome = BoardGeometry.EnemyHomeRow(m.Seat);
                int progress = Math.Abs(unit.Cell.Row - enemyHome) - Math.Abs(m.To.Row - enemyHome);
                double score = progress * 1.5 + 0.2;
                if (unit.HasKeyword(Keyword.Garrison) && unit.Cell.Row == BoardGeometry.HomeRow(m.Seat) && m.To.Row != unit.Cell.Row)
                    score -= 4; // leaving the home row drops the garrison bonus
                return score;
            }

            case UseLeaderSkillCommand ls:
            {
                var target = ls.TargetUnitId is { } id ? s.FindUnit(id) : null;
                if (target != null && target.OwnerSeat != ls.Seat)
                    return -100; // never aim a friendly skill at the enemy
                return 0.8;
            }

            case PlayCardCommand p:
            {
                var def = db.Get(s.Player(p.Seat).Hand.First(h => h.EntityId == p.CardEntityId).CardId);
                return def.Type == CardType.Unit ? ScoreDeploy(s, p, def) : ScoreOrder(s, p, def);
            }

            default:
                return -1000; // Concede — never
        }
    }

    private static double ScoreDeploy(GameState s, PlayCardCommand p, CardDefinition def)
    {
        // Battlecries that take a unit target (buffs) must aim at a friendly unit.
        if (p.TargetUnitId is { } id && s.FindUnit(id) is { } t && t.OwnerSeat != p.Seat)
            return -100;
        return 4 + def.Cost * 2;
    }

    private static double ScoreOrder(GameState s, PlayCardCommand p, CardDefinition def)
    {
        double score = 0;
        foreach (var e in def.Effects.Where(e => e.Trigger == "play"))
        {
            var target = p.TargetUnitId is { } id ? s.FindUnit(id) : null;
            bool targetIsEnemy = target != null && target.OwnerSeat != p.Seat;

            switch (e.Action)
            {
                case "damage" when e.Target is "target_unit" or "target_unit_own_half":
                    if (!targetIsEnemy) return -100;
                    score += e.Amount * 2 + (e.Amount >= target!.CurrentHp ? UnitValue(target) * 3 : 0);
                    break;
                case "damage" when e.Target == "column_enemies":
                {
                    var hit = s.Units.Where(u => u.OwnerSeat != p.Seat && p.TargetCell is { } cell && u.Cell.Col == cell.Col).ToList();
                    score += hit.Sum(u => e.Amount * 2 + (e.Amount >= u.CurrentHp ? UnitValue(u) * 3 : 0));
                    break;
                }
                case "buff" or "heal" or "grant_keyword" or "move_bonus" when e.Target is "target_unit":
                    if (targetIsEnemy) return -100;
                    score += 2 + def.Cost;
                    break;
                case "buff" when e.Target is "allies_home_row" or "all_allies":
                {
                    int count = e.Target == "all_allies"
                        ? s.Units.Count(u => u.OwnerSeat == p.Seat)
                        : s.Units.Count(u => u.OwnerSeat == p.Seat && u.Cell.Row == BoardGeometry.HomeRow(p.Seat));
                    score += count * (e.Atk + e.Hp) * 1.5;
                    break;
                }
                case "summon":
                    score += 3 + def.Cost;
                    break;
                case "draw":
                    score += e.Amount * 2;
                    break;
                case "gain_mana":
                    score += 0.5;
                    break;
                default:
                    score += 1;
                    break;
            }
        }
        return score;
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
            dmg += 1;
        if (target.HasKeyword(Keyword.HoldFast) && !target.MovedThisRound)
            dmg -= 1;
        return Math.Max(0, dmg);
    }

    private static double UnitValue(UnitInstance u) => u.Atk * 1.5 + u.CurrentHp;
}
