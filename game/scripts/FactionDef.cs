using Godot;

namespace HoldTheLine.Game;

/// <summary>
/// One faction's display metadata — fully editable in the Godot Inspector via res://data/faction_catalog.tres
/// (docs/22 批次D4). Everything the client surfaces about a faction (full name, short tag, lore blurb, tint
/// colour, card frame art) lives here instead of scattered switch statements; edit the .tres, no recompile.
/// </summary>
[GlobalClass]
public partial class FactionDef : Resource
{
    /// <summary>Stable faction id used by card data (iron_vow / wildpack / duskweaver / undervault / neutral).</summary>
    [Export] public string Id { get; set; } = "";

    /// <summary>Full display name shown on card faces and detail popups (e.g. 铁誓军团).</summary>
    [Export] public string DisplayName { get; set; } = "";

    /// <summary>Two-character short tag for compact surfaces — hotseat 交接提示, editor card list (e.g. 铁誓).</summary>
    [Export] public string ShortMark { get; set; } = "";

    /// <summary>Faction lore line shown at the bottom of the card detail popup.</summary>
    [Export(PropertyHint.MultilineText)] public string Lore { get; set; } = "";

    /// <summary>Signature tint: card border colour, deck-row tint in menus.</summary>
    [Export] public Color PrimaryColor { get; set; } = Colors.White;

    /// <summary>Card frame texture file name under res://assets/art/ui (e.g. frame_iron_vow.png).</summary>
    [Export] public string FrameTexture { get; set; } = "";
}
