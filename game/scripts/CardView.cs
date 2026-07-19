using Godot;
using HoldTheLine.Rules.Cards;

namespace HoldTheLine.Game;

/// <summary>
/// Shared, self-contained renderer for a <see cref="CardDefinition"/> — a framed card face plus a full
/// "详细卡牌介绍" popup (art, stats, rules text, keyword explanations, faction lore). Presentation-only:
/// it reads static card data and the placeholder-art atlas through <see cref="BattleTheme"/>, and never
/// touches the authoritative rules internals. Used by the deck editor (browse/inspect) and the in-match
/// "查看牌组" panel, so the two surfaces stay in step with the battle scene's own card look.
/// </summary>
public static class CardView
{
    // ---------- framed card face (mirrors the battle scene's hand-card look) ----------

    /// <summary>Full card face: art, faction frame, cost/atk/hp gems, type badge, name, rules text.</summary>
    public static Control BuildFace(CardDefinition def, Vector2 size, bool backing = true)
    {
        float w = size.X, h = size.Y;
        bool isOrder = def.Type != CardType.Unit;
        var root = new Control { Size = size, MouseFilter = Control.MouseFilterEnum.Ignore };

        if (backing)
        {
            var bg = new Panel { Size = size, MouseFilter = Control.MouseFilterEnum.Ignore };
            bg.AddThemeStyleboxOverride("panel", BattleTheme.Box(BattleTheme.PanelDark,
                isOrder ? BattleTheme.Accent : FactionColor(def.Faction), 3, 12));
            root.AddChild(bg);
        }

        int gem = Mathf.RoundToInt(h * 0.155f);
        int nameSize = Mathf.RoundToInt(h * 0.062f);
        int bodySize = Mathf.RoundToInt(h * 0.048f);

        var art = BattleTheme.Tex($"cards/{def.Id}.png");
        var frame = BattleTheme.Tex($"ui/frame_{def.Faction}.png") ?? BattleTheme.Tex("ui/frame_neutral.png");
        if (art != null && frame != null)
        {
            // Frame art window measured on the generated frames: x 16.5%~84%, y 15.2%~68.8%.
            root.AddChild(BattleTheme.Art(art, new Vector2(w * 0.165f, h * 0.152f), new Vector2(w * 0.675f, h * 0.536f)));
            root.AddChild(BattleTheme.Art(frame, Vector2.Zero, size, TextureRect.StretchModeEnum.Scale));

            var name = BattleTheme.MakeOutlinedLabel(def.Name, nameSize,
                isOrder ? BattleTheme.Accent : BattleTheme.TextMain, HorizontalAlignment.Center);
            name.ClipContents = true;
            name.Position = new Vector2(8, h * 0.64f);
            name.Size = new Vector2(w - 16, nameSize + 10);
            root.AddChild(name);

            if (def.Text.Length > 0)
            {
                var platePos = new Vector2(w * 0.14f, h * 0.715f);
                var plateSize = new Vector2(w * 0.72f, h * 0.19f);
                var plate = new Panel { Position = platePos, Size = plateSize, MouseFilter = Control.MouseFilterEnum.Ignore };
                plate.AddThemeStyleboxOverride("panel", BattleTheme.Box(
                    new Color(0.07f, 0.06f, 0.05f, 0.78f), new Color(0.62f, 0.5f, 0.3f, 0.55f), 1, 8));
                root.AddChild(plate);

                // AutowrapMode BEFORE Size (wrap off → min width = full text width, Size gets clamped up).
                var body = BattleTheme.MakeLabel(BattleTheme.BodyText(def.Text), bodySize,
                    new Color(0.93f, 0.89f, 0.8f), HorizontalAlignment.Center);
                body.AddThemeFontOverride("font", BattleTheme.UiFontBold);
                body.AutowrapMode = TextServer.AutowrapMode.Arbitrary;
                body.VerticalAlignment = VerticalAlignment.Center;
                body.ClipContents = true;
                body.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
                body.Position = platePos + new Vector2(w * 0.02f, 2);
                body.Size = plateSize - new Vector2(w * 0.04f, 4);
                root.AddChild(body);
            }
        }
        else
        {
            var name = BattleTheme.MakeOutlinedLabel(def.Name, nameSize + 2, BattleTheme.TextMain, HorizontalAlignment.Center);
            name.AutowrapMode = TextServer.AutowrapMode.Arbitrary;
            name.ClipContents = true;
            name.Position = new Vector2(8, h * 0.18f);
            name.Size = new Vector2(w - 16, nameSize * 2.6f);
            root.AddChild(name);

            var body = BattleTheme.MakeLabel(BattleTheme.BodyText(def.Text), bodySize + 2, BattleTheme.TextDim, HorizontalAlignment.Center);
            body.AutowrapMode = TextServer.AutowrapMode.Arbitrary;
            body.ClipContents = true;
            body.Position = new Vector2(10, h * 0.42f);
            body.Size = new Vector2(w - 20, h * 0.42f);
            root.AddChild(body);
        }

        root.AddChild(Gem(def.Cost.ToString(), BattleTheme.CostColor, new Vector2(2, 2), BattleTheme.Tex("ui/gem_cost.png"), gem));
        if (!isOrder)
        {
            root.AddChild(Gem(def.Atk.ToString(), BattleTheme.AtkColor, new Vector2(2, h - gem - 2), BattleTheme.Tex("ui/gem_atk.png"), gem));
            root.AddChild(Gem(def.Hp.ToString(), BattleTheme.HpColor, new Vector2(w - gem - 2, h - gem - 2), BattleTheme.Tex("ui/gem_hp.png"), gem));
        }
        else
        {
            var badge = new Panel { Position = new Vector2(w - gem - 2, 2), Size = new Vector2(gem, gem), MouseFilter = Control.MouseFilterEnum.Ignore };
            badge.AddThemeStyleboxOverride("panel", BattleTheme.Box(BattleTheme.AccentSoft, BattleTheme.Accent, 2, gem / 2));
            var glyph = BattleTheme.MakeOutlinedLabel("令", Mathf.RoundToInt(gem * 0.5f), Colors.White, HorizontalAlignment.Center);
            glyph.Size = new Vector2(gem, gem);
            badge.AddChild(glyph);
            root.AddChild(badge);
        }
        return root;
    }

