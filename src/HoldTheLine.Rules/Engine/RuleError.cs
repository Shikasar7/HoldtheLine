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
}

public sealed record RuleError(RuleErrorCode Code, string Message);
