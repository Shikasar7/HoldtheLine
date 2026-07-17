using Godot;

namespace HoldTheLine.Game;

/// <summary>Main menu (plan P4): pick vs-AI (choose your deck) or hotseat, then load the battle.</summary>
public partial class MenuScene : Control
{
    private const string BattlePath = "res://scenes/battle/Battle.tscn";

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        var bg = new ColorRect { Color = BattleTheme.Background };
        bg.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(bg);

        if (BattleTheme.Tex("screens/key_art_main.png") is { } keyArt)
        {
            var art = BattleTheme.Art(keyArt, Vector2.Zero, new Vector2(BattleTheme.ScreenW, BattleTheme.ScreenH));
            art.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            art.Modulate = new Color(0.58f, 0.58f, 0.58f); // dim under menu text
            AddChild(art);
        }

        var title = BattleTheme.MakeOutlinedLabel("守 线", 96, BattleTheme.TextMain, HorizontalAlignment.Center);
        title.Position = new Vector2(0, 130);
        title.Size = new Vector2(BattleTheme.ScreenW, 120);
        AddChild(title);

        var subtitle = BattleTheme.MakeLabel("HOLD THE LINE   ·   原型 Demo", 30, BattleTheme.Accent, HorizontalAlignment.Center);
        subtitle.Position = new Vector2(0, 250);
        subtitle.Size = new Vector2(BattleTheme.ScreenW, 44);
        AddChild(subtitle);

        var sectionAi = BattleTheme.MakeLabel("人机对战 — 选择你的卡组", 26, BattleTheme.TextDim, HorizontalAlignment.Center);
        sectionAi.Position = new Vector2(0, 400);
        sectionAi.Size = new Vector2(BattleTheme.ScreenW, 36);
        AddChild(sectionAi);

        AddButton("以【铁壁】出战  (铁誓军团 · 防守)", new Vector2(660, 456), BattleTheme.SeatColor0,
            () => StartVsAi("iron_wall", "wildpack_hunt"));
        AddButton("以【狂猎】出战  (荒野游群 · 快攻)", new Vector2(660, 542), BattleTheme.SeatColor1,
            () => StartVsAi("wildpack_hunt", "iron_wall"));
        AddButton("以【晚祷】出战  (黄昏教团 · 法术连锁)", new Vector2(660, 628), BattleTheme.SeatColor1,
            () => StartVsAi("duskweaver_vesper", "undervault_sunline"));
        AddButton("以【贯日阵列】出战  (掘世匠会 · 后排火力)", new Vector2(660, 714), BattleTheme.SeatColor0,
            () => StartVsAi("undervault_sunline", "iron_wall"));
        // Faction emblems (art lands in X4; Tex() returns null until then, so these are no-ops meanwhile).
        Emblem("ui/emblem_iron_vow.png", 456);
        Emblem("ui/emblem_wildpack.png", 542);
        Emblem("ui/emblem_duskweaver.png", 628);
        Emblem("ui/emblem_undervault.png", 714);

