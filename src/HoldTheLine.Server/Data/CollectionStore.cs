namespace HoldTheLine.Server.Data;

/// <summary>
/// Per-account card collection (M3 plan §3.1, D-M3-2). The table exists so the data model is in place,
/// but the Beta is configured <b>全解锁</b> — every card is available to everyone — so ownership checks
/// short-circuit to true and the synthesis/dust economy stays modeled-but-closed until launch.
/// </summary>
public sealed class CollectionStore
{
    public const string BetaMode = "unlock_all";

    private readonly Db _db;

    public CollectionStore(Db db)
    {
        _db = db;
        _db.Run(c => AccountStore.Exec(c, """
            CREATE TABLE IF NOT EXISTS collections (
                guest_id TEXT NOT NULL,
                card_id  TEXT NOT NULL,
                count    INTEGER NOT NULL,
                PRIMARY KEY (guest_id, card_id)
            );
            """));
    }

    /// <summary>Collection mode surfaced to the client — "unlock_all" during Beta.</summary>
    public string Mode => BetaMode;

    /// <summary>Whether an account may use a card. Always true in Beta (unlock_all).</summary>
    public bool Owns(string guestId, string cardId) => true;
}
