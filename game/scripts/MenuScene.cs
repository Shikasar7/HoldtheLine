using Godot;
using HoldTheLine.Net.Protocol;

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

    // ---------- online lobby (M3 C1): connect → profile → ranked queue / friend rooms ----------

    private string _lobbyDeck = "iron_wall";
    private const float Cx = 660f;

    private static readonly (string Id, string Label, Color Color)[] DeckOptions =
    [
        ("iron_wall", "铁壁 · 铁誓", BattleTheme.SeatColor0),
        ("wildpack_hunt", "狂猎 · 游群", BattleTheme.SeatColor1),
        ("duskweaver_vesper", "晚祷 · 教团", Color.FromHtml("8b5fa6")),
        ("undervault_sunline", "贯日 · 匠会", Color.FromHtml("b5883f")),
    ];

    /// <summary>Entry from the main menu: straight to the lobby if the session is up, else connect first.</summary>
    private void ShowOnlinePanel()
    {
        if (Session.Connected) ShowLobby();
        else ShowConnect();
    }

    private ColorRect? _panel;

    private ColorRect NewPanel()
    {
        CloseOverlay(); // free the previous overlay so panels don't stack / bleed through
        var dim = new ColorRect { Color = new Color(0.02f, 0.02f, 0.02f, 0.98f) };
        dim.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(dim);
        _panel = dim;
        return dim;
    }

    private void CloseOverlay()
    {
        if (_panel is { } p && GodotObject.IsInstanceValid(p))
            p.QueueFree();
        _panel = null;
    }

    private static Label PanelLabel(Control parent, string text, float y, int size, Color color)
    {
        var l = BattleTheme.MakeOutlinedLabel(text, size, color, HorizontalAlignment.Center);
        Positioned(l, new Vector2(0, y), new Vector2(BattleTheme.ScreenW, 46));
        parent.AddChild(l);
        return l;
    }

    private void ShowConnect()
    {
        var p = NewPanel();
        PanelLabel(p, "联 机 · 连接", 150, 60, BattleTheme.TextMain);
        PanelLabel(p, "登录后可排位匹配、创建/加入好友房间", 236, 22, BattleTheme.TextDim);

        PanelLabel(p, "昵称", 340, 22, BattleTheme.Accent);
        var nick = Field(GameConfig.Nickname == "玩家" ? "" : GameConfig.Nickname, "你的昵称", new Vector2(Cx, 376), 600);
        p.AddChild(nick);
        PanelLabel(p, "服务器地址", 456, 22, BattleTheme.Accent);
        var url = Field(GameConfig.ServerUrl, "ws://主机IP:5210/ws", new Vector2(Cx, 492), 600);
        p.AddChild(url);

        var status = PanelLabel(p, "", 610, 24, BattleTheme.TextDim);
        p.AddChild(Btn("连接", new Vector2(Cx, 664), new Vector2(290, 76), async () =>
        {
            status.Text = "连接中…";
            status.AddThemeColorOverride("font_color", BattleTheme.TextDim);
            GameConfig.Nickname = string.IsNullOrWhiteSpace(nick.Text) ? "玩家" : nick.Text.Trim();
            GameConfig.ServerUrl = string.IsNullOrWhiteSpace(url.Text) ? GameConfig.ServerUrl : url.Text.Trim();
            var err = await Session.ConnectAsync(GameConfig.ServerUrl, GameConfig.Nickname);
            if (err is null) ShowLobby();
            else { status.Text = $"连接失败:{err}"; status.AddThemeColorOverride("font_color", BattleTheme.DangerColor); }
        }));
        p.AddChild(Btn("返回", new Vector2(Cx + 310, 664), new Vector2(290, 76), CloseOverlay));
    }

    private void ShowLobby()
    {
        var p = NewPanel();
        var pf = Session.Profile;
        PanelLabel(p, pf != null ? pf.Name : "已连接", 150, 56, BattleTheme.TextMain);
        PanelLabel(p, pf != null ? $"评分 {pf.Rating}   ·   胜 {pf.Wins} / 负 {pf.Losses}" : "", 224, 26, BattleTheme.Accent);

        PanelLabel(p, "当前卡组", 300, 22, BattleTheme.TextDim);
        var deckBtns = new Button[DeckOptions.Length];
        void Repaint() { for (int i = 0; i < DeckOptions.Length; i++) BattleTheme.SetButtonBg(deckBtns[i], _lobbyDeck == DeckOptions[i].Id ? DeckOptions[i].Color : BattleTheme.PanelDark); }
        for (int i = 0; i < DeckOptions.Length; i++)
        {
            var opt = DeckOptions[i];
            deckBtns[i] = Btn(opt.Label, new Vector2(Cx + (i % 2) * 310, 334 + (i / 2) * 66), new Vector2(290, 58), () => { _lobbyDeck = opt.Id; Repaint(); });
            p.AddChild(deckBtns[i]);
        }
        Repaint();

        p.AddChild(Btn("排位匹配", new Vector2(Cx, 486), new Vector2(600, 76), StartQueue));
        p.AddChild(Btn("好友房间", new Vector2(Cx, 574), new Vector2(600, 68), ShowFriendRoom));
        p.AddChild(Btn("卡组编辑 (施工中 · C2)", new Vector2(Cx, 654), new Vector2(600, 60), () => { }));
        p.AddChild(Btn("断开连接", new Vector2(Cx, 726), new Vector2(290, 60), async () => { await Session.DisconnectAsync(); CloseOverlay(); }));
        p.AddChild(Btn("返回", new Vector2(Cx + 310, 726), new Vector2(290, 60), CloseOverlay));
    }

    private async void StartQueue()
    {
        Session.ArmMatchHost();
        var remote = Session.Remote!;
        var p = NewPanel();
        PanelLabel(p, "排位匹配中…", 380, 48, BattleTheme.Accent);
        var status = PanelLabel(p, "正在寻找对手", 460, 26, BattleTheme.TextDim);

        void OnStatus(QueueStatus q) => Callable.From(() =>
        {
            if (GodotObject.IsInstanceValid(status))
                status.Text = q.BotFallbackIn is > 0 ? $"已等待 {q.WaitedSeconds}s   ·   {q.BotFallbackIn}s 后练习赛保底" : $"已等待 {q.WaitedSeconds}s";
        }).CallDeferred();
        Session.QueueStatusReceived += OnStatus;

        p.AddChild(Btn("取消", new Vector2(Cx, 540), new Vector2(600, 64), async () =>
        {
            Session.QueueStatusReceived -= OnStatus;
            await Session.SendAsync(new LeaveQueue());
            ShowLobby();
        }));

        try
        {
            await Session.SendAsync(new JoinQueue { DeckId = _lobbyDeck });
            await remote.WaitForMatchAsync();
            Session.QueueStatusReceived -= OnStatus;
            Callable.From(GoToBattleOnline).CallDeferred();
        }
        catch (System.Exception ex)
        {
            Session.QueueStatusReceived -= OnStatus;
            SetStatusDeferred(status, $"匹配失败:{ex.Message}");
        }
    }

    private void ShowFriendRoom()
    {
        var p = NewPanel();
        PanelLabel(p, "好友房间", 200, 52, BattleTheme.TextMain);
        PanelLabel(p, "一人创建房间,把房间号发给另一人加入", 276, 22, BattleTheme.TextDim);
        var status = PanelLabel(p, "", 520, 24, BattleTheme.Accent);

        p.AddChild(Btn("创建房间", new Vector2(Cx, 360), new Vector2(600, 76), () => HostRoom(status)));
        var code = Field("", "房间号", new Vector2(Cx, 464), 380);
        code.AddThemeFontSizeOverride("font_size", 26);
        p.AddChild(code);
        p.AddChild(Btn("加入", new Vector2(Cx + 400, 464), new Vector2(200, 60), () => JoinRoomCode(code.Text, status)));
        p.AddChild(Btn("返回", new Vector2(Cx, 600), new Vector2(600, 60), ShowLobby));
    }

    private async void HostRoom(Label status)
    {
        Session.ArmMatchHost();
        var remote = Session.Remote!;
        void OnCreated(RoomCreated rc) => SetStatusDeferred(status, $"房间号  {rc.Code}   ·   等待朋友加入…");
        Session.RoomCreatedOk += OnCreated;
        try
        {
            await Session.SendAsync(new CreateRoom { DeckId = _lobbyDeck });
            await remote.WaitForMatchAsync();
            Session.RoomCreatedOk -= OnCreated;
            Callable.From(GoToBattleOnline).CallDeferred();
        }
        catch (System.Exception ex) { Session.RoomCreatedOk -= OnCreated; SetStatusDeferred(status, $"失败:{ex.Message}"); }
    }

    private async void JoinRoomCode(string code, Label status)
    {
        if (string.IsNullOrWhiteSpace(code)) return;
        Session.ArmMatchHost();
        var remote = Session.Remote!;
        void OnErr(ErrorMsg e) => SetStatusDeferred(status, $"错误:{e.Code}");
        Session.Errored += OnErr;
        try
        {
            await Session.SendAsync(new JoinRoom { Code = code.Trim().ToUpperInvariant(), DeckId = _lobbyDeck });
            await remote.WaitForMatchAsync();
            Session.Errored -= OnErr;
            Callable.From(GoToBattleOnline).CallDeferred();
        }
        catch (System.Exception ex) { Session.Errored -= OnErr; SetStatusDeferred(status, $"失败:{ex.Message}"); }
    }

    private static void SetStatusDeferred(Label status, string text) =>
        Callable.From(() => { if (GodotObject.IsInstanceValid(status)) status.Text = text; }).CallDeferred();

    private void GoToBattleOnline()
    {
        GameConfig.SetOnlineAttached();
        GetTree().ChangeSceneToFile(BattlePath);
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
