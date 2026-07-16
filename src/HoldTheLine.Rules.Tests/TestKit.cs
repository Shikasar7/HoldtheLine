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

    public static CardDatabase Db { get; } = new([
        Vanilla, BigVanilla, Charger, Assaulter, Scout, Archer, GuardUnit, Holder,
        Trampler, Sneak, Shielded, BattlecryBuffer, Bomber, Coin, ZapOrder, DrawOrder,
    ]);

    public static Resolver NewResolver() => new(Db);

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
