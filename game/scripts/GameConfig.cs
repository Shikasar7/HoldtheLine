namespace HoldTheLine.Game;

/// <summary>
/// Match setup chosen on the menu and read by BattleScene after the scene change. Static so it
/// survives ChangeSceneToFile. In vs-AI mode the human is <see cref="HumanSeat"/> and the other
/// seat is driven by the greedy AI (via LocalGameHost.SuggestCommand).
/// </summary>
public static class GameConfig
{
    public static bool VsAi;
    public static int HumanSeat;      // seat the local player controls in vs-AI mode
    public static string Deck0 = "iron_wall";
    public static string Deck1 = "wildpack_hunt";
    public static bool Configured;

    // Online (M2 N2). When Online is set, BattleScene connects to a server instead of running a
    // LocalGameHost; the local seat is assigned by the server's match_started, not chosen here.
    public static bool Online;
    /// <summary>Default = the public battle server on the cicala.chat VPS (docs/08 §4). Friends just
    /// open the online panel and create/join — no address typing. Editable for LAN/local testing.</summary>
    public static string ServerUrl = "ws://212.64.21.174:5210/ws";
    public static string Nickname = "玩家";

    public static void SetVsAi(string humanDeck, string aiDeck)
    {
        Online = false;
        VsAi = true;
        HumanSeat = 0;
        Deck0 = humanDeck;
        Deck1 = aiDeck;
        Configured = true;
    }

    public static void SetHotseat()
    {
        VsAi = false; Online = false;
        HumanSeat = 0;
        Deck0 = "iron_wall";
        Deck1 = "wildpack_hunt";
        Configured = true;
    }

    /// <summary>Online via the shared <see cref="Session"/> (M3 C1 lobby): the connection + match are
    /// already established, so BattleScene attaches to Session.Remote rather than dialing out itself.</summary>
    public static void SetOnlineAttached()
    {
        VsAi = false;
        Online = true;
        Configured = true;
    }
}
