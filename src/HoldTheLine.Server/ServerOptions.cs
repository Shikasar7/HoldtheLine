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

    /// <summary>起手重抽 (docs/11): give every match a mulligan phase before the first turn. On by default in
    /// production; tests that exercise the plain turn flow start a server with this off.</summary>
    public bool MulliganEnabled { get; set; } = true;

    /// <summary>Shared mulligan clock: seconds before the server auto-submits keep-all for any seat that
    /// hasn't chosen (docs/11 D9). 0 = auto-resolve immediately (tests).</summary>
    public int MulliganSeconds { get; set; } = 45;

    /// <summary>Optional explicit path to the card/leader/deck data root; auto-discovered when null.</summary>
    public string? DataRoot { get; set; }

    /// <summary>Optional directory to write per-match command logs (JSONL: config header + one line per
    /// accepted command) for deterministic replay / bug repro. Null disables logging.</summary>
    public string? CommandLogDir { get; set; }

    /// <summary>Optional message-of-the-day returned in HelloOk.</summary>
    public string? Motd { get; set; }

    /// <summary>docs/15 §2: minimum client app version (SemVer) the server accepts. Null/empty disables the
    /// check entirely. When set, a hello whose <see cref="HoldTheLine.Net.Protocol.Hello.ClientVersion"/> is
    /// below this (a missing value counts as "0.0.0") is either logged or hard-rejected, per
    /// <see cref="EnforceMinClientVersion"/>. Distinct from the exact protocol/rules gate.</summary>
    public string? MinClientVersion { get; set; }

    /// <summary>docs/15 §2: when false (default) an outdated client is only logged and still allowed in —
    /// the soft-launch stance while the auto-update channel (通道 A) is still being rolled out. Flip to true
    /// (once enough players have the updater) to hard-reject with "client_outdated". No effect unless
    /// <see cref="MinClientVersion"/> is set.</summary>
    public bool EnforceMinClientVersion { get; set; }

    /// <summary>SQLite database file (M3 B0). Null / empty opens a private in-memory db (tests, throwaway
    /// runs); production sets <c>/var/lib/holdtheline/holdtheline.db</c> via HTL_Server__DbPath.</summary>
    public string? DbPath { get; set; }

    /// <summary>Optional directory for daily online SQLite backups (docs/12 B2). Null disables backups —
    /// the default, so tests and local runs don't spawn the service. Production sets
    /// <c>/var/lib/holdtheline/backups</c> via HTL_Server__BackupDir.</summary>
    public string? BackupDir { get; set; }

    /// <summary>开发者测试修改器 (dev-only): when true, the server honours <c>dev_cheat</c> requests (一键回费 /
    /// 从牌库取牌) in unranked friend rooms. Default false: production ignores dev_cheat entirely. Enable per
    /// test session via <c>HTL_Server__DevCheatsEnabled=true</c>, then turn it back off.</summary>
    public bool DevCheatsEnabled { get; set; }

    /// <summary>Allow an enabled developer modifier inside ranked queue matches too. Kept as a separate,
    /// default-false switch so accidentally leaving <see cref="DevCheatsEnabled"/> on cannot affect the live
    /// ladder. Development servers may explicitly set
    /// <c>HTL_Server__DevCheatsAllowRanked=true</c>.</summary>
    public bool DevCheatsAllowRanked { get; set; }
}
