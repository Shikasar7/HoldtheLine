using Godot;

namespace HoldTheLine.Game;

/// <summary>
/// The editable table of faction display metadata (docs/22 批次D4). Edit res://data/faction_catalog.tres in
/// the Godot Inspector — names, short tags, lore, tint colours, card-frame file names — with no code change
/// or recompile. Loaded once and cached; if the .tres is missing or broken it falls back to
/// <see cref="BuildDefault"/>, which is byte-for-byte the old hardcoded switch values (also the source used
/// to (re)generate the .tres).
/// </summary>
[GlobalClass]
public partial class FactionCatalog : Resource
{
    [Export] public Godot.Collections.Array<FactionDef> Factions { get; set; } = new();

    private static FactionCatalog? _cached;

    /// <summary>The catalog every display surface reads: .tres if present, else the built-in default.</summary>
    public static FactionCatalog Instance => _cached ??= GameData.LoadFactionCatalog();

    /// <summary>Entry for a faction id, or null when unknown.</summary>
    public FactionDef? Find(string id)
    {
        foreach (var f in Factions)
            if (f is not null && f.Id == id)
                return f;
        return null;
    }

    public static FactionDef? Get(string faction) => Instance.Find(faction);

    /// <summary>Entry for a faction id, falling back to the neutral entry — mirrors the old switches' default
    /// branch, where every unknown faction rendered as 中立.</summary>
    public static FactionDef? GetOrNeutral(string faction) => Instance.Find(faction) ?? Instance.Find("neutral");

    /// <summary>The pre-catalog hardcoded values (CardView.FactionName/FactionColor/FactionLore +
    /// CardArtEditor/BattleScene.FactionMark + the frame_{faction}.png convention as of v0.7.5), rebuilt as
    /// data. Kept as the runtime fallback AND as the canonical content of res://data/faction_catalog.tres.</summary>
    public static FactionCatalog BuildDefault()
    {
        var cat = new FactionCatalog();
        void Add(string id, string name, string mark, string lore, Color color, string frame) =>
            cat.Factions.Add(new FactionDef
            {
                Id = id, DisplayName = name, ShortMark = mark, Lore = lore,
                PrimaryColor = color, FrameTexture = frame,
            });

        Add("iron_vow", "铁誓军团", "铁誓",
            "铁誓军团 —— 誓约骑士与堡垒工程师,断层战争中最后的正规军。以墙为盾,寸土不让。",
            BattleTheme.SeatColor0, "frame_iron_vow.png");
        Add("wildpack", "荒野游群", "游群",
            "荒野游群 —— 兽人与掠猎兽骑手,在断层荒原上以速度为生存法则。风过之处,防线洞开。",
            BattleTheme.SeatColor1, "frame_wildpack.png");
        Add("duskweaver", "黄昏教团", "教团",
            "黄昏教团 —— 焚火祭司与灰烬信徒,以格、行、列为祭坛的法术连锁者。误伤友军是代价,也是燃料。",
            Color.FromHtml("8b5fa6"), "frame_duskweaver.png");
        Add("undervault", "掘世匠会", "匠会",
            "掘世匠会 —— 掘地矮人与蒸汽工程师,把阵型钉死成答案。架起炮台,隔墙点名。",
            Color.FromHtml("b5883f"), "frame_undervault.png");
        Add("neutral", "中立", "中立",
            "中立 —— 游荡在断层各段防线之间的雇佣兵、民兵与工匠,为辉尘而战。",
            BattleTheme.TextDim, "frame_neutral.png");
        return cat;
    }
}
