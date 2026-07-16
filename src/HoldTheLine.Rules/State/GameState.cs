using HoldTheLine.Rules.Cards;
using HoldTheLine.Rules.Geometry;

namespace HoldTheLine.Rules.State;

/// <summary>A card in a hidden zone (hand/deck). Identity is the EntityId; the CardId is what gets redacted.</summary>
public sealed class CardInstance
{
    public int EntityId { get; set; }
    public string CardId { get; set; } = "";
}

public sealed class UnitInstance
{
    public int EntityId { get; set; }
    public string CardId { get; set; } = "";
    public int OwnerSeat { get; set; }
    public Cell Cell { get; set; }

    public int Atk { get; set; }
    public int MaxHp { get; set; }
    public int CurrentHp { get; set; }

    /// <summary>Global turn number this unit entered play (summoning-sickness check).</summary>
    public int DeployedOnTurn { get; set; }

    public int MovementUsed { get; set; }
    public int AttacksUsed { get; set; }

    /// <summary>True once the unit moves; reset at its owner's turn start. Governs HoldFast (坚守).</summary>
    public bool MovedThisRound { get; set; }

    /// <summary>持盾 charge still available.</summary>
    public bool ShieldActive { get; set; }

    /// <summary>Runtime copy of the definition's keywords (buffs may add/remove later).</summary>
    public List<KeywordSpec> Keywords { get; set; } = new();

    public bool HasKeyword(Keyword k) => Keywords.Any(s => s.Keyword == k);

    public int KeywordValue(Keyword k) => Keywords.FirstOrDefault(s => s.Keyword == k)?.Value ?? 0;

    public int MovementPerTurn => HasKeyword(Keyword.Swift) ? KeywordValue(Keyword.Swift) : 1;
}

public sealed class PlayerState
{
    public int Seat { get; set; }
    public string LeaderId { get; set; } = "";
    public int LeaderHp { get; set; }
    public int Mana { get; set; }
    public int ManaMax { get; set; }
    public List<CardInstance> Hand { get; set; } = new();
    /// <summary>Draw order: last element is the top of the deck.</summary>
    public List<CardInstance> Deck { get; set; } = new();
    public List<string> Graveyard { get; set; } = new();
    public int Fatigue { get; set; }
}

public sealed record GameResult
{
    /// <summary>-1 means a draw (both leaders died simultaneously).</summary>
    public required int WinnerSeat { get; init; }
    public required string Reason { get; init; }
}

/// <summary>
/// The complete authoritative match state. Fully serializable; cloned by the resolver before
/// every mutation so callers keep snapshot semantics. Never exposed to the presentation layer —
/// clients get <c>PlayerView</c> and events.
/// </summary>
public sealed class GameState
{
    public string RulesVersion { get; set; } = RulesInfo.Version;

    /// <summary>Global 1-based counter; increments on every player turn (not every round).</summary>
    public int TurnNumber { get; set; }

    public int ActiveSeat { get; set; }
    public int NextEntityId { get; set; } = 1;
    public int EventSequence { get; set; }

    public List<UnitInstance> Units { get; set; } = new();
    public PlayerState[] Players { get; set; } = [];
    public DeterministicRng Rng { get; set; } = new();
    public GameResult? Result { get; set; }

    public PlayerState Player(int seat) => Players[seat];
    public PlayerState ActivePlayer => Players[ActiveSeat];

    public UnitInstance? UnitAt(Cell cell) => Units.FirstOrDefault(u => u.Cell == cell);

    public UnitInstance? FindUnit(int entityId) => Units.FirstOrDefault(u => u.EntityId == entityId);

    public int TakeEntityId() => NextEntityId++;
}
