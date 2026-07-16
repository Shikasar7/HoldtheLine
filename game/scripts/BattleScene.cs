using Godot;
using HoldTheLine.Rules.Cards;
using HoldTheLine.Rules.Commands;
using HoldTheLine.Rules.Engine;
using HoldTheLine.Rules.Events;
using HoldTheLine.Rules.Geometry;
using HoldTheLine.Rules.Hosting;

namespace HoldTheLine.Game;

/// <summary>
/// Hotseat battle scene (plan P3). Pure presentation: talks to LocalGameHost via commands, events,
/// PlayerView and LegalCommands only — never authoritative state (enforced by ArchitectureTests).
/// Both players share the screen; the active player's hand shows, the opponent's is hidden.
/// </summary>
public partial class BattleScene : Control
{
    private CardDatabase _cards = null!;
    private LeaderDatabase _leaders = null!;
    private LocalGameHost _host = null!;

    private Control _boardLayer = null!, _standeeLayer = null!, _handLayer = null!, _hudLayer = null!, _overlayLayer = null!;
    private readonly Button[,] _cellButtons = new Button[BattleTheme.Cols, BattleTheme.Rows];
    private readonly Dictionary<int, Button> _standees = new();
    private Button _oppLeaderBtn = null!, _endTurnBtn = null!, _leaderPowerBtn = null!;
    private Label _turnLabel = null!, _oppInfo = null!, _selfInfo = null!, _logLabel = null!;

    private readonly List<GameEvent> _pendingEvents = new();
    private bool _busy;

    // Selection / targeting state.
    private enum SelKind { None, Card, Unit, Leader }
    private SelKind _selKind = SelKind.None;
    private List<Command> _candidates = new();
    private Cell? _chosenCell;

    // Perspective: the active player always sees their own home row at the bottom (180° flip for seat 1).
    private int _persp;

    private (int Col, int Row) BoardToScreen(Cell c) =>
        _persp == 0 ? (c.Col, c.Row) : (BattleTheme.Cols - 1 - c.Col, BattleTheme.Rows - 1 - c.Row);

    private Cell ScreenToBoard(int scol, int srow) =>
        _persp == 0 ? new Cell(scol, srow) : new Cell(BattleTheme.Cols - 1 - scol, BattleTheme.Rows - 1 - srow);

    private Vector2 CellScreenPos(Cell c)
    {
        var (scol, srow) = BoardToScreen(c);
        return BattleTheme.CellPos(scol, srow);
    }

    private Button CellButton(Cell c)
    {
        var (scol, srow) = BoardToScreen(c);
        return _cellButtons[scol, srow];
    }

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _cards = GameData.LoadCards();
        _leaders = GameData.LoadLeaders();

        var decks = GameData.LoadDecks();
        var iron = decks.First(d => d.Id == "iron_wall");
        var wild = decks.First(d => d.Id == "wildpack_hunt");

        var config = new MatchConfig
        {
            Seed = ((ulong)GD.Randi() << 32) | GD.Randi(),
            FirstSeat = (int)(GD.Randi() % 2),
            Deck0 = iron.Expand(), Leader0 = iron.Leader,
            Deck1 = wild.Expand(), Leader1 = wild.Leader,
        };
        _host = new LocalGameHost(_cards, _leaders, config);
        _host.Subscribe(0, e => _pendingEvents.Add(e)); // seat-0 stream: public events drive animation

