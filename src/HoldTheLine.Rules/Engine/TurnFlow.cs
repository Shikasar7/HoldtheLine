using HoldTheLine.Rules.Events;

namespace HoldTheLine.Rules.Engine;

/// <summary>Turn start sequence shared by game creation and EndTurn resolution (GDD §2.3).</summary>
internal static class TurnFlow
{
    public const int ManaMaxCap = 10;

    /// <summary>Advances to the given seat's turn: TurnNumber++, mana ramp+refill, unit action reset, draw 1.</summary>
    public static void StartTurn(ResolutionContext ctx, int seat)
    {
        var state = ctx.State;
        state.TurnNumber++;
        state.ActiveSeat = seat;

        var player = state.Player(seat);
        player.ManaMax = Math.Min(ManaMaxCap, player.ManaMax + 1);
        player.Mana = player.ManaMax;
        player.LeaderSkillUsedThisTurn = false;

        foreach (var unit in state.Units.Where(u => u.OwnerSeat == seat))
        {
            unit.MovementUsed = 0;
            unit.AttacksUsed = 0;
            unit.MovedThisRound = false;
            unit.BonusMovement = 0;
        }

        // "Until your next turn" grants (e.g. 筑垒) expire now; then re-check Garrison in case a grant lapsed.
        ctx.ExpireYourNextTurnGrants(seat);
        foreach (var unit in state.Units)
            ctx.RecomputeGarrison(unit);

        ctx.Emit(new TurnStartedEvent
        {
            Seat = seat,
            TurnNumber = state.TurnNumber,
            Mana = player.Mana,
            ManaMax = player.ManaMax,
        });

        ctx.DrawCards(seat, 1);
    }
}
