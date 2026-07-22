using Godot;

namespace HoldTheLine.Game;

/// <summary>
/// One keyword's display text — fully editable in the Godot Inspector via res://data/keyword_catalog.tres
/// (docs/22 批次D4). The rules layer keeps the mechanics; this only carries what the card face shows: the
/// Chinese display name and the explanation line under the detail popup.
/// </summary>
[GlobalClass]
public partial class KeywordDef : Resource
{
    /// <summary>The rules-layer enum member name this entry describes (e.g. "Charge", "Swift").</summary>
    [Export] public string KeywordName { get; set; } = "";

    /// <summary>Chinese display name (e.g. 冲锋). For <see cref="HasValue"/> keywords the card face appends
    /// the spec's number after it (疾行 2) — that concatenation stays in CardView.</summary>
    [Export] public string DisplayName { get; set; } = "";

    /// <summary>Explanation line shown as 【名称】说明 in the card detail popup.</summary>
    [Export(PropertyHint.MultilineText)] public string Description { get; set; } = "";

    /// <summary>True when the keyword carries a per-card number that the display name is suffixed with
    /// (疾行 N / 射程 N); the description then refers to that N in prose.</summary>
    [Export] public bool HasValue { get; set; }
}
