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
    NotAdjacent,
    NoAttacksLeft,
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
