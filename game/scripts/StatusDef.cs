using Godot;
using HoldTheLine.Rules.Cards;

namespace HoldTheLine.Game;

/// <summary>How a status badge decides whether it shows on a unit this frame.</summary>
public enum StatusTrigger
{
    /// <summary>Show while the unit has <see cref="StatusDef.BoundKeyword"/> in its LIVE keyword set. Covers the
    /// bulk of statuses (熔岩巨剑 / 法术护体 / 潜行 / 定身 / 福泽 …) — bind a keyword and you are done, no code.</summary>
    Keyword,

    /// <summary>Show while 持盾 is live/unspent (u.ShieldActive) — a runtime flag, not a keyword, so a shield that
    /// was consumed this animation stops showing immediately.</summary>
    ShieldLive,

    /// <summary>Show while 坚守 is actually reducing damage — has the HoldFast keyword AND has not moved this round.</summary>
    HoldFastLive,

    /// <summary>Visibility and the corner number are computed in C# keyed by <see cref="StatusDef.Id"/> (成长倒数 /
    /// 引导增伤 / 引导减费). You can still restyle these in the editor; only their NUMBER stays in code.</summary>
    Computed,
}

/// <summary>Which edge and colour a badge lands on: buffs stack down the left (green), debuffs down the right (red).</summary>
public enum StatusSide { Buff, Debuff }

/// <summary>
/// One status a unit can advertise on its card face — fully editable in the Godot Inspector. Add, remove or
/// restyle these in res://data/status_catalog.tres without touching code: change the glyph, drag in an icon,
/// pick Buff/Debuff, set the stacking order, and bind it to a keyword. The only thing that still lives in C#
/// is the corner NUMBER of the three <see cref="StatusTrigger.Computed"/> entries (成长 / 引导增伤 / 引导减费).
/// </summary>
[GlobalClass]
public partial class StatusDef : Resource
{
    // ── To add a status: set Glyph (or drag an Icon), pick Side, set Order, leave Trigger on Keyword, then
    //    choose Bound Keyword from the dropdown. That's it — Id can stay blank for keyword statuses. ──

    /// <summary>Short fallback glyph drawn when <see cref="Icon"/> is empty (e.g. 盾 / 潜 / 缚).</summary>
    [Export] public string Glyph { get; set; } = "";

    /// <summary>Optional badge art; when set it replaces the glyph. Drag a texture in from res://assets/art/ui.</summary>
    [Export] public Texture2D? Icon { get; set; }

    /// <summary>Left column + green (Buff) or right column + red (Debuff).</summary>
    [Export] public StatusSide Side { get; set; } = StatusSide.Buff;

    /// <summary>Stacking order within its column (low first). Ties keep catalog order.</summary>
    [Export] public int Order { get; set; }

    /// <summary>What makes this badge appear. Leave on Keyword for the common case, then set Bound Keyword below.</summary>
    [Export] public StatusTrigger Trigger { get; set; } = StatusTrigger.Keyword;

    /// <summary>The keyword this badge tracks — the ONLY thing you bind for a Keyword-triggered status. Ignored
    /// for Shield Live / Hold Fast Live / Computed.</summary>
    [Export] public Keyword BoundKeyword { get; set; }

    // Advanced — only matters for the three Computed statuses (成长 / 引导增伤 / 引导减费), whose corner number is
    // produced in code keyed by this Id. Keyword statuses can leave it blank.
    [ExportGroup("Advanced (Computed only)")]
    /// <summary>Dispatch key for Computed entries (growth / channel_deepen / channel_discount). Cosmetic for the rest.</summary>
    [Export] public string Id { get; set; } = "";
}
