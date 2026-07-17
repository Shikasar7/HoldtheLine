namespace HoldTheLine.Server;

/// <summary>
/// Server configuration, bound from the "Server" section of appsettings / environment. All timing
/// values are the plan §5.2 defaults and can be overridden per deployment.
/// </summary>
public sealed class ServerOptions
{
    /// <summary>Kestrel bind address(es). 0.0.0.0 so LAN / Tailscale peers can reach it.</summary>
    public string Urls { get; set; } = "http://0.0.0.0:5210";

    /// <summary>Turn time limit before the server auto-submits EndTurn (N3).</summary>
    public int TurnSeconds { get; set; } = 90;

    /// <summary>Grace window a disconnected player has to reconnect before forfeiting (N3).</summary>
    public int DisconnectGraceSeconds { get; set; } = 120;

    /// <summary>Optional explicit path to the card/leader/deck data root; auto-discovered when null.</summary>
    public string? DataRoot { get; set; }

    /// <summary>Optional directory to write per-match command logs (JSONL: config header + one line per
    /// accepted command) for deterministic replay / bug repro. Null disables logging.</summary>
    public string? CommandLogDir { get; set; }

    /// <summary>Optional message-of-the-day returned in HelloOk.</summary>
    public string? Motd { get; set; }

    /// <summary>SQLite database file (M3 B0). Null / empty opens a private in-memory db (tests, throwaway
    /// runs); production sets <c>/var/lib/holdtheline/holdtheline.db</c> via HTL_Server__DbPath.</summary>
    public string? DbPath { get; set; }
}
