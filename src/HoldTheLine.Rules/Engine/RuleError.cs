namespace HoldTheLine.Rules.Engine;

public enum RuleErrorCode
{
    GameOver,
    NotYourTurn,
    UnknownEntity,
    NotYourUnit,
    NotEnoughMana,
    InvalidTarget,
    CellOccupied,
    CellOutsideBoard,
    NotHomeRow,
    SummoningSickness,
    NoMovementLeft,
    Emplaced,
    /// <summary>定身 (docs/21 §1.5): a temporarily rooted unit cannot move (it can still attack/retaliate).</summary>
    Rooted,
    NotAdjacent,
    NoAttacksLeft,
    /// <summary>烟幕 (docs/21 §1.6): a unit standing in a smoke zone cannot attack.</summary>
    Smoked,
    OutOfRange,
    LineBlocked,
    GuardEnforced,
    NotOnEnemyHomeRow,
    InvalidDeck,
    NotImplemented,
    InvalidCommand,
    /// <summary>A non-mulligan command was submitted while the match is still in the 起手重抽 phase.</summary>
    MulliganPending,
}

public sealed record RuleError(RuleErrorCode Code, string Message);