    /// <summary>A cost/atk/hp gem — the gem texture if present, else a colored disc — with the number on top.</summary>
    public static Control Gem(string text, Color color, Vector2 pos, Texture2D? gemTex, int size)
    {
        var holder = new Control { Position = pos, Size = new Vector2(size, size), MouseFilter = Control.MouseFilterEnum.Ignore };
        if (gemTex != null)
            holder.AddChild(BattleTheme.Art(gemTex, Vector2.Zero, new Vector2(size, size), TextureRect.StretchModeEnum.KeepAspectCentered));
        else
        {
            var disc = new Panel { Size = new Vector2(size, size), MouseFilter = Control.MouseFilterEnum.Ignore };
            disc.AddThemeStyleboxOverride("panel", BattleTheme.Box(color.Darkened(0.1f), Colors.White, 1, size / 2));
            holder.AddChild(disc);
        }
        var n = BattleTheme.MakeOutlinedLabel(text, Mathf.RoundToInt(size * 0.55f), Colors.White, HorizontalAlignment.Center);
        n.Size = new Vector2(size, size);
        holder.AddChild(n);
        return holder;
    }

    // ---------- detail popup (full "详细卡牌介绍") ----------

    private const float PanelW = 560f;
    private const float PanelH = 812f;

    /// <summary>Show a modal card-detail overlay over <paramref name="host"/>: art, name, rarity/faction/type,
    /// cost/atk/hp gems, rules text, per-keyword explanations, and faction lore. Click the dim backdrop or ✕ to close.</summary>
    public static void ShowDetailPopup(Control host, CardDefinition def)
    {
        var dim = new ColorRect { Color = new Color(0.02f, 0.02f, 0.02f, 0.72f) };
        dim.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        host.AddChild(dim);

        // Backdrop swallows clicks and closes the popup.
        var backBtn = new Button { Flat = true };
        backBtn.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        backBtn.Pressed += dim.QueueFree;
        dim.AddChild(backBtn);

        float px = (BattleTheme.ScreenW - PanelW) / 2f;
        float py = (BattleTheme.ScreenH - PanelH) / 2f;
        var panel = new Panel { Position = new Vector2(px, py), Size = new Vector2(PanelW, PanelH) };
        panel.AddThemeStyleboxOverride("panel", BattleTheme.Box(BattleTheme.PanelDark, BattleTheme.Accent, 2, 14));
        panel.MouseFilter = Control.MouseFilterEnum.Stop; // clicks on the card must not fall through to the backdrop
        dim.AddChild(panel);

        FillDetail(panel, def);

        var close = BattleTheme.MakeButton(new Vector2(PanelW - 48, 12), new Vector2(38, 38), BattleTheme.PanelDark, BattleTheme.TextDim, 1, 8);
        close.Text = "✕";
        close.AddThemeFontSizeOverride("font_size", 22);
        close.Pressed += dim.QueueFree;
        panel.AddChild(close);
    }

