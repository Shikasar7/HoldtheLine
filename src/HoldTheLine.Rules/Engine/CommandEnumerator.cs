using HoldTheLine.Rules.Cards;
using HoldTheLine.Rules.Commands;
using HoldTheLine.Rules.Geometry;
using HoldTheLine.Rules.State;

namespace HoldTheLine.Rules.Engine;

/// <summary>
/// Enumerates legal commands for the active seat. Candidates are generated cheaply, then
/// confirmed by dry-running the resolver — legality has exactly one definition (the resolver),
/// so this can never drift from the actual rules. Powers the simulator now and the heuristic AI
/// in P4. Fine at prototype scale; optimize with a real validator split only if profiling says so.
/// </summary>
public static class CommandEnumerator
{
    public static List<Command> LegalCommands(GameState state, CardDatabase db, LeaderDatabase? leaders = null)
    {
        leaders ??= LeaderDatabase.Empty;
        var resolver = new Resolver(db, leaders);
        var candidates = new List<Command>();
        int seat = state.ActiveSeat;
        var player = state.Player(seat);

        foreach (var card in player.Hand)
        {
            var def = db.Get(card.CardId);
            if (def.Cost > player.Mana)
                continue;

            switch (def.Type)
            {
                case CardType.Unit:
                    int homeRow = BoardGeometry.HomeRow(seat);
                    bool needsUnit = def.Effects.Any(e => e.Trigger == "battlecry" && e.NeedsUnitTarget);
                    for (int col = 0; col < BoardGeometry.Cols; col++)
                    {
                        var cell = new Cell(col, homeRow);
                        if (state.UnitAt(cell) != null)
                            continue;
                        if (needsUnit)
                            candidates.AddRange(state.Units.Select(u => (Command)new PlayCardCommand
                                { Seat = seat, CardEntityId = card.EntityId, TargetCell = cell, TargetUnitId = u.EntityId }));
                        else
                            candidates.Add(new PlayCardCommand { Seat = seat, CardEntityId = card.EntityId, TargetCell = cell });
                    }
                    break;

                case CardType.Order:
                    candidates.AddRange(OrderTargets(state, seat, card.EntityId, def));
                    break;
            }
        }

        foreach (var unit in state.Units.Where(u => u.OwnerSeat == seat))
        {
            foreach (var to in MoveDestinations(unit))
                candidates.Add(new MoveUnitCommand { Seat = seat, UnitEntityId = unit.EntityId, To = to });

            foreach (var enemy in state.Units.Where(u => u.OwnerSeat != seat))
                candidates.Add(new AttackCommand
                {
                    Seat = seat,
                    AttackerEntityId = unit.EntityId,
                    TargetUnitId = enemy.EntityId,
                    OccupyCellOnKill = unit.HasKeyword(Keyword.Trample),
                });

            candidates.Add(new AttackCommand { Seat = seat, AttackerEntityId = unit.EntityId, TargetLeader = true });
        }

        if (leaders.TryGet(player.LeaderId, out var leader) && !player.LeaderSkillUsedThisTurn && player.Mana >= leader.SkillCost)
            candidates.AddRange(LeaderSkillTargets(state, seat, leader));

        var legal = candidates.Where(c => resolver.Execute(state, c).Success).ToList();
        legal.Add(new EndTurnCommand { Seat = seat });
        return legal;
    }

    private static IEnumerable<Command> OrderTargets(GameState state, int seat, int cardEntityId, CardDefinition def)
    {
        bool needsUnit = def.Effects.Any(e => e.Trigger == "play" && e.NeedsUnitTarget);
        bool needsCell = def.Effects.Any(e => e.Trigger == "play" && e.NeedsCellTarget);

        if (needsUnit)
            return state.Units.Select(u => (Command)new PlayCardCommand
                { Seat = seat, CardEntityId = cardEntityId, TargetUnitId = u.EntityId });

        if (needsCell)
            return AllBoardCells().Select(cell => (Command)new PlayCardCommand
                { Seat = seat, CardEntityId = cardEntityId, TargetCell = cell });

        return [new PlayCardCommand { Seat = seat, CardEntityId = cardEntityId }];
    }

    private static IEnumerable<Command> LeaderSkillTargets(GameState state, int seat, LeaderDefinition leader)
    {
        if (leader.SkillNeedsUnitTarget)
            return state.Units.Select(u => (Command)new UseLeaderSkillCommand { Seat = seat, TargetUnitId = u.EntityId });
        if (leader.SkillNeedsCellTarget)
            return AllBoardCells().Select(cell => (Command)new UseLeaderSkillCommand { Seat = seat, TargetCell = cell });
        return [new UseLeaderSkillCommand { Seat = seat }];
    }

    /// <summary>Every cell on the board. Cell-target orders/skills (cell_cross_all, row_enemies, …) may point
    /// anywhere; the resolver is the final arbiter of legality, so over-generating candidates is safe.</summary>
    private static IEnumerable<Cell> AllBoardCells()
    {
        for (int row = 0; row < BoardGeometry.Rows; row++)
            for (int col = 0; col < BoardGeometry.Cols; col++)
                yield return new Cell(col, row);
    }

    private static IEnumerable<Cell> MoveDestinations(UnitInstance unit)
    {
        if (unit.HasKeyword(Keyword.Emplacement))
            yield break; // 架设: pinned to its cell — never enumerate a move for it.

        foreach (var c in BoardGeometry.AdjacentCells(unit.Cell))
            yield return c;

        if (unit.HasKeyword(Keyword.Leap))
        {
            Cell[] jumps =
            [
                new(unit.Cell.Col + 2, unit.Cell.Row), new(unit.Cell.Col - 2, unit.Cell.Row),
                new(unit.Cell.Col, unit.Cell.Row + 2), new(unit.Cell.Col, unit.Cell.Row - 2),
            ];
            foreach (var c in jumps)
                if (BoardGeometry.IsInside(c))
                    yield return c;
        }
    }
}
