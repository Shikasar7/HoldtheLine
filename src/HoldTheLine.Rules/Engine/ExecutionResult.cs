using HoldTheLine.Rules.Events;
using HoldTheLine.Rules.State;

namespace HoldTheLine.Rules.Engine;

/// <summary>
/// Outcome of executing one command. On success, <see cref="State"/> is a NEW state instance —
/// the input state is never mutated. On failure the input state is untouched and Events is empty.
/// </summary>
public sealed record ExecutionResult
{
    public required bool Success { get; init; }
    public GameState? State { get; init; }
    public IReadOnlyList<GameEvent> Events { get; init; } = [];
    public RuleError? Error { get; init; }

    public static ExecutionResult Ok(GameState state, IReadOnlyList<GameEvent> events) =>
        new() { Success = true, State = state, Events = events };

    public static ExecutionResult Fail(RuleErrorCode code, string message) =>
        new() { Success = false, Error = new RuleError(code, message) };
}
