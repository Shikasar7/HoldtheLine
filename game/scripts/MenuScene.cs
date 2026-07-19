using System.Collections.Generic;
using System.Linq;
using Godot;
using HoldTheLine.Net;
using HoldTheLine.Net.Protocol;
using HoldTheLine.Rules;
using HoldTheLine.Rules.Ai;
using HoldTheLine.Rules.Cards;

namespace HoldTheLine.Game;

/// <summary>Main menu (plan P4): pick vs-AI (choose your deck) or hotseat, then load the battle.</summary>
public partial class MenuScene : Control
{
    private const string BattlePath = "res://scenes/battle/Battle.tscn";

    // docs/15 通道 A: the update banner (one persistent button created in _Ready and updated IN PLACE —
    // recreating it per Changed event would re-append it above any open overlay panel and free the button
    // mid-click during progress ticks) + the last version.json we fetched for the portable/itch "you're
    // behind" hint, and the handler we registered on UpdateService so _ExitTree can detach it (the
    // autoload outlives this scene).
    private Button _updateBanner = null!;
    private System.Action? _bannerAction; // current click behavior; the Pressed hookup dispatches through it
    private UpdateService.ServerVersionInfo? _serverVersion = UpdateService.LastServerVersion;
    private System.Action? _updateChangedHandler;

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

        // Main menu (docs/12 C1): one entry per mode. Deck/difficulty/opponent for vs-AI are chosen in the panel.
        AddButton("人机对战", new Vector2(660, 456), BattleTheme.SeatColor0, () => ShowVsAiPanel());
        AddButton("双人热座", new Vector2(660, 542), BattleTheme.AccentSoft, StartHotseat);
        AddButton("联机对战", new Vector2(660, 628), BattleTheme.SeatColor1, ShowOnlinePanel);
        AddButton("卡组管理", new Vector2(660, 714), Color.FromHtml("8b5fa6"), ShowDeckManager);
        AddButton("退出", new Vector2(660, 806), BattleTheme.PanelDark, () => GetTree().Quit());

        AddVersionLabel();
        AddUpdateBanner();
        HookUpdateChecks();

        // docs/16 login flow: nickname is a persisted display name now (was re-typed every connect).
        if (!string.IsNullOrWhiteSpace(Prefs.Nickname)) GameConfig.Nickname = Prefs.Nickname;

