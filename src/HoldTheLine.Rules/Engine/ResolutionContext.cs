using HoldTheLine.Rules.Cards;
using HoldTheLine.Rules.Events;
using HoldTheLine.Rules.State;

namespace HoldTheLine.Rules.Engine;

/// <summary>
/// Mutable working context for resolving ONE command against a cloned state. All shared
/// mutation helpers (damage, draw, deaths, game end) live here so every code path applies
/// keywords in the same order: HoldFast reduction → Shield absorption → HP loss.
/// </summary>
internal sealed class ResolutionContext
{
    private const int MaxHandSize = 10;
    private const int ManaCap = 10;
    private const int DeathCascadeLimit = 100;

    public GameState State { get; }
    public CardDatabase Db { get; }
    public List<GameEvent> Events { get; } = new();

    public ResolutionContext(GameState state, CardDatabase db)
    {
        State = state;
        Db = db;
    }

    public void Emit(GameEvent e)
    {
        e.Sequence = State.EventSequence++;
        Events.Add(e);
    }

    // ---- damage ----

    /// <summary>Applies damage with HoldFast/Shield semantics. Does NOT sweep deaths — callers batch that via ProcessDeaths so simultaneous strikes resolve simultaneously.</summary>
    public void DamageUnit(UnitInstance target, int amount)
    {
        if (target.HasKeyword(Keyword.HoldFast) && !target.MovedThisRound)
            amount = Math.Max(0, amount - 1);

        if (amount <= 0)
        {
            Emit(new UnitDamagedEvent { UnitEntityId = target.EntityId, Amount = 0, NewHp = target.CurrentHp });
            return;
        }

        if (target.ShieldActive)
        {
            target.ShieldActive = false;
            Emit(new UnitDamagedEvent { UnitEntityId = target.EntityId, Amount = 0, NewHp = target.CurrentHp, ShieldAbsorbed = true });
            return;
        }

        target.CurrentHp -= amount;
        Emit(new UnitDamagedEvent { UnitEntityId = target.EntityId, Amount = amount, NewHp = target.CurrentHp });
    }

    public void DamageLeader(int seat, int amount)
    {
        if (amount <= 0)
            return;
        var player = State.Player(seat);
        player.LeaderHp -= amount;
        Emit(new LeaderDamagedEvent { Seat = seat, Amount = amount, NewHp = player.LeaderHp });
        CheckGameEnd();
    }

    // ---- deaths ----

    /// <summary>
    /// Removes dead units and fires their deathrattles, looping until stable (deathrattles may
    /// kill more units). Removal order within a sweep follows Units list order (deploy order),
    /// which keeps replays deterministic.
    /// </summary>
    public void ProcessDeaths()
    {
        for (int cascade = 0; cascade < DeathCascadeLimit; cascade++)
        {
            var dead = State.Units.Where(u => u.CurrentHp <= 0).ToList();
            if (dead.Count == 0)
                return;

            foreach (var unit in dead)
            {
                State.Units.Remove(unit);
                State.Player(unit.OwnerSeat).Graveyard.Add(unit.CardId);
                Emit(new UnitDiedEvent { UnitEntityId = unit.EntityId, CardId = unit.CardId, Cell = unit.Cell });
            }

            // Deathrattles fire after the whole sweep is removed, in death order.
            foreach (var unit in dead)
            {
                var def = Db.Get(unit.CardId);
                EffectEngine.RunTrigger(this, unit, unit.OwnerSeat, def.Effects, "deathrattle", targetUnitId: null);
            }
        }
        throw new InvalidOperationException($"Death cascade exceeded {DeathCascadeLimit} iterations — infinite loop in card effects?");
    }

    // ---- cards ----

    public void DrawCards(int seat, int count)
    {
        var player = State.Player(seat);
        for (int i = 0; i < count; i++)
        {
            if (player.Deck.Count == 0)
            {
                player.Fatigue++;
                Emit(new FatigueEvent { Seat = seat, Amount = player.Fatigue });
                DamageLeader(seat, player.Fatigue);
                continue;
            }

            var card = player.Deck[^1];
            player.Deck.RemoveAt(player.Deck.Count - 1);

            if (player.Hand.Count >= MaxHandSize)
            {
                Emit(new CardBurnedEvent { Seat = seat, CardId = card.CardId });
                continue;
            }

            player.Hand.Add(card);
            Emit(new CardDrawnEvent { Seat = seat, CardEntityId = card.EntityId, CardId = card.CardId });
        }
    }

    public void GainMana(int seat, int amount)
    {
        var player = State.Player(seat);
        player.Mana = Math.Min(ManaCap, player.Mana + amount);
        Emit(new ManaGainedEvent { Seat = seat, Amount = amount, NewMana = player.Mana });
    }

    // ---- game end ----

    public void CheckGameEnd()
    {
        if (State.Result != null)
            return;

        bool dead0 = State.Player(0).LeaderHp <= 0;
        bool dead1 = State.Player(1).LeaderHp <= 0;
        if (!dead0 && !dead1)
            return;

        int winner = dead0 && dead1 ? -1 : dead0 ? 1 : 0;
        State.Result = new GameResult { WinnerSeat = winner, Reason = dead0 && dead1 ? "draw" : "leader_defeated" };
        Emit(new GameEndedEvent { WinnerSeat = winner, Reason = State.Result.Reason });
    }
}
