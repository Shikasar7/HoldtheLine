using HoldTheLine.Rules.Cards;
using HoldTheLine.Rules.Engine;
using HoldTheLine.Rules.Geometry;
using HoldTheLine.Rules.State;

namespace HoldTheLine.Rules.Tests;

/// <summary>Shared fixtures: a fixed card pool and state builders for direct-to-board scenarios.</summary>
public static class TestKit
{
    public static readonly CardDefinition Vanilla = new()
    { Id = "t_vanilla", Name = "Vanilla 2/3", Cost = 2, Atk = 2, Hp = 3 };

    public static readonly CardDefinition BigVanilla = new()
    { Id = "t_big", Name = "Big 5/6", Cost = 5, Atk = 5, Hp = 6 };

    public static readonly CardDefinition Charger = new()
    { Id = "t_charger", Name = "Charger 2/1", Cost = 2, Atk = 2, Hp = 1, Keywords = [new(Keyword.Charge)] };

    public static readonly CardDefinition Assaulter = new()
    { Id = "t_assault", Name = "Assaulter 2/2", Cost = 2, Atk = 2, Hp = 2, Keywords = [new(Keyword.Assault)] };

    public static readonly CardDefinition Scout = new()
    { Id = "t_scout", Name = "Scout 1/1", Cost = 1, Atk = 1, Hp = 1, Keywords = [new(Keyword.Swift, 2)] };

    public static readonly CardDefinition Archer = new()
    { Id = "t_archer", Name = "Archer 2/2", Cost = 3, Atk = 2, Hp = 2, Keywords = [new(Keyword.Range, 2)] };

    public static readonly CardDefinition GuardUnit = new()
    { Id = "t_guard", Name = "Guard 1/4", Cost = 2, Atk = 1, Hp = 4, Keywords = [new(Keyword.Guard)] };

    public static readonly CardDefinition Holder = new()
    { Id = "t_holder", Name = "Holder 2/4", Cost = 3, Atk = 2, Hp = 4, Keywords = [new(Keyword.HoldFast)] };

    public static readonly CardDefinition Trampler = new()
    { Id = "t_trampler", Name = "Trampler 4/3", Cost = 4, Atk = 4, Hp = 3, Keywords = [new(Keyword.Trample)] };

    public static readonly CardDefinition Sneak = new()
    { Id = "t_sneak", Name = "Sneak 3/2", Cost = 3, Atk = 3, Hp = 2, Keywords = [new(Keyword.CheapShot)] };

    public static readonly CardDefinition Shielded = new()
    { Id = "t_shield", Name = "Shielded 2/2", Cost = 3, Atk = 2, Hp = 2, Keywords = [new(Keyword.Shield)] };

    public static readonly CardDefinition BattlecryBuffer = new()
    {
        Id = "t_buffer", Name = "Buffer 1/1", Cost = 2, Atk = 1, Hp = 1,
        Effects = [new EffectSpec { Trigger = "battlecry", Action = "buff", Target = "adjacent_allies", Hp = 1 }],
    };

    public static readonly CardDefinition Bomber = new()
    {
        Id = "t_bomber", Name = "Bomber 1/1", Cost = 2, Atk = 1, Hp = 1,
        Effects = [new EffectSpec { Trigger = "deathrattle", Action = "damage", Target = "adjacent_enemies", Amount = 2 }],
    };

    public static readonly CardDefinition Coin = new()
    {
        Id = "neutral_coin", Name = "军令硬币", Type = CardType.Order, Rarity = Rarity.Token, Cost = 0,
        Effects = [new EffectSpec { Trigger = "play", Action = "gain_mana", Amount = 1 }],
    };

    public static readonly CardDefinition ZapOrder = new()
    {
        Id = "t_zap", Name = "Zap", Type = CardType.Order, Cost = 1,
        Effects = [new EffectSpec { Trigger = "play", Action = "damage", Target = "target_unit", Amount = 2 }],
    };