        AddButton("双人热座对战", new Vector2(660, 806), BattleTheme.AccentSoft, StartHotseat);
        AddButton("联机对战", new Vector2(660, 888), BattleTheme.SeatColor0, ShowOnlinePanel);
        AddButton("退出", new Vector2(660, 970), BattleTheme.PanelDark, () => GetTree().Quit());
    }

    // ---------- online connect panel (M2 N2) ----------

    private string _onlineDeck = "iron_wall";

    private void ShowOnlinePanel()
    {
        var dim = new ColorRect { Color = new Color(0.02f, 0.02f, 0.02f, 0.92f) };
        dim.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(dim);

        void Label(string text, float y, int size, Color color) =>
            dim.AddChild(Positioned(BattleTheme.MakeOutlinedLabel(text, size, color, HorizontalAlignment.Center), new Vector2(0, y), new Vector2(BattleTheme.ScreenW, 44)));

        Label("联 机 对 战", 150, 60, BattleTheme.TextMain);
        Label("同一台服务器上,一人创建房间、另一人用房间号加入", 236, 22, BattleTheme.TextDim);

        const float cx = 660f, fieldW = 600f;
        Label("昵称", 320, 22, BattleTheme.Accent);
        var nick = Field(GameConfig.Nickname == "玩家" ? "" : GameConfig.Nickname, "你的昵称", new Vector2(cx, 356), fieldW);
        dim.AddChild(nick);

        Label("服务器地址", 424, 22, BattleTheme.Accent);
        var url = Field(GameConfig.ServerUrl, "ws://主机IP:5210/ws", new Vector2(cx, 460), fieldW);
        dim.AddChild(url);

        // Deck pick (2x2 grid over all four factions).
        Label("卡组", 524, 22, BattleTheme.Accent);
        (string id, string label, Color color)[] deckOptions =
        [
            ("iron_wall", "铁壁 · 铁誓", BattleTheme.SeatColor0),
            ("wildpack_hunt", "狂猎 · 游群", BattleTheme.SeatColor1),
            ("duskweaver_vesper", "晚祷 · 教团", Color.FromHtml("8b5fa6")),
            ("undervault_sunline", "贯日 · 匠会", Color.FromHtml("b5883f")),
        ];
        var deckBtns = new Button[deckOptions.Length];
        void Repaint()
        {
            for (int i = 0; i < deckOptions.Length; i++)
                BattleTheme.SetButtonBg(deckBtns[i], _onlineDeck == deckOptions[i].id ? deckOptions[i].color : BattleTheme.PanelDark);
        }
        for (int i = 0; i < deckOptions.Length; i++)
        {
            var opt = deckOptions[i];
            var pos = new Vector2(cx + (i % 2) * 310, 556 + (i / 2) * 72);
            deckBtns[i] = Btn(opt.label, pos, new Vector2(290, 64), () => { _onlineDeck = opt.id; Repaint(); });
            dim.AddChild(deckBtns[i]);
        }
        Repaint();

        // Create.
        dim.AddChild(Btn("创建房间", new Vector2(cx, 716), new Vector2(290, 76), () =>
            StartOnline(url.Text, nick.Text, createRoom: true, "")));

        // Join with a room code.
        var code = Field("", "房间号", new Vector2(cx + 310, 716), 180);
        code.AddThemeFontSizeOverride("font_size", 26);
        dim.AddChild(code);
        dim.AddChild(Btn("加入", new Vector2(cx + 500, 716), new Vector2(100, 76), () =>
            StartOnline(url.Text, nick.Text, createRoom: false, code.Text)));

        dim.AddChild(Btn("返回", new Vector2(cx, 838), new Vector2(600, 64), () => dim.QueueFree()));
    }

    private LineEdit Field(string text, string placeholder, Vector2 pos, float width)
    {
        var f = new LineEdit
        {
            Text = text,
            PlaceholderText = placeholder,
            Position = pos,
            Size = new Vector2(width, 60),
        };
        f.AddThemeFontSizeOverride("font_size", 26);
        return f;
    }

    private Button Btn(string text, Vector2 pos, Vector2 size, System.Action onPressed)
    {
        var b = BattleTheme.MakeButton(pos, size, BattleTheme.PanelDark, BattleTheme.Accent, 2, 10);
        b.Text = text;
        b.AddThemeFontSizeOverride("font_size", 24);
        b.Pressed += onPressed;
        return b;
    }

    private static Control Positioned(Control c, Vector2 pos, Vector2 size)
    {
        c.Position = pos;
        c.Size = size;
        return c;
    }

    private void StartOnline(string url, string nick, bool createRoom, string code)
    {
        if (!createRoom && string.IsNullOrWhiteSpace(code))
            return; // need a room code to join
        GameConfig.SetOnline(string.IsNullOrWhiteSpace(url) ? GameConfig.ServerUrl : url.Trim(),
            nick, createRoom, code, _onlineDeck);
        GetTree().ChangeSceneToFile(BattlePath);
    }

    private void Emblem(string texPath, float y)
    {
        if (BattleTheme.Tex(texPath) is { } tex)
            AddChild(BattleTheme.Art(tex, new Vector2(572, y), new Vector2(72, 72), TextureRect.StretchModeEnum.KeepAspectCentered));
    }

    private void AddButton(string text, Vector2 pos, Color color, System.Action onPressed)
    {
        var btn = BattleTheme.MakeButton(pos, new Vector2(600, 72), color, BattleTheme.Accent, 2, 12);
        btn.Text = text;
        btn.AddThemeFontSizeOverride("font_size", 26);
        btn.Pressed += onPressed;
        AddChild(btn);
    }

    private void StartVsAi(string humanDeck, string aiDeck)
    {
        GameConfig.SetVsAi(humanDeck, aiDeck);
        GetTree().ChangeSceneToFile(BattlePath);
    }

    private void StartHotseat()
    {
        GameConfig.SetHotseat();
        GetTree().ChangeSceneToFile(BattlePath);
    }
}
