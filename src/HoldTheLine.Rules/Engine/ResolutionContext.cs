using HoldTheLine.Rules.Cards;
using HoldTheLine.Rules.Events;
using HoldTheLine.Rules.Geometry;
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

    /// <summary>
    /// 消灭 (destroy): drops the unit straight into the death sweep, bypassing DamageUnit — so 持盾
    /// and 坚守 give no protection. Death itself is emitted by <see cref="ProcessDeaths"/> (UnitDiedEvent),
    /// so this adds no new event type. Deathrattles fire as normal.
    /// </summary>
    public void DestroyUnit(UnitInstance target) => target.CurrentHp = 0;

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

    /// <summary>
    /// 火种循环 (recall_order): moves up to <paramref name="count"/> RANDOM order cards from the
    /// seat's graveyard back to hand. Random pick runs on the match Rng (replay-deterministic);
    /// unit cards in the graveyard are never eligible. Reuses the draw pipeline's hand-limit
    /// semantics (overdraw burns) and the existing CardDrawn/CardBurned events — zero new protocol.
    /// </summary>
    public void RecallOrders(int seat, int count)
    {
        var player = State.Player(seat);
        for (int i = 0; i < count; i++)
        {
            var orders = player.Graveyard.Where(id => Db.Get(id).Type == CardType.Order).ToList();
            if (orders.Count == 0)
                return;

            string pick = orders[State.Rng.NextInt(orders.Count)];
            player.Graveyard.Remove(pick); // first occurrence — ids are interchangeable copies

            if (player.Hand.Count >= MaxHandSize)
            {
                Emit(new CardBurnedEvent { Seat = seat, CardId = pick });
                continue;
            }

            var card = new CardInstance { EntityId = State.TakeEntityId(), CardId = pick };
            player.Hand.Add(card);
            Emit(new CardDrawnEvent { Seat = seat, CardEntityId = card.EntityId, CardId = pick });
        }
    }

    public void GainMana(int seat, int amount)
    {
        var player = State.Player(seat);
        player.Mana = Math.Min(ManaCap, player.Mana + amount);
        Emit(new ManaGainedEvent { Seat = seat, Amount = amount, NewMana = player.Mana });
    }

    // ---- P2 effect mutations ----

    public void HealUnit(UnitInstance target, int amount)
    {
        int before = target.CurrentHp;
        target.CurrentHp = Math.Min(target.MaxHp, target.CurrentHp + Math.Max(0, amount));
        Emit(new UnitHealedEvent { UnitEntityId = target.EntityId, Amount = target.CurrentHp - before, NewHp = target.CurrentHp });
    }

    public void AddMoveBonus(UnitInstance target, int amount)
    {
        target.BonusMovement += amount;
        Emit(new UnitMoveBonusEvent { UnitEntityId = target.EntityId, Amount = amount, NewBonusMovement = target.BonusMovement });
    }

    /// <summary>Grants a keyword permanently or for a limited duration. Shield grants (re)arm the shield charge.</summary>
    public void GrantKeyword(UnitInstance target, Keyword keyword, int value, string duration, int grantedBySeat)
    {
        if (duration == "permanent")
        {
            if (!target.Keywords.Any(s => s.Keyword == keyword && s.Value == value))
                target.Keywords.Add(new KeywordSpec(keyword, value));
        }
        else
        {
            target.TempGrants.Add(new TempKeywordGrant
            {
                Spec = new KeywordSpec(keyword, value),
                Expiry = duration,
                GrantedBySeat = grantedBySeat,
            });
        }

        if (keyword == Keyword.Shield)
            target.ShieldActive = true;

        Emit(new UnitKeywordGrantedEvent { UnitEntityId = target.EntityId, Keyword = keyword, Value = value, Duration = duration });
        RecomputeGarrison(target); // in case Garrison itself was granted
    }

    /// <summary>Summons up to <paramref name="count"/> copies of a card onto the seat's home-row empty cells (west→east).</summary>
    public void SummonUnits(int seat, string cardId, int count)
    {
        var def = Db.Get(cardId);
        int homeRow = BoardGeometry.HomeRow(seat);
        int placed = 0;
        for (int col = 0; col < BoardGeometry.Cols && placed < count; col++)
        {
            var cell = new Cell(col, homeRow);
            if (State.UnitAt(cell) != null)
                continue;

            var unit = new UnitInstance
            {
                EntityId = State.TakeEntityId(),
                CardId = def.Id,
                OwnerSeat = seat,
                Cell = cell,
                Atk = def.Atk,
                MaxHp = def.Hp,
                CurrentHp = def.Hp,
                DeployedOnTurn = State.TurnNumber,
                ShieldActive = def.HasKeyword(Keyword.Shield),
                Keywords = def.Keywords.ToList(),
            };
            State.Units.Add(unit);
            Emit(new UnitDeployedEvent
            {
                Seat = seat, UnitEntityId = unit.EntityId, CardId = def.Id,
                Cell = cell, Atk = unit.Atk, Hp = unit.CurrentHp,
            });
            RecomputeGarrison(unit);
            placed++;
        }
    }

    /// <summary>
    /// 驻防 (Garrison): +1/+1 while on the owner's home row. Toggled symmetrically so damage is
    /// preserved — a garrisoned unit reduced to 1 borrowed HP that then leaves the line dies
    /// (the bonus was holding it together). Idempotent via <see cref="UnitInstance.GarrisonApplied"/>.
    /// </summary>
    public void RecomputeGarrison(UnitInstance unit)
    {
        bool shouldHave = unit.HasKeyword(Keyword.Garrison)
            && unit.Cell.Row == BoardGeometry.HomeRow(unit.OwnerSeat);

        if (shouldHave == unit.GarrisonApplied)
            return;

        int delta = shouldHave ? 1 : -1;
        unit.Atk += delta;
        unit.MaxHp += delta;
        unit.CurrentHp += delta;
        unit.GarrisonApplied = shouldHave;

        Emit(new UnitBuffedEvent
        {
            UnitEntityId = unit.EntityId,
            AtkDelta = delta, HpDelta = delta,
            NewAtk = unit.Atk, NewHp = unit.CurrentHp,
            IsGarrison = true,
        });
    }

    /// <summary>Drops all end-of-turn temporary grants (called when the active player ends their turn).</summary>
    public void ExpireEndOfTurnGrants()
    {
        foreach (var unit in State.Units)
            unit.TempGrants.RemoveAll(g => g.Expiry == "end_of_turn");
    }

    /// <summary>Drops "your next turn" grants owned by <paramref name="seat"/> (called at that seat's turn start).</summary>
    public void ExpireYourNextTurnGrants(int seat)
    {
        foreach (var unit in State.Units)
            unit.TempGrants.RemoveAll(g => g.Expiry == "your_next_turn" && g.GrantedBySeat == seat);
    }

    // ---- 教团触发器: ally_order_played ----

    /// <summary>
    /// Fires every friendly unit's <c>ally_order_played</c> effects after one of <paramref name="seat"/>'s
    /// Order cards has fully resolved (docs/06 §3.1). Units trigger in deploy order (Units list order) so
    /// replays stay deterministic; a trigger that kills a later source removes it before its turn comes
    /// (sweep semantics). The military coin is an Order and so counts; leader skills are not Orders and do not.
    /// </summary>
    public void FireAllyOrderPlayed(int seat)
    {
        var sources = State.Units
            .Where(u => u.OwnerSeat == seat && Db.Get(u.CardId).Effects.Any(e => e.Trigger == "ally_order_played"))
            .ToList();

        foreach (var unit in sources)
        {
            if (State.FindUnit(unit.EntityId) is null)
                continue; // died to an earlier trigger in this pass
            var def = Db.Get(unit.CardId);
            EffectEngine.RunTrigger(this, unit, seat, def.Effects, "ally_order_played", targetUnitId: null);
        }
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