    public static readonly CardDefinition DrawOrder = new()
    {
        Id = "t_draw2", Name = "Draw Two", Type = CardType.Order, Cost = 2,
        Effects = [new EffectSpec { Trigger = "play", Action = "draw", Amount = 2 }],
    };

    // ---- P2 fixtures ----

    public static readonly CardDefinition Garrison = new()
    { Id = "t_garrison", Name = "Garrison 2/2", Cost = 2, Atk = 2, Hp = 2, Keywords = [new(Keyword.Garrison)] };

    public static readonly CardDefinition Leaper = new()
    { Id = "t_leaper", Name = "Leaper 3/3", Cost = 3, Atk = 3, Hp = 3, Keywords = [new(Keyword.Leap)] };

    public static readonly CardDefinition PupToken = new()
    { Id = "t_pup", Name = "Pup 1/1", Rarity = Rarity.Token, Cost = 1, Atk = 1, Hp = 1, Keywords = [new(Keyword.Swift, 2)] };

    public static readonly CardDefinition Medic = new()
    {
        Id = "t_medic", Name = "Medic 2/3", Cost = 3, Atk = 2, Hp = 3,
        Effects = [new EffectSpec { Trigger = "battlecry", Action = "heal", Target = "target_unit", Amount = 2 }],
    };

    public static readonly CardDefinition GrantGuardOrder = new()
    {
        Id = "t_grant_guard", Name = "Entrench", Type = CardType.Order, Cost = 2,
        Effects = [new EffectSpec { Trigger = "play", Action = "grant_keyword", Target = "target_unit", GrantKeyword = Keyword.Guard, Duration = "permanent" }],
    };

    public static readonly CardDefinition PounceOrder = new()
    {
        Id = "t_pounce", Name = "Pounce", Type = CardType.Order, Cost = 2,
        Effects = [new EffectSpec { Trigger = "play", Action = "grant_keyword", Target = "target_unit", GrantKeyword = Keyword.CheapShot, Duration = "end_of_turn" }],
    };

    public static readonly CardDefinition GrantShieldOrder = new()
    {
        Id = "t_grant_shield", Name = "Shield Wall", Type = CardType.Order, Cost = 2,
        Effects = [new EffectSpec { Trigger = "play", Action = "grant_keyword", Target = "target_unit", GrantKeyword = Keyword.Shield, Duration = "permanent" }],
    };

    public static readonly CardDefinition SpeedOrder = new()
    {
        Id = "t_speed", Name = "Hunt Signal", Type = CardType.Order, Cost = 1,
        Effects = [new EffectSpec { Trigger = "play", Action = "move_bonus", Target = "target_unit", Amount = 2 }],
    };

    public static readonly CardDefinition SummonOrder = new()
    {
        Id = "t_pack_call", Name = "Pack Call", Type = CardType.Order, Rarity = Rarity.Rare, Cost = 3,
        Effects = [new EffectSpec { Trigger = "play", Action = "summon", SummonCardId = "t_pup", Amount = 2 }],
    };

    public static readonly CardDefinition ColumnOrder = new()
    {
        Id = "t_column", Name = "Focused Barrage", Type = CardType.Order, Rarity = Rarity.Rare, Cost = 3,
        Effects = [new EffectSpec { Trigger = "play", Action = "damage", Target = "column_enemies", Amount = 1 }],
    };

    public static readonly CardDefinition HomeRowBuffOrder = new()
    {
        Id = "t_homebuff", Name = "Hold the Line", Type = CardType.Order, Rarity = Rarity.Rare, Cost = 3,
        Effects = [new EffectSpec { Trigger = "play", Action = "buff", Target = "allies_home_row", Atk = 1, Hp = 1 }],
    };

    public static readonly CardDefinition OwnHalfSnipe = new()
    {
        Id = "t_ownhalf", Name = "Overline Execution", Type = CardType.Order, Rarity = Rarity.Rare, Cost = 4,
        Effects = [new EffectSpec { Trigger = "play", Action = "damage", Target = "target_unit_own_half", Amount = 4 }],
    };