    /// <summary>Lay the card detail into <paramref name="panel"/> (its own local coordinates). Shared by the
    /// popup above and any host that wants to embed the detail directly.</summary>
    public static void FillDetail(Panel panel, CardDefinition def)
    {
        bool isOrder = def.Type != CardType.Unit;
        const float pad = 18f;
        float innerW = PanelW - pad * 2;
        var faction = FactionColor(def.Faction);

        // Card art.
        const float artH = 288f;
        if (BattleTheme.Tex($"cards/{def.Id}.png") is { } artTex)
            panel.AddChild(BattleTheme.Art(artTex, new Vector2(pad, pad), new Vector2(innerW, artH)));
        else
        {
            panel.AddChild(new ColorRect { Color = faction.Darkened(0.2f), Position = new Vector2(pad, pad), Size = new Vector2(innerW, artH), MouseFilter = Control.MouseFilterEnum.Ignore });
            var ph = BattleTheme.MakeOutlinedLabel(def.Name, 34, new Color(1, 1, 1, 0.85f), HorizontalAlignment.Center);
            ph.AutowrapMode = TextServer.AutowrapMode.Arbitrary;
            ph.Position = new Vector2(pad, pad);
            ph.Size = new Vector2(innerW, artH);
            panel.AddChild(ph);
        }

        // Name over the art's lower edge, on a soft dark strip.
        var nameStrip = new ColorRect { Color = new Color(0.05f, 0.045f, 0.04f, 0.62f), Position = new Vector2(pad, pad + artH - 46), Size = new Vector2(innerW, 46), MouseFilter = Control.MouseFilterEnum.Ignore };
        panel.AddChild(nameStrip);
        var nameL = BattleTheme.MakeOutlinedLabel(def.Name, 26, isOrder ? BattleTheme.Accent : BattleTheme.TextMain);
        nameL.Position = new Vector2(pad + 14, pad + artH - 44);
        nameL.Size = new Vector2(innerW * 0.6f, 42);
        panel.AddChild(nameL);
        var metaL = BattleTheme.MakeOutlinedLabel($"{RarityName(def.Rarity)} · {FactionName(def.Faction)} · {TypeName(def.Type)}", 14, BattleTheme.TextDim, HorizontalAlignment.Right);
        metaL.Position = new Vector2(pad + innerW * 0.44f - 14, pad + artH - 42);
        metaL.Size = new Vector2(innerW * 0.56f, 38);
        panel.AddChild(metaL);

        // Stats row.
        float y = pad + artH + 14;
        var stats = isOrder
            ? new (string Num, string Caption, Color Color, Texture2D? Gem)[] { (def.Cost.ToString(), "辉尘", BattleTheme.CostColor, BattleTheme.Tex("ui/gem_cost.png")) }
            : new (string Num, string Caption, Color Color, Texture2D? Gem)[]
            {
                (def.Cost.ToString(), "辉尘", BattleTheme.CostColor, BattleTheme.Tex("ui/gem_cost.png")),
                (def.Atk.ToString(), "攻击", BattleTheme.AtkColor, BattleTheme.Tex("ui/gem_atk.png")),
                (def.Hp.ToString(), "生命", BattleTheme.HpColor, BattleTheme.Tex("ui/gem_hp.png")),
            };
        float sx = pad + 6;
        foreach (var (num, caption, color, gemTex) in stats)
        {
            panel.AddChild(Gem(num, color, new Vector2(sx, y), gemTex, 46));
            var cap = BattleTheme.MakeLabel(caption, 17, BattleTheme.TextMain);
            cap.Position = new Vector2(sx + 54, y + 11);
            cap.Size = new Vector2(110, 24);
            panel.AddChild(cap);
            sx += 172;
        }
        y += 46 + 16;

        // Rules text.
        if (def.Text.Length > 0)
        {
            string bodyText = BattleTheme.BodyText(def.Text);
            float plateH = 26f + 26f * Mathf.Ceil(bodyText.Length / 26f);
            var plate = new Panel { Position = new Vector2(pad, y), Size = new Vector2(innerW, plateH), MouseFilter = Control.MouseFilterEnum.Ignore };
            plate.AddThemeStyleboxOverride("panel", BattleTheme.Box(
                new Color(0.07f, 0.06f, 0.05f, 0.7f), new Color(0.62f, 0.5f, 0.3f, 0.45f), 1, 8));
            panel.AddChild(plate);
            var body = BattleTheme.MakeLabel(bodyText, 17, new Color(0.93f, 0.89f, 0.8f), HorizontalAlignment.Center);
            body.AddThemeFontOverride("font", BattleTheme.UiFontBold);
            body.AutowrapMode = TextServer.AutowrapMode.Arbitrary;
            body.VerticalAlignment = VerticalAlignment.Center;
            body.ClipContents = true;
            body.Position = new Vector2(pad + 12, y + 2);
            body.Size = new Vector2(innerW - 24, plateH - 4);
            panel.AddChild(body);
            y += plateH + 12;
        }

        // Keyword explanations.
        foreach (var k in def.Keywords)
        {
            var kwl = BattleTheme.MakeLabel($"【{KeywordName(k)}】{BattleTheme.BodyText(KeywordDesc(k.Keyword))}", 15, BattleTheme.Accent);
            kwl.AutowrapMode = TextServer.AutowrapMode.Arbitrary;
            kwl.VerticalAlignment = VerticalAlignment.Top;
            kwl.ClipContents = true;
            kwl.Position = new Vector2(pad + 4, y);
            kwl.Size = new Vector2(innerW - 8, 46);
            panel.AddChild(kwl);
            y += 50;
        }

        // Faction lore pinned to the bottom — skipped when the content above needs the room.
        if (y < PanelH - 80)
        {
            var lore = BattleTheme.MakeLabel(FactionLore(def.Faction), 13, BattleTheme.TextDim);
            lore.AutowrapMode = TextServer.AutowrapMode.Arbitrary;
            lore.VerticalAlignment = VerticalAlignment.Bottom;
            lore.ClipContents = true;
            lore.Position = new Vector2(pad, PanelH - 70);
            lore.Size = new Vector2(innerW, 56);
            panel.AddChild(lore);
        }
    }

