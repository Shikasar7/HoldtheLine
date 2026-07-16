using HoldTheLine.Rules.Commands;
using HoldTheLine.Rules.Engine;
using HoldTheLine.Rules.Events;

namespace HoldTheLine.Rules.Hosting;

/// <summary>
/// The seam between a match and its clients. LocalGameHost (prototype) and NetworkGameHost
/// (M2) implement the same shape, so the battle UI and AI never know which one they're on.
/// Deliberately async even though the local host resolves synchronously — a client written
/// against this cannot accidentally depend on same-tick results (hard constraint #5, plan §3.1).
/// </summary>
public interface IGameHost
{
    /// <summary>Submit a command for a seat. Resolution events arrive via subscriptions, not the return value.</summary>
    Task<CommandResult> SubmitCommandAsync(int seat, Command command);

    /// <summary>Subscribe to the seat-redacted event stream. Dispose to unsubscribe.</summary>
    IDisposable Subscribe(int seat, Action<GameEvent> onEvent);

    /// <summary>Seat-redacted snapshot for initial sync / reconnection.</summary>
    PlayerView GetView(int seat);

    /// <summary>All events so far, redacted for the seat (catch-up after subscribing late).</summary>
    IReadOnlyList<GameEvent> GetEventLog(int seat);
}

public sealed record CommandResult
{
    public required bool Accepted { get; init; }
    public RuleError? Error { get; init; }

    public static readonly CommandResult Ok = new() { Accepted = true };
    public static CommandResult Rejected(RuleError error) => new() { Accepted = false, Error = error };
}
