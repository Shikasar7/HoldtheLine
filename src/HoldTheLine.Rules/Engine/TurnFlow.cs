using HoldTheLine.Rules.Events;
using HoldTheLine.Rules.Geometry;

namespace HoldTheLine.Rules.Engine;

/// <summary>Turn start sequence shared by game creation and EndTurn resolution (GDD §2.3).</summary>
internal static class TurnFlow
{
    public const int ManaMaxCap = 10;

    /// <summary>压力潮汐 begins this round (a round = both players' turns). Tuning knob, see GDD §2.7.</summary>
    public const int PressureTideStartRound = 8;

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
        player.FirstKindleOrderDone = false; // 薪火回响 resets each turn (docs/21 §3.1)

        foreach (var unit in state.Units.Where(u => u.OwnerSeat == seat))
        {
            unit.MovementUsed = 0;
            unit.AttacksUsed = 0;
            unit.MovedThisRound = false;
            unit.BonusMovement = 0;
            unit.SelfMovedAtkGainsThisTurn = 0;
            unit.SoulReturnGainsThisTurn = 0;
            unit.OrderGrowthThisTurn = 0;
        }

        // "Until your next turn" grants (e.g. 筑垒) expire now; then re-check Garrison in case a grant lapsed.
        ctx.ExpireYourNextTurnGrants(seat);
        ctx.ExpireSmoke(seat); // 烟幕区 lapses at its caster's next turn (docs/21 §1.6)
        foreach (var unit in state.Units)
            ctx.RecomputeGarrison(unit);

        ctx.Emit(new TurnStartedEvent
        {
            Seat = seat,
            TurnNumber = state.TurnNumber,
            Mana = player.Mana,
            ManaMax = player.ManaMax,
        });

        ApplyPressureTide(ctx, seat);

        // 成长 (docs/21 §1.8): each of your growth units advances one step at your turn start (may transform).
        // Snapshot first — AccelerateGrowth rewrites CardId on transform, which would disturb a live filter.
        foreach (var unit in state.Units.Where(u => u.OwnerSeat == seat && ctx.Db.Get(u.CardId).Growth is not null).ToList())
            ctx.AccelerateGrowth(unit);

        ctx.DrawCards(seat, 1);
    }

    /// <summary>
    /// 压力潮汐 (GDD §2.7, anti-turtle revision): from round <see cref="PressureTideStartRound"/>,
    /// starting your turn with no unit in the ENEMY half bleeds your leader for
    /// (round - start + 1). Turtling stays legal — it just stops being free.
    /// </summary>
    private static void ApplyPressureTide(ResolutionContext ctx, int seat)
    {
        int round = (ctx.State.TurnNumber + 1) / 2;
        if (round < PressureTideStartRound)
            return;
        bool pressing = ctx.State.Units.Any(u => u.OwnerSeat == seat && !BoardGeometry.InOwnHalf(seat, u.Cell));
        if (pressing)
            return;

        int amount = round - PressureTideStartRound + 1;
        ctx.Emit(new PressureTideEvent { Seat = seat, Round = round, Amount = amount });
        ctx.DamageLeader(seat, amount);
    }
}
