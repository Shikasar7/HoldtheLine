using System.Collections.Generic;
using System.Linq;
using Godot;
using HoldTheLine.Net;
using HoldTheLine.Net.Protocol;
using HoldTheLine.Rules;
using HoldTheLine.Rules.Cards;

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
        AddButtonSized("联机对战", new Vector2(660, 888), new Vector2(290, 72), BattleTheme.SeatColor0, ShowOnlinePanel);
        AddButtonSized("卡组管理", new Vector2(970, 888), new Vector2(290, 72), Color.FromHtml("8b5fa6"), ShowDeckManager);
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
        // Built-in starter decks first, then the player's saved decks (from the last profile push).
        var options = new List<(string Id, string Label, Color Color)>();
        foreach (var d in DeckOptions) options.Add((d.Id, d.Label, d.Color));
        if (pf != null)
            foreach (var d in pf.Decks) options.Add((d.Id, d.Name, FactionTint(d.Faction)));
        if (options.All(o => o.Id != _lobbyDeck)) _lobbyDeck = options[0].Id; // deleted deck → fall back

        var deckBtns = new Button[options.Count];
        void Repaint() { for (int i = 0; i < options.Count; i++) BattleTheme.SetButtonBg(deckBtns[i], _lobbyDeck == options[i].Id ? options[i].Color : BattleTheme.PanelDark); }
        for (int i = 0; i < options.Count; i++)
        {
            var opt = options[i];
            var pos = new Vector2(Cx + (i % 2) * 310, 334 + (i / 2) * 66);
            deckBtns[i] = Btn(opt.Label, pos, new Vector2(290, 58), () => { _lobbyDeck = opt.Id; Repaint(); });
            p.AddChild(deckBtns[i]);
            // Saved decks (after the builtins) get an edit chip — Profile.Decks carries the full card
            // list (protocol v3), so the editor can open it directly, no extra round-trip.
            if (i >= DeckOptions.Length && pf != null)
            {
                var ds = pf.Decks[i - DeckOptions.Length];
                var edit = Btn("改", new Vector2(pos.X + 240, pos.Y + 5), new Vector2(46, 48), () => EditServerDeck(ds));
                edit.AddThemeFontSizeOverride("font_size", 18);
                p.AddChild(edit);
            }
        }
        Repaint();

        float y = 334 + ((options.Count + 1) / 2) * 66 + 16;
        p.AddChild(Btn("排位匹配", new Vector2(Cx, y), new Vector2(600, 76), StartQueue));
        p.AddChild(Btn("好友房间", new Vector2(Cx, y + 88), new Vector2(600, 68), ShowFriendRoom));
        p.AddChild(Btn("卡组编辑", new Vector2(Cx, y + 164), new Vector2(290, 60), OpenDeckEditor));
        p.AddChild(Btn("天梯排行", new Vector2(Cx + 310, y + 164), new Vector2(290, 60), ShowLadder));
        p.AddChild(Btn("断开连接", new Vector2(Cx, y + 236), new Vector2(290, 60), async () => { await Session.DisconnectAsync(); CloseOverlay(); }));
        p.AddChild(Btn("返回", new Vector2(Cx + 310, y + 236), new Vector2(290, 60), CloseOverlay));
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
        // leave_room: a room created here would otherwise stay open after backing out — the friend joining
        // later would start a match no one is waiting on (stale WaitForMatchAsync continuation fires).
        p.AddChild(Btn("返回", new Vector2(Cx, 600), new Vector2(600, 60), () => { _ = Session.SendAsync(new LeaveRoom()); ShowLobby(); }));
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
        GameConfig.LocalDeckCards = ResolveDeckCards(_lobbyDeck); // so the in-match 查看牌组 can show it
        GetTree().ChangeSceneToFile(BattlePath);
    }

    /// <summary>Resolve a lobby deck id to its flat card list: a built-in preconstructed deck, else a saved
    /// deck from the last profile push. Null if unknown (the 查看牌组 panel then shows "unavailable").</summary>
    private static IReadOnlyList<string>? ResolveDeckCards(string deckId)
    {
        var builtin = GameData.LoadDecks().FirstOrDefault(d => d.Id == deckId);
        if (builtin != null) return builtin.Expand();
        var saved = Session.Profile?.Decks.FirstOrDefault(d => d.Id == deckId);
        return saved?.CardIds;
    }

    /// <summary>Open the deck editor (M3 C2). Session is static, so the connection survives the scene swap;
    /// on save the editor returns to the menu, where re-opening the lobby shows the refreshed deck list.</summary>
    private void OpenDeckEditor()
    {
        DeckEditContext.Editing = null; // new deck; editing an existing one goes through the per-deck 改 chip
        GetTree().ChangeSceneToFile("res://scenes/menu/Deck.tscn");
    }

    /// <summary>Edit a server deck: link it to its local copy (by server id) so a save updates both, adopting
    /// it into local storage on first edit when there's no local copy yet.</summary>
    private void EditServerDeck(DeckSummary ds)
    {
        var local = DeckStorage.LoadAll().FirstOrDefault(d => d.ServerId == ds.Id);
        DeckEditContext.Editing = new DeckEditContext.Deck(local?.Id ?? DeckStorage.NewId(), ds.Name, ds.Faction, ds.CardIds, ds.Id);
        GetTree().ChangeSceneToFile("res://scenes/menu/Deck.tscn");
    }

    // ---------- deck manager (local storage: multiple decks, edit / rename / copy / delete / vs-AI) ----------

    private void ShowDeckManager()
    {
        var p = NewPanel();
        PanelLabel(p, "卡 组 管 理", 66, 52, BattleTheme.TextMain);
        PanelLabel(p, "本地卡组:编辑 / 改名 / 复制 / 口令 / 删除,或直接用于人机对战", 144, 22, BattleTheme.TextDim);
        // Transient status line under the title (口令 copy / import feedback), reused by the 口令 row buttons.
        var status = PanelLabel(p, "", 192, 22, BattleTheme.Accent);

        var decks = DeckStorage.LoadAll();
        var scroll = new ScrollContainer { Position = new Vector2(380, 224), Size = new Vector2(1160, 672) };
        scroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
        p.AddChild(scroll);
        var list = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        list.AddThemeConstantOverride("separation", 10);
        scroll.AddChild(list);

        if (decks.Count == 0)
        {
            var empty = BattleTheme.MakeLabel("还没有本地卡组 —— 点“新建卡组”开始", 26, BattleTheme.TextDim, HorizontalAlignment.Center);
            empty.CustomMinimumSize = new Vector2(1140, 140);
            list.AddChild(empty);
        }
        else
            foreach (var d in decks)
                list.AddChild(DeckManagerRow(d, status));

        p.AddChild(Btn("新建卡组", new Vector2(Cx, 912), new Vector2(290, 60), () => { DeckEditContext.Editing = null; GetTree().ChangeSceneToFile("res://scenes/menu/Deck.tscn"); }));
        p.AddChild(Btn("导入口令", new Vector2(970, 912), new Vector2(290, 60), ShowImportDeckCode));
        p.AddChild(Btn("返回", new Vector2(Cx, 982), new Vector2(600, 56), CloseOverlay));
    }

    private Control DeckManagerRow(StoredDeck d, Label status)
    {
        var row = new Panel { CustomMinimumSize = new Vector2(1140, 72) };
        row.AddThemeStyleboxOverride("panel", BattleTheme.Box(BattleTheme.PanelDark, FactionTint(d.Faction), 2, 10));

        var name = BattleTheme.MakeOutlinedLabel(d.Name, 24, BattleTheme.TextMain);
        name.Position = new Vector2(20, 4); name.Size = new Vector2(300, 36); name.ClipText = true;
        row.AddChild(name);
        var sub = BattleTheme.MakeLabel($"{CardView.FactionName(d.Faction)} · {d.CardIds.Count}张", 15, BattleTheme.TextDim);
        sub.Position = new Vector2(20, 42); sub.Size = new Vector2(300, 26);
        row.AddChild(sub);

        float bx = 336;
        void Act(string t, float w, Color c, System.Action a)
        {
            var b = BattleTheme.MakeButton(new Vector2(bx, 8), new Vector2(w, 56), c, BattleTheme.Accent, 1, 8);
            b.Text = t; b.AddThemeFontSizeOverride("font_size", 20);
            b.Pressed += a; row.AddChild(b); bx += w + 10;
        }
        Act("人机对战", 150, BattleTheme.AccentSoft, () => StartVsAiWithDeck(d));
        Act("编辑", 110, BattleTheme.PanelDark, () =>
        {
            DeckEditContext.Editing = new DeckEditContext.Deck(d.Id, d.Name, d.Faction, d.CardIds, d.ServerId);
            GetTree().ChangeSceneToFile("res://scenes/menu/Deck.tscn");
        });
        Act("改名", 110, BattleTheme.PanelDark, () => PromptRename(d));
        Act("复制", 110, BattleTheme.PanelDark, () =>
        {
            DeckStorage.Save(d with { Id = DeckStorage.NewId(), Name = d.Name + " 副本", ServerId = null });
            ShowDeckManager();
        });
        Act("口令", 110, BattleTheme.PanelDark, () =>
        {
            DisplayServer.ClipboardSet(DeckCode.Encode(d.Leader, d.CardIds, RulesInfo.Version, ClientDataHash()));
            if (GodotObject.IsInstanceValid(status))
            {
                status.Text = $"「{d.Name}」的口令已复制到剪贴板";
                status.AddThemeColorOverride("font_color", BattleTheme.Accent);
            }
        });
        Act("删除", 110, BattleTheme.DangerColor, () => ConfirmDelete(d));
        return row;
    }

    // Content fingerprint of the loaded card/leader/deck data — the same value hello sends to the server
    // (Session.cs), so a code exported here validates against a matching build anywhere. Computed once.
    private static string? _clientDataHash;
    private static string ClientDataHash() =>
        _clientDataHash ??= DataHash.Compute(GameData.LoadCards(), GameData.LoadLeaders(), GameData.LoadDecks());

    // ---------- deck-code import (docs/12 A1.2): paste a HTL1- code → validate → save a local deck ----------

    private void ShowImportDeckCode()
    {
        var p = NewPanel();
        PanelLabel(p, "导入卡组口令", 300, 48, BattleTheme.TextMain);
        PanelLabel(p, "把朋友分享的 HTL1- 口令粘贴到下面", 372, 22, BattleTheme.TextDim);
        var field = Field("", "HTL1-…", new Vector2(Cx, 452), 600);
        p.AddChild(field);
        var status = PanelLabel(p, "", 540, 24, BattleTheme.DangerColor);

        p.AddChild(Btn("导入", new Vector2(Cx, 616), new Vector2(290, 64), () =>
        {
            void Fail(string msg)
            {
                status.Text = msg;
                status.AddThemeColorOverride("font_color", BattleTheme.DangerColor);
            }

            var (err, payload) = DeckCode.Decode((field.Text ?? "").Trim());
            switch (err)
            {
                case DeckCodeError.BadFormat: Fail("口令格式不对或已损坏"); return;
                case DeckCodeError.UnsupportedVersion: Fail("口令版本过新,请更新游戏"); return;
            }
            var p2 = payload!;
            // Never silently import a code from a different card table — values would mismatch mid-match.
            if (DeckCode.Check(p2, RulesInfo.Version, ClientDataHash()) == DeckCodeError.DataMismatch)
            {
                Fail($"口令来自不同的卡表版本(对方 {p2.Rules},本机 {RulesInfo.Version})");
                return;
            }

            var db = GameData.LoadCards();
            var invalid = DeckValidator.Validate(p2.Cards, db);
            if (invalid != null) { Fail(invalid.Message); return; }

            var leaders = GameData.LoadLeaders();
            if (!leaders.TryGet(p2.Leader, out var leaderDef)) { Fail("口令中的领袖不存在"); return; }

            // Faction = the deck's first non-neutral card faction; all-neutral falls back to the leader's.
            string faction = p2.Cards
                .Select(id => db.TryGet(id, out var def) ? def.Faction : DeckValidator.NeutralFaction)
                .FirstOrDefault(f => f != DeckValidator.NeutralFaction) ?? leaderDef.Faction;

            DeckStorage.Save(new StoredDeck
            {
                Id = DeckStorage.NewId(),
                Name = "导入的卡组",
                Faction = faction,
                Leader = p2.Leader,
                CardIds = p2.Cards.ToList(),
                ServerId = null,
            });
            ShowDeckManager();
        }));
        p.AddChild(Btn("取消", new Vector2(Cx + 310, 616), new Vector2(290, 64), ShowDeckManager));
    }

    private void StartVsAiWithDeck(StoredDeck d)
    {
        var builtins = GameData.LoadDecks();
        var ai = builtins[(int)(GD.Randi() % (uint)builtins.Count)]; // AI plays a random preconstructed deck
        GameConfig.SetVsAiCustom(d.CardIds, d.Leader, ai.Id);
        GetTree().ChangeSceneToFile(BattlePath);
    }

    private void PromptRename(StoredDeck d)
    {
        var p = NewPanel();
        PanelLabel(p, "重命名卡组", 360, 40, BattleTheme.TextMain);
        var field = Field(d.Name, "卡组名", new Vector2(Cx, 456), 600);
        p.AddChild(field);
        p.AddChild(Btn("确定", new Vector2(Cx, 560), new Vector2(290, 64), () =>
        {
            string name = string.IsNullOrWhiteSpace(field.Text) ? d.Name : field.Text.Trim();
            DeckStorage.Save(d with { Name = name });
            if (Session.Connected && !string.IsNullOrEmpty(d.ServerId))
                _ = Session.SendAsync(new SaveDeck { DeckId = d.ServerId, Name = name, Leader = d.Leader, CardIds = d.CardIds });
            ShowDeckManager();
        }));
        p.AddChild(Btn("取消", new Vector2(Cx + 310, 560), new Vector2(290, 64), ShowDeckManager));
    }

    private void ConfirmDelete(StoredDeck d)
    {
        var p = NewPanel();
        PanelLabel(p, $"删除「{d.Name}」?", 372, 40, BattleTheme.DangerColor);
        PanelLabel(p, "本地卡组将被移除,此操作不可恢复", 440, 22, BattleTheme.TextDim);
        p.AddChild(Btn("删除", new Vector2(Cx, 540), new Vector2(290, 64), () =>
        {
            DeckStorage.Delete(d.Id);
            if (Session.Connected && !string.IsNullOrEmpty(d.ServerId))
                _ = Session.SendAsync(new DeleteDeck { DeckId = d.ServerId });
            ShowDeckManager();
        }));
        p.AddChild(Btn("取消", new Vector2(Cx + 310, 540), new Vector2(290, 64), ShowDeckManager));
    }

    private static Color FactionTint(string faction) => faction switch
    {
        "iron_vow" => BattleTheme.SeatColor0,
        "wildpack" => BattleTheme.SeatColor1,
        "duskweaver" => Color.FromHtml("8b5fa6"),
        "undervault" => Color.FromHtml("b5883f"),
        _ => BattleTheme.AccentSoft,
    };

    // ---------- ladder (M3 C3): Top-N + my rank ----------

    private const float LadderX = Cx - 40f; // scroll/header left edge
    private static readonly (float X, float W, HorizontalAlignment Align)[] LadderCols =
        [(16, 80, HorizontalAlignment.Center), (110, 300, HorizontalAlignment.Left), (410, 110, HorizontalAlignment.Right), (530, 116, HorizontalAlignment.Right)];

    private void ShowLadder()
    {
        var p = NewPanel();
        PanelLabel(p, "天 梯 排 行", 90, 52, BattleTheme.TextMain);
        var status = PanelLabel(p, "加载中…", 168, 24, BattleTheme.Accent);

        // Column header, aligned to the same x-grid the rows use.
        string[] heads = ["排名", "玩家", "评分", "战绩"];
        Color[] headCol = [BattleTheme.TextDim, BattleTheme.TextDim, BattleTheme.TextDim, BattleTheme.TextDim];
        for (int i = 0; i < heads.Length; i++)
        {
            var h = BattleTheme.MakeLabel(heads[i], 22, headCol[i], LadderCols[i].Align);
            Positioned(h, new Vector2(LadderX + LadderCols[i].X, 224), new Vector2(LadderCols[i].W, 30));
            p.AddChild(h);
        }

        var scroll = new ScrollContainer { Position = new Vector2(LadderX, 268), Size = new Vector2(680, 640) };
        scroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
        p.AddChild(scroll);
        var list = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        list.AddThemeConstantOverride("separation", 4);
        scroll.AddChild(list);

        void OnLadder(Ladder l) => Callable.From(() =>
        {
            if (!GodotObject.IsInstanceValid(status)) return;
            status.Text = l.MyRank > 0 ? $"我的排名  #{l.MyRank}" : "尚未上榜(打一场排位即可上榜)";
            foreach (Node c in list.GetChildren()) c.QueueFree();
            foreach (var e in l.Entries) list.AddChild(LadderRow(e, l.MyRank));
            if (l.Entries.Count == 0)
            {
                var empty = BattleTheme.MakeLabel("暂无排名数据", 24, BattleTheme.TextDim, HorizontalAlignment.Center);
                empty.CustomMinimumSize = new Vector2(660, 120);
                list.AddChild(empty);
            }
        }).CallDeferred();
        Session.LadderReceived += OnLadder;

        p.AddChild(Btn("返回", new Vector2(Cx, 936), new Vector2(600, 64), () => { Session.LadderReceived -= OnLadder; ShowLobby(); }));
        _ = Session.SendAsync(new GetLadder());
    }

    private static Control LadderRow(LadderEntry e, int myRank)
    {
        bool me = myRank > 0 && e.Rank == myRank;
        var row = new Panel { CustomMinimumSize = new Vector2(660, 46) };
        row.AddThemeStyleboxOverride("panel", BattleTheme.Box(me ? BattleTheme.AccentSoft : BattleTheme.PanelDark, me ? BattleTheme.Accent : null, me ? 2 : 0, 8));
        void Cell(string text, int col, Color color)
        {
            var l = BattleTheme.MakeLabel(text, 24, color, LadderCols[col].Align);
            Positioned(l, new Vector2(LadderCols[col].X, 0), new Vector2(LadderCols[col].W, 46));
            l.ClipText = true;
            row.AddChild(l);
        }
        Cell($"#{e.Rank}", 0, e.Rank <= 3 ? BattleTheme.AtkColor : BattleTheme.TextDim);
        Cell(e.Name, 1, BattleTheme.TextMain);
        Cell(e.Rating.ToString(), 2, BattleTheme.Accent);
        Cell($"{e.Wins}胜 {e.Losses}负", 3, BattleTheme.TextDim);
        return row;
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

    private void AddButton(string text, Vector2 pos, Color color, System.Action onPressed) =>
        AddButtonSized(text, pos, new Vector2(600, 72), color, onPressed);

    private void AddButtonSized(string text, Vector2 pos, Vector2 size, Color color, System.Action onPressed)
    {
        var btn = BattleTheme.MakeButton(pos, size, color, BattleTheme.Accent, 2, 12);
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
