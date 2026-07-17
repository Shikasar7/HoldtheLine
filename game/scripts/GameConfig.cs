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
    public static string ServerUrl = "ws://127.0.0.1:5210/ws";
    public static string Nickname = "玩家";
    public static bool CreateRoom;    // true = host a room; false = join RoomCode
    public static string RoomCode = "";
    public static string OnlineDeck = "iron_wall";

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

    public static void SetOnline(string serverUrl, string nickname, bool createRoom, string roomCode, string deck)
    {
        VsAi = false;
        Online = true;
        ServerUrl = serverUrl;
        Nickname = string.IsNullOrWhiteSpace(nickname) ? "玩家" : nickname;
        CreateRoom = createRoom;
        RoomCode = roomCode.Trim().ToUpperInvariant();
        OnlineDeck = deck;
        Configured = true;
    }
}
