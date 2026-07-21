using Godot;
using HoldTheLine.Rules.Cards;

namespace HoldTheLine.Game;

/// <summary>
/// The editable table of every status badge the battle board can show. Edit res://data/status_catalog.tres in
/// the Godot Inspector — expand <see cref="Statuses"/>, add or remove entries, drag icons, pick Buff/Debuff and
/// order — to manage which statuses appear, with no code change or recompile. The battle scene reads this at
/// load; if the .tres is missing it falls back to <see cref="BuildDefault"/>, which is byte-for-byte the old
/// hardcoded list (also the source used to (re)generate the .tres).
/// </summary>
[GlobalClass]
public partial class StatusCatalog : Resource
{
    [Export] public Godot.Collections.Array<StatusDef> Statuses { get; set; } = new();

    private static Texture2D? Ui(string file)
    {
        string path = $"res://assets/art/ui/{file}";
        return ResourceLoader.Exists(path) ? GD.Load<Texture2D>(path) : null;
    }

    /// <summary>The pre-catalog hardcoded set (BattleScene.StandeeStatuses as of v0.7.5), rebuilt as data. Kept as
    /// the runtime fallback AND as the canonical content when saving res://data/status_catalog.tres.</summary>
    public static StatusCatalog BuildDefault()
    {
        var cat = new StatusCatalog();
        void Add(string id, string glyph, StatusSide side, StatusTrigger trigger, int order,
                 Keyword keyword = default, string? icon = null) =>
            cat.Statuses.Add(new StatusDef
            {
                Id = id, Glyph = glyph, Side = side, Trigger = trigger, Order = order,
                BoundKeyword = keyword, Icon = icon is null ? null : Ui(icon),
            });

        // ---- buffs (left column, top → bottom) ----
        Add("shield",          "盾", StatusSide.Buff, StatusTrigger.ShieldLive,   0);                                   // 持盾
        Add("blessing",        "福", StatusSide.Buff, StatusTrigger.Keyword,      1, Keyword.Blessing);                 // 福泽
        Add("hold_fast",       "坚", StatusSide.Buff, StatusTrigger.HoldFastLive, 2);                                   // 坚守 (only while in effect)
        Add("molten_sword",    "剑", StatusSide.Buff, StatusTrigger.Keyword,      3, Keyword.MoltenSword, "status_molten_sword.png"); // 熔岩巨剑
        Add("spell_ward",      "罩", StatusSide.Buff, StatusTrigger.Keyword,      4, Keyword.SpellWard,   "status_spell_ward.png");    // 法术护体
        Add("hidden",          "潜", StatusSide.Buff, StatusTrigger.Keyword,      5, Keyword.Hidden,      "status_hidden.png");        // 潜行
        Add("growth",          "长", StatusSide.Buff, StatusTrigger.Computed,     6, icon: "status_growth.png");        // 成长倒数 (corner in code)
        Add("channel_deepen",  "增", StatusSide.Buff, StatusTrigger.Computed,     7);                                   // 引导增伤 N
        Add("channel_discount","减", StatusSide.Buff, StatusTrigger.Computed,     8);                                   // 引导减费 N
        // ---- debuffs (right column) ----
        Add("rooted",          "缚", StatusSide.Debuff, StatusTrigger.Keyword,    0, Keyword.Rooted,      "status_rooted.png");        // 定身
        return cat;
    }
}
