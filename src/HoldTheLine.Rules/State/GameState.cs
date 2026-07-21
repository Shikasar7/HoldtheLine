using HoldTheLine.Rules.Cards;
using HoldTheLine.Rules.Geometry;

namespace HoldTheLine.Rules.State;

/// <summary>A card in a hidden zone (hand/deck). Identity is the EntityId; the CardId is what gets redacted.</summary>
public sealed class CardInstance
{
    public int EntityId { get; set; }
    public string CardId { get; set; } = "";
}

/// <summary>A keyword granted for a limited time (grant_keyword with a non-permanent duration).</summary>
public sealed class TempKeywordGrant
{
    public KeywordSpec Spec { get; set; } = new(Keyword.Taunt);
    /// <summary>end_of_turn | your_next_turn.</summary>
    public string Expiry { get; set; } = "end_of_turn";
    /// <summary>Seat that granted it — needed to resolve "your next turn".</summary>
    public int GrantedBySeat { get; set; }
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

    /// <summary>How many times self_moved has granted this unit ATK this turn (capped at
    /// <see cref="Engine.Resolver.SelfMovedAtkGainCap"/>); reset at the owner's turn start.</summary>
    public int SelfMovedAtkGainsThisTurn { get; set; }

    /// <summary>归魂 (docs/21 §1.4): how many times this unit's ally_died_your_turn has fired this turn
    /// (capped at <see cref="Engine.ResolutionContext.SoulReturnCap"/>); reset at the owner's turn start.</summary>
    public int SoulReturnGainsThisTurn { get; set; }

    /// <summary>自体成长上限 (docs/21 §1.9): how many capped ally_order_played self-growths have fired this
    /// turn (capped at <see cref="Engine.ResolutionContext.OrderGrowthCap"/>); reset at the owner's turn start.</summary>
    public int OrderGrowthThisTurn { get; set; }

    /// <summary>Extra movement points this turn (move_bonus effects); reset at the owner's turn start.</summary>
    public int BonusMovement { get; set; }

    /// <summary>Whether the 驻防 (Garrison) +1/+1 is currently applied (unit is on its home row).</summary>
    public bool GarrisonApplied { get; set; }

    /// <summary>成长 (docs/21 §1.8): steps accumulated toward transformation — +1 at the owner's turn start and
    /// +1 per 薪炎 hit. Reset on transform. Inert unless the unit's card carries a <see cref="Cards.GrowthSpec"/>.</summary>
    public int GrowthProgress { get; set; }

    /// <summary>Runtime copy of the definition's keywords (permanent grants append here).</summary>
    public List<KeywordSpec> Keywords { get; set; } = new();

    /// <summary>Time-limited keyword grants (pounce, 筑垒, …); expire at turn boundaries.</summary>
    public List<TempKeywordGrant> TempGrants { get; set; } = new();

    public bool HasKeyword(Keyword k) =>
        Keywords.Any(s => s.Keyword == k) || TempGrants.Any(g => g.Spec.Keyword == k);

    /// <summary>Highest value across permanent and temporary grants of the keyword (for Swift/Range).</summary>
    public int KeywordValue(Keyword k)
    {
        int value = 0;
        bool found = false;
        foreach (var s in Keywords)
            if (s.Keyword == k) { value = Math.Max(value, s.Value); found = true; }
        foreach (var g in TempGrants)
            if (g.Spec.Keyword == k) { value = Math.Max(value, g.Spec.Value); found = true; }
        return found ? value : 0;
    }

    public int MovementPerTurn => (HasKeyword(Keyword.Swift) ? KeywordValue(Keyword.Swift) : 1) + BonusMovement;
}

/// <summary>
/// A status attached to a board cell rather than a unit (docs/21 §1.6) — the shared base for 烟幕区 (smoke)
/// and 烬火陷阱 (trap). Carries an owner and a visibility flag so PlayerView can redact hidden traps, plus
/// two lifetime models: <see cref="Expiry"/> (a turn-boundary marker, used by smoke) and <see cref="TurnsLeft"/>
/// (a countdown, used by a revealed trap's fire). Old snapshots without CellStates deserialize to an empty list.
/// </summary>
public sealed class CellState
{
    public Cell Cell { get; set; }
    /// <summary>smoke | trap.</summary>
    public string Kind { get; set; } = "smoke";
    public int OwnerSeat { get; set; }
    /// <summary>Turn-boundary lifetime (smoke): "your_next_turn" clears at the owner's next turn start.</summary>
    public string Expiry { get; set; } = "your_next_turn";
    /// <summary>Hidden from the opponent's PlayerView until revealed (trap). Smoke is public (false).</summary>
    public bool Hidden { get; set; }
    /// <summary>Trap: has been triggered and its fire is now burning (public). Set in step 4c.</summary>
    public bool Revealed { get; set; }
    /// <summary>Trap: turns of burning left after reveal; counted down at turn boundaries. Unused by smoke.</summary>
    public int TurnsLeft { get; set; }
}

