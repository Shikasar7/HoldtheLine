namespace HoldTheLine.Rules;

public static class RulesInfo
{
    /// <summary>Ruleset version. Bump on any change to resolution semantics or serialized shapes.</summary>
    /// <remarks>0.2.0 (2026-07-17): new-faction primitives — Manhattan 射程 + no line-blocking, 架设/贯穿,
    /// destroy, ally_order_played, and the row/cross/column-ally selectors (docs/06 §3, docs/07 X0).
    /// 0.3.0 (2026-07-18): second-batch primitives — 灼蚀 (sear, ignores 坚守), self_moved trigger,
    /// all_ally_emplacements selector; 80 new cards, pool 83→163 (docs/10).</remarks>
    public const string Version = "0.3.0";
}
