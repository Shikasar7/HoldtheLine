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
    public static List<Command> LegalCommands(GameState state, CardDatabase db)
    {
        var resolver = new Resolver(db);
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
                    for (int col = 0; col < BoardGeometry.Cols; col++)
                    {
                        var cell = new Cell(col, homeRow);
                        if (state.UnitAt(cell) != null)
                            continue;
                        if (def.Effects.Any(e => e.Trigger == "battlecry" && e.Target == "target_unit"))
                            candidates.AddRange(state.Units.Select(u => (Command)new PlayCardCommand
                                { Seat = seat, CardEntityId = card.EntityId, TargetCell = cell, TargetUnitId = u.EntityId }));
                        else
                            candidates.Add(new PlayCardCommand { Seat = seat, CardEntityId = card.EntityId, TargetCell = cell });
                    }
                    break;

                case CardType.Order:
                    if (def.Effects.Any(e => e.Trigger == "play" && e.Target == "target_unit"))
                        candidates.AddRange(state.Units.Select(u => (Command)new PlayCardCommand
                            { Seat = seat, CardEntityId = card.EntityId, TargetUnitId = u.EntityId }));
                    else
                        candidates.Add(new PlayCardCommand { Seat = seat, CardEntityId = card.EntityId });
                    break;
            }
        }

        foreach (var unit in state.Units.Where(u => u.OwnerSeat == seat))
        {
            foreach (var to in BoardGeometry.AdjacentCells(unit.Cell))
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

        var legal = candidates.Where(c => resolver.Execute(state, c).Success).ToList();
        legal.Add(new EndTurnCommand { Seat = seat });
        return legal;
    }
}