        BuildStaticUi();
        FullRender();
    }

    // ---------- static scaffold ----------

    private void BuildStaticUi()
    {
        var bg = new ColorRect { Color = BattleTheme.Background };
        bg.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        bg.MouseFilter = MouseFilterEnum.Ignore;
        AddChild(bg);

        _boardLayer = NewLayer();
        _standeeLayer = NewLayer();
        _handLayer = NewLayer();
        _hudLayer = NewLayer();
        _overlayLayer = NewLayer();

        // Board cells, indexed by SCREEN position (bottom row = the viewer's deploy zone).
        for (int scol = 0; scol < BattleTheme.Cols; scol++)
            for (int srow = 0; srow < BattleTheme.Rows; srow++)
            {
                int sc = scol, sr = srow;
                var btn = BattleTheme.MakeButton(BattleTheme.CellPos(scol, srow), new Vector2(BattleTheme.CellW, BattleTheme.CellH), CellBaseColor(srow));
                btn.Pressed += () => OnCellClicked(sc, sr);
                _boardLayer.AddChild(btn);
                _cellButtons[scol, srow] = btn;
            }

        // HUD.
        _turnLabel = BattleTheme.MakeLabel("", 34, BattleTheme.TextMain, HorizontalAlignment.Center);
        _turnLabel.Position = new Vector2(660, 20);
        _turnLabel.Size = new Vector2(600, 44);
        _hudLayer.AddChild(_turnLabel);

        _oppInfo = BattleTheme.MakeLabel("", 24, BattleTheme.TextDim);
        _oppInfo.Position = new Vector2(60, 70);
        _oppInfo.Size = new Vector2(700, 40);
        _hudLayer.AddChild(_oppInfo);

        _oppLeaderBtn = BattleTheme.MakeButton(new Vector2(1500, 40), new Vector2(360, 96), BattleTheme.PanelDark, BattleTheme.SeatColor1, 3, 10);
        _oppLeaderBtn.Pressed += () => OnLeaderClicked(1);
        _hudLayer.AddChild(_oppLeaderBtn);

        _selfInfo = BattleTheme.MakeLabel("", 24, BattleTheme.TextDim);
        _selfInfo.Position = new Vector2(60, 800);
        _selfInfo.Size = new Vector2(900, 40);
        _hudLayer.AddChild(_selfInfo);

        _leaderPowerBtn = BattleTheme.MakeButton(new Vector2(60, 844), new Vector2(300, 64), BattleTheme.PanelDark, BattleTheme.SeatColor0, 2, 10);
        _leaderPowerBtn.Pressed += OnLeaderPower;
        _hudLayer.AddChild(_leaderPowerBtn);

        _endTurnBtn = BattleTheme.MakeButton(new Vector2(1600, 844), new Vector2(260, 90), BattleTheme.AccentSoft, BattleTheme.Accent, 2, 12);
        _endTurnBtn.Text = "结束回合";
        _endTurnBtn.AddThemeFontSizeOverride("font_size", 28);
        _endTurnBtn.Pressed += OnEndTurn;
        _hudLayer.AddChild(_endTurnBtn);

        _logLabel = BattleTheme.MakeLabel("", 20, BattleTheme.TextDim, HorizontalAlignment.Center);
        _logLabel.Position = new Vector2(360, 1050);
        _logLabel.Size = new Vector2(1200, 26);
        _hudLayer.AddChild(_logLabel);
    }

    private Control NewLayer()
    {
        var layer = new Control();
        layer.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        layer.MouseFilter = MouseFilterEnum.Ignore;
        AddChild(layer);
        return layer;
    }

    // ---------- full render from truth ----------

    private int ActiveSeat => _host.GetView(0).ActiveSeat;

    private void FullRender()
    {
        var view = _host.GetView(ActiveSeat);
        _persp = view.ActiveSeat;
        RenderStandees(view);
        RenderHand(view);
        RenderHud(view);
        ClearSelection();
        RefreshInteractable(view);
    }

    private void RenderStandees(PlayerView view)
    {
        foreach (var node in _standees.Values)
            node.QueueFree();
        _standees.Clear();

        foreach (var u in view.Units)
        {
            var pos = CellScreenPos(u.Cell) + new Vector2(7, 7);
            var size = new Vector2(BattleTheme.CellW - 14, BattleTheme.CellH - 14);
            var btn = BattleTheme.MakeButton(pos, size, BattleTheme.SeatColor(u.OwnerSeat).Darkened(0.15f), BattleTheme.SeatColor(u.OwnerSeat), 3, 8);
            int id = u.EntityId;
            btn.Pressed += () => OnUnitClicked(id);

            btn.AddChild(Pip(u.Atk.ToString(), BattleTheme.AtkColor, new Vector2(6, 4)));
            var hpColor = u.CurrentHp < u.MaxHp ? BattleTheme.DangerColor : BattleTheme.HpColor;
            btn.AddChild(Pip(u.CurrentHp.ToString(), hpColor, new Vector2(size.X - 40, 4)));

            var name = BattleTheme.MakeLabel(ShortName(u.CardId), 17, BattleTheme.TextMain, HorizontalAlignment.Center);
            name.Position = new Vector2(6, 44);
            name.Size = new Vector2(size.X - 12, 40);
            name.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            btn.AddChild(name);

            string kw = KeywordLine(u.Keywords);
            if (kw.Length > 0)
            {
                var kwl = BattleTheme.MakeLabel(kw, 15, BattleTheme.Accent, HorizontalAlignment.Center);
                kwl.Position = new Vector2(4, size.Y - 26);
                kwl.Size = new Vector2(size.X - 8, 22);
                btn.AddChild(kwl);
            }

            if (u.OwnerSeat == view.ActiveSeat && u.MovementUsed == 0 && u.AttacksUsed == 0)
                btn.Modulate = new Color(1, 1, 1, 1);
            btn.SetMeta("owner", u.OwnerSeat);
            _standeeLayer.AddChild(btn);
            _standees[u.EntityId] = btn;
        }
    }

    private void RenderHand(PlayerView view)
    {
        foreach (Node c in _handLayer.GetChildren())
            c.QueueFree();

        var hand = view.Self.Hand;
        int n = hand.Count;
        if (n == 0)
            return;

        const float cardW = 150f, cardH = 214f;
        float spacing = n <= 1 ? 0 : Mathf.Min(165f, (1500f - cardW) / (n - 1));
        float startX = 960f - ((n - 1) * spacing) / 2f - cardW / 2f;
        float y = 838f;

        for (int i = 0; i < n; i++)
        {
            var ch = hand[i];
            var def = _cards.Get(ch.CardId);
            var pos = new Vector2(startX + i * spacing, y);
            var card = BattleTheme.MakeButton(pos, new Vector2(cardW, cardH), BattleTheme.PanelDark, FactionColor(def.Faction), 3, 10);
            int id = ch.EntityId;
            card.Pressed += () => OnCardClicked(id);

            card.AddChild(Pip(def.Cost.ToString(), BattleTheme.CostColor, new Vector2(6, 6)));
            if (def.Type == CardType.Unit)
            {
                card.AddChild(Pip(def.Atk.ToString(), BattleTheme.AtkColor, new Vector2(6, cardH - 38)));
                card.AddChild(Pip(def.Hp.ToString(), BattleTheme.HpColor, new Vector2(cardW - 40, cardH - 38)));
            }
            else
            {
                var tag = BattleTheme.MakeLabel("指令", 15, BattleTheme.TextDim, HorizontalAlignment.Center);
                tag.Position = new Vector2(0, cardH - 34);
                tag.Size = new Vector2(cardW, 24);
                card.AddChild(tag);
            }

            var name = BattleTheme.MakeLabel(def.Name, 18, BattleTheme.TextMain, HorizontalAlignment.Center);
            name.Position = new Vector2(6, 44);
            name.Size = new Vector2(cardW - 12, 44);
            name.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            card.AddChild(name);

            var body = BattleTheme.MakeLabel(def.Text, 13, BattleTheme.TextDim, HorizontalAlignment.Center);
            body.Position = new Vector2(8, 96);
            body.Size = new Vector2(cardW - 16, 96);
            body.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            card.AddChild(body);

            _handLayer.AddChild(card);
        }
    }

    private void RenderHud(PlayerView view)
    {
        int active = view.ActiveSeat;
        _turnLabel.Text = active == 0 ? "▼ 玩家1(铁誓)的回合" : "▲ 玩家2(游群)的回合";
        _turnLabel.AddThemeColorOverride("font_color", BattleTheme.SeatColor(active));

        var self = view.Self;
        var opp = view.Opponent;
        _oppInfo.Text = $"手牌 {opp.HandCount}   牌库 {opp.DeckCount}   辉尘 {opp.Mana}/{opp.ManaMax}";
        _oppLeaderBtn.Text = $"{LeaderName(opp.LeaderId)}\n♥ {opp.LeaderHp}";
        _oppLeaderBtn.AddThemeFontSizeOverride("font_size", 24);
        _selfInfo.Text = $"{LeaderName(self.LeaderId)}   ♥ {self.LeaderHp}   辉尘 {self.Mana}/{self.ManaMax}   牌库 {self.DeckCount}";

        string skill = LeaderSkillText(self.LeaderId);
        _leaderPowerBtn.Text = skill;
        _leaderPowerBtn.AddThemeFontSizeOverride("font_size", 18);
    }

    // ---------- interaction gating ----------

    private void RefreshInteractable(PlayerView? view = null)
    {
        view ??= _host.GetView(ActiveSeat);
        bool over = view.Result != null;
        _endTurnBtn.Disabled = _busy || over;
        var legal = _host.LegalCommands(view.ActiveSeat);
        _leaderPowerBtn.Disabled = _busy || over || !legal.Any(c => c is UseLeaderSkillCommand);
        _leaderPowerBtn.Visible = legal.Any(c => c is UseLeaderSkillCommand) || _selKind == SelKind.Leader || LeaderSkillText(view.Self.LeaderId).Length > 0;
    }

    // ---------- selection ----------

    private void ClearSelection()
    {
        _selKind = SelKind.None;
        _candidates.Clear();
        _chosenCell = null;
        ClearHighlights();
    }

    private void ClearHighlights()
    {
        for (int scol = 0; scol < BattleTheme.Cols; scol++)
            for (int srow = 0; srow < BattleTheme.Rows; srow++)
                BattleTheme.SetButtonBg(_cellButtons[scol, srow], CellBaseColor(srow));

        foreach (var (id, node) in _standees)
        {
            int owner = (int)node.GetMeta("owner");
            BattleTheme.SetButtonBg(node, BattleTheme.SeatColor(owner).Darkened(0.15f), BattleTheme.SeatColor(owner), 3);
        }
        BattleTheme.SetButtonBg(_oppLeaderBtn, BattleTheme.PanelDark, BattleTheme.SeatColor1, 3, 10);
    }

    private void HighlightCell(Cell cell) =>
        BattleTheme.SetButtonBg(CellButton(cell), BattleTheme.AccentSoft, BattleTheme.Accent, 4);

    private void HighlightUnit(int id)
    {
        if (_standees.TryGetValue(id, out var node))
        {
            int owner = (int)node.GetMeta("owner");
            BattleTheme.SetButtonBg(node, BattleTheme.SeatColor(owner).Darkened(0.15f), BattleTheme.Accent, 5);
        }
    }

    private void HighlightLeader() =>
        BattleTheme.SetButtonBg(_oppLeaderBtn, BattleTheme.PanelDark, BattleTheme.Accent, 5, 10);

    private void OnCardClicked(int cardEntityId)
    {
        if (_busy) return;
        int seat = ActiveSeat;
        var legal = _host.LegalCommands(seat);
        _candidates = legal.Where(c => c is PlayCardCommand p && p.CardEntityId == cardEntityId).ToList();
        _selKind = SelKind.Card;
        _chosenCell = null;
        ClearHighlights();

        if (_candidates.Count == 0) { Log("这张牌现在打不出。"); ClearSelection(); return; }
        // No-target card (e.g. 抽牌指令): play immediately.
        if (_candidates.Count == 1 && CellOf(_candidates[0]) is null && UnitOf(_candidates[0]) is null)
        { Submit(_candidates[0]); return; }

        HighlightCardCandidates();
    }

    private void HighlightCardCandidates()
    {
        var cells = _candidates.Select(CellOf).Where(c => c != null).Select(c => c!.Value).Distinct().ToList();
        bool needCell = cells.Count > 0 && _chosenCell is null;
        if (needCell)
        {
            foreach (var cell in cells)
                HighlightCell(cell);
            Log("选择一个格子放置 / 施放。");
        }
        else
        {
            foreach (var id in _candidates.Select(UnitOf).Where(u => u != null).Select(u => u!.Value).Distinct())
                HighlightUnit(id);
            Log("选择目标随从。");
        }
    }

    private void OnUnitClicked(int entityId)
    {
        if (_busy) return;
        int seat = ActiveSeat;

        // If we're mid-target-pick, treat as a target first.
        if (_selKind is SelKind.Card or SelKind.Leader && TryPickUnitTarget(entityId)) return;

        var view = _host.GetView(seat);
        var unit = view.Units.FirstOrDefault(u => u.EntityId == entityId);
        if (unit is null) return;

        if (unit.OwnerSeat != seat)
        {
            // Clicking an enemy while a unit is selected = attack it.
            if (_selKind == SelKind.Unit && TryPickUnitTarget(entityId)) return;
            Log("那是敌方随从。");
            return;
        }

        var legal = _host.LegalCommands(seat);
        _candidates = legal.Where(c =>
            (c is MoveUnitCommand m && m.UnitEntityId == entityId) ||
            (c is AttackCommand a && a.AttackerEntityId == entityId)).ToList();
        _selKind = SelKind.Unit;
        _chosenCell = null;
        ClearHighlights();
        HighlightUnit(entityId);

        if (_candidates.Count == 0) { Log("这个随从本回合无法行动。"); return; }
        foreach (var cmd in _candidates)
        {
            if (cmd is MoveUnitCommand m) HighlightCell(m.To);
            else if (cmd is AttackCommand a) { if (a.TargetLeader) HighlightLeader(); else if (a.TargetUnitId is { } t) HighlightUnit(t); }
        }
        Log("移动到高亮格,或攻击高亮目标。");
    }

    private bool TryPickUnitTarget(int unitId)
    {
        var filtered = _candidates.Where(c => UnitOf(c) == unitId).ToList();
        if (filtered.Count == 0) return false;
        if (filtered.Count == 1) { Submit(filtered[0]); return true; }
        _candidates = filtered;
        ClearHighlights();
        HighlightCardCandidates();
        return true;
    }

    private void OnCellClicked(int scol, int srow)
    {
        if (_busy || _selKind == SelKind.None) return;
        var cell = ScreenToBoard(scol, srow);

        // Exact cell match, else column fallback (spatial column orders).
        var exact = _candidates.Where(c => CellOf(c) is { } cc && cc.Col == cell.Col && cc.Row == cell.Row).ToList();
        var pick = exact.Count > 0 ? exact : _candidates.Where(c => CellOf(c) is { } cc && cc.Col == cell.Col).ToList();
        if (pick.Count == 0) { Log("这里不是合法目标。"); return; }
        if (pick.Count == 1 && (UnitOf(pick[0]) is null)) { Submit(pick[0]); return; }

        // Deploy cell chosen but a battlecry target is still needed.
        _candidates = pick;
        _chosenCell = cell;
        ClearHighlights();
        HighlightCardCandidates();
    }

    private void OnLeaderClicked(int leaderSeat)
    {
        if (_busy) return;
        var filtered = _candidates.Where(c => c is AttackCommand { TargetLeader: true }).ToList();
        if (filtered.Count == 1) { Submit(filtered[0]); return; }
        Log("需要先选中一个能攻击本体的随从。");
    }

    private void OnLeaderPower()
    {
        if (_busy) return;
        int seat = ActiveSeat;
        var legal = _host.LegalCommands(seat);
        _candidates = legal.Where(c => c is UseLeaderSkillCommand).ToList();
        _selKind = SelKind.Leader;
        _chosenCell = null;
        ClearHighlights();
        if (_candidates.Count == 0) { Log("现在无法发动领袖技能。"); ClearSelection(); return; }
        if (_candidates.Count == 1 && CellOf(_candidates[0]) is null && UnitOf(_candidates[0]) is null)
        { Submit(_candidates[0]); return; }
        HighlightCardCandidates();
    }

    private void OnEndTurn()
    {
        if (_busy) return;
        Submit(new EndTurnCommand { Seat = ActiveSeat });
    }

    // ---------- command submission + event animation ----------

    private async void Submit(Command cmd)
    {
        if (_busy) return;
        _busy = true;
        ClearSelection();
        RefreshInteractable();

        int seatBefore = ActiveSeat;
        var result = await _host.SubmitCommandAsync(cmd.Seat, cmd);
        if (!result.Accepted)
        {
            Log($"非法操作:{result.Error?.Code}");
            _busy = false;
            RefreshInteractable();
            return;
        }

        var events = _pendingEvents.ToList();
        _pendingEvents.Clear();
        await AnimateEvents(events);

        var ended = events.OfType<GameEndedEvent>().FirstOrDefault();
        FullRender();
        _busy = false;

        if (ended != null) { ShowWinOverlay(ended); return; }

        int seatAfter = ActiveSeat;
        if (seatAfter != seatBefore)
            ShowPassOverlay(seatAfter);
        else
            RefreshInteractable();
    }

    private async Task AnimateEvents(IReadOnlyList<GameEvent> events)
    {
        foreach (var e in events)
        {
            switch (e)
            {
                case UnitMovedEvent m when _standees.TryGetValue(m.UnitEntityId, out var node):
                    await TweenTo(node, CellScreenPos(m.To) + new Vector2(7, 7), 0.16);
                    break;
                case AttackedEvent a when _standees.TryGetValue(a.AttackerEntityId, out var atk):
                    await Thump(atk);
                    break;
                case UnitDamagedEvent d when _standees.TryGetValue(d.UnitEntityId, out var tgt):
                    Flash(tgt, d.ShieldAbsorbed ? BattleTheme.CostColor : BattleTheme.DangerColor);
                    if (d.Amount > 0) FloatText(tgt.Position + new Vector2(50, 10), $"-{d.Amount}", BattleTheme.DangerColor);
                    await Delay(0.12);
                    break;
                case UnitHealedEvent h when h.Amount > 0 && _standees.TryGetValue(h.UnitEntityId, out var hn):
                    Flash(hn, BattleTheme.HpColor);
                    FloatText(hn.Position + new Vector2(50, 10), $"+{h.Amount}", BattleTheme.HpColor);
                    await Delay(0.12);
                    break;
                case UnitBuffedEvent b when _standees.TryGetValue(b.UnitEntityId, out var bn):
                    Flash(bn, BattleTheme.Accent);
                    await Delay(0.08);
                    break;
                case LeaderDamagedEvent ld:
                    Flash(_oppLeaderBtn, BattleTheme.DangerColor);
                    FloatText(_oppLeaderBtn.Position + new Vector2(160, 40), $"-{ld.Amount}", BattleTheme.DangerColor);
                    await Delay(0.14);
                    break;
                case UnitDiedEvent dd when _standees.TryGetValue(dd.UnitEntityId, out var dn):
                    await Fade(dn);
                    break;
                default:
                    break;
            }
        }
    }

    // ---------- overlays ----------

    private void ShowPassOverlay(int seat)
    {
        _handLayer.Visible = false;
        var panel = BattleTheme.MakeButton(Vector2.Zero, new Vector2(BattleTheme.ScreenW, BattleTheme.ScreenH), new Color(0.03f, 0.03f, 0.03f, 0.92f), radius: 0);
        var msg = BattleTheme.MakeLabel($"轮到 {(seat == 0 ? "玩家1(铁誓)" : "玩家2(游群)")}\n\n点击继续", 44, BattleTheme.TextMain, HorizontalAlignment.Center);
        msg.Position = new Vector2(0, 420);
        msg.Size = new Vector2(BattleTheme.ScreenW, 240);
        panel.AddChild(msg);
        panel.Pressed += () => { panel.QueueFree(); _handLayer.Visible = true; RefreshInteractable(); };
        _overlayLayer.AddChild(panel);
    }

    private void ShowWinOverlay(GameEndedEvent ended)
    {
        var panel = BattleTheme.MakeButton(Vector2.Zero, new Vector2(BattleTheme.ScreenW, BattleTheme.ScreenH), new Color(0.02f, 0.02f, 0.02f, 0.9f), radius: 0);
        string who = ended.WinnerSeat switch { 0 => "玩家1(铁誓)获胜", 1 => "玩家2(游群)获胜", _ => "平局" };
        var msg = BattleTheme.MakeLabel(who, 56, BattleTheme.Accent, HorizontalAlignment.Center);
        msg.Position = new Vector2(0, 380);
        msg.Size = new Vector2(BattleTheme.ScreenW, 100);
        panel.AddChild(msg);
        var again = BattleTheme.MakeLabel("点击再来一局", 30, BattleTheme.TextDim, HorizontalAlignment.Center);
        again.Position = new Vector2(0, 520);
        again.Size = new Vector2(BattleTheme.ScreenW, 60);
        panel.AddChild(again);
        panel.Pressed += () => GetTree().ReloadCurrentScene();
        _overlayLayer.AddChild(panel);
    }

    // ---------- tiny animation helpers ----------

    private async Task TweenTo(Control node, Vector2 target, double dur)
    {
        var t = CreateTween();
        t.TweenProperty(node, "position", target, dur).SetTrans(Tween.TransitionType.Sine);
        await ToSignal(t, Tween.SignalName.Finished);
    }

    private async Task Thump(Control node)
    {
        var home = node.Position;
        var t = CreateTween();
        t.TweenProperty(node, "position", home + new Vector2(0, -14), 0.06);
        t.TweenProperty(node, "position", home, 0.06);
        await ToSignal(t, Tween.SignalName.Finished);
    }

    private void Flash(Control node, Color color)
    {
        var t = CreateTween();
        node.Modulate = color;
        t.TweenProperty(node, "modulate", Colors.White, 0.25);
    }

    private async Task Fade(Control node)
    {
        var t = CreateTween();
        t.TweenProperty(node, "modulate:a", 0.0f, 0.2);
        await ToSignal(t, Tween.SignalName.Finished);
    }

    private void FloatText(Vector2 pos, string text, Color color)
    {
        var label = BattleTheme.MakeLabel(text, 30, color, HorizontalAlignment.Center);
        label.Position = pos;
        label.Size = new Vector2(80, 40);
        _overlayLayer.AddChild(label);
        var t = CreateTween();
        t.SetParallel(true);
        t.TweenProperty(label, "position", pos + new Vector2(0, -60), 0.6);
        t.TweenProperty(label, "modulate:a", 0.0f, 0.6);
        t.Chain().TweenCallback(Callable.From(label.QueueFree));
    }

    private async Task Delay(double sec) => await ToSignal(GetTree().CreateTimer(sec), Godot.Timer.SignalName.Timeout);

    // ---------- command target helpers ----------

    private static Cell? CellOf(Command c) => c switch
    {
        PlayCardCommand p => p.TargetCell,
        UseLeaderSkillCommand s => s.TargetCell,
        _ => null,
    };

    private static int? UnitOf(Command c) => c switch
    {
        PlayCardCommand p => p.TargetUnitId,
        UseLeaderSkillCommand s => s.TargetUnitId,
        AttackCommand a when !a.TargetLeader => a.TargetUnitId,
        _ => null,
    };

    // ---------- small view helpers ----------

    private static Label Pip(string text, Color color, Vector2 pos)
    {
        var label = BattleTheme.MakeLabel(text, 24, color, HorizontalAlignment.Center);
        label.Position = pos;
        label.Size = new Vector2(34, 34);
        return label;
    }

    private string ShortName(string cardId)
    {
        var name = _cards.Get(cardId).Name;
        int dot = name.IndexOf('·');
        return dot > 0 ? name[..dot] : name;
    }

    private string LeaderName(string leaderId) => _leaders.TryGet(leaderId, out var l) ? l.Name : "指挥官";

    private string LeaderSkillText(string leaderId) =>
        _leaders.TryGet(leaderId, out var l) && l.SkillEffects.Count > 0 ? $"{l.SkillName}\n{l.SkillCost}费" : "";

    private static Color FactionColor(string faction) => faction switch
    {
        "iron_vow" => BattleTheme.SeatColor0,
        "wildpack" => BattleTheme.SeatColor1,
        _ => BattleTheme.TextDim,
    };

    // By SCREEN row: the viewer's deploy zone is always the bottom row, the enemy's the top.
    private static Color CellBaseColor(int screenRow) =>
        screenRow == 0 ? BattleTheme.HomeRowP0 : screenRow == BattleTheme.Rows - 1 ? BattleTheme.HomeRowP1 : BattleTheme.CellEmpty;

    private static string KeywordLine(IReadOnlyList<KeywordSpec> keywords)
    {
        var parts = new List<string>();
        foreach (var k in keywords)
            parts.Add(k.Keyword switch
            {
                Keyword.Charge => "冲",
                Keyword.Assault => "突",
                Keyword.Swift => $"疾{k.Value}",
                Keyword.Range => $"程{k.Value}",
                Keyword.Guard => "守",
                Keyword.HoldFast => "坚",
                Keyword.Trample => "踏",
                Keyword.CheapShot => "偷",
                Keyword.Shield => "盾",
                Keyword.Garrison => "防",
                Keyword.Leap => "跃",
                _ => "",
            });
        return string.Join(" ", parts.Where(p => p.Length > 0));
    }

    private void Log(string message) => _logLabel.Text = message;
}