    // ---------- shared card-data display strings (the canonical copy for editor + battle surfaces) ----------

    public static Color FactionColor(string faction) => faction switch
    {
        "iron_vow" => BattleTheme.SeatColor0,
        "wildpack" => BattleTheme.SeatColor1,
        "duskweaver" => Color.FromHtml("8b5fa6"),
        "undervault" => Color.FromHtml("b5883f"),
        _ => BattleTheme.TextDim,
    };

    public static string FactionName(string faction) => faction switch
    {
        "iron_vow" => "铁誓军团",
        "wildpack" => "荒野游群",
        "duskweaver" => "黄昏教团",
        "undervault" => "掘世匠会",
        _ => "中立",
    };

    public static string TypeName(CardType type) => type switch
    {
        CardType.Unit => "随从",
        CardType.Order => "指令",
        _ => "其他",
    };

    public static string RarityName(Rarity r) => r switch
    {
        Rarity.Common => "普通",
        Rarity.Rare => "稀有",
        Rarity.Epic => "史诗",
        Rarity.Legendary => "传说",
        _ => "衍生",
    };

    public static string FactionLore(string faction) => faction switch
    {
        "iron_vow" => "铁誓军团 —— 誓约骑士与堡垒工程师,断层战争中最后的正规军。以墙为盾,寸土不让。",
        "wildpack" => "荒野游群 —— 兽人与掠猎兽骑手,在断层荒原上以速度为生存法则。风过之处,防线洞开。",
        "duskweaver" => "黄昏教团 —— 焚火祭司与灰烬信徒,以格、行、列为祭坛的法术连锁者。误伤友军是代价,也是燃料。",
        "undervault" => "掘世匠会 —— 掘地矮人与蒸汽工程师,把阵型钉死成答案。架起炮台,隔墙点名。",
        _ => "中立 —— 游荡在断层各段防线之间的雇佣兵、民兵与工匠,为辉尘而战。",
    };