        // Credential gate driven SOLELY by Prefs.Entered (a deliberate entry), NOT by whether identity.json
        // exists. ConnectAsync mints identity.json before it even dials, so keying off the file would treat a
        // failed/aborted login (or a logout whose file-delete failed) as "entered" and silently drop the user
        // into an auto-connected guest. Cost: an existing pre-docs/16 install shows the login page once —
        // tapping 游客进入 reuses its saved identity (guest OR account, now labeled correctly via Profile.Username),
        // so no progress is lost.
        if (!Prefs.Entered)
            ShowLoginPage();
        else if (!Session.Connected)
            _ = Session.ConnectAsync(GameConfig.ServerUrl, GameConfig.Nickname);
    }

    /// <summary>The persistent (initially hidden) update banner. Added here — before any overlay — so
    /// overlays always render above it; RefreshUpdateBanner only mutates it, never re-adds it.</summary>
    private void AddUpdateBanner()
    {
        _updateBanner = BattleTheme.MakeButton(new Vector2(BattleTheme.ScreenW / 2f - 460, 356), new Vector2(920, 56), BattleTheme.PanelDark, BattleTheme.Accent, 2, 10);
        _updateBanner.AddThemeFontSizeOverride("font_size", 22);
        _updateBanner.Visible = false;
        _updateBanner.Pressed += () => _bannerAction?.Invoke();
        AddChild(_updateBanner);
    }

    // ---------- auto-update surface (docs/15 通道 A) ----------

    /// <summary>Small build-version stamp in the corner (docs/15 §1) — the value hello sends and the updater tracks.</summary>
    private void AddVersionLabel()
    {
        var v = BattleTheme.MakeLabel($"v{GameConfig.ClientVersion}", 18, BattleTheme.TextDim);
        v.Position = new Vector2(24, BattleTheme.ScreenH - 40);
        v.Size = new Vector2(320, 28);
        AddChild(v);
    }

    /// <summary>Kick the right update check for this install form and keep the banner in sync with it.
    /// The Velopack form self-updates over GitHub; portable/itch just ask the server what's latest.</summary>
    private void HookUpdateChecks()
    {
        var svc = UpdateService.Instance;
        if (svc is null) return;

        _updateChangedHandler = RefreshUpdateBanner;
        svc.Changed += _updateChangedHandler;

        if (svc.InstallForm == UpdateService.Form.Velopack)
            _ = svc.CheckAndDownloadAsync();
        else
            _ = CheckServerVersionAsync();

        RefreshUpdateBanner(); // reflect state the persistent autoload may already have reached
    }

    private async System.Threading.Tasks.Task CheckServerVersionAsync()
    {
        _serverVersion = await UpdateService.FetchServerVersionAsync(GameConfig.ServerUrl) ?? _serverVersion;
        Callable.From(RefreshUpdateBanner).CallDeferred();
    }

    /// <summary>Repaint the top-of-menu banner from the current updater state: a ready Velopack update
    /// (click → restart-and-apply), an in-progress download (progress %), or a "behind" hint for the
    /// portable/itch forms (click → open the download page). Nothing to show → hidden. Mutates the
    /// persistent button (see <see cref="AddUpdateBanner"/>) — no node churn on progress ticks.</summary>
    private void RefreshUpdateBanner()
    {
        if (!GodotObject.IsInstanceValid(this) || !GodotObject.IsInstanceValid(_updateBanner)) return;

        var svc = UpdateService.Instance;
        if (svc is null) return;

        string? text = null;
        Color color = BattleTheme.PanelDark;
        System.Action? onClick = null;

        if (svc.InstallForm == UpdateService.Form.Velopack)
        {
            switch (svc.State)
            {
                case UpdateService.Phase.Downloading:
                    text = $"正在下载新版本…  {Mathf.RoundToInt(svc.DownloadProgress * 100)}%";
                    break;
                case UpdateService.Phase.Ready:
                    text = $"新版本 v{svc.LatestVersion} 已就绪 —— 点击重启更新";
                    color = BattleTheme.SeatColor0;
                    onClick = svc.ApplyAndRestart;
                    break;
            }
        }
        else if (UpdateService.IsBehind(_serverVersion))
        {
            text = $"发现新版本 v{_serverVersion!.Latest} —— 点击前往下载页";
            color = BattleTheme.SeatColor0;
            onClick = () => svc.OpenDownloadPage(_serverVersion);
        }

        _bannerAction = onClick;
        if (text is null)
        {
            _updateBanner.Visible = false;
            return;
        }
        _updateBanner.Text = text;
        BattleTheme.SetButtonBg(_updateBanner, color, BattleTheme.Accent, 2, 10);
        _updateBanner.Disabled = onClick is null;
        _updateBanner.Visible = true;
    }

    public override void _ExitTree()
    {
        if (_updateChangedHandler is { } h && UpdateService.Instance is { } svc)
            svc.Changed -= h;
        _updateChangedHandler = null;
    }

    private static bool IsUpdateCode(string? code) =>
        code is "client_outdated" or "version_mismatch" or "data_mismatch";

    private static string ForcedUpdateReason(string? code) => code switch
    {
        "client_outdated" => "服务器要求更新的版本才能联机",
        "version_mismatch" => "客户端与服务器版本不一致,需更新后联机",
        "data_mismatch" => "本机卡表与服务器不一致,需更新后联机",
        _ => "需要更新后才能联机",
    };

    /// <summary>The forced-update gate (docs/15 §3): shown when a connect is rejected with an update code.
    /// Same affordances as the menu banner but modal — you can back out to the offline menu (vs-AI still
    /// works) yet can't dismiss it into an online session without updating.</summary>
    private void ShowForcedUpdate(string? code)
    {
        var svc = UpdateService.Instance;
        var p = NewPanel();
        PanelLabel(p, "需 要 更 新", 210, 56, BattleTheme.DangerColor);
        PanelLabel(p, ForcedUpdateReason(code), 300, 24, BattleTheme.TextDim);
        PanelLabel(p, $"当前版本 v{GameConfig.ClientVersion}", 344, 22, BattleTheme.TextDim);
        var status = PanelLabel(p, "", 456, 24, BattleTheme.Accent);

        if (svc is { InstallForm: UpdateService.Form.Velopack })
        {
            var action = Btn("下载并更新", new Vector2(Cx, 548), new Vector2(600, 72), null!);
            action.Pressed += async () =>
            {
                if (svc.State == UpdateService.Phase.Ready) { svc.ApplyAndRestart(); return; }
                action.Disabled = true;
                SetStatusDeferred(status, "正在下载新版本…");
                // force: explicit player action — bypass the recheck cooldown; if a background download is
                // already in flight this awaits THAT task, so the outcome below reflects reality.
                await svc.CheckAndDownloadAsync(force: true);
                bool ready = svc.State == UpdateService.Phase.Ready;
                SetStatusDeferred(status, ready ? "下载完成,点击重启更新" : "下载失败,请稍后重试或前往下载页");
                Callable.From(() => { action.Disabled = false; if (ready) action.Text = "重启更新"; }).CallDeferred();
            };
            p.AddChild(action);
            p.AddChild(Btn("前往下载页", new Vector2(Cx, 636), new Vector2(290, 60), () => svc.OpenDownloadPage(_serverVersion)));
            p.AddChild(Btn("返回菜单", new Vector2(Cx + 310, 636), new Vector2(290, 60), CloseOverlay));
        }
        else
        {
            p.AddChild(Btn("前往下载页", new Vector2(Cx, 548), new Vector2(600, 72), () => svc?.OpenDownloadPage(_serverVersion)));
            p.AddChild(Btn("返回菜单", new Vector2(Cx, 636), new Vector2(600, 60), CloseOverlay));
        }
    }

    // ---------- login flow (docs/16): credential gate shown on first run / after logout ----------

    /// <summary>The entry page (docs/16): login / register / guest, plus an editable server address for
    /// LAN testing. Blocks the menu behind it until the player picks an entry; on success the overlay
    /// closes onto the (already-connected) menu. Reachable again only via logout.</summary>
    private void ShowLoginPage()
    {
        var p = NewPanel();
        PanelLabel(p, "守 线", 120, 84, BattleTheme.TextMain);
        PanelLabel(p, "HOLD THE LINE", 224, 26, BattleTheme.Accent);
        PanelLabel(p, "登录、注册,或以游客身份进入", 296, 22, BattleTheme.TextDim);

        // Server address (defaults to the public wss server; editable for LAN/local play). Captured into
        // GameConfig before any entry so all three flows dial the same host.
        PanelLabel(p, "服务器地址", 690, 20, BattleTheme.TextDim);
        var url = Field(GameConfig.ServerUrl, "ws://主机IP:5210/ws", new Vector2(Cx, 724), 600);
        url.AddThemeFontSizeOverride("font_size", 20);
        p.AddChild(url);
        void Go(System.Action next)
        {
            GameConfig.ServerUrl = string.IsNullOrWhiteSpace(url.Text) ? GameConfig.ServerUrl : url.Text.Trim();
            next();
        }

        p.AddChild(Btn("登录", new Vector2(Cx, 392), new Vector2(600, 76), () => Go(() => ShowAuthForm(isRegister: false))));
        p.AddChild(Btn("注册新账号", new Vector2(Cx, 484), new Vector2(600, 76), () => Go(() => ShowAuthForm(isRegister: true))));
        p.AddChild(Btn("游客进入", new Vector2(Cx, 576), new Vector2(600, 76), () => Go(EnterAsGuest)));
    }

    /// <summary>Ensure a connection to the CURRENT server URL for the login-page entries. Reconnects when the
    /// user edited the address since the live socket was opened (docs/16) — otherwise a stale connection to the
    /// old host would silently absorb the new URL. Returns null on success, else the human-readable error
    /// (LastConnectErrorCode carries the update code, if any).</summary>
    private static async System.Threading.Tasks.Task<string?> EnsureConnectedAsync()
    {
        if (Session.Connected && Session.ConnectedUrl == GameConfig.ServerUrl) return null;
        if (Session.Connected) await Session.DisconnectAsync(); // address changed → drop the old host first
        return await Session.ConnectAsync(GameConfig.ServerUrl, GameConfig.Nickname);
    }

    /// <summary>If the last connect was rejected with an update-required code, raise the forced-update modal
    /// and return true; else false. Single home for the docs/15 gate shared by every entry point.</summary>
    private bool TryForcedUpdate()
    {
        if (IsUpdateCode(Session.LastConnectErrorCode)) { ShowForcedUpdate(Session.LastConnectErrorCode); return true; }
        return false;
    }

    /// <summary>Login (isRegister=false) or register-a-new-account (isRegister=true) — one form; they differ
    /// only in copy, password floor, the register-only fresh-identity, and which auth call + offerName.</summary>
    private void ShowAuthForm(bool isRegister)
    {
        var p = NewPanel();
        PanelLabel(p, isRegister ? "注 册 新 账 号" : "登 录", 150, 56, BattleTheme.TextMain);
        PanelLabel(p, isRegister ? "创建一个全新账号(与任何游客进度无关),用户名+密码登录"
                                 : "登录已有账号(会挤下其它已登录的设备)", 236, 22, BattleTheme.TextDim);
        PanelLabel(p, "用户名", 320, 22, BattleTheme.Accent);
        var user = Field(isRegister ? "" : Prefs.LastUsername, "2-20 个字符", new Vector2(Cx, 356), 600);
        p.AddChild(user);
        PanelLabel(p, "密码", 452, 22, BattleTheme.Accent);
        var pass = Field("", "至少 8 位", new Vector2(Cx, 488), 600);
        pass.Secret = true;
        p.AddChild(pass);
        var status = PanelLabel(p, "", 586, 24, BattleTheme.DangerColor);
        var go = Btn(isRegister ? "注册" : "登录", new Vector2(Cx, 648), new Vector2(600, 72), null!);
        // Guarded against the panel being freed mid-await (返回 during a slow connect/auth).
        void Set(string t, Color c) { if (GodotObject.IsInstanceValid(status)) { status.Text = t; status.AddThemeColorOverride("font_color", c); } }
        void Enable() { if (GodotObject.IsInstanceValid(go)) go.Disabled = false; }

        go.Pressed += async () =>
        {
            string u = user.Text.Trim();
            if (u.Length is < 2 or > 20) { Set("用户名需 2-20 个字符", BattleTheme.DangerColor); return; }
            if (pass.Text.Length < (isRegister ? 8 : 1)) { Set(isRegister ? "密码至少 8 位" : "请输入密码", BattleTheme.DangerColor); return; }
            if (GodotObject.IsInstanceValid(go)) go.Disabled = true;
            Set("连接中…", BattleTheme.TextDim);
            // 方案 A (docs/16 §2): a brand-new account = a brand-new guest identity. Clear only when NOT already
            // connected — the login page appears solely when there is nothing to protect.
            if (isRegister && !Session.Connected) Identity.Clear();
            var connErr = await EnsureConnectedAsync();
            if (connErr != null) { Enable(); if (!TryForcedUpdate()) Set($"连接失败:{connErr}", BattleTheme.DangerColor); return; }
            if (!SecureChannelOk()) { Enable(); Set("密码功能需要加密连接(wss)", BattleTheme.DangerColor); return; }
            var err = isRegister ? await Session.RegisterAsync(u, pass.Text) : await Session.LoginAsync(u, pass.Text);
            if (err is null) { Prefs.LastUsername = u; FinishEntry(offerName: isRegister); }
            else { Enable(); Set(AuthErrorText(err), BattleTheme.DangerColor); }
        };
        p.AddChild(go);
        p.AddChild(Btn("返回", new Vector2(Cx, 736), new Vector2(600, 60), ShowLoginPage));
    }

    private async void EnterAsGuest()
    {
        // A guest reuses (or, on a fresh install, mints) the local identity via ConnectAsync. Offline is fine
        // — the menu's vs-AI / hotseat work regardless; the credential just persists for next launch.
        var err = await EnsureConnectedAsync();
        if (err != null && TryForcedUpdate()) return;
        FinishEntry(offerName: true);
    }

    /// <summary>Mark the player as entered and leave the login page. Guest/register may still lack a display
    /// name → offer to set one (online only); login inherits the account's name via the profile push.</summary>
    private void FinishEntry(bool offerName)
    {
        Prefs.Entered = true;
        if (offerName && Session.Connected && (string.IsNullOrWhiteSpace(GameConfig.Nickname) || GameConfig.Nickname == "玩家"))
            PromptFirstName();
        else
            CloseOverlay();
    }

    /// <summary>First-time display-name prompt (docs/16 §3). Skippable; changeable later in 账号.</summary>
    private void PromptFirstName()
    {
        var p = NewPanel();
        PanelLabel(p, "设 置 昵 称", 300, 52, BattleTheme.TextMain);
        PanelLabel(p, "给自己起个显示名(之后可在“账号”里随时修改)", 380, 22, BattleTheme.TextDim);
        var field = Field("", "1-20 个字符", new Vector2(Cx, 456), 600);
        p.AddChild(field);
        var status = PanelLabel(p, "", 540, 22, BattleTheme.DangerColor);
        p.AddChild(Btn("确定", new Vector2(Cx, 596), new Vector2(290, 64), async () =>
        {
            string n = field.Text.Trim();
            if (n.Length is < 1 or > 20) { if (GodotObject.IsInstanceValid(status)) status.Text = "昵称需 1-20 个字符"; return; }
            var err = await Session.SetNameAsync(n);
            if (!GodotObject.IsInstanceValid(status)) return; // panel closed during the call
            if (err is null) CloseOverlay(); // Prefs.Nickname is synced by the resulting Profile push
            else status.Text = AuthErrorText(err);
        }));
        p.AddChild(Btn("跳过", new Vector2(Cx + 310, 596), new Vector2(290, 64), CloseOverlay));
    }

    /// <summary>Logout (docs/16): drop the connection, wipe local credentials, and return to the login page.
    /// Guests get an extra warning — their secret is unrecoverable once cleared.</summary>
    private void ShowLogoutConfirm()
    {
        bool guest = Session.BoundUsername is null;
        var p = NewPanel();
        PanelLabel(p, "登 出", 320, 52, BattleTheme.DangerColor);
        PanelLabel(p, guest
            ? "游客身份登出后无法找回,建议先绑定账号(注册)。确定登出?"
            : "登出后回到登录页,可用账号重新登录。确定登出?", 400, 22, BattleTheme.TextDim);
        p.AddChild(Btn("登出", new Vector2(Cx, 512), new Vector2(290, 64), async () =>
        {
            await Session.DisconnectAsync();
            Identity.Clear();
            Prefs.Entered = false;
            Prefs.Nickname = "";
            Prefs.LastUsername = "";
            GameConfig.Nickname = "玩家";
            ShowLoginPage();
        }));
        p.AddChild(Btn("取消", new Vector2(Cx + 310, 512), new Vector2(290, 64), ShowAccountPanel));
    }

    // ---------- online lobby (M3 C1): connect → profile → ranked queue / friend rooms ----------

    private string _lobbyDeck = ""; // resolved on lobby open: last used → newest edited → first option
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

    /// <summary>Reconnect helper reached from 联机对战 only when the startup auto-connect failed (offline).
    /// No nickname field (docs/16 §4.7): the display name is a persisted profile value now, changed in 账号.</summary>
    private void ShowConnect()
    {
        var p = NewPanel();
        PanelLabel(p, "联 机 · 连接", 150, 60, BattleTheme.TextMain);
        PanelLabel(p, "连接到对战服务器,进入大厅后即可排位 / 好友对战", 236, 22, BattleTheme.TextDim);

        PanelLabel(p, "服务器地址", 400, 22, BattleTheme.Accent);
        var url = Field(GameConfig.ServerUrl, "ws://主机IP:5210/ws", new Vector2(Cx, 436), 600);
        p.AddChild(url);

        var status = PanelLabel(p, "", 560, 24, BattleTheme.TextDim);
        p.AddChild(Btn("连接", new Vector2(Cx, 624), new Vector2(290, 76), async () =>
        {
            status.Text = "连接中…";
            status.AddThemeColorOverride("font_color", BattleTheme.TextDim);
            GameConfig.ServerUrl = string.IsNullOrWhiteSpace(url.Text) ? GameConfig.ServerUrl : url.Text.Trim();
            var err = await Session.ConnectAsync(GameConfig.ServerUrl, GameConfig.Nickname);
            if (err is null) ShowLobby();
            else if (IsUpdateCode(Session.LastConnectErrorCode)) ShowForcedUpdate(Session.LastConnectErrorCode);
            else { status.Text = $"连接失败:{err}"; status.AddThemeColorOverride("font_color", BattleTheme.DangerColor); }
        }));
        p.AddChild(Btn("返回", new Vector2(Cx + 310, 624), new Vector2(290, 76), CloseOverlay));
    }

    private void ShowLobby()
    {
        var p = NewPanel();
        var pf = Session.Profile;
        PanelLabel(p, pf != null ? pf.Name : "已连接", 150, 56, BattleTheme.TextMain);
        PanelLabel(p, pf != null ? $"评分 {pf.Rating}   ·   胜 {pf.Wins} / 负 {pf.Losses}" : "", 224, 26, BattleTheme.Accent);

        PanelLabel(p, "当前卡组", 300, 22, BattleTheme.TextDim);
        // The player's saved decks first (from the last profile push), then the built-in starters.
        var options = new List<(string Id, string Label, Color Color, string Tip)>();
        if (pf != null)
            foreach (var d in pf.Decks) options.Add((d.Id, d.Name, FactionTint(d.Faction), DeckTip(d.Leader, d.CardIds)));
        foreach (var d in DeckOptions) options.Add((d.Id, d.Label, d.Color, BuiltinDeckTip(d.Id)));
        if (options.All(o => o.Id != _lobbyDeck)) // first open / deleted deck → last used, else newest edited
            _lobbyDeck = DefaultLobbyDeck(options);

        int ownCount = pf?.Decks.Count ?? 0;
        var deckBtns = new Button[options.Count];
        void Repaint() { for (int i = 0; i < options.Count; i++) BattleTheme.SetButtonBg(deckBtns[i], _lobbyDeck == options[i].Id ? options[i].Color : BattleTheme.PanelDark); }
        for (int i = 0; i < options.Count; i++)
        {
            var opt = options[i];
            var pos = new Vector2(Cx + (i % 2) * 310, 334 + (i / 2) * 66);
            deckBtns[i] = Btn(opt.Label, pos, new Vector2(290, 58), () => { _lobbyDeck = opt.Id; Repaint(); });
            deckBtns[i].TooltipText = opt.Tip;
            p.AddChild(deckBtns[i]);
            // Saved decks (before the builtins) get an edit chip — Profile.Decks carries the full card
            // list (protocol v3), so the editor can open it directly, no extra round-trip.
            if (i < ownCount && pf != null)
            {
                var ds = pf.Decks[i];
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
        // Bottom row: 账号 / 断开连接 / 返回 (docs/12 B1: account panel entry sits beside disconnect).
        p.AddChild(Btn("账号", new Vector2(Cx, y + 236), new Vector2(190, 60), ShowAccountPanel));
        p.AddChild(Btn("断开连接", new Vector2(Cx + 200, y + 236), new Vector2(190, 60), async () => { await Session.DisconnectAsync(); CloseOverlay(); }));
        p.AddChild(Btn("返回", new Vector2(Cx + 400, y + 236), new Vector2(200, 60), CloseOverlay));
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
            // Ranked is human-vs-human only — no practice-bot fallback. Just show how long we've waited.
            if (GodotObject.IsInstanceValid(status))
                status.Text = $"已等待 {q.WaitedSeconds}s   ·   正在寻找对手";
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
        Prefs.LastLobbyDeck = _lobbyDeck; // a match actually started with it → preselect it next time
        GameConfig.SetOnlineAttached();
        GameConfig.LocalDeckCards = ResolveDeckCards(_lobbyDeck); // so the in-match 查看牌组 can show it
        GetTree().ChangeSceneToFile(BattlePath);
    }

    /// <summary>Default lobby deck when nothing is selected yet: the deck last taken into an online match,
    /// else the newest-edited local deck that the server also has (matched by its server id), else the
    /// first option (own decks sort before the builtins).</summary>
    private static string DefaultLobbyDeck(List<(string Id, string Label, Color Color, string Tip)> options)
    {
        string last = Prefs.LastLobbyDeck;
        if (options.Any(o => o.Id == last))
            return last;
        foreach (var d in DeckStorage.LoadAll().OrderByDescending(x => x.UpdatedAt))
            if (d.ServerId is { } sid && options.Any(o => o.Id == sid))
                return sid;
        return options[0].Id;
    }

    // Card/leader lookups for the hover tooltips — loaded once, shared by every picker.
    private static CardDatabase? _tipCards;
    private static LeaderDatabase? _tipLeaders;

    /// <summary>Hover tooltip for a deck option: leader plus the card list grouped as 「n× 名称(费)」,
    /// cheapest first — so the picker shows a deck's composition without opening the editor.</summary>
    private static string DeckTip(string? leader, IReadOnlyList<string> cardIds)
    {
        _tipCards ??= GameData.LoadCards();
        _tipLeaders ??= GameData.LoadLeaders();
        var sb = new System.Text.StringBuilder();
        if (leader != null && _tipLeaders.TryGet(leader, out var ld))
            sb.AppendLine($"领袖:{ld.Name}");
        var known = cardIds.Where(id => _tipCards.TryGet(id, out _))
            .GroupBy(id => id)
            .Select(g => (Def: _tipCards.Get(g.Key), Count: g.Count()))
            .OrderBy(x => x.Def.Cost).ThenBy(x => x.Def.Name, System.StringComparer.Ordinal);
        foreach (var (def, count) in known)
            sb.AppendLine($"{count}× {def.Name}({def.Cost}费)");
        return sb.ToString().TrimEnd();
    }

    private static string BuiltinDeckTip(string deckId)
    {
        var d = GameData.LoadDecks().FirstOrDefault(x => x.Id == deckId);
        return d is null ? "" : DeckTip(d.Leader, d.Expand());
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

    // ---------- account (docs/12 B1): register/login on top of the persistent guest identity ----------

    /// <summary>Server auth error code → panel copy (the six frozen B1 codes). Local errors
    /// ("未连接"/"服务器无响应") aren't codes and pass through unchanged.</summary>
    private static string AuthErrorText(string codeOrText) => codeOrText switch
    {
        "name_taken" => "用户名已被占用",
        "weak_password" => "密码太短(至少 8 位)",
        "bad_credentials" => "用户名或密码错误",
        "too_many_attempts" => "尝试过于频繁,请稍后再试",
        "not_identified" => "当前为临时身份,无法注册",
        "already_bound" => "该身份已绑定过用户名",
        "invalid_name" => "昵称需 1-20 个字符", // docs/16 set_name
        _ => codeOrText,
    };

    /// <summary>Password auth needs an encrypted channel. wss:// is fine; plain ws:// is only allowed to
    /// loopback / LAN hosts (debug), where there's no untrusted hop.</summary>
    private static bool SecureChannelOk()
    {
        var url = GameConfig.ServerUrl ?? "";
        if (url.StartsWith("wss://", System.StringComparison.Ordinal)) return true;
        try
        {
            var host = new System.Uri(url).Host;
            return host is "127.0.0.1" or "localhost"
                || host.StartsWith("192.168.", System.StringComparison.Ordinal)
                || host.StartsWith("10.", System.StringComparison.Ordinal);
        }
        catch { return false; }
    }

    /// <summary>The in-lobby profile page (docs/16 §1): change the display name, bind a guest to an account
    /// (register keeps its "把当前进度绑定" semantics) or switch accounts (login), and log out.</summary>
    private void ShowAccountPanel()
    {
        var p = NewPanel();
        PanelLabel(p, "账 号", 90, 52, BattleTheme.TextMain);
        PanelLabel(p, Session.BoundUsername is { } bound ? $"已绑定账号:{bound}" : "当前为游客身份(本机密钥)", 168, 24, BattleTheme.Accent);

        // --- display name (docs/16 §3): change it in place, applies immediately ---
        PanelLabel(p, "显示名", 232, 22, BattleTheme.TextDim);
        // Field + button share the same centered 660–1260 column the rest of the panel uses. Show the actual
        // name (never blank on the '玩家' default) so a player named 玩家 sees and can confirm it.
        var nameField = Field(GameConfig.Nickname, "1-20 个字符", new Vector2(Cx, 268), 420);
        p.AddChild(nameField);
        var nameStatus = PanelLabel(p, "", 344, 20, BattleTheme.Accent);
        var renameBtn = Btn("改名", new Vector2(1100, 268), new Vector2(160, 60), null!);
        void SetName(string t, Color c) { if (GodotObject.IsInstanceValid(nameStatus)) { nameStatus.Text = t; nameStatus.AddThemeColorOverride("font_color", c); } }
        renameBtn.Pressed += async () =>
        {
            string n = nameField.Text.Trim();
            if (n.Length is < 1 or > 20) { SetName("昵称需 1-20 个字符", BattleTheme.DangerColor); return; }
            var err = await Session.SetNameAsync(n);
            SetName(err is null ? "已更新" : AuthErrorText(err), err is null ? BattleTheme.Accent : BattleTheme.DangerColor);
        };
        p.AddChild(renameBtn);

        // --- bind (guest) / switch account ---
        PanelLabel(p, Session.BoundUsername is null
            ? "绑定账号:注册用户名+密码,把当前游客进度存到账号"
            : "切换账号:登录另一个已有账号(会挤下旧设备)", 396, 20, BattleTheme.TextDim);
        PanelLabel(p, "用户名", 436, 22, BattleTheme.Accent);
        var user = Field("", "2-20 个字符", new Vector2(Cx, 472), 600);
        p.AddChild(user);
        PanelLabel(p, "密码", 552, 22, BattleTheme.Accent);
        var pass = Field("", "至少 8 位", new Vector2(Cx, 588), 600);
        pass.Secret = true;
        p.AddChild(pass);

        var status = PanelLabel(p, "", 668, 22, BattleTheme.DangerColor);
        void SetStatus(string text, Color color) { if (GodotObject.IsInstanceValid(status)) { status.Text = text; status.AddThemeColorOverride("font_color", color); } }

        var register = Btn("注册(绑定当前进度)", new Vector2(Cx, 716), new Vector2(290, 60), null!);
        var login = Btn("登录(切换账号)", new Vector2(Cx + 310, 716), new Vector2(290, 60), null!);
        register.Pressed += async () =>
        {
            string u = user.Text.Trim(); // capture before the await — the panel may be freed by then
            SetStatus("注册中…", BattleTheme.TextDim);
            var err = await Session.RegisterAsync(u, pass.Text);
            if (err is null) SetStatus($"注册成功,已绑定「{u}」", BattleTheme.Accent);
            else SetStatus(AuthErrorText(err), BattleTheme.DangerColor);
        };
        login.Pressed += async () =>
        {
            string u = user.Text.Trim();
            SetStatus("登录中…", BattleTheme.TextDim);
            var err = await Session.LoginAsync(u, pass.Text);
            if (err is null) { if (GodotObject.IsInstanceValid(this)) ShowLobby(); } // profile re-pushed → lobby shows the account's decks/rating
            else SetStatus(AuthErrorText(err), BattleTheme.DangerColor);
        };
        p.AddChild(register);
        p.AddChild(login);

        // Plaintext gate: no passwords over an untrusted ws:// hop (rename is fine — it carries no secret).
        if (!SecureChannelOk())
        {
            register.Disabled = true;
            login.Disabled = true;
            SetStatus("密码功能需要加密连接(wss)", BattleTheme.DangerColor);
        }

        p.AddChild(Btn("登出", new Vector2(Cx, 792), new Vector2(290, 60), ShowLogoutConfirm));
        p.AddChild(Btn("返回", new Vector2(Cx + 310, 792), new Vector2(290, 60), ShowLobby));
    }

    // ---------- vs-AI setup (docs/12 C1+C3): my deck × difficulty × opponent, one panel ----------

    private string _vsAiMyDeck = "";                        // resolved on panel open: last used → newest edited → first
    private AiLevel _vsAiLevel = AiLevel.Hard;
    private string _vsAiOppDeck = "random";                 // "random", a built-in id, or "local:<id>"

    /// <summary>Options for a deck grid: the player's local decks first, then the four built-ins.</summary>
    private static List<(string Key, string Label, Color Color, string Tip)> DeckGridOptions(bool withRandom)
    {
        var opts = new List<(string Key, string Label, Color Color, string Tip)>();
        if (withRandom) opts.Add(("random", "随机对手", BattleTheme.Accent, "按难度随机挑一套对手卡组"));
        foreach (var d in DeckStorage.LoadAll()) opts.Add(($"local:{d.Id}", d.Name, FactionTint(d.Faction), DeckTip(d.Leader, d.CardIds)));
        foreach (var d in DeckOptions) opts.Add((d.Id, d.Label, d.Color, BuiltinDeckTip(d.Id)));
        return opts;
    }

    /// <summary>Default vs-AI deck when nothing is selected yet: last used → newest-edited local → first.</summary>
    private static string DefaultVsAiDeck(List<(string Key, string Label, Color Color, string Tip)> opts)
    {
        string last = Prefs.LastVsAiDeck;
        if (opts.Any(o => o.Key == last))
            return last;
        if (DeckStorage.NewestEdited() is { } newest && opts.Any(o => o.Key == $"local:{newest.Id}"))
            return $"local:{newest.Id}";
        return opts[0].Key;
    }

    private void ShowVsAiPanel(StoredDeck? preselect = null)
    {
        if (preselect != null) _vsAiMyDeck = $"local:{preselect.Id}";

        var p = NewPanel();
        PanelLabel(p, "人 机 对 战", 48, 48, BattleTheme.TextMain);

        // 1) my deck
        PanelLabel(p, "我的卡组", 112, 22, BattleTheme.Accent);
        var myOpts = DeckGridOptions(withRandom: false);
        if (myOpts.All(o => o.Key != _vsAiMyDeck)) _vsAiMyDeck = DefaultVsAiDeck(myOpts); // first open / deleted → fall back
        DeckGrid(p, new Vector2(Cx, 146), new Vector2(620, 150), myOpts, () => _vsAiMyDeck, k => _vsAiMyDeck = k);

        // 2) difficulty
        PanelLabel(p, "难度", 306, 22, BattleTheme.Accent);
        var levels = new (AiLevel L, string Label)[] { (AiLevel.Easy, "简单"), (AiLevel.Normal, "普通"), (AiLevel.Hard, "困难") };
        var lvlBtns = new Button[levels.Length];
        void RepaintLevel() { for (int i = 0; i < levels.Length; i++) BattleTheme.SetButtonBg(lvlBtns[i], _vsAiLevel == levels[i].L ? BattleTheme.AccentSoft : BattleTheme.PanelDark); }
        for (int i = 0; i < levels.Length; i++)
        {
            var lv = levels[i];
            var b = Btn(lv.Label, new Vector2(Cx + i * 205, 340), new Vector2(190, 58), () => { _vsAiLevel = lv.L; RepaintLevel(); });
            lvlBtns[i] = b;
            p.AddChild(b);
        }
        RepaintLevel();

        // 3) opponent (random + built-ins + local, one grid)
        PanelLabel(p, "对手", 414, 22, BattleTheme.Accent);
        var oppOpts = DeckGridOptions(withRandom: true);
        if (oppOpts.All(o => o.Key != _vsAiOppDeck)) _vsAiOppDeck = "random";
        DeckGrid(p, new Vector2(Cx, 448), new Vector2(620, 150), oppOpts, () => _vsAiOppDeck, k => _vsAiOppDeck = k);

        p.AddChild(Btn("开  战", new Vector2(Cx, 616), new Vector2(600, 76), StartVsAiMatch));
        p.AddChild(Btn("返回", new Vector2(Cx, 704), new Vector2(600, 56), CloseOverlay));
    }

    /// <summary>A scrollable two-column grid of selectable deck buttons; the selected key highlights.</summary>
    private void DeckGrid(Control parent, Vector2 pos, Vector2 size,
        List<(string Key, string Label, Color Color, string Tip)> opts, System.Func<string> get, System.Action<string> set)
    {
        var scroll = new ScrollContainer { Position = pos, Size = size };
        scroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
        parent.AddChild(scroll);
        var inner = new Control { CustomMinimumSize = new Vector2(size.X - 24, ((opts.Count + 1) / 2) * 66) };
        scroll.AddChild(inner);

        var btns = new Button[opts.Count];
        System.Action repaint = null!;
        repaint = () => { for (int i = 0; i < opts.Count; i++) BattleTheme.SetButtonBg(btns[i], get() == opts[i].Key ? opts[i].Color : BattleTheme.PanelDark); };
        for (int i = 0; i < opts.Count; i++)
        {
            var o = opts[i];
            var b = BattleTheme.MakeButton(new Vector2((i % 2) * 300, (i / 2) * 66), new Vector2(288, 58), BattleTheme.PanelDark, BattleTheme.Accent, 1, 8);
            b.Text = o.Label; b.AddThemeFontSizeOverride("font_size", 20); b.ClipText = true;
            b.TooltipText = o.Tip;
            b.Pressed += () => { set(o.Key); repaint(); };
            inner.AddChild(b);
            btns[i] = b;
        }
        repaint();
    }

    /// <summary>Resolve a grid key to a seat deck: a built-in id (cards null) or a local card list + leader.</summary>
    private static (string? Builtin, IReadOnlyList<string>? Cards, string? Leader) ResolveVsAiDeck(string key)
    {
        if (key.StartsWith("local:"))
        {
            var d = DeckStorage.Get(key["local:".Length..]);
            return d is null ? (null, null, null) : (null, d.CardIds, d.Leader);
        }
        return (key, null, null);
    }

    private void StartVsAiMatch()
    {
        Prefs.LastVsAiDeck = _vsAiMyDeck; // preselect it next time the panel opens
        var (hb, hc, hl) = ResolveVsAiDeck(_vsAiMyDeck);
        // Random opponent is resolved to a concrete built-in now (the panel doesn't reveal which — you see it in-match).
        string oppKey = _vsAiOppDeck == "random" ? AiDeckPool.PickRandom(_vsAiLevel) : _vsAiOppDeck;
        var (ab, ac, al) = ResolveVsAiDeck(oppKey);
        GameConfig.SetVsAiMatch(hb, hc, hl, ab, ac, al, _vsAiLevel);
        GetTree().ChangeSceneToFile(BattlePath);
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
        var row = new Panel { CustomMinimumSize = new Vector2(1140, 72), TooltipText = DeckTip(d.Leader, d.CardIds) };
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
        Act("人机对战", 150, BattleTheme.AccentSoft, () => ShowVsAiPanel(preselect: d));
        Act("编辑", 110, BattleTheme.PanelDark, () =>
        {
            DeckEditContext.Editing = new DeckEditContext.Deck(d.Id, d.Name, d.Faction, d.CardIds, d.ServerId);
            GetTree().ChangeSceneToFile("res://scenes/menu/Deck.tscn");
        });
        Act("改名", 110, BattleTheme.PanelDark, () => PromptRename(d));
        Act("复制", 110, BattleTheme.PanelDark, () =>
        {
            DeckStorage.Save(d with { Id = DeckStorage.NewId(), Name = DeckStorage.UniqueName(d.Name + " 副本"), ServerId = null });
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
                Name = DeckStorage.UniqueName("导入的卡组"),
                Faction = faction,
                Leader = p2.Leader,
                CardIds = p2.Cards.ToList(),
                ServerId = null,
            });
            ShowDeckManager();
        }));
        p.AddChild(Btn("取消", new Vector2(Cx + 310, 616), new Vector2(290, 64), ShowDeckManager));
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
            name = DeckStorage.UniqueName(name, excludeId: d.Id);
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

    private void StartHotseat()
    {
        GameConfig.SetHotseat();
        GetTree().ChangeSceneToFile(BattlePath);
    }
}