/// <summary>A face-down reactive secret (docs/21 §1.7) waiting in a seat's secret zone — 焰誓反制. The opponent
/// sees only the COUNT of these (never the CardId/Kind). Distinct from a 烬火陷阱, which is a hidden board cell.</summary>
public sealed class Secret
{
    public string CardId { get; set; } = "";
    /// <summary>counter_order (焰誓反制). The trigger + payload are looked up from the card definition.</summary>
    public string Kind { get; set; } = "";
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
    /// <summary>Leader skill is once per turn; reset at this player's turn start.</summary>
    public bool LeaderSkillUsedThisTurn { get; set; }
    /// <summary>蓄能余量 (docs/21 §1.3): a stored bonus added to this seat's next 薪炎 (spell.kindle) damage
    /// order, then cleared. Persists across turns until consumed; 焰跃术士's 战吼 grants it. Old snapshots
    /// without this field deserialize to 0.</summary>
    public int SpellCharge { get; set; }

    /// <summary>秘密区 (docs/21 §1.7): face-down reactive secrets. The opponent's PlayerView carries only the
    /// count. Old snapshots without this field deserialize to an empty list.</summary>
    public List<Secret> Secrets { get; set; } = new();

    /// <summary>薪火回响 (docs/21 §3.1): whether this seat has already played a 薪炎 damage order this turn (so
    /// 门德 only echoes the FIRST). Reset at the seat's turn start.</summary>
    public bool FirstKindleOrderDone { get; set; }
}

public sealed record GameResult
{
    /// <summary>-1 means a draw (both leaders died simultaneously).</summary>
    public required int WinnerSeat { get; init; }
    public required string Reason { get; init; }
}

/// <summary>
/// The 起手重抽 (mulligan) phase state (docs/11). Present (non-null on <see cref="GameState.Mulligan"/>)
/// only while at least one seat still owes a mulligan; cleared to null once both are done, so the state
/// returns to today's shape and serializes identically to a pre-mulligan match. Each seat draws its
/// replacements from an independent RNG stream so results are order-independent and unpredictable to the
/// opponent. <see cref="FirstSeat"/>/<see cref="CoinCardId"/> are stashed here so the Resolver can start
/// the first turn + hand out the coin on completion without reaching for MatchConfig.
/// </summary>
public sealed class MulliganState
{
    /// <summary>Per-seat: has this seat submitted its mulligan yet.</summary>
    public bool[] Done { get; set; } = [false, false];
    /// <summary>Per-seat independent SplitMix64 stream (Seed ^ seat salt); the match Rng is untouched.</summary>
    public ulong[] RngState { get; set; } = new ulong[2];
    public int FirstSeat { get; set; }
    public string CoinCardId { get; set; } = "";
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

    /// <summary>格子状态 (docs/21 §1.6): 烟幕区 / 烬火陷阱. Old snapshots without this field deserialize to empty.</summary>
    public List<CellState> CellStates { get; set; } = new();

    public PlayerState[] Players { get; set; } = [];
    public DeterministicRng Rng { get; set; } = new();
    public GameResult? Result { get; set; }

    /// <summary>Non-null while the match is in the 起手重抽 phase (docs/11); null = normal play. Old
    /// snapshots without this field deserialize to null → today's behaviour.</summary>
    public MulliganState? Mulligan { get; set; }

    public PlayerState Player(int seat) => Players[seat];
    public PlayerState ActivePlayer => Players[ActiveSeat];

    public UnitInstance? UnitAt(Cell cell) => Units.FirstOrDefault(u => u.Cell == cell);

    public UnitInstance? FindUnit(int entityId) => Units.FirstOrDefault(u => u.EntityId == entityId);

    /// <summary>烟幕 (docs/21 §1.6): whether a smoke zone currently covers this cell — units standing here
    /// cannot attack and do not retaliate. Positional, so a unit that walks off is no longer smoked.</summary>
    public bool IsSmoked(Cell cell) => CellStates.Any(s => s.Kind == "smoke" && s.Cell == cell);

    public int TakeEntityId() => NextEntityId++;
}
