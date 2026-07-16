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
    private Panel _detailPanel = null!; // left-side card inspector (click a piece to show it)

    private readonly List<GameEvent> _pendingEvents = new();
    private bool _busy;

    private SfxBank _sfx = null!;

    // Mode.
    private bool _vsAi;
    private int _humanSeat;
    private int _aiSeat;

    // Selection / targeting state.
    private enum SelKind { None, Card, Unit, Leader }
    private SelKind _selKind = SelKind.None;
    private List<Command> _candidates = new();
    private Cell? _chosenCell;

    // Card drag-to-play.
    private static readonly Vector2 GhostSize = new(150, 214);
    private int? _dragCardId;
    private bool _dragMoved;
    private Vector2 _dragStart;
    private Control? _dragGhost;

    private enum HitKind { Cell, Unit, Leader }
    private readonly record struct Hit(HitKind Kind, Cell Cell, int UnitId);

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
        if (!GameConfig.Configured)
            GameConfig.SetHotseat(); // launched directly (e.g. from the editor) → default to hotseat
        _vsAi = GameConfig.VsAi;
        _humanSeat = GameConfig.HumanSeat;
        _aiSeat = 1 - _humanSeat;

        _cards = GameData.LoadCards();
        _leaders = GameData.LoadLeaders();

        var decks = GameData.LoadDecks();
        var d0 = decks.First(d => d.Id == GameConfig.Deck0);
        var d1 = decks.First(d => d.Id == GameConfig.Deck1);

        var config = new MatchConfig
        {
            Seed = ((ulong)GD.Randi() << 32) | GD.Randi(),
            FirstSeat = (int)(GD.Randi() % 2),
            Deck0 = d0.Expand(), Leader0 = d0.Leader,
            Deck1 = d1.Expand(), Leader1 = d1.Leader,
        };
        _host = new LocalGameHost(_cards, _leaders, config);
        _host.Subscribe(0, e => _pendingEvents.Add(e)); // seat-0 stream: public events drive animation

        BuildStaticUi();
        _sfx = new SfxBank(this);
        FullRender();

        if (_vsAi && ActiveSeat == _aiSeat)
            _ = RunAiTurn(); // AI won the coin toss — it opens
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

        var menuBtn = BattleTheme.MakeButton(new Vector2(20, 20), new Vector2(120, 44), BattleTheme.PanelDark, BattleTheme.TextDim, 1, 8);
        menuBtn.Text = "菜单 (Esc)";
        menuBtn.AddThemeFontSizeOverride("font_size", 18);
        menuBtn.Pressed += () => GetTree().ChangeSceneToFile("res://scenes/menu/Menu.tscn");
        _hudLayer.AddChild(menuBtn);

        _detailPanel = new Panel { Position = DetailOrigin, Size = new Vector2(DetailW, DetailH), Visible = false };
        _detailPanel.AddThemeStyleboxOverride("panel", BattleTheme.Box(BattleTheme.PanelDark, BattleTheme.Accent, 2, 12));
        _hudLayer.AddChild(_detailPanel);
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

    // Whose eyes we render through: the human in vs-AI (fixed), the active seat in hotseat (flips).
    private int ViewSeat => _vsAi ? _humanSeat : ActiveSeat;

    private void FullRender()
    {
        var view = _host.GetView(ViewSeat);
        _persp = ViewSeat;

        // Units the viewer can still act with this turn (drives the "ready vs spent/sick" look).
        var actionable = new HashSet<int>();
        foreach (var c in _host.LegalCommands(ViewSeat))
            switch (c)
            {
                case MoveUnitCommand m: actionable.Add(m.UnitEntityId); break;
                case AttackCommand a: actionable.Add(a.AttackerEntityId); break;
            }

        RenderStandees(view, actionable);
        RenderHand(view);
        RenderHud(view);
        ClearSelection();
        RefreshInteractable(view);
    }

    private void RenderStandees(PlayerView view, HashSet<int> actionable)
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

            // Dim the active player's own units that can no longer act (集结中 or already spent).
            if (u.OwnerSeat == view.ActiveSeat && !actionable.Contains(u.EntityId))
                btn.Modulate = new Color(0.6f, 0.6f, 0.6f);
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
            card.ButtonDown += () => BeginCardDrag(id); // tap = select, drag = play (see _Input/EndCardDrag)

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
        bool viewerActive = view.ActiveSeat == ViewSeat;
        _turnLabel.Text = _vsAi
            ? (viewerActive ? "▼ 你的回合" : "▲ 对手思考中…")
            : $"▼ {LeaderName(view.Self.LeaderId)} 的回合";
        _turnLabel.AddThemeColorOverride("font_color", viewerActive ? BattleTheme.Accent : BattleTheme.TextDim);

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
        view ??= _host.GetView(ViewSeat);
        bool over = view.Result != null;
        bool canAct = !_busy && !over && (!_vsAi || ActiveSeat == _humanSeat);
        _endTurnBtn.Disabled = !canAct;
        IReadOnlyList<Command> legal = canAct ? _host.LegalCommands(ActiveSeat) : [];
        _leaderPowerBtn.Disabled = !canAct || !legal.Any(c => c is UseLeaderSkillCommand);
        _leaderPowerBtn.Visible = LeaderSkillText(view.Self.LeaderId).Length > 0;
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

    private void OnCardClicked(int cardEntityId) => SelectCard(cardEntityId, autoSubmit: true);

    private void SelectCard(int cardEntityId, bool autoSubmit)
    {
        if (_busy) return;
        int seat = ActiveSeat;
        var legal = _host.LegalCommands(seat);
        _candidates = legal.Where(c => c is PlayCardCommand p && p.CardEntityId == cardEntityId).ToList();
        _selKind = SelKind.Card;
        _chosenCell = null;
        ClearHighlights();

        if (_candidates.Count == 0) { Log("这张牌现在打不出。"); ClearSelection(); return; }
        // No-target card (e.g. 抽牌指令): a tap plays it immediately; a drag waits for the drop.
        if (autoSubmit && _candidates.Count == 1 && CellOf(_candidates[0]) is null && UnitOf(_candidates[0]) is null)
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
        // Inspecting a piece works any time — even during animations or the AI's turn.
        var unit = _host.GetView(ViewSeat).Units.FirstOrDefault(u => u.EntityId == entityId);
        if (unit != null) ShowUnitDetail(unit);

        if (_busy) return;
        int seat = ActiveSeat;

        // If we're mid-target-pick, treat as a target first.
        if (_selKind is SelKind.Card or SelKind.Leader && TryPickUnitTarget(entityId)) return;

        if (unit is null) return;

        if (unit.OwnerSeat != seat)
        {
            // Clicking an enemy while a unit is selected = attack it.
            if (_selKind == SelKind.Unit && TryPickUnitTarget(entityId)) return;
            return; // enemy piece: detail is already shown, nothing else to do
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
        PickCell(ScreenToBoard(scol, srow));
    }

    private void PickCell(Cell cell)
    {
        if (_selKind == SelKind.None) return;

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

    private void OnLeaderClicked(int leaderSeat) => PickLeader();

    private void PickLeader()
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

    // ---------- card drag-to-play ----------

    public override void _Input(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true, Keycode: Key.Escape })
        {
            GetTree().ChangeSceneToFile("res://scenes/menu/Menu.tscn");
            return;
        }
        if (_dragCardId is null)
            return;

        if (@event is InputEventMouseMotion mm)
        {
            if (!_dragMoved && mm.Position.DistanceTo(_dragStart) > 6f)
                _dragMoved = true;
            if (_dragGhost != null)
                _dragGhost.Position = mm.Position - GhostSize / 2f;
        }
        else if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false } mb)
        {
            GetViewport().SetInputAsHandled(); // eat the release so the cell/card button underneath doesn't also fire
            EndCardDrag(mb.Position);
        }
    }

    private void BeginCardDrag(int cardEntityId)
    {
        if (_busy || _dragCardId != null)
            return;
        _dragCardId = cardEntityId;
        _dragMoved = false;
        _dragStart = GetGlobalMousePosition();
        _dragGhost = BuildGhost(cardEntityId);
        _dragGhost.Position = _dragStart - GhostSize / 2f;
        _overlayLayer.AddChild(_dragGhost);
        SelectCard(cardEntityId, autoSubmit: false); // highlight legal targets while dragging
    }

    private void EndCardDrag(Vector2 pos)
    {
        int id = _dragCardId!.Value;
        _dragCardId = null;
        _dragGhost?.QueueFree();
        _dragGhost = null;

        if (!_dragMoved) { OnCardClicked(id); return; }  // a tap, not a drag → select
        if (_busy) { ClearSelection(); return; }

        var hit = HitTest(pos);
        if (hit is null) { ClearSelection(); Log("已取消。"); return; }
        switch (hit.Value.Kind)
        {
            case HitKind.Cell: PickCell(hit.Value.Cell); break;
            case HitKind.Unit: TryPickUnitTarget(hit.Value.UnitId); break;
            case HitKind.Leader: PickLeader(); break;
        }
    }

    private Hit? HitTest(Vector2 pos)
    {
        foreach (var (id, node) in _standees)
            if (node.GetGlobalRect().HasPoint(pos))
                return new Hit(HitKind.Unit, default, id);
        if (_oppLeaderBtn.GetGlobalRect().HasPoint(pos))
            return new Hit(HitKind.Leader, default, 0);
        for (int scol = 0; scol < BattleTheme.Cols; scol++)
            for (int srow = 0; srow < BattleTheme.Rows; srow++)
                if (_cellButtons[scol, srow].GetGlobalRect().HasPoint(pos))
                    return new Hit(HitKind.Cell, ScreenToBoard(scol, srow), 0);
        return null;
    }

    private Control BuildGhost(int cardEntityId)
    {
        var ghost = BattleTheme.MakeButton(Vector2.Zero, GhostSize, BattleTheme.PanelDark, BattleTheme.Accent, 3, 10);
        ghost.Disabled = true;
        ghost.MouseFilter = MouseFilterEnum.Ignore;
        ghost.Modulate = new Color(1, 1, 1, 0.85f);

        var ch = _host.GetView(ActiveSeat).Self.Hand.FirstOrDefault(h => h.EntityId == cardEntityId);
        if (ch != null)
        {
            var def = _cards.Get(ch.CardId);
            ghost.AddChild(Pip(def.Cost.ToString(), BattleTheme.CostColor, new Vector2(6, 6)));
            var name = BattleTheme.MakeLabel(def.Name, 18, BattleTheme.TextMain, HorizontalAlignment.Center);
            name.Position = new Vector2(6, 84);
            name.Size = new Vector2(GhostSize.X - 12, 46);
            name.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            ghost.AddChild(name);
        }
        return ghost;
    }

    // ---------- command submission + event animation ----------

    private async void Submit(Command cmd)
    {
        if (_busy) return;
        _busy = true;
        RefreshInteractable();

        int seatBefore = ActiveSeat;
        if (!await Apply(cmd)) { _busy = false; return; } // rejected, or ended (overlay shown)

        int seatAfter = ActiveSeat;
        if (_vsAi && seatAfter == _aiSeat) { await RunAiTurn(); return; }        // RunAiTurn owns _busy
        if (!_vsAi && seatAfter != seatBefore) { _busy = false; ShowPassOverlay(seatAfter); return; }
        _busy = false;
        RefreshInteractable();
    }

    /// <summary>Submit one command, animate its events, re-render. Returns false if rejected or the game ended.</summary>
    private async Task<bool> Apply(Command cmd)
    {
        ClearSelection();
        var result = await _host.SubmitCommandAsync(cmd.Seat, cmd);
        if (!result.Accepted) { Log($"非法操作:{result.Error?.Code}"); return false; }

        var events = _pendingEvents.ToList();
        _pendingEvents.Clear();
        await AnimateEvents(events);
        FullRender();

        var ended = events.OfType<GameEndedEvent>().FirstOrDefault();
        if (ended != null) { ShowWinOverlay(ended); return false; }
        return true;
    }

    /// <summary>Drives the AI seat: pick → apply → repeat until it ends its turn or the game ends.</summary>
    private async Task RunAiTurn()
    {
        _busy = true;
        RefreshInteractable();
        while (_vsAi && ActiveSeat == _aiSeat && _host.GetView(0).Result == null)
        {
            await Delay(0.5);
            var cmd = _host.SuggestCommand(_aiSeat);
            if (cmd is null) break;
            if (!await Apply(cmd)) return; // game ended (overlay shown), keep input locked
            if (cmd is EndTurnCommand) break;
        }
        _busy = false;
        RefreshInteractable();
    }

    private async Task AnimateEvents(IReadOnlyList<GameEvent> events)
    {
        foreach (var e in events)
        {
            switch (e)
            {
                case UnitDeployedEvent:
                    _sfx.Play("play");
                    await Delay(0.05);
                    break;
                case UnitMovedEvent m when _standees.TryGetValue(m.UnitEntityId, out var node):
                    _sfx.Play("move");
                    await TweenTo(node, CellScreenPos(m.To) + new Vector2(7, 7), 0.16);
                    break;
                case AttackedEvent a when _standees.TryGetValue(a.AttackerEntityId, out var atk):
                    _sfx.Play("attack");
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
                    _sfx.Play("leaderhit");
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
        var panel = new ColorRect { Color = new Color(0.02f, 0.02f, 0.02f, 0.9f) };
        panel.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _overlayLayer.AddChild(panel);

        string who;
        Color tint;
        if (ended.WinnerSeat < 0) { who = "平局"; tint = BattleTheme.TextMain; }
        else if (_vsAi) { bool win = ended.WinnerSeat == _humanSeat; who = win ? "胜 利" : "败 北"; tint = win ? BattleTheme.Accent : BattleTheme.DangerColor; }
        else { who = $"{LeaderName(_host.GetView(ended.WinnerSeat).Self.LeaderId)} 获胜"; tint = BattleTheme.Accent; }

        var msg = BattleTheme.MakeLabel(who, 64, tint, HorizontalAlignment.Center);
        msg.Position = new Vector2(0, 360);
        msg.Size = new Vector2(BattleTheme.ScreenW, 100);
        panel.AddChild(msg);

        var again = BattleTheme.MakeButton(new Vector2(BattleTheme.ScreenW / 2 - 320, 520), new Vector2(280, 80), BattleTheme.AccentSoft, BattleTheme.Accent, 2, 12);
        again.Text = "再来一局";
        again.AddThemeFontSizeOverride("font_size", 26);
        again.Pressed += () => GetTree().ReloadCurrentScene();
        panel.AddChild(again);

        var menu = BattleTheme.MakeButton(new Vector2(BattleTheme.ScreenW / 2 + 40, 520), new Vector2(280, 80), BattleTheme.PanelDark, BattleTheme.Accent, 2, 12);
        menu.Text = "返回菜单";
        menu.AddThemeFontSizeOverride("font_size", 26);
        menu.Pressed += () => GetTree().ChangeSceneToFile("res://scenes/menu/Menu.tscn");
        panel.AddChild(menu);
    }

    // ---------- card inspector (click a piece) ----------

    private static readonly Vector2 DetailOrigin = new(28, 140);
    private const float DetailW = 512f;
    private const float DetailH = 656f;

    private void ShowUnitDetail(UnitView u)
    {
        var def = _cards.Get(u.CardId);
        foreach (Node child in _detailPanel.GetChildren())
            child.QueueFree();
        _detailPanel.Visible = true;

        const float pad = 16f;
        float innerW = DetailW - pad * 2;
        var faction = FactionColor(def.Faction);

        // Card art: the real image once the AI-art pipeline ships it, else a faction-tinted placeholder.
        const float artH = 196f;
        var artPath = $"res://assets/cards/{def.Id}.png";
        if (ResourceLoader.Exists(artPath))
        {
            _detailPanel.AddChild(new TextureRect
            {
                Texture = GD.Load<Texture2D>(artPath),
                Position = new Vector2(pad, pad),
                Size = new Vector2(innerW, artH),
                MouseFilter = MouseFilterEnum.Ignore,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
            });
        }
        else
        {
            _detailPanel.AddChild(new ColorRect { Color = faction.Darkened(0.2f), Position = new Vector2(pad, pad), Size = new Vector2(innerW, artH), MouseFilter = MouseFilterEnum.Ignore });
            var ph = BattleTheme.MakeLabel(def.Name, 34, new Color(1, 1, 1, 0.85f), HorizontalAlignment.Center);
            ph.Position = new Vector2(pad, pad);
            ph.Size = new Vector2(innerW, artH);
            ph.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            _detailPanel.AddChild(ph);
            var tag = BattleTheme.MakeLabel("〔插画占位〕", 14, new Color(1, 1, 1, 0.5f), HorizontalAlignment.Center);
            tag.Position = new Vector2(pad, pad + artH - 26);
            tag.Size = new Vector2(innerW, 20);
            _detailPanel.AddChild(tag);
        }

        float y = pad + artH + 12;
        void Add(string text, int size, Color color, float h, bool wrap = false)
        {
            var label = BattleTheme.MakeLabel(text, size, color);
            label.Position = new Vector2(pad, y);
            label.Size = new Vector2(innerW, h);
            if (wrap) label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            _detailPanel.AddChild(label);
            y += h + 6;
        }

        Add(def.Name, 30, BattleTheme.TextMain, 40);
        Add($"{RarityName(def.Rarity)} · {FactionName(def.Faction)} · 随从", 16, BattleTheme.TextDim, 22);
        Add($"辉尘 {def.Cost}      攻击 {u.Atk}      生命 {u.CurrentHp}/{u.MaxHp}", 20, BattleTheme.TextMain, 28);
        if (def.Text.Length > 0)
            Add(def.Text, 16, BattleTheme.TextMain, 58, wrap: true);

        foreach (var k in u.Keywords)
            Add($"【{KeywordDisplayName(k)}】{KeywordDesc(k.Keyword)}", 15, BattleTheme.Accent, 46, wrap: true);

        var lore = BattleTheme.MakeLabel(FactionLore(def.Faction), 14, BattleTheme.TextDim);
        lore.Position = new Vector2(pad, DetailH - 58);
        lore.Size = new Vector2(innerW, 48);
        lore.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _detailPanel.AddChild(lore);

        // Close button added last so it sits on top of the art and is always clickable.
        var close = BattleTheme.MakeButton(new Vector2(DetailW - 46, 10), new Vector2(36, 36), BattleTheme.PanelDark, BattleTheme.TextDim, 1, 8);
        close.Text = "✕";
        close.AddThemeFontSizeOverride("font_size", 20);
        close.Pressed += HideDetail;
        _detailPanel.AddChild(close);
    }

    private void HideDetail() => _detailPanel.Visible = false;

    private static string RarityName(Rarity r) => r switch
    {
        Rarity.Common => "普通",
        Rarity.Rare => "稀有",
        Rarity.Epic => "史诗",
        Rarity.Legendary => "传说",
        _ => "衍生",
    };

    private static string FactionName(string faction) => faction switch
    {
        "iron_vow" => "铁誓军团",
        "wildpack" => "荒野游群",
        _ => "中立",
    };

    private static string FactionLore(string faction) => faction switch
    {
        "iron_vow" => "铁誓军团 —— 誓约骑士与堡垒工程师,断层战争中最后的正规军。以墙为盾,寸土不让。",
        "wildpack" => "荒野游群 —— 兽人与掠猎兽骑手,在断层荒原上以速度为生存法则。风过之处,防线洞开。",
        _ => "中立 —— 游荡在断层各段防线之间的雇佣兵、民兵与工匠,为辉尘而战。",
    };

    private static string KeywordDisplayName(KeywordSpec k) => k.Keyword switch
    {
        Keyword.Swift => $"疾行 {k.Value}",
        Keyword.Range => $"射程 {k.Value}",
        _ => KeywordDesc0(k.Keyword),
    };

    private static string KeywordDesc0(Keyword k) => k switch
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
        _ => k.ToString(),
    };

    private static string KeywordDesc(Keyword k) => k switch
    {
        Keyword.Charge => "部署当回合即可移动与攻击。",
        Keyword.Assault => "部署当回合可攻击,但不能移动。",
        Keyword.Swift => "每回合可移动的格数提升。",
        Keyword.Range => "可攻击同一行/列、直线且中间无阻挡的敌人,不受反击。",
        Keyword.Guard => "与其相邻的敌方随从必须优先攻击它。",
        Keyword.HoldFast => "本回合未移动时,受到的伤害 -1。",
        Keyword.Trample => "近战消灭敌方随从后,可立即占据其空出的格子。",
        Keyword.CheapShot => "近战攻击不受反击。",
        Keyword.Shield => "免疫下一次受到的伤害。",
        Keyword.Garrison => "位于己方底线行时 +1/+1。",
        Keyword.Leap => "移动时可跨过一个随从,直线跳跃 2 格。",
        Keyword.PackTactics => "近战攻击一个与你另一友方相邻的敌人时,伤害 +1。",
        Keyword.Hidden => "不能被选为目标,直到它造成伤害。",
        _ => "",
    };

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
        MoveUnitCommand m => m.To,
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
                Keyword.PackTactics => "围",
                _ => "",
            });
        return string.Join(" ", parts.Where(p => p.Length > 0));
    }

    private void Log(string message) => _logLabel.Text = message;
}