    public static string KeywordName(KeywordSpec k) => k.Keyword switch
    {
        Keyword.Swift => $"疾行 {k.Value}",
        Keyword.Range => $"射程 {k.Value}",
        _ => KeywordName0(k.Keyword),
    };

    private static string KeywordName0(Keyword k) => k switch
    {
        Keyword.Charge => "冲锋",
        Keyword.Assault => "突袭",
        Keyword.Guard => "守护",
        Keyword.HoldFast => "坚守",
        Keyword.Trample => "践踏",
        Keyword.CheapShot => "偷袭",
        Keyword.Shield => "持盾",
        Keyword.Garrison => "驻防",
        Keyword.Leap => "跃障",
        Keyword.PackTactics => "围猎",
        Keyword.Hidden => "伏兵",
        Keyword.Emplacement => "架设",
        Keyword.Pierce => "贯穿",
        _ => k.ToString(),
    };

    public static string KeywordDesc(Keyword k) => k switch
    {
        Keyword.Charge => "部署当回合即可移动与攻击。",
        Keyword.Assault => "部署当回合可攻击,但不能移动。",
        Keyword.Swift => "每回合可移动的格数提升。",
        Keyword.Range => "可攻击 N 步(横纵相加)内的任意敌人,越过其他随从;仅当目标能反击到你(在其射程/相邻内)时才吃反击。",
        Keyword.Guard => "与其相邻的敌方随从必须优先攻击它。",
        Keyword.HoldFast => "本回合未移动时,受到的伤害 -1。",
        Keyword.Trample => "近战攻击时,对目标周围相邻的所有单位(含友方)也造成等量伤害。",
        Keyword.CheapShot => "近战攻击不受反击。",
        Keyword.Shield => "免疫下一次受到的伤害。",
        Keyword.Garrison => "位于己方底线行时 +1/+1。",
        Keyword.Leap => "移动时可跨过一个随从,直线跳跃 2 格。",
        Keyword.PackTactics => "近战攻击一个与你另一友方相邻的敌人时,伤害 +2。",
        Keyword.Hidden => "不能被选为目标,直到它造成伤害。",
        Keyword.Emplacement => "架设:不能移动;受到指令/技能/战吼等效果伤害 +1(普通攻击不加)。",
        Keyword.Pierce => "贯穿:远程攻击时,同时对目标正后方一格的随从(不分敌我)造成等额伤害。",
        _ => "",
    };
}
