using HoldTheLine.Rules.Cards;
using HoldTheLine.Rules.Geometry;
using HoldTheLine.Rules.State;

namespace HoldTheLine.Rules.Hosting;

/// <summary>
/// A seat-redacted snapshot: everything a client is ALLOWED to know, and nothing more. Used for
/// initial sync and (later) reconnection. The presentation layer builds itself from this plus the
/// event stream — never from GameState.
/// </summary>
public sealed record PlayerView
{
    public required int ViewerSeat { get; init; }
    public required int TurnNumber { get; init; }
    public required int ActiveSeat { get; init; }
    public required SelfView Self { get; init; }
    public required OpponentView Opponent { get; init; }
    public required IReadOnlyList<UnitView> Units { get; init; }
    public GameResult? Result { get; init; }

    /// <summary>起手重抽 (docs/11): this seat still owes a mulligan (drives the mulligan UI). Absent field
    /// on an old snapshot deserializes to false.</summary>
    public bool MulliganPending { get; init; }
    /// <summary>The opponent still owes a mulligan ("waiting for opponent…").</summary>
    public bool OpponentMulliganPending { get; init; }

    public static PlayerView From(GameState state, int viewerSeat)
    {
        var self = state.Player(viewerSeat);
        var opp = state.Player(1 - viewerSeat);
        return new PlayerView
        {
            ViewerSeat = viewerSeat,
            TurnNumber = state.TurnNumber,
            ActiveSeat = state.ActiveSeat,
            Result = state.Result,
            MulliganPending = state.Mulligan is { } m && !m.Done[viewerSeat],
            OpponentMulliganPending = state.Mulligan is { } mo && !mo.Done[1 - viewerSeat],
            Self = new SelfView
            {
                LeaderId = self.LeaderId,
                LeaderHp = self.LeaderHp,
                Mana = self.Mana,
                ManaMax = self.ManaMax,
                DeckCount = self.Deck.Count,
                Fatigue = self.Fatigue,
                SpellCharge = self.SpellCharge,
                Hand = self.Hand.Select(c => new CardInHandView { EntityId = c.EntityId, CardId = c.CardId }).ToList(),
            },
            Opponent = new OpponentView
            {
                LeaderId = opp.LeaderId,
                LeaderHp = opp.LeaderHp,
                Mana = opp.Mana,
                ManaMax = opp.ManaMax,
                DeckCount = opp.Deck.Count,
                HandCount = opp.Hand.Count,
                Fatigue = opp.Fatigue,
                SpellCharge = opp.SpellCharge,
            },
            Units = state.Units.Select(UnitView.From).ToList(),
        };
    }
}

public sealed record SelfView
{
    public required string LeaderId { get; init; }
    public required int LeaderHp { get; init; }
    public required int Mana { get; init; }
    public required int ManaMax { get; init; }
    public required int DeckCount { get; init; }
    public required int Fatigue { get; init; }
    /// <summary>蓄能余量 (docs/21 §1.3) for the leader-side counter. Old snapshots deserialize to 0.</summary>
    public int SpellCharge { get; init; }
    public required IReadOnlyList<CardInHandView> Hand { get; init; }
}

public sealed record OpponentView
{
    public required string LeaderId { get; init; }
    public required int LeaderHp { get; init; }
    public required int Mana { get; init; }
    public required int ManaMax { get; init; }
    public required int DeckCount { get; init; }
    public required int HandCount { get; init; }
    public required int Fatigue { get; init; }
    /// <summary>Opponent's 蓄能余量 (public, docs/21 §1.3).</summary>
    public int SpellCharge { get; init; }
}

public sealed record CardInHandView
{
    public required int EntityId { get; init; }
    public required string CardId { get; init; }
}

public sealed record UnitView
{
    public required int EntityId { get; init; }
    public required string CardId { get; init; }
    public required int OwnerSeat { get; init; }
    public required Cell Cell { get; init; }
    public required int Atk { get; init; }
    public required int CurrentHp { get; init; }
    public required int MaxHp { get; init; }
    public required bool ShieldActive { get; init; }
    public required bool MovedThisRound { get; init; }
    public required int MovementUsed { get; init; }
    public required int AttacksUsed { get; init; }
    public required IReadOnlyList<KeywordSpec> Keywords { get; init; }

    public static UnitView From(UnitInstance u) => new()
    {
        EntityId = u.EntityId,
        CardId = u.CardId,
        OwnerSeat = u.OwnerSeat,
        Cell = u.Cell,
        Atk = u.Atk,
        CurrentHp = u.CurrentHp,
        MaxHp = u.MaxHp,
        ShieldActive = u.ShieldActive,
        MovedThisRound = u.MovedThisRound,
        MovementUsed = u.MovementUsed,
        AttacksUsed = u.AttacksUsed,
        Keywords = u.Keywords.ToList(),
    };
}
