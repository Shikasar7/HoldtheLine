using Godot;
using HoldTheLine.Rules.Cards;

namespace HoldTheLine.Game;

/// <summary>Maps the public turret loadout to authored presentation variants.</summary>
public static class TurretVisuals
{
    public const string CoreId = "uv_turret_core";
    public const string AnchorId = "uv_mod_anchor_platform";
    public const string TrackedId = "uv_mod_tracked_chassis";
    public const string AutoloaderId = "uv_mod_autoloader";
    public const string GrandId = "uv_mod_grand_cannon";

    /// <summary>
    /// Upper assemblies compose independently with the chassis. If both chassis modules are
    /// equipped, the most recently installed one is shown: the rules keep both bonuses, while
    /// the latest physical mounting remains the readable silhouette.
    /// </summary>
    public static string StandeeId(IReadOnlyList<string>? modules)
    {
        if (modules is null) return CoreId;
        bool grand = modules.Contains(GrandId);
        bool loader = modules.Contains(AutoloaderId);
        string upper = grand && loader ? "grand_autoloader"
            : grand ? "grand"
            : loader ? "autoloader"
            : "";

        string chassis = "";
        for (int i = modules.Count - 1; i >= 0; i--)
        {
            if (modules[i] == TrackedId) { chassis = "tracked"; break; }
            if (modules[i] == AnchorId) { chassis = "anchor"; break; }
        }

        string suffix = string.Join("_", new[] { upper, chassis }.Where(s => s.Length > 0));
        return suffix.Length == 0 ? CoreId : $"{CoreId}_{suffix}";
    }

    /// <summary>Only the two silhouette-defining upper modules change the large card illustration.</summary>
    public static string CardArtId(IReadOnlyList<string>? modules)
    {
        if (modules is null) return CoreId;
        bool grand = modules.Contains(GrandId);
        bool loader = modules.Contains(AutoloaderId);
        return grand && loader ? $"{CoreId}_grand_autoloader"
            : grand ? $"{CoreId}_grand"
            : loader ? $"{CoreId}_autoloader"
            : CoreId;
    }

    /// <summary>Restrained industrial rarity colors; shape/glyph remains the color-blind fallback.</summary>
    public static Color RarityColor(Rarity rarity) => rarity switch
    {
        Rarity.Rare => new Color("58a9a4"),       // oxidized teal
        Rarity.Epic => new Color("a66b9b"),      // muted magenta
        Rarity.Legendary => new Color("e2a43b"), // furnace gold
        _ => new Color("9aa0a4"),                // iron silver
    };

    public static string ModuleGlyph(CardDefinition module) => module.Id switch
    {
        AnchorId => "架",
        TrackedId => "履",
        AutoloaderId => "装",
        GrandId => "核",
        "uv_mod_failsafe_pod" => "保",
        _ when module.Module is { OnHit: not "none" } => "弹",
        _ when module.Module is { Lifesteal: not "none" } => "弹",
        _ => "炮",
    };
}
