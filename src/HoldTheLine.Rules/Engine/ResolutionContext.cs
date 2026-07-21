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
    // 9 gives the second player (6-card opener + 军令硬币 = 7) two draws of slack before hoarding starts
    // burning cards, while still capping the 教团 recall engines below the old Hearthstone-style 10 (0.5.0).
    private const int MaxHandSize = 9;
    private const int ManaCap = 10;
    private const int DeathCascadeLimit = 100;

    /// <summary>归魂 (docs/21 §1.4): max ally_died_your_turn firings per unit per turn.</summary>
    public const int SoulReturnCap = 2;

    /// <summary>自体成长上限 (docs/21 §1.9): max capped ally_order_played self-growths per unit per turn.</summary>
    public const int OrderGrowthCap = 2;

    /// <summary>烬火陷阱 (docs/21 §1.7): 薪炎 灼蚀 dealt per trigger, and how many turns its fire burns after reveal.</summary>
    public const int TrapSearDamage = 3;
    public const int TrapBurnTurns = 2;

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

    /// <summary>Applies damage with 守护/坚守/福泽/持盾 semantics. Does NOT sweep deaths — callers batch that via
    /// ProcessDeaths so simultaneous strikes resolve simultaneously. <paramref name="ignoreHoldFast"/> is set
    /// by 灼蚀 (sear): the 坚守 reduction is skipped, but 福泽/持盾 are unchanged (docs/10 §6#2).</summary>
    /// <param name="guardRedirected">True only for the recursive call that lands redirected damage on a 守护
    /// guardian: it does NOT redirect again (no loop) and every event it emits is tagged 守护 for the client.</param>
    public void DamageUnit(UnitInstance target, int amount, bool ignoreHoldFast = false, bool guardRedirected = false)
    {
        // 守护 (Guardian): a real hit (amount > 0) on a unit with an adjacent friendly guardian is soaked by
        // that guardian instead — through ITS own reductions. The spared target shows 守护-0; the guardian's
        // own DamageUnit shows 守护-<actual>. Only the original target redirects (guardRedirected guards the loop).
        if (!guardRedirected && amount > 0 && GuardianFor(target) is { } guardian)
        {
            Emit(new UnitDamagedEvent { UnitEntityId = target.EntityId, Amount = 0, NewHp = target.CurrentHp, GuardRedirect = true });
            DamageUnit(guardian, amount, ignoreHoldFast, guardRedirected: true);
            return;
        }

        if (!ignoreHoldFast && target.HasKeyword(Keyword.HoldFast) && !target.MovedThisRound)
            amount = Math.Max(0, amount - 1);

        // 福泽 (Blessing): an adjacent friendly aura shaves 1 more off (stacks with 坚守; sear does not skip it).
        if (HasBlessingAura(target))
            amount = Math.Max(0, amount - 1);

        if (amount <= 0)
        {
            Emit(new UnitDamagedEvent { UnitEntityId = target.EntityId, Amount = 0, NewHp = target.CurrentHp, GuardRedirect = guardRedirected });
            return;
        }

        if (target.ShieldActive)
        {
            target.ShieldActive = false;
            Emit(new UnitDamagedEvent { UnitEntityId = target.EntityId, Amount = 0, NewHp = target.CurrentHp, ShieldAbsorbed = true, GuardRedirect = guardRedirected });
            return;
        }

        target.CurrentHp -= amount;
        Emit(new UnitDamagedEvent { UnitEntityId = target.EntityId, Amount = amount, NewHp = target.CurrentHp, GuardRedirect = guardRedirected });
    }

    /// <summary>The friendly 守护 guardian that soaks damage aimed at <paramref name="target"/>: an orthogonally
    /// adjacent ally (never the target itself) with 守护. Deterministic (first in Units order). Null if none.</summary>
    private UnitInstance? GuardianFor(UnitInstance target) =>
        BoardGeometry.AdjacentCells(target.Cell)
            .Select(State.UnitAt)
            .FirstOrDefault(u => u != null && u.OwnerSeat == target.OwnerSeat
                && u.EntityId != target.EntityId && u.HasKeyword(Keyword.Guardian));

    /// <summary>Whether an orthogonally adjacent friendly unit carries 福泽 (so <paramref name="target"/> takes
    /// 1 less damage). The unit's own 福泽 never counts — the aura helps neighbours, not the source.</summary>
    private bool HasBlessingAura(UnitInstance target) =>
        BoardGeometry.AdjacentCells(target.Cell)
            .Select(State.UnitAt)
            .Any(u => u != null && u.OwnerSeat == target.OwnerSeat && u.HasKeyword(Keyword.Blessing));

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

            // 归魂 (docs/21 §1.4): during its owner's turn, each friendly death in this sweep feeds that seat's
            // surviving 归魂 units 1 辉尘, capped per unit per turn. Deathrattle-chained deaths surface in the
            // next cascade iteration and feed it there.
            int friendlyDeaths = dead.Count(u => u.OwnerSeat == State.ActiveSeat);
            if (friendlyDeaths > 0)
                FireSoulReturn(State.ActiveSeat, friendlyDeaths);
        }
        throw new InvalidOperationException($"Death cascade exceeded {DeathCascadeLimit} iterations — infinite loop in card effects?");
    }

    /// <summary>Hands <paramref name="deathCount"/> 辉尘 to each surviving 归魂 unit of <paramref name="seat"/>,
    /// capped at <see cref="SoulReturnCap"/> firings per unit per turn (docs/21 §1.4). Called from the death
    /// sweep, so the effect runs through the normal trigger path (gain_mana adds no deaths → no re-entrancy).</summary>
    private void FireSoulReturn(int seat, int deathCount)
    {
        var sources = State.Units
            .Where(u => u.OwnerSeat == seat && Db.Get(u.CardId).Effects.Any(e => e.Trigger == "ally_died_your_turn"))
            .ToList();
        foreach (var unit in sources)
        {
            var def = Db.Get(unit.CardId);
            for (int i = 0; i < deathCount && unit.SoulReturnGainsThisTurn < SoulReturnCap; i++)
            {
                EffectEngine.RunTrigger(this, unit, seat, def.Effects, "ally_died_your_turn", targetUnitId: null);
                unit.SoulReturnGainsThisTurn++;
            }
        }
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
                // Overflow no longer removes the card from play — it goes to the graveyard (0.7.0). Still
                // reported via CardBurnedEvent (the "couldn't hold it" beat); the card is now recyclable
                // (an overdrawn Order can be fished back by 火种循环).
                player.Graveyard.Add(card.CardId);
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
                // Overflow → graveyard (0.7.0): the recalled order simply stays in the graveyard (removed
                // above, re-added here) rather than leaving the game, so it remains recyclable next time.
                player.Graveyard.Add(pick);
                Emit(new CardBurnedEvent { Seat = seat, CardId = pick });
                continue;
            }

            var card = new CardInstance { EntityId = State.TakeEntityId(), CardId = pick };
            player.Hand.Add(card);
            Emit(new CardDrawnEvent { Seat = seat, CardEntityId = card.EntityId, CardId = pick });
        }
    }

    /// <summary>Hands the 军令硬币 to <paramref name="seat"/> (no-op when the config carries no coin). Shared by
    /// game creation and mulligan completion, where the coin is deferred past the 起手重抽 phase (docs/11 D7).</summary>
    public void GiveCoin(int seat, string coinCardId)
    {
        if (coinCardId.Length == 0)
            return;
        var coin = new CardInstance { EntityId = State.TakeEntityId(), CardId = coinCardId };
        State.Player(seat).Hand.Add(coin);
        Emit(new CardDrawnEvent { Seat = seat, CardEntityId = coin.EntityId, CardId = coin.CardId });
    }

    public void GainMana(int seat, int amount)
    {
        var player = State.Player(seat);
        player.Mana = Math.Min(ManaCap, player.Mana + amount);
        Emit(new ManaGainedEvent { Seat = seat, Amount = amount, NewMana = player.Mana });
    }

    /// <summary>蓄能 (docs/21 §1.3): stack <paramref name="amount"/> onto the seat's spell charge (焰跃术士 战吼).</summary>
    public void AddSpellCharge(int seat, int amount)
    {
        if (amount <= 0)
            return;
        var player = State.Player(seat);
        player.SpellCharge += amount;
        Emit(new SpellChargeChangedEvent { Seat = seat, NewCharge = player.SpellCharge });
    }

    /// <summary>Spends the seat's whole spell charge (a 薪炎 order consumed it).</summary>
    public void ConsumeSpellCharge(int seat)
    {
        var player = State.Player(seat);
        if (player.SpellCharge == 0)
            return;
        player.SpellCharge = 0;
        Emit(new SpellChargeChangedEvent { Seat = seat, NewCharge = 0 });
    }

    // ---- 格子状态: 烟幕 (docs/21 §1.6) ----

    /// <summary>烟幕弹: smoke the target cell and its orthogonal cross (up to 5 cells; edges self-clip). Units
    /// standing on a smoke cell cannot attack and do not retaliate; the zone lapses at the caster's next turn.</summary>
    public void PlaceSmoke(int seat, Cell center)
    {
        var cells = new HashSet<Cell>(BoardGeometry.AdjacentCells(center)) { center };
        foreach (var cell in cells)
            State.CellStates.Add(new CellState { Cell = cell, Kind = "smoke", OwnerSeat = seat, Expiry = "your_next_turn" });
        Emit(new SmokeAppliedEvent { Seat = seat, Center = center, Cells = cells.ToList() });
    }

    /// <summary>Clears <paramref name="seat"/>'s smoke zones at that seat's turn start (docs/21 §1.6).</summary>
    public void ExpireSmoke(int seat)
    {
        var gone = State.CellStates.Where(s => s.Kind == "smoke" && s.OwnerSeat == seat).ToList();
        if (gone.Count == 0)
            return;
        State.CellStates.RemoveAll(s => s.Kind == "smoke" && s.OwnerSeat == seat);
        Emit(new SmokeExpiredEvent { Seat = seat, Cells = gone.Select(s => s.Cell).ToList() });
    }

    // ---- 格子状态: 烬火陷阱 (docs/21 §1.7) ----

    /// <summary>Buries a hidden 烬火陷阱 on <paramref name="cell"/> owned by <paramref name="seat"/> — only that
    /// seat sees it (PlayerView redaction). Legality (empty cell, not the enemy backline) is checked in the
    /// resolver before this runs.</summary>
    public void PlaceTrap(int seat, Cell cell) =>
        State.CellStates.Add(new CellState { Cell = cell, Kind = "trap", OwnerSeat = seat, Hidden = true });

    /// <summary>Entry trigger (docs/21 §1.7): if <paramref name="unit"/> now stands on a trap it takes 薪炎 灼蚀;
    /// the first trigger reveals the trap and lights its 2-turn fire. Re-entering a burning trap deals it again
    /// without resetting the timer. No-op if the unit died en route (e.g. lost 驻防 HP before this check).</summary>
    public void TriggerTrapOnEntry(UnitInstance unit)
    {
        if (State.FindUnit(unit.EntityId) is null)
            return;
        var trap = State.CellStates.FirstOrDefault(s => s.Kind == "trap" && s.Cell == unit.Cell);
        if (trap is null)
            return;
        bool firstTrigger = trap.Hidden;
        if (firstTrigger)
        {
            trap.Hidden = false;
            trap.Revealed = true;
            trap.TurnsLeft = TrapBurnTurns;
        }
        ApplyTrapSear(trap, unit, firstTrigger);
        ProcessDeaths();
    }

    /// <summary>End-of-turn re-tick (docs/21 §1.7): every revealed trap burns its current occupant, then counts
    /// down; the fire is removed when it reaches zero.</summary>
    public void TickTraps()
    {
        foreach (var trap in State.CellStates.Where(s => s.Kind == "trap" && s.Revealed).ToList())
        {
            if (State.UnitAt(trap.Cell) is { } occupant)
                ApplyTrapSear(trap, occupant, revealed: false);
            trap.TurnsLeft--;
            if (trap.TurnsLeft <= 0)
            {
                State.CellStates.Remove(trap);
                Emit(new TrapExpiredEvent { OwnerSeat = trap.OwnerSeat, Cell = trap.Cell });
            }
        }
        ProcessDeaths();
    }

    private void ApplyTrapSear(CellState trap, UnitInstance victim, bool revealed)
    {
        int amount = TrapSearDamage + (victim.HasKeyword(Keyword.Emplacement) ? 1 : 0);
        Emit(new TrapTriggeredEvent
        { OwnerSeat = trap.OwnerSeat, Cell = trap.Cell, VictimUnitId = victim.EntityId, Damage = amount, Revealed = revealed });
        DamageUnit(victim, amount, ignoreHoldFast: true); // 薪炎灼蚀 ignores 坚守 (福泽/守护/持盾 still apply)
    }

    // ---- 秘密区: 焰誓反制 (docs/21 §1.7 / §3.2) ----

    /// <summary>Sets a face-down secret in <paramref name="seat"/>'s 秘密区. The opponent learns only the count.</summary>
    public void AddSecret(int seat, int cardEntityId, string cardId, string kind, int manaSpent)
    {
        var player = State.Player(seat);
        player.Secrets.Add(new Secret { CardId = cardId, Kind = kind });
        Emit(new SecretPlayedEvent
        { Seat = seat, CardEntityId = cardEntityId, CardId = cardId, ManaSpent = manaSpent, SecretCount = player.Secrets.Count });
    }

    /// <summary>焰誓反制 (docs/21 §3.2): if <paramref name="defenderSeat"/> holds a counter_order secret, void the
    /// enemy order that selected its minion, reveal the secret (→ graveyard), and deal its 薪炎 punishment to a
    /// random minion on <paramref name="casterSeat"/>'s side. Returns true when an order was countered.</summary>
    public bool TryTriggerCounterSecret(int defenderSeat, int casterSeat)
    {
        var defender = State.Player(defenderSeat);
        var secret = defender.Secrets.FirstOrDefault(s => s.Kind == "counter_order");
        if (secret is null)
            return false;
        defender.Secrets.Remove(secret);
        defender.Graveyard.Add(secret.CardId);
        Emit(new SecretRevealedEvent { OwnerSeat = defenderSeat, CardId = secret.CardId });
        Emit(new OrderCounteredEvent { OwnerSeat = defenderSeat, CasterSeat = casterSeat });

        // Punishment: the amount rides on the secret card's own add_secret effect (焰誓反制 = 3 薪炎; regular
        // spell damage, NOT 灼蚀, so 坚守 still reduces it). Random live minion on the countered caster's side.
        var spec = Db.Get(secret.CardId).Effects.First(e => e.Action == "add_secret");
        var victims = State.Units.Where(u => u.OwnerSeat == casterSeat && u.CurrentHp > 0).ToList();
        if (victims.Count > 0 && spec.Amount > 0)
        {
            var victim = victims[State.Rng.NextInt(victims.Count)];
            DamageUnit(victim, spec.Amount + (victim.HasKeyword(Keyword.Emplacement) ? 1 : 0));
            ProcessDeaths();
        }
        return true;
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
            TriggerTrapOnEntry(unit); // 烬火陷阱: 含召唤落点 (docs/21 §1.7)
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
            var effects = def.Effects.Where(e => e.Trigger == "ally_order_played").ToList();

            // 自体成长上限 (docs/21 §1.9): a capped self-growth (buff self, not uncapped) stacks at most
            // OrderGrowthCap times per turn — from the 3rd order on, 灰烬侍徒/烬眼先知/烬火唱徒 stop growing,
            // while their non-growth effects and 奥菲兰's uncapped growth keep firing. Mirrors self_moved's cap.
            bool growth = effects.Any(IsCappedSelfGrowth);
            if (growth && unit.OrderGrowthThisTurn >= OrderGrowthCap)
                effects = effects.Where(e => !IsCappedSelfGrowth(e)).ToList();
            else if (growth)
                unit.OrderGrowthThisTurn++;

            if (effects.Count > 0)
                EffectEngine.RunTrigger(this, unit, seat, effects, "ally_order_played", targetUnitId: null);
        }
    }

    private static bool IsCappedSelfGrowth(EffectSpec e) =>
        e.Trigger == "ally_order_played" && e.Action == "buff" && e.Target == "self" && !e.Uncapped;

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