    public static readonly CardDefinition AllAlliesBuff = new()
    {
        Id = "t_allbuff", Name = "Blood Scent", Type = CardType.Order, Cost = 3,
        Effects = [new EffectSpec { Trigger = "play", Action = "buff", Target = "all_allies", Atk = 1 }],
    };

    public static CardDatabase Db { get; } = new([
        Vanilla, BigVanilla, Charger, Assaulter, Scout, Archer, GuardUnit, Holder,
        Trampler, Sneak, Shielded, BattlecryBuffer, Bomber, Coin, ZapOrder, DrawOrder,
        Garrison, Leaper, PupToken, Medic, GrantGuardOrder, PounceOrder, GrantShieldOrder,
        SpeedOrder, SummonOrder, ColumnOrder, HomeRowBuffOrder, OwnHalfSnipe, AllAlliesBuff,
    ]);

    // 筑垒: grant Guard until your next turn. 狩猎号角: +1 movement this turn.
    public static readonly LeaderDefinition Valen = new()
    {
        Id = "leader_valen", Name = "Valen", SkillCost = 2,
        SkillEffects = [new EffectSpec { Trigger = "leader_skill", Action = "grant_keyword", Target = "target_unit", GrantKeyword = Keyword.Guard, Duration = "your_next_turn" }],
    };

    public static readonly LeaderDefinition Saen = new()
    {
        Id = "leader_saen", Name = "Saen", SkillCost = 2,
        SkillEffects = [new EffectSpec { Trigger = "leader_skill", Action = "move_bonus", Target = "target_unit", Amount = 1 }],
    };

    public static LeaderDatabase Leaders { get; } = new([Valen, Saen]);

    public static Resolver NewResolver() => new(Db, Leaders);

    /// <summary>A started game (turn 1, seat 0 active) with the given decks. Deterministic via seed.</summary>
    public static GameState NewGame(ulong seed = 42, IReadOnlyList<string>? deck0 = null, IReadOnlyList<string>? deck1 = null)
    {
        var deck = Enumerable.Repeat(Vanilla.Id, 12).ToList();
        var (state, _) = GameFactory.CreateGame(new MatchConfig
        {
            Seed = seed,
            Deck0 = deck0 ?? deck,
            Deck1 = deck1 ?? deck,
        }, Db);
        return state;
    }

    /// <summary>Places a ready-to-act unit directly on the board, bypassing deploy/sickness (DeployedOnTurn = 0).</summary>
    public static UnitInstance Place(GameState state, int seat, string cardId, Cell cell)
    {
        var def = Db.Get(cardId);
        var unit = new UnitInstance
        {
            EntityId = state.TakeEntityId(),
            CardId = def.Id,
            OwnerSeat = seat,
            Cell = cell,
            Atk = def.Atk,
            MaxHp = def.Hp,
            CurrentHp = def.Hp,
            DeployedOnTurn = 0,
            ShieldActive = def.HasKeyword(Keyword.Shield),
            Keywords = def.Keywords.ToList(),
        };
        // Mirror the engine: a 驻防 unit placed on its home row already carries the +1/+1.
        if (def.HasKeyword(Keyword.Garrison) && cell.Row == Geometry.BoardGeometry.HomeRow(seat))
        {
            unit.Atk += 1;
            unit.MaxHp += 1;
            unit.CurrentHp += 1;
            unit.GarrisonApplied = true;
        }
        state.Units.Add(unit);
        return unit;
    }

    /// <summary>Puts a specific card into a player's hand and returns its entity id.</summary>
    public static int GiveCard(GameState state, int seat, string cardId)
    {
        var card = new CardInstance { EntityId = state.TakeEntityId(), CardId = cardId };
        state.Player(seat).Hand.Add(card);
        return card.EntityId;
    }
}
