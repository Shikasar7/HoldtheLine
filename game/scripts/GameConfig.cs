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

    public static void SetVsAi(string humanDeck, string aiDeck)
    {
        VsAi = true;
        HumanSeat = 0;
        Deck0 = humanDeck;
        Deck1 = aiDeck;
        Configured = true;
    }

    public static void SetHotseat()
    {
        VsAi = false;
        HumanSeat = 0;
        Deck0 = "iron_wall";
        Deck1 = "wildpack_hunt";
        Configured = true;
    }
}
