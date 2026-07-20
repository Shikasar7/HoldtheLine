using Godot;
using HoldTheLine.Net.Client;
using HoldTheLine.Net.Protocol;
using HoldTheLine.Rules;
using HoldTheLine.Rules.Ai;
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
	private IGameHost _host = null!;
	private LocalGameHost? _localHost;   // set for hotseat / vs-AI (SuggestCommand lives here)
	private RemoteGameHost? _remoteHost; // set for online (the shared Session's match host)
	private bool _onlineReady;           // match_started applied and local seat known
	private bool _connFailed;            // reconnect permanently failed — a concede can no longer settle the match
	private System.Action<ConnectionState>? _connHandler; // stored so it can be unhooked from the persistent client
	private System.Action<RatingChange>? _ratingHandler;  // ranked ELO delta pushed post-match (C3); unhooked on exit
	private RatingChange? _ratingChange; // latest rating_change (may arrive before or after the win overlay)
	private Label? _ratingLabel;         // rating line on the result screen, filled/animated once the delta is known

	private Control _boardLayer = null!, _standeeLayer = null!, _handLayer = null!, _hudLayer = null!, _overlayLayer = null!;
	private readonly Button[,] _cellButtons = new Button[BattleTheme.Cols, BattleTheme.Rows];
	private readonly Dictionary<int, Button> _standees = new();
	private readonly HashSet<int> _emplacementUnits = new(); // entityIds of 架设 units — drives the "架设 +1" effect-damage tag
	private Button _oppLeaderBtn = null!, _endTurnBtn = null!, _leaderPowerBtn = null!, _cancelBtn = null!;
	private Label _turnLabel = null!, _oppInfo = null!, _selfInfo = null!, _logLabel = null!;
	private Panel _detailPanel = null!; // left-side card inspector (click a piece to show it)

	// docs/17 压力潮汐提示: a persistent HUD hint under the turn label (countdown → next-tide amount →
	// lethal warning). _tidePulse breathes the label while a next-turn tide would be lethal-and-unavoidable;
	// _tideLethalWarned fires the one-time cue (sound + center float) only on ENTERING that state.
	private Label _tideLabel = null!;
	private Tween? _tidePulse;
	private readonly bool[] _tideLethalWarned = new bool[2]; // per-seat one-time-alert guard (hotseat flips seats)

	private bool _busy;

	// Match stats for the result screen (accumulated from the public event stream).
	private readonly int[] _lineBreaks = new int[2]; // leader attacks each seat landed (推过底线打脸)
	private readonly int[] _leaderDmg = new int[2];  // total damage each leader took (combat + fatigue + tide)

	private SfxBank _sfx = null!;

	// AI art (null → geometric placeholder fallback everywhere).
	private Texture2D? _boardTex, _cardBackTex, _gemCost, _gemAtk, _gemHp;
	private readonly Dictionary<string, Texture2D?> _frameTex = new();
	private TextureRect? _oppAvatar, _selfAvatar;

	// Mode.
	private bool _vsAi;
	private bool _online;
	private int _humanSeat;
	private int _aiSeat;

	private Control? _gameMenu; // in-match menu overlay (继续 / 查看牌组 / 投降 / 返回菜单)
	private readonly IReadOnlyList<string>?[] _deckCards = new IReadOnlyList<string>?[2]; // brought decklists, for 查看牌组

	private Control? _mulliganPanel;     // 起手重抽 overlay (docs/11 §6), offline + online
	private int _mulliganMode;           // online only: 0 none, 1 selecting, 2 waiting-for-opponent
	private int _mulliganShownSeat = -1; // offline hotseat: seat whose panel is currently up
	private Label? _mulliganTimerLabel;  // countdown label on the online panel; freed with the panel
	private int _mulliganTimerSecs;      // ticks down locally, re-synced by MulliganTimerReceived

	// Presentation queue (plan §10 item 9). Every public event — whether the in-process host dispatched
	// it on the main thread or the RemoteGameHost received it on the WebSocket thread — lands in this
	// thread-safe queue and is played back one BEAT at a time by a single consumer (RunPlayback), paced
	// by animation rather than by network arrival. Local and online drive the same consumer, so the feel
	// work in items 2/3/5 (attack stages, projectiles, hit feedback, opponent card reveal) has one seam.
	private readonly System.Collections.Concurrent.ConcurrentQueue<GameEvent> _playQueue = new();
	private bool _playing;
	private Label? _connLabel;   // connection / opponent status banner (online only)
	private Label? _timerLabel;  // turn countdown (online only)
	private double _turnSecondsLeft;
	private int _turnActiveSeat = -1;

	// A view is "fixed" (always the local seat, mirrored for seat 1) in vs-AI and online; hotseat flips.
	private bool FixedView => _vsAi || _online;

	// Selection / targeting state.
	private enum SelKind { None, Card, Unit, Leader }
	private SelKind _selKind = SelKind.None;
	private List<Command> _candidates = new();
	private Cell? _chosenCell;
	private bool _crossPreview; // true while aiming a 十字 AOE order (cell_cross_all) — hover shows the footprint

	// The hand card whose effect is currently being aimed — lifted + enlarged so the player never loses
	// track of "which card am I resolving" while picking a target (paired with the 取消 button).
	private readonly Dictionary<int, Button> _handCards = new(); // entityId → hand card button (rebuilt each RenderHand)
	private Button? _liftedCard; // the node currently lifted; tracked separately so a transient un-lift keeps the model
	// Which hand card is selected — DERIVED, never stored: every Card-kind candidate is a PlayCardCommand for the
	// same card. Deriving it means a transient un-lift (mid-drag, a _busy pulse) can never forget it (review fix).
	private int? SelectedCardId =>
		_selKind == SelKind.Card && _candidates.FirstOrDefault() is PlayCardCommand p ? p.CardEntityId : null;

	// Hand layout: cards nearly fill the hand strip; the leader panel stacks vertically on the left.
	private static readonly Vector2 HandCardSize = new(196, 280);
	private const float HandY = 792f, HandLeft = 372f, HandRight = 1584f;

	// Hover preview (enlarged card, full rules text).
	private static readonly Vector2 PreviewSize = new(320, 458);
	private Control? _cardPreview;

	// Leader-skill hover tooltip (both leaders): mouse over a skill plate → its full effect text.
	private Control? _leaderTooltip;
	private string _selfLeaderId = "", _oppLeaderId = ""; // latest ids, read by the hover handlers

	// Card drag-to-play.
	private static readonly Vector2 GhostSize = new(150, 214);
	private int? _dragCardId;
	private bool _dragMoved;
	private Vector2 _dragStart;
	private Vector2 _dragTarget;      // mouse target the ghost eases toward each frame (item 1)
	private Control? _dragGhost;
	private Cell? _dragHoverCell;     // legal cell currently under the cursor (item 1: strengthen its highlight)

	private Tween? _shakeTween;       // active screen-shake tween (item 2/6), killed before a new one starts

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
		_online = GameConfig.Online;

		_cards = GameData.LoadCards();
		_leaders = GameData.LoadLeaders();

		BuildStaticUi();
		_sfx = new SfxBank(this);

		if (_online)
		{
			_ = SetupOnline(); // async connect → match → render (see partial below)
			return;
		}

		_humanSeat = GameConfig.HumanSeat;
		_aiSeat = 1 - _humanSeat;

		// Explicit card lists (custom decks from local storage) take precedence over the built-in id lookup.
		var (cards0, leader0) = ResolveOfflineDeck(GameConfig.Deck0, GameConfig.Deck0CardIds, GameConfig.Deck0Leader);
		var (cards1, leader1) = ResolveOfflineDeck(GameConfig.Deck1, GameConfig.Deck1CardIds, GameConfig.Deck1Leader);
		_deckCards[0] = cards0;
		_deckCards[1] = cards1;

		var config = new MatchConfig
		{
			Seed = ((ulong)GD.Randi() << 32) | GD.Randi(),
			FirstSeat = (int)(GD.Randi() % 2),
			Deck0 = cards0, Leader0 = leader0,
			Deck1 = cards1, Leader1 = leader1,
			MulliganEnabled = true, // 起手重抽 (docs/11)
		};
		// vs-AI tier (docs/12 C2); hotseat ignores the profile (no seat consumes SuggestCommand) — harmless to pass.
		var local = new LocalGameHost(_cards, _leaders, config, aiProfile: AiProfile.For(GameConfig.VsAiLevel));
		_localHost = local;
		_host = local;
		_host.Subscribe(0, e => _playQueue.Enqueue(e)); // seat-0 public stream → presentation queue (RunPlayback animates + tallies)

		FullRender();

		if (InMulliganPhase()) { BeginMulliganOffline(); return; } // 起手重抽 precedes the first turn
		if (_vsAi && ActiveSeat == _aiSeat)
			_ = RunAiTurn(); // AI won the coin toss — it opens
	}

	/// <summary>Resolve one offline seat's deck: an explicit card list (a custom deck from local storage)
	/// wins, otherwise fall back to the built-in preconstructed deck looked up by id.</summary>
	private static (IReadOnlyList<string> Cards, string Leader) ResolveOfflineDeck(string builtinId, IReadOnlyList<string>? customCards, string? customLeader)
	{
		if (customCards is { Count: > 0 })
			return (customCards, customLeader ?? "");
		var d = GameData.LoadDecks().First(x => x.Id == builtinId);
		return (d.Expand(), d.Leader);
	}

	// ---------- static scaffold ----------

	private void BuildStaticUi()
	{
		var bg = new ColorRect { Color = BattleTheme.Background };
		bg.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		bg.MouseFilter = MouseFilterEnum.Ignore;
		AddChild(bg);

		_boardTex = BattleTheme.Tex("board/board_main.png");
		_cardBackTex = BattleTheme.Tex("ui/card_back.png");
		_gemCost = BattleTheme.Tex("ui/gem_cost.png");
		_gemAtk = BattleTheme.Tex("ui/gem_atk.png");
		_gemHp = BattleTheme.Tex("ui/gem_hp.png");
		foreach (var f in new[] { "iron_vow", "wildpack", "neutral", "duskweaver", "undervault" })
			_frameTex[f] = BattleTheme.Tex($"ui/frame_{f}.png"); // duskweaver/undervault frames land in X4 (null-safe until then)

		if (_boardTex != null)
		{
			var table = BattleTheme.Art(_boardTex, Vector2.Zero, new Vector2(BattleTheme.ScreenW, BattleTheme.ScreenH));
			table.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
			AddChild(table);

			// Dark strips behind the HUD rows so text reads on the light table art.
			var shade = new Color(0.06f, 0.05f, 0.04f, 0.42f);
			foreach (var (sy, sh) in new[] { (0f, 148f), (784f, BattleTheme.ScreenH - 784f) })
			{
				var strip = new ColorRect { Color = shade, Position = new Vector2(0, sy), Size = new Vector2(BattleTheme.ScreenW, sh) };
				strip.MouseFilter = MouseFilterEnum.Ignore;
				AddChild(strip);
			}
		}

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
				var btn = BattleTheme.MakeButton(BattleTheme.CellPos(scol, srow), new Vector2(BattleTheme.CellW, BattleTheme.CellH), CellBase(srow));
				btn.Pressed += () => OnCellClicked(sc, sr);
				btn.MouseEntered += () => OnCellHover(sc, sr); // 友伤确认: preview the AOE footprint
				btn.MouseExited += OnCellHoverExit;
				_boardLayer.AddChild(btn);
				_cellButtons[scol, srow] = btn;
			}

		// HUD.
		_turnLabel = BattleTheme.MakeOutlinedLabel("", 34, BattleTheme.TextMain, HorizontalAlignment.Center);
		_turnLabel.Position = new Vector2(660, 20);
		_turnLabel.Size = new Vector2(600, 44);
		_hudLayer.AddChild(_turnLabel);

		// docs/17: tide hint sits just under the turn label, same center column.
		_tideLabel = BattleTheme.MakeOutlinedLabel("", 22, BattleTheme.TextDim, HorizontalAlignment.Center);
		_tideLabel.Position = new Vector2(660, 62);
		_tideLabel.Size = new Vector2(600, 30);
		_tideLabel.Visible = false;
		_hudLayer.AddChild(_tideLabel);

		// Opponent strip: card-back chip + info + leader plate (avatar filled per-render, hotseat flips it).
		if (_cardBackTex != null)
			_hudLayer.AddChild(BattleTheme.Art(_cardBackTex, new Vector2(60, 52), new Vector2(52, 78)));
		_oppInfo = BattleTheme.MakeOutlinedLabel("", 24, BattleTheme.TextMain);
		_oppInfo.Position = new Vector2(_cardBackTex != null ? 128 : 60, 70);
		_oppInfo.Size = new Vector2(700, 40);
		_hudLayer.AddChild(_oppInfo);

		_oppLeaderBtn = BattleTheme.MakeButton(new Vector2(1500, 40), new Vector2(360, 96), BattleTheme.PanelDark, BattleTheme.SeatColor1, 3, 10);
		_oppLeaderBtn.Pressed += () => OnLeaderClicked(1);
		_oppLeaderBtn.MouseEntered += () => ShowLeaderTooltip(_oppLeaderId, _oppLeaderBtn.GetRect(), below: true);
		_oppLeaderBtn.MouseExited += HideLeaderTooltip;
		_oppAvatar = new TextureRect
		{
			ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
			StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
			MouseFilter = MouseFilterEnum.Ignore,
			Position = new Vector2(8, 8), Size = new Vector2(80, 80),
		};
		_oppLeaderBtn.AddChild(_oppAvatar);
		_hudLayer.AddChild(_oppLeaderBtn);

		// Self leader block: stacked vertically at the far left so the hand strip gets the width.
		_selfAvatar = new TextureRect
		{
			ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
			StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
			MouseFilter = MouseFilterEnum.Ignore,
			Position = new Vector2(24, 796), Size = new Vector2(100, 100),
		};
		_hudLayer.AddChild(_selfAvatar);

		_selfInfo = BattleTheme.MakeOutlinedLabel("", 17, BattleTheme.TextMain);
		_selfInfo.Position = new Vector2(134, 800);
		_selfInfo.Size = new Vector2(226, 92);
		_hudLayer.AddChild(_selfInfo);

		_leaderPowerBtn = BattleTheme.MakeButton(new Vector2(24, 916), new Vector2(336, 68), BattleTheme.PanelDark, BattleTheme.SeatColor0, 2, 10);
		_leaderPowerBtn.Pressed += OnLeaderPower;
		_leaderPowerBtn.MouseEntered += () => ShowLeaderTooltip(_selfLeaderId, _leaderPowerBtn.GetRect(), below: false);
		_leaderPowerBtn.MouseExited += HideLeaderTooltip;
		_hudLayer.AddChild(_leaderPowerBtn);

		_endTurnBtn = BattleTheme.MakeButton(new Vector2(1600, 844), new Vector2(260, 90), BattleTheme.AccentSoft, BattleTheme.Accent, 2, 12);
		if (BattleTheme.Tex("ui/button_plate.png") is { } plate)
		{
			_endTurnBtn.AddChild(BattleTheme.Art(plate, Vector2.Zero, new Vector2(260, 90)));
			var etLabel = BattleTheme.MakeOutlinedLabel("结束回合", 28, BattleTheme.TextMain, HorizontalAlignment.Center);
			etLabel.Size = new Vector2(260, 90);
			_endTurnBtn.AddChild(etLabel);
		}
		else
		{
			_endTurnBtn.Text = "结束回合";
			_endTurnBtn.AddThemeFontSizeOverride("font_size", 28);
		}
		_endTurnBtn.Pressed += OnEndTurn;
		_hudLayer.AddChild(_endTurnBtn);

		// 取消: back out of a card/unit/leader selection (also bound to Esc). Hidden until something is
		// being aimed — sits just above 结束回合, clear of the hand strip.
		_cancelBtn = BattleTheme.MakeButton(new Vector2(1600, 752), new Vector2(260, 74), BattleTheme.PanelDark, BattleTheme.DangerColor, 2, 12);
		_cancelBtn.Text = "✕ 取消 (Esc)";
		_cancelBtn.AddThemeFontSizeOverride("font_size", 24);
		_cancelBtn.Visible = false;
		_cancelBtn.Pressed += OnCancelSelection;
		_hudLayer.AddChild(_cancelBtn);

		// Log sits between board and hand (bigger hand cards now cover the old bottom slot).
		_logLabel = BattleTheme.MakeOutlinedLabel("", 20, BattleTheme.TextDim, HorizontalAlignment.Center);
		_logLabel.Position = new Vector2(360, 752);
		_logLabel.Size = new Vector2(1200, 28);
		_hudLayer.AddChild(_logLabel);

		var menuBtn = BattleTheme.MakeButton(new Vector2(20, 20), new Vector2(120, 44), BattleTheme.PanelDark, BattleTheme.TextDim, 1, 8);
		menuBtn.Text = "菜单 (Esc)";
		menuBtn.AddThemeFontSizeOverride("font_size", 18);
		menuBtn.Pressed += ToggleGameMenu;
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

	// Whose eyes we render through: the local seat when fixed (vs-AI / online), the active seat in
	// hotseat (flips each turn). Seat 1's board is mirrored automatically via _persp = ViewSeat.
	private int ViewSeat => FixedView ? _humanSeat : ActiveSeat;

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
		_emplacementUnits.Clear();

		foreach (var u in view.Units)
		{
			var pos = CellScreenPos(u.Cell) + new Vector2(7, 7);
			var size = new Vector2(BattleTheme.CellW - 14, BattleTheme.CellH - 14);
			var seatColor = BattleTheme.SeatColor(u.OwnerSeat);
			var art = BattleTheme.Tex($"standees/{u.CardId}.png");

			// With art the panel goes translucent (seat-tinted) so the board shows through; border keeps ownership readable.
			var bg = art != null
				? new Color(seatColor.R, seatColor.G, seatColor.B, 0.22f)
				: seatColor.Darkened(0.15f);
			var btn = BattleTheme.MakeButton(pos, size, bg, seatColor, 3, 8);
			int id = u.EntityId;
			btn.Pressed += () => OnUnitClicked(id);

			if (art != null)
				btn.AddChild(BattleTheme.Art(art, new Vector2(2, 2), size - new Vector2(4, 4),
					TextureRect.StretchModeEnum.KeepAspectCentered));

			btn.AddChild(Pip(u.Atk.ToString(), BattleTheme.AtkColor, new Vector2(6, 4), _gemAtk));
			var hpColor = u.CurrentHp < u.MaxHp ? BattleTheme.DangerColor : BattleTheme.HpColor;
			btn.AddChild(Pip(u.CurrentHp.ToString(), hpColor, new Vector2(size.X - 40, 4), _gemHp));

			var name = BattleTheme.MakeOutlinedLabel(ShortName(u.CardId), art != null ? 15 : 17, BattleTheme.TextMain, HorizontalAlignment.Center);
			name.AutowrapMode = TextServer.AutowrapMode.Arbitrary;
			name.ClipContents = true;
			if (art != null) { name.Position = new Vector2(4, size.Y - 24); name.Size = new Vector2(size.X - 8, 22); }
			else { name.Position = new Vector2(6, 44); name.Size = new Vector2(size.X - 12, 40); }
			btn.AddChild(name);

			string kw = KeywordLine(u.Keywords);
			if (kw.Length > 0)
			{
				var kwl = BattleTheme.MakeOutlinedLabel(kw, 14, BattleTheme.Accent, HorizontalAlignment.Center);
				kwl.Position = new Vector2(4, art != null ? size.Y - 46 : size.Y - 26);
				kwl.Size = new Vector2(size.X - 8, 22);
				btn.AddChild(kwl);
			}

			// Status indicators (buffs/debuffs) on the card face — driven by LIVE state (u.ShieldActive), not
			// the static keyword list, so 持盾 shows/clears the moment the charge is granted/spent.
			SetStandeeStatuses(btn, StandeeStatuses(u.ShieldActive));

			// Dim the active player's own units that can no longer act (集结中 or already spent).
			if (u.OwnerSeat == view.ActiveSeat && !actionable.Contains(u.EntityId))
				btn.Modulate = new Color(0.6f, 0.6f, 0.6f);
			btn.SetMeta("owner", u.OwnerSeat);
			btn.SetMeta("bg", bg);
			_standeeLayer.AddChild(btn);
			_standees[u.EntityId] = btn;
			if (u.Keywords.Any(k => k.Keyword == Keyword.Emplacement))
				_emplacementUnits.Add(u.EntityId); // 架设 is innate & entityIds never recycle → never a false tag
		}
	}

	private void RenderHand(PlayerView view)
	{
		HideCardPreview();
		foreach (Node c in _handLayer.GetChildren())
			c.QueueFree();
		_handCards.Clear();

		var hand = view.Self.Hand;
		int n = hand.Count;
		if (n == 0)
			return;

		float spacing = n <= 1 ? 0 : Mathf.Min(HandCardSize.X + 14f, (HandRight - HandLeft - HandCardSize.X) / (n - 1));
		float startX = (HandLeft + HandRight) / 2f - ((n - 1) * spacing) / 2f - HandCardSize.X / 2f;

		for (int i = 0; i < n; i++)
		{
			var ch = hand[i];
			var def = _cards.Get(ch.CardId);
			bool isOrder = def.Type != CardType.Unit;
			var pos = new Vector2(startX + i * spacing, HandY);
			// Border color doubles as the card-type cue: faction color = unit, 辉尘 teal = order.
			var border = isOrder ? BattleTheme.Accent : FactionColor(def.Faction);
			var card = BattleTheme.MakeButton(pos, HandCardSize, BattleTheme.PanelDark, border, 3, 10);
			int id = ch.EntityId;
			float cardX = pos.X;
			card.PivotOffset = HandCardSize / 2f;          // lift/scale the selected card about its centre
			card.SetMeta("basePos", pos);                  // restored by ClearCardHighlight
			card.SetMeta("border", border);                // the card-type cue, re-applied after de-selecting
			card.ButtonDown += () => BeginCardDrag(id); // tap = select, drag = play (see _Input/EndCardDrag)
			card.MouseEntered += () => ShowCardPreview(def, cardX);
			card.MouseExited += HideCardPreview;
			card.AddChild(BuildCardVisual(def, HandCardSize, compact: true));
			_handLayer.AddChild(card);
			_handCards[id] = card;
		}
		_liftedCard = null; // the old lifted node was just freed; the new nodes start un-lifted
	}

	// ---------- card visuals (shared by hand cards and the hover preview) ----------

	/// <summary>Full card face: art, faction frame, gems, type badge, name, rules text.</summary>
	private Control BuildCardVisual(CardDefinition def, Vector2 size, bool compact, bool backing = false)
	{
		float w = size.X, h = size.Y;
		bool isOrder = def.Type != CardType.Unit;
		var root = new Control { Size = size, MouseFilter = MouseFilterEnum.Ignore };

		if (backing)
		{
			var bg = new Panel { Size = size, MouseFilter = MouseFilterEnum.Ignore };
			bg.AddThemeStyleboxOverride("panel", BattleTheme.Box(BattleTheme.PanelDark,
				isOrder ? BattleTheme.Accent : FactionColor(def.Faction), 3, 12));
			root.AddChild(bg);
		}

		int gem = Mathf.RoundToInt(h * 0.155f);
		int nameSize = Mathf.RoundToInt(h * 0.062f);
		int bodySize = Mathf.RoundToInt(h * (compact ? 0.048f : 0.042f)); // 预览字号略缩,换取放下全文

		var art = BattleTheme.Tex($"cards/{def.Id}.png");
		var frame = _frameTex.GetValueOrDefault(def.Faction) ?? _frameTex.GetValueOrDefault("neutral");
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

			// Rules text on a soft dark plate: the frames' leather panels vary too much in
			// brightness (wildpack is near-dark) for bare ink to stay readable.
			if (def.Text.Length > 0)
			{
				var platePos = new Vector2(w * 0.14f, h * 0.715f);
				var plateSize = new Vector2(w * 0.72f, h * (compact ? 0.19f : 0.215f)); // 两行完整显示
				var plate = new Panel { Position = platePos, Size = plateSize, MouseFilter = MouseFilterEnum.Ignore };
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
				if (compact)
					body.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis; // 悬停放大看全文
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

		root.AddChild(Pip(def.Cost.ToString(), BattleTheme.CostColor, new Vector2(2, 2), _gemCost, gem));
		if (!isOrder)
		{
			root.AddChild(Pip(def.Atk.ToString(), BattleTheme.AtkColor, new Vector2(2, h - gem - 2), _gemAtk, gem));
			root.AddChild(Pip(def.Hp.ToString(), BattleTheme.HpColor, new Vector2(w - gem - 2, h - gem - 2), _gemHp, gem));
		}
		else
		{
			// Order badge (top-right 辉尘 roundel): units carry atk/hp gems, orders carry this — 一眼可辨.
			var badge = new Panel { Position = new Vector2(w - gem - 2, 2), Size = new Vector2(gem, gem), MouseFilter = MouseFilterEnum.Ignore };
			badge.AddThemeStyleboxOverride("panel", BattleTheme.Box(BattleTheme.AccentSoft, BattleTheme.Accent, 2, gem / 2));
			var glyph = BattleTheme.MakeOutlinedLabel("令", Mathf.RoundToInt(gem * 0.5f), Colors.White, HorizontalAlignment.Center);
			glyph.Size = new Vector2(gem, gem);
			badge.AddChild(glyph);
			root.AddChild(badge);
		}
		return root;
	}

	private void ShowCardPreview(CardDefinition def, float cardX)
	{
		if (_dragCardId != null)
			return;
		HideCardPreview();

		// Enlarged card + a full-rules plate below it (the frames' own text panels are too small for long texts).
		string fullText = BattleTheme.BodyText(def.Text);
		float plateH = fullText.Length > 0 ? 76f + 24f * Mathf.Ceil(fullText.Length / 15f) : 0f;
		float totalH = PreviewSize.Y + (plateH > 0 ? plateH + 8f : 0f);
		float x = Mathf.Clamp(cardX + HandCardSize.X / 2 - PreviewSize.X / 2, 10, BattleTheme.ScreenW - PreviewSize.X - 10);
		var root = new Control
		{
			Position = new Vector2(x, HandY - totalH - 12),
			Size = new Vector2(PreviewSize.X, totalH),
			MouseFilter = MouseFilterEnum.Ignore,
		};
		root.AddChild(BuildCardVisual(def, PreviewSize, compact: true, backing: true));

		if (plateH > 0)
		{
			bool isOrder = def.Type != CardType.Unit;
			var plate = new Panel { Position = new Vector2(0, PreviewSize.Y + 8f), Size = new Vector2(PreviewSize.X, plateH), MouseFilter = MouseFilterEnum.Ignore };
			plate.AddThemeStyleboxOverride("panel", BattleTheme.Box(BattleTheme.PanelDark,
				isOrder ? BattleTheme.Accent : FactionColor(def.Faction), 2, 10));

			var tag = BattleTheme.MakeOutlinedLabel(isOrder ? "指令" : "随从", 16,
				isOrder ? BattleTheme.Accent : BattleTheme.TextDim, HorizontalAlignment.Center);
			tag.Position = new Vector2(12, 8);
			tag.Size = new Vector2(PreviewSize.X - 24, 22);
			plate.AddChild(tag);

			var text = BattleTheme.MakeLabel(fullText, 19, BattleTheme.TextMain, HorizontalAlignment.Center);
			text.AutowrapMode = TextServer.AutowrapMode.Arbitrary;
			text.VerticalAlignment = VerticalAlignment.Top;
			text.Position = new Vector2(14, 36);
			text.Size = new Vector2(PreviewSize.X - 28, plateH - 44);
			plate.AddChild(text);
			root.AddChild(plate);
		}

		_overlayLayer.AddChild(root);
		_cardPreview = root;
	}

	private void HideCardPreview()
	{
		_cardPreview?.QueueFree();
		_cardPreview = null;
	}

	// ---------- leader-skill hover tooltip ----------

	/// <summary>Themed panel explaining a leader's skill, anchored to the hovered plate (below the
	/// top-right opponent plate, above the bottom-left own power button).</summary>
	private void ShowLeaderTooltip(string leaderId, Rect2 anchor, bool below)
	{
		HideLeaderTooltip();
		if (_dragCardId != null) return; // don't cover the board mid-drag
		if (!_leaders.TryGet(leaderId, out var l) || l.SkillEffects.Count == 0) return;

		var accent = FactionColor(l.Faction);
		string body = SkillEffectText(l);

		const float w = 420f, pad = 16f, headerH = 28f, lineH = 28f;
		int lines = Mathf.Max(1, Mathf.CeilToInt(body.Length / 17f)); // ~17 CJK glyphs per wrapped line
		float bodyH = lines * lineH;
		float h = pad + headerH + 6f + bodyH + pad;

		float x = Mathf.Clamp(anchor.Position.X + anchor.Size.X / 2f - w / 2f, 10f, BattleTheme.ScreenW - w - 10f);
		float y = below ? anchor.Position.Y + anchor.Size.Y + 10f : anchor.Position.Y - h - 10f;

		var root = new Control { Position = new Vector2(x, y), Size = new Vector2(w, h), MouseFilter = MouseFilterEnum.Ignore };

		var panel = new Panel { Size = new Vector2(w, h), MouseFilter = MouseFilterEnum.Ignore };
		panel.AddThemeStyleboxOverride("panel", BattleTheme.Box(BattleTheme.PanelDark, accent, 2, 12));
		root.AddChild(panel);

		var header = BattleTheme.MakeOutlinedLabel($"{l.SkillName} · {l.SkillCost} 费", 22, accent);
		header.Position = new Vector2(pad, pad - 4);
		header.Size = new Vector2(w - 2 * pad, headerH);
		root.AddChild(header);

		var text = BattleTheme.MakeLabel(body, 20, BattleTheme.TextMain);
		text.AutowrapMode = TextServer.AutowrapMode.Arbitrary;
		text.VerticalAlignment = VerticalAlignment.Top;
		text.Position = new Vector2(pad, pad + headerH + 4);
		text.Size = new Vector2(w - 2 * pad, bodyH + 4);
		root.AddChild(text);

		_overlayLayer.AddChild(root);
		_leaderTooltip = root;
	}

	private void HideLeaderTooltip()
	{
		_leaderTooltip?.QueueFree();
		_leaderTooltip = null;
	}

	// The one-liner text is "技能名(N费):效果"; the header already carries name+cost, so drop that prefix
	// for the body and fall back to the whole line when the pattern isn't present.
	private static string SkillEffectText(LeaderDefinition l)
	{
		string body = BattleTheme.BodyText(l.Text);
		int colon = body.IndexOfAny(['：', ':']);
		return colon >= 0 && colon < body.Length - 1 ? body[(colon + 1)..].TrimStart() : body;
	}

	private void RenderHud(PlayerView view)
	{
		bool viewerActive = view.ActiveSeat == ViewSeat;
		_turnLabel.Text = FixedView
			? (viewerActive ? "▼ 你的回合" : "▲ 对手回合…")
			: $"▼ {LeaderName(view.Self.LeaderId)} 的回合";
		_turnLabel.AddThemeColorOverride("font_color", viewerActive ? BattleTheme.Accent : BattleTheme.TextDim);

		var self = view.Self;
		var opp = view.Opponent;
		_selfLeaderId = self.LeaderId;
		_oppLeaderId = opp.LeaderId;
		_oppInfo.Text = $"手牌 {opp.HandCount}   牌库 {opp.DeckCount}   辉尘 {opp.Mana}/{opp.ManaMax}";
		_oppLeaderBtn.Text = $"{LeaderName(opp.LeaderId)}\n♥ {opp.LeaderHp}";
		_oppLeaderBtn.AddThemeFontSizeOverride("font_size", 24);
		// Stacked next to the avatar: name / vitals on separate lines to keep the block narrow.
		_selfInfo.Text = $"{LeaderName(self.LeaderId)}\n♥ {self.LeaderHp}   辉尘 {self.Mana}/{self.ManaMax}\n牌库 {self.DeckCount}";

		string skill = LeaderSkillText(self.LeaderId);
		_leaderPowerBtn.Text = skill;
		_leaderPowerBtn.AddThemeFontSizeOverride("font_size", 18);

		if (_oppAvatar != null) _oppAvatar.Texture = BattleTheme.Tex($"leaders/{opp.LeaderId}.png");
		if (_selfAvatar != null) _selfAvatar.Texture = BattleTheme.Tex($"leaders/{self.LeaderId}.png");

		RefreshTideHint(view);
	}

	/// <summary>docs/17 压力潮汐提示 (all derived from PlayerView — no rules/protocol changes): before the tide
	/// starts, a low-key "还有 N 轮"; once it bites, "下次潮汐 -X"; and a breathing red "将致命" when this seat's
	/// next turn would take an unavoidable lethal hit. Perspective = <see cref="ViewSeat"/> (fixed seat online /
	/// vs-AI, the active operator in hotseat). The tide is judged at YOUR turn start, so a pressing unit only
	/// spares you *if it survives the opponent's turn* — hence the conditional wording.</summary>
	private void RefreshTideHint(PlayerView view)
	{
		if (view.Result != null) { StopTidePulse(); _tideLabel.Visible = false; return; }

		int seat = ViewSeat;
		int start = RulesInfo.PressureTideStartRound;
		int round = (view.TurnNumber + 1) / 2;

		// Which round your NEXT turn falls in, and the bleed it would deal (round - start + 1; 0 before start).
		int nextOwnTurn = view.ActiveSeat == seat ? view.TurnNumber + 2 : view.TurnNumber + 1;
		int nextRound = (nextOwnTurn + 1) / 2;
		int nextAmount = nextRound >= start ? nextRound - start + 1 : 0;

		bool pressing = view.Units.Any(u => u.OwnerSeat == seat && !BoardGeometry.InOwnHalf(seat, u.Cell));
		bool lethal = nextAmount > 0 && nextAmount >= view.Self.LeaderHp;
		// On YOUR own turn the tide is judged at TurnNumber+2, so you can still push a unit forward this turn to
		// dodge it — the kill is only truly unavoidable on the OPPONENT's turn (you can't act before it hits).
		bool canStillAct = view.ActiveSeat == seat;

		string text;
		Color color;
		if (nextAmount <= 0)
		{
			text = $"潮汐将至 · 还有 {System.Math.Max(0, start - round)} 轮";
			color = BattleTheme.TextDim;
		}
		else if (lethal && !pressing && !canStillAct)
		{
			text = "⚠ 下次潮汐将致命!";
			color = BattleTheme.DangerColor;
		}
		else if (lethal && !pressing) // your turn: still avoidable → conditional wording, no alarm cue
		{
			text = "⚠ 潮汐将致命 —— 需本回合压进敌方半场";
			color = BattleTheme.DangerColor;
		}
		else if (pressing)
		{
			// Conditional: the pressing unit can be cleared on the opponent's turn, so never promise immunity.
			text = lethal ? $"⚠ 潮汐 -{nextAmount}(已压制,若被逐回将致命)" : $"潮汐 -{nextAmount}(已压制,若被逐回则触发)";
			color = lethal ? BattleTheme.DangerColor : BattleTheme.TextDim;
		}
		else
		{
			text = $"下次潮汐 -{nextAmount}";
			color = BattleTheme.Accent;
		}

		_tideLabel.Text = text;
		_tideLabel.AddThemeColorOverride("font_color", color);
		_tideLabel.Visible = true;

		// The strongest cue (pulse + one-time sound/float) is reserved for a kill you can no longer avoid.
		// _tideLethalWarned is per-seat so hotseat's second player still gets their own one-time alert.
		bool danger = lethal && !pressing && !canStillAct;
		if (danger) StartTidePulse(); else StopTidePulse();
		if (danger && !_tideLethalWarned[seat])
		{
			_tideLethalWarned[seat] = true;
			_sfx.Play("tide");
			FloatText(new Vector2(BattleTheme.ScreenW / 2f, 360), "⚠ 下次潮汐将致命!", BattleTheme.DangerColor);
		}
		else if (!danger)
			_tideLethalWarned[seat] = false; // re-arm this seat once out of the unavoidable-lethal state
	}

	private void StartTidePulse()
	{
		if (_tidePulse != null && _tidePulse.IsValid()) return; // already breathing — don't restart each render
		_tidePulse = CreateTween();
		_tidePulse.SetLoops();
		_tidePulse.TweenProperty(_tideLabel, "modulate:a", 0.35f, 0.5).SetTrans(Tween.TransitionType.Sine);
		_tidePulse.TweenProperty(_tideLabel, "modulate:a", 1.0f, 0.5).SetTrans(Tween.TransitionType.Sine);
	}

	private void StopTidePulse()
	{
		if (_tidePulse != null) { _tidePulse.Kill(); _tidePulse = null; }
		if (GodotObject.IsInstanceValid(_tideLabel)) _tideLabel.Modulate = Colors.White;
	}

	// ---------- interaction gating ----------

	private void RefreshInteractable(PlayerView? view = null)
	{
		view ??= _host.GetView(ViewSeat);
		bool over = view.Result != null;
		bool canAct = !_busy && !over && (!FixedView || ActiveSeat == _humanSeat);
		_endTurnBtn.Disabled = !canAct;
		IReadOnlyList<Command> legal = canAct ? _host.LegalCommands(ActiveSeat) : [];
		_leaderPowerBtn.Disabled = !canAct || !legal.Any(c => c is UseLeaderSkillCommand);
		_leaderPowerBtn.Visible = LeaderSkillText(view.Self.LeaderId).Length > 0;
		RefreshSelectionUi(); // keep 取消 / card-lift consistent whenever _busy or the turn changes
	}

	// ---------- selection ----------

	private void ClearSelection()
	{
		_selKind = SelKind.None;
		_candidates.Clear();
		_chosenCell = null;
		_crossPreview = false;
		ClearHighlights();
		RefreshSelectionUi(); // SelectedCardId now derives to null → hides 取消 and drops the card lift
	}

	/// <summary>Reconcile the transient selection UI — the 取消 button's visibility and the lifted/enlarged
	/// "card in play" highlight — with the current selection. Called wherever a selection settles or clears.
	/// Skipped while a card is being dragged (the drag ghost already shows which card is in flight).</summary>
	private void RefreshSelectionUi()
	{
		if (_cancelBtn is null) return; // HUD not built yet
		bool active = _selKind != SelKind.None && !_busy && _dragCardId is null;
		_cancelBtn.Visible = active;
		if (active && SelectedCardId is { } id)
			HighlightSelectedCard(id);
		else
			ClearCardHighlight();
	}

	// item: 高亮当前生效卡 — pull the selected hand card half-out, enlarge it, and ring it in accent so a
	// mid-target-pick player can't misread which card's effect they are resolving.
	private void HighlightSelectedCard(int cardEntityId)
	{
		if (!_handCards.TryGetValue(cardEntityId, out var card) || !IsInstanceValid(card))
		{
			ClearCardHighlight();
			return;
		}
		if (ReferenceEquals(_liftedCard, card)) return; // already lifted — idempotent, avoids restyle thrash
		ClearCardHighlight();                            // drop any previously-lifted card first
		card.SetMeta("baseIndex", card.GetIndex());      // restore draw/hit order on de-select (overlapping hands)
		var basePos = (Vector2)card.GetMeta("basePos");
		card.Position = basePos - new Vector2(0, 96); // 抽出半截
		card.Scale = new Vector2(1.18f, 1.18f);        // 变大
		card.MoveToFront();                            // above its hand neighbours (still under the HUD layer)
		BattleTheme.SetButtonBg(card, BattleTheme.PanelDark, BattleTheme.Accent, 6, 10); // 高亮描边
		_liftedCard = card;
	}

	private void ClearCardHighlight()
	{
		if (_liftedCard is { } card && IsInstanceValid(card))
		{
			card.Position = (Vector2)card.GetMeta("basePos");
			card.Scale = Vector2.One;
			BattleTheme.SetButtonBg(card, BattleTheme.PanelDark, (Color)card.GetMeta("border"), 3, 10);
			if (card.GetParent() is { } parent) parent.MoveChild(card, (int)card.GetMeta("baseIndex")); // undo MoveToFront
		}
		_liftedCard = null;
	}

	private void OnCancelSelection()
	{
		if (_selKind == SelKind.None) return;
		_sfx.Play("button");
		ClearSelection();
		Log("已取消。");
	}

	// 友伤确认 (docs/07 X3.2): while aiming a 十字 AOE order, hovering a legal cell previews the whole
	// blast — cyan on empty/enemy cells, a red frame on any FRIENDLY unit caught in it (misplay guard).
	private void OnCellHover(int scol, int srow)
	{
		if (_busy || !_crossPreview || _selKind != SelKind.Card) return;
		var center = ScreenToBoard(scol, srow);
		if (!_candidates.Any(c => CellOf(c) is { } cc && cc == center)) return; // not a legal center
		ShowCrossFootprint(center);
	}

	/// <summary>Highlight the 十字 blast centred on <paramref name="center"/>: cyan on empty/enemy cells,
	/// a red frame on any FRIENDLY unit caught in it (友伤确认). Shared by tap-hover and drag-hover.</summary>
	private void ShowCrossFootprint(Cell center)
	{
		ClearHighlights();
		var view = _host.GetView(ViewSeat);
		foreach (var cell in BoardGeometry.AdjacentCells(center).Append(center))
		{
			var u = view.Units.FirstOrDefault(x => x.Cell == cell);
			if (u == null) { HighlightCell(cell); continue; }
			HighlightUnitColor(u.EntityId, u.OwnerSeat == ActiveSeat ? BattleTheme.DangerColor : BattleTheme.Accent);
		}
	}

	private void OnCellHoverExit()
	{
		if (_busy || !_crossPreview || _selKind != SelKind.Card) return;
		ClearHighlights();
		HighlightCardCandidates(); // restore the plain legal-cell highlight
	}

	private void ClearHighlights()
	{
		for (int scol = 0; scol < BattleTheme.Cols; scol++)
			for (int srow = 0; srow < BattleTheme.Rows; srow++)
				BattleTheme.SetButtonBg(_cellButtons[scol, srow], CellBase(srow));

		foreach (var (id, node) in _standees)
		{
			int owner = (int)node.GetMeta("owner");
			BattleTheme.SetButtonBg(node, (Color)node.GetMeta("bg"), BattleTheme.SeatColor(owner), 3);
		}
		BattleTheme.SetButtonBg(_oppLeaderBtn, BattleTheme.PanelDark, BattleTheme.SeatColor1, 3, 10);
	}

	private void HighlightCell(Cell cell) =>
		BattleTheme.SetButtonBg(CellButton(cell), BattleTheme.AccentSoft, BattleTheme.Accent, 4);

	private void HighlightUnit(int id) => HighlightUnitColor(id, BattleTheme.Accent);

	private void HighlightUnitColor(int id, Color border)
	{
		if (_standees.TryGetValue(id, out var node))
		{
			int owner = (int)node.GetMeta("owner");
			BattleTheme.SetButtonBg(node, BattleTheme.SeatColor(owner).Darkened(0.15f), border, 5);
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
		var handCard = _host.GetView(ViewSeat).Self.Hand.FirstOrDefault(h => h.EntityId == cardEntityId);
		_crossPreview = handCard != null && _cards.TryGet(handCard.CardId, out var cardDef)
			&& cardDef.Effects.Any(e => e.Target == "cell_cross_all"); // 十字 AOE → hover shows friendly-fire footprint
		ClearHighlights();

		if (_candidates.Count == 0) { Log("这张牌现在打不出。"); ClearSelection(); return; }
		// No-target card (e.g. 抽牌指令): a tap plays it immediately; a drag waits for the drop.
		if (autoSubmit && _candidates.Count == 1 && CellOf(_candidates[0]) is null && UnitOf(_candidates[0]) is null)
		{ Submit(_candidates[0]); return; }

		HighlightCardCandidates();
		RefreshSelectionUi(); // lift the card + show 取消 (skipped mid-drag; re-applied once the drag drops)
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

		// If we're mid-target-pick, treat as a target first (a unit target, or the unit's cell for an AOE).
		if (_selKind is SelKind.Card or SelKind.Leader && TryPickUnitOrItsCell(entityId)) return;

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
		RefreshSelectionUi(); // a selected unit can also be backed out of with 取消

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
		RefreshSelectionUi();
		return true;
	}

	/// <summary>A click landing on a unit while aiming: first try it as a unit target, otherwise (a
	/// row/column/十字 AOE order or leader skill) treat it as picking that unit's CELL — a click on an
	/// occupied cell must aim the AOE there, not fizzle (the standee sits on top of the cell button).</summary>
	private bool TryPickUnitOrItsCell(int unitId)
	{
		if (TryPickUnitTarget(unitId)) return true;
		var u = _host.GetView(ViewSeat).Units.FirstOrDefault(x => x.EntityId == unitId);
		if (u != null && _candidates.Any(c => CellOf(c) is { } cc && cc == u.Cell))
		{
			PickCell(u.Cell);
			return true;
		}
		return false;
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
		RefreshSelectionUi(); // after a drag-drop this is where the lift + 取消 first appear
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
		_sfx.Play("button");
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
		RefreshSelectionUi(); // leader skill is aiming — offer 取消
	}

	private void OnEndTurn()
	{
		if (_busy) return;
		_sfx.Play("button");
		Submit(new EndTurnCommand { Seat = ActiveSeat });
	}

	// ---------- card drag-to-play ----------

	public override void _Input(InputEvent @event)
	{
		if (@event is InputEventKey { Pressed: true, Keycode: Key.Escape })
		{
			GetViewport().SetInputAsHandled(); // consume Esc so it can't also reach a focused overlay button
			// Priority: an open menu closes first; else back out of an active aim; else open the menu.
			if (_gameMenu is { } m && GodotObject.IsInstanceValid(m)) { ToggleGameMenu(); return; }
			if (_selKind != SelKind.None && !_busy && _dragCardId is null) { OnCancelSelection(); return; }
			ToggleGameMenu(); // open the in-match menu (继续 / 查看牌组 / 投降 / 返回菜单)
			return;
		}
		if (_dragCardId is null)
			return;

		if (@event is InputEventMouseMotion mm)
		{
			if (!_dragMoved && mm.Position.DistanceTo(_dragStart) > 6f)
				_dragMoved = true;
			_dragTarget = mm.Position;      // _Process eases the ghost toward this (critically-damped follow)
			HighlightDragHover(mm.Position);
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
		HideCardPreview();
		_dragCardId = cardEntityId;
		_dragMoved = false;
		_dragStart = GetGlobalMousePosition();
		_dragTarget = _dragStart;
		_dragHoverCell = null;
		_dragGhost = BuildGhost(cardEntityId);
		_dragGhost.PivotOffset = GhostSize / 2f;
		_dragGhost.Position = _dragStart - GhostSize / 2f;
		_dragGhost.Scale = new Vector2(1.06f, 1.06f); // item 1: lift + slight tilt on pickup
		_dragGhost.Rotation = 0.05f;
		_overlayLayer.AddChild(_dragGhost);
		SelectCard(cardEntityId, autoSubmit: false); // highlight legal targets while dragging
	}

	// item 1: while dragging, the legal cell under the cursor lights brighter than the rest. Recomputes
	// only when the hovered cell changes (mouse-motion fires often).
	private void HighlightDragHover(Vector2 mousePos)
	{
		if (_selKind != SelKind.Card) return;
		var hit = HitTest(mousePos);
		Cell? cell = hit switch
		{
			{ Kind: HitKind.Cell } h => h.Cell,
			// Over a standee: resolve to its cell so occupied cells light up for AOE aiming.
			{ Kind: HitKind.Unit } h => _host.GetView(ViewSeat).Units.FirstOrDefault(u => u.EntityId == h.UnitId)?.Cell,
			_ => null,
		};
		if (Nullable.Equals(cell, _dragHoverCell)) return;
		_dragHoverCell = cell;

		// 十字 AOE: show the full blast footprint (friendly-fire in red) while dragging over a legal cell.
		if (_crossPreview && cell is { } center && _candidates.Any(c => CellOf(c) is { } x && x == center))
		{
			ShowCrossFootprint(center);
			return;
		}

		HighlightCardCandidates();
		if (cell is { } cc && _candidates.Any(c => CellOf(c) is { } x && x.Col == cc.Col && x.Row == cc.Row))
			BattleTheme.SetButtonBg(CellButton(cc), BattleTheme.Accent, Colors.White, 5);
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
			case HitKind.Unit: TryPickUnitOrItsCell(hit.Value.UnitId); break;
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
			ghost.AddChild(Pip(def.Cost.ToString(), BattleTheme.CostColor, new Vector2(6, 6), _gemCost));
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
		if (_online) { SubmitOnline(cmd); return; }
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

	/// <summary>Submit one command, then play its events back through the shared presentation queue.
	/// The in-process host dispatches synchronously, so every event is already queued by the time the
	/// submit returns; RunPlayback drains and animates them. Returns false if rejected or the game ended
	/// (in which case RunPlayback has already shown the win overlay).</summary>
	private async Task<bool> Apply(Command cmd)
	{
		ClearSelection();
		var result = await _host.SubmitCommandAsync(cmd.Seat, cmd);
		if (!result.Accepted) { Log($"非法操作:{result.Error?.Code}"); return false; }

		await RunPlayback();
		return _host.GetView(0).Result == null;
	}

	/// <summary>Drives the AI seat: pick → apply → repeat until it ends its turn or the game ends.</summary>
	private async Task RunAiTurn()
	{
		_busy = true;
		RefreshInteractable();
		while (_vsAi && ActiveSeat == _aiSeat && _host.GetView(0).Result == null)
		{
			await Delay(0.5);
			var cmd = _localHost!.SuggestCommand(_aiSeat);
			if (cmd is null) break;
			if (!await Apply(cmd)) return; // game ended (overlay shown), keep input locked
			if (cmd is EndTurnCommand) break;
		}
		_busy = false;
		RefreshInteractable();
	}

	// ---------- online: connect, event pump, submit, concede (M2 N2) ----------

	/// <summary>Connect, hello, create/join a room, wait for the match, then hand off to the main
	/// thread to render. Runs off the main thread after the first await, so every UI touch here is
	/// marshalled via Callable.CallDeferred.</summary>
	private async Task SetupOnline()
	{
		SetConn("连接对局…");

		// C1 lobby model (the only online path since the legacy direct-dial was removed): the lobby
		// already connected and started the match on the shared Session — attach to its RemoteGameHost.
		if (Session.Remote is not { } shared)
		{
			SetConn("联机会话不存在——请从主菜单联机入口进入");
			return;
		}

		_remoteHost = shared;
		_host = shared;
		WireRemoteEvents(shared);
		// Ranked ELO result (C3): the server pushes rating_change post-match on the shared Session socket.
		// Only ranked-queue matches send it — casual friend rooms never do, so the label just stays blank.
		_ratingHandler = rc => Callable.From(() => OnRatingChange(rc)).CallDeferred();
		Session.RatingChanged += _ratingHandler;
		try
		{
			int seat = await shared.WaitForMatchAsync(); // already applied → returns the seat immediately
			Session.EnableReconnect();
			Callable.From(() => OnMatchReady(seat)).CallDeferred();
		}
		catch (System.Exception ex)
		{
			var m = ex.Message;
			Callable.From(() => SetConn($"连接失败:{m}")).CallDeferred();
		}
	}

	/// <summary>Subscribe the battle-specific handlers to a match host (shared or self-owned). Events arrive
	/// on the WS thread, so each hop marshals to the main thread. The conn handler is stored so it can be
	/// unhooked from a persistent (shared) client on exit.</summary>
	private void WireRemoteEvents(RemoteGameHost remote)
	{
		remote.Subscribe(0, e => { _playQueue.Enqueue(e); Callable.From(KickPlayback).CallDeferred(); });
		remote.ViewUpdated += _ => Callable.From(RefreshFromSnapshot).CallDeferred();
		_connHandler = s => Callable.From(() => OnConnState(s)).CallDeferred();
		remote.ConnectionStateChanged += _connHandler;
		remote.OpponentStatusChanged += (connected, grace) => Callable.From(() => OnOpponentStatus(connected)).CallDeferred();
		remote.TurnTimerReceived += (seat, secs) => Callable.From(() => OnTurnTimer(seat, secs)).CallDeferred();
		remote.MulliganTimerReceived += secs => Callable.From(() => OnMulliganTimer(secs)).CallDeferred();
	}

	/// <summary>Main-thread handoff once the server's match_started is in. Sets the local seat and
	/// renders; only now may the event pump run (it reads _humanSeat via ViewSeat).</summary>
	private void OnMatchReady(int seat)
	{
		_humanSeat = seat;
		_aiSeat = 1 - seat;
		_deckCards[seat] = GameConfig.LocalDeckCards; // the deck this client queued with, for 查看牌组
		_onlineReady = true;
		SetConn("");
		FullRender();
		RefreshMulliganUi(); // show the 起手重抽 panel if the match opened into a mulligan phase
		if (!_playQueue.IsEmpty) KickPlayback();
	}

	/// <summary>Re-render after a pure snapshot update (reconnect resync carries no events, so the
	/// event pump won't fire). No-op during normal event flow — the pump owns that.</summary>
	private void RefreshFromSnapshot()
	{
		if (_onlineReady && !_playing && _playQueue.IsEmpty && _remoteHost?.GetView(_humanSeat).Result is null)
			FullRender();
	}

	/// <summary>Our own connection lifecycle: lock input + banner while reconnecting, clear on recovery.</summary>
	private void OnConnState(ConnectionState state)
	{
		switch (state)
		{
			case ConnectionState.Reconnecting:
				_busy = true; RefreshInteractable();
				SetConn("与服务器断线,重连中…");
				break;
			case ConnectionState.Connected when _onlineReady:
				_connFailed = false;
				SetConn("");
				_busy = false; RefreshInteractable();
				break;
			case ConnectionState.Failed:
				_connFailed = true; // unlocks LeaveAsConcede's direct exit — a concede can no longer land
				_busy = true; RefreshInteractable();
				SetConn("连接已断开,无法重连。Esc 返回菜单。");
				break;
		}
	}

	/// <summary>Opponent dropped / came back — banner only; the server runs their grace-forfeit timer.</summary>
	private void OnOpponentStatus(bool connected)
	{
		SetConn(connected ? "" : "对手掉线,等待重连…");
	}

	/// <summary>New turn clock from the server; _Process counts it down locally (the server is authoritative).</summary>
	private void OnTurnTimer(int seat, int secondsLeft)
	{
		_turnActiveSeat = seat;
		_turnSecondsLeft = secondsLeft;
	}

	public override void _Process(double delta)
	{
		if (_dragGhost != null)
		{
			// Critically-damped follow (item 1): the ghost trails the cursor instead of hard-snapping.
			float k = 1f - Mathf.Exp(-25f * (float)delta);
			_dragGhost.Position = _dragGhost.Position.Lerp(_dragTarget - GhostSize / 2f, k);
		}

		if (!_online || !_onlineReady || _turnActiveSeat < 0)
			return;
		if (_turnSecondsLeft > 0)
			_turnSecondsLeft = System.Math.Max(0, _turnSecondsLeft - delta);

		if (_timerLabel == null)
		{
			_timerLabel = BattleTheme.MakeOutlinedLabel("", 26, BattleTheme.TextMain, HorizontalAlignment.Center);
			_timerLabel.Position = new Vector2(BattleTheme.ScreenW / 2f - 120, 96);
			_timerLabel.Size = new Vector2(240, 36);
			_hudLayer.AddChild(_timerLabel);
		}

		bool over = _remoteHost?.GetView(_humanSeat).Result != null;
		if (over)
		{
			_timerLabel.Visible = false;
			return;
		}
		int secs = (int)System.Math.Ceiling(_turnSecondsLeft);
		bool mine = _turnActiveSeat == _humanSeat;
		_timerLabel.Visible = true;
		_timerLabel.Text = $"⏱ {(mine ? "你" : "对手")} {secs}s";
		_timerLabel.AddThemeColorOverride("font_color", secs <= 10 ? BattleTheme.DangerColor : BattleTheme.TextDim);
	}

	/// <summary>Online kick: wake the shared consumer from the main thread once WS events have landed in
	/// the queue. No-op while one is already running (it absorbs the new arrivals) or before match start.</summary>
	private void KickPlayback()
	{
		if (!_onlineReady || _playing) return;
		_ = RunPlaybackOnline();
	}

	/// <summary>Online wrapper around the shared consumer: lock input for the burst (an opponent turn, or
	/// our own command's result), then unlock once the queue is quiet — unless the game just ended, in
	/// which case the win overlay owns the screen and input stays locked.</summary>
	private async Task RunPlaybackOnline()
	{
		_busy = true;
		RefreshInteractable();
		await RunPlayback();
		RefreshMulliganUi(); // reflect mulligan progress (my panel → waiting → normal) after each batch
		if (_host.GetView(_humanSeat).Result == null) { _busy = false; RefreshInteractable(); }
	}

	/// <summary>The single presentation consumer (plan §10 item 9). Drains the play queue one BEAT at a
	/// time — an attack and the strikes it lands play as one beat, so a unit's death animates only after
	/// the blow that killed it — and re-renders from truth at each quiescent point. Playback is paced by
	/// animation and decoupled from arrival: events that land mid-playback are picked up by the outer
	/// loop. Idempotent — a re-entrant call returns at once, letting the running consumer own the drain.</summary>
	private async Task RunPlayback()
	{
		if (_playing) return;
		_playing = true;
		try
		{
			do
			{
				while (TryDequeueBeat(out var beat))
				{
					foreach (var e in beat) AccumulateStat(e);
					await AnimateEvents(beat);
					if (beat.OfType<GameEndedEvent>().FirstOrDefault() is { } ended)
					{ FullRender(); ShowWinOverlay(ended); return; }
				}
				FullRender();
			} while (!_playQueue.IsEmpty);
		}
		finally { _playing = false; }
	}

	/// <summary>Pull one presentation beat off the queue. Usually a single event; an AttackedEvent also
	/// takes the strikes it causes (unit/leader damage, deaths) so they play as one unit — the seam the
	/// later feel work (items 2/3/5: projectile flight, hit-stop, on-land damage) refines. Safe to peek
	/// the head to decide grouping: there is only ever one consumer, and producers append to the tail.</summary>
	private bool TryDequeueBeat(out List<GameEvent> beat)
	{
		beat = new List<GameEvent>();
		if (!_playQueue.TryDequeue(out var first))
			return false;
		beat.Add(first);
		if (first is AttackedEvent)
			while (_playQueue.TryPeek(out var next) && IsStrikeAftermath(next) && _playQueue.TryDequeue(out var e))
				beat.Add(e);
		return true;
	}

	// Events that only ever arise as the resolution of the attack just dequeued, so they fold into its
	// beat. A normal move, heal or buff is a separate action carrying its own leading event (a card play,
	// a move command, a leader skill), so it is never mis-grouped onto the preceding attack.
	private static bool IsStrikeAftermath(GameEvent e) =>
		e is UnitDamagedEvent or UnitDiedEvent or LeaderDamagedEvent;

	/// <summary>Send a command over the wire. The result batch is animated by the pump; only the
	/// rejection/error paths touch UI here, marshalled back to the main thread.</summary>
	private async void SubmitOnline(Command cmd)
	{
		if (_busy) return;
		_busy = true;
		ClearSelection();
		RefreshInteractable();

		CommandResult result;
		try
		{
			result = await _host.SubmitCommandAsync(cmd.Seat, cmd);
		}
		catch (System.Exception ex)
		{
			var m = ex.Message;
			Callable.From(() => { Log($"网络错误:{m}"); _busy = false; RefreshInteractable(); }).CallDeferred();
			return;
		}

		if (!result.Accepted)
		{
			var code = result.Error?.Code.ToString() ?? "?";
			Callable.From(() => { Log($"非法操作:{code}"); _busy = false; RefreshInteractable(); }).CallDeferred();
		}
		// accepted → the resulting EventsMsg drives the pump, which clears _busy
	}

	/// <summary>Esc mid-match = concede. The resulting GameEnded flows through the pump to the overlay.</summary>
	private void ConcedeOnline()
	{
		if (_remoteHost is null || !_onlineReady) return;
		_ = _host.SubmitCommandAsync(_humanSeat, new ConcedeCommand { Seat = _humanSeat });
	}

	/// <summary>Connection / room-status banner (online only).</summary>
	private void SetConn(string text)
	{
		if (_connLabel == null)
		{
			_connLabel = BattleTheme.MakeOutlinedLabel("", 28, BattleTheme.TextMain, HorizontalAlignment.Center);
			_connLabel.Position = new Vector2(0, 452);
			_connLabel.Size = new Vector2(BattleTheme.ScreenW, 160);
			_connLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
			_overlayLayer.AddChild(_connLabel);
		}
		_connLabel.Text = text;
		_connLabel.Visible = text.Length > 0;
	}

	public override void _ExitTree()
	{
		if (_online)
		{
			// Shared Session connection (lobby): unhook our conn handler from the persistent client and
			// arm a fresh host for the next match; DON'T close the socket — the lobby owns it.
			if (_remoteHost is { } r && _connHandler is { } h)
				r.ConnectionStateChanged -= h;
			if (_ratingHandler is { } rh)
				Session.RatingChanged -= rh; // static event → must detach or it pins this freed scene
			Session.ArmMatchHost();
		}
	}

	private async Task AnimateEvents(IReadOnlyList<GameEvent> beat)
	{
		// An attack cluster (item 9's beat) plays as a staged strike: the blow lands, THEN the damage,
		// death and line-break reactions fire (see PlayAttackBeat). Everything else is a single event.
		if (beat.Count > 0 && beat[0] is AttackedEvent atk)
		{
			await PlayAttackBeat(atk, beat);
			return;
		}
		foreach (var e in beat)
			await PlaySingle(e);
	}

	private async Task PlaySingle(GameEvent e)
	{
		switch (e)
		{
			case CardPlayedEvent cp:
				await ShowOpponentCardReveal(cp);   // item 5: show an opponent's play before it lands
				if (_cards.TryGet(cp.CardId, out var pd) && pd.Type == CardType.Order)
				{
					_sfx.Play("cast");
					await FlashOnCastEngines(cp.Seat); // 教团 on-cast: light the caster's ally_order_played engines
				}
				break;
			case CardDrawnEvent cd when cd.Seat == ViewSeat:
				_sfx.Play("draw");
				break;
			case UnitDeployedEvent:
				_sfx.Play("play");
				await Delay(0.05);
				break;
			case UnitMovedEvent m when _standees.TryGetValue(m.UnitEntityId, out var node):
				_sfx.Play("move");
				await TweenTo(node, CellScreenPos(m.To) + new Vector2(7, 7), 0.16);
				break;
			case UnitDamagedEvent d:
				await ReactDamage(d, null);          // standalone (battlecry / order / skill) — no lunge origin
				break;
			case UnitHealedEvent h when h.Amount > 0 && _standees.TryGetValue(h.UnitEntityId, out var hn):
				Flash(hn, BattleTheme.HpColor);
				FloatNumber(Center(hn), $"+{h.Amount}", BattleTheme.HpColor, h.Amount);
				await Delay(0.12);
				break;
			case UnitBuffedEvent b when _standees.TryGetValue(b.UnitEntityId, out var bn):
				Flash(bn, BattleTheme.Accent);
				await Delay(0.08);
				break;
			case UnitKeywordGrantedEvent kg when kg.Keyword == Keyword.Shield && _standees.TryGetValue(kg.UnitEntityId, out var kn):
				_sfx.Play("play");
				Flash(kn, BattleTheme.CostColor);
				SetStandeeStatuses(kn, StandeeStatuses(true)); // 持盾新增 → 立刻更新卡面指示器
				await Delay(0.1);
				break;
			case PressureTideEvent tide:
				// 压力潮汐: the bleed is explained here; the follow-up LeaderDamagedEvent animates the HP hit.
				_sfx.Play("tide");
				FloatText(new Vector2(BattleTheme.ScreenW / 2f, 430),
					$"压力潮汐!{(tide.Seat == 0 ? "玩家1" : "玩家2")}未攻入敌方半场 -{tide.Amount}", BattleTheme.DangerColor);
				await Delay(0.5);
				break;
			case LeaderDamagedEvent ld:
				await ReactLeaderDamage(ld, fromAttack: false); // standalone (tide / fatigue)
				break;
			case UnitDiedEvent dd:
				await ReactDeath(dd);
				break;
			case TurnStartedEvent ts when FixedView:
				await ShowTurnBanner(ts.Seat);       // item 8 (hotseat uses the pass overlay instead)
				break;
			default:
				break;
		}
	}

	// ---------- item 2: staged attack (melee lunge / ranged projectile) ----------

	/// <summary>Play one attack beat: melee windup→charge→hit→return, or a ranged projectile that must
	/// LAND before its damage resolves. The aftermath events (damage / death / leader hit / trample move)
	/// fire on the contact frame, so a unit dies only after the blow that killed it (plan §10 item 9).</summary>
	private async Task PlayAttackBeat(AttackedEvent atk, IReadOnlyList<GameEvent> beat)
	{
		var attacker = _standees.GetValueOrDefault(atk.AttackerEntityId);
		Vector2 targetPos = AttackTargetCenter(atk);
		Vector2 origin = attacker != null ? Center(attacker) : targetPos;
		// A unit hit ≥ ~2 cells away is a shot; a leader plate sits in the corner (distance unreliable), so
		// fall back to the attacker's 射程 keyword there.
		bool ranged = atk.TargetUnitId is int
			? attacker != null && origin.DistanceTo(targetPos) > 210f
			: AttackerHasRange(atk.AttackerEntityId);
		Vector2 home = attacker?.Position ?? Vector2.Zero;

		if (ranged)
		{
			_sfx.Play("shoot");
			await FireProjectile(origin, targetPos);
		}
		else if (attacker != null)
		{
			await MeleeWindup(attacker, targetPos); // pull back, then charge 40% of the way in
		}

		// contact frame
		_sfx.Play("attack");
		ScreenShake(ranged ? 2f : 3f);

		bool attackerDied = false;
		int hits = 0;
		foreach (var e in beat.Skip(1))
			switch (e)
			{
				case UnitDamagedEvent d:
					if (hits++ > 0) await Delay(0.08);  // multi-hit stagger (item 4)
					await ReactDamage(d, origin);
					break;
				case LeaderDamagedEvent ld:
					await ReactLeaderDamage(ld, fromAttack: true);
					break;
				case UnitDiedEvent dd:
					if (dd.UnitEntityId == atk.AttackerEntityId) attackerDied = true;
					await ReactDeath(dd);
					break;
				case UnitMovedEvent tm when _standees.TryGetValue(tm.UnitEntityId, out var mn):
					await TweenTo(mn, CellScreenPos(tm.To) + new Vector2(7, 7), 0.14); // 践踏 advance after a kill
					break;
			}

		if (!ranged && attacker != null && !attackerDied)
			await SnapBack(attacker, home);
	}

	private async Task MeleeWindup(Control node, Vector2 targetCenter)
	{
		var home = node.Position;
		Vector2 dir = targetCenter - Center(node);
		Vector2 back = dir.LengthSquared() > 1f ? home - dir.Normalized() * 10f : home; // ~0.1s pull-back
		Vector2 lunge = home + dir * 0.4f;                                              // 40% charge in
		var t = CreateTween();
		t.TweenProperty(node, "position", back, 0.10).SetTrans(Tween.TransitionType.Sine);
		t.TweenProperty(node, "position", lunge, 0.12).SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.In);
		await ToSignal(t, Tween.SignalName.Finished);
	}

	private async Task SnapBack(Control node, Vector2 home)
	{
		var t = CreateTween();
		t.TweenProperty(node, "position", home, 0.12).SetTrans(Tween.TransitionType.Sine);
		await ToSignal(t, Tween.SignalName.Finished);
	}

	// Ranged shot: a glowing bolt flies from attacker to target; damage only resolves once it lands.
	private async Task FireProjectile(Vector2 from, Vector2 to)
	{
		var size = new Vector2(52, 26);
		var proj = new Control { Size = size, PivotOffset = size / 2f, MouseFilter = MouseFilterEnum.Ignore };
		proj.Position = from - size / 2f;
		proj.Rotation = (to - from).Angle(); // the bolt art points right; align it to the flight direction
		if (BattleTheme.Tex("fx/projectile_bolt.png") is { } bolt)
		{
			proj.AddChild(BattleTheme.Art(bolt, Vector2.Zero, size, TextureRect.StretchModeEnum.KeepAspectCentered));
		}
		else // placeholder fallback (halo + core)
		{
			proj.AddChild(new ColorRect { Color = new Color(BattleTheme.Accent.R, BattleTheme.Accent.G, BattleTheme.Accent.B, 0.35f), Size = size, MouseFilter = MouseFilterEnum.Ignore });
			proj.AddChild(new ColorRect { Color = BattleTheme.Accent.Lightened(0.4f), Position = new Vector2(size.X * 0.35f, size.Y * 0.25f), Size = size * 0.35f, MouseFilter = MouseFilterEnum.Ignore });
		}
		_overlayLayer.AddChild(proj);
		var t = CreateTween();
		t.TweenProperty(proj, "position", to - size / 2f, 0.25).SetTrans(Tween.TransitionType.Sine);
		await ToSignal(t, Tween.SignalName.Finished);
		proj.QueueFree();
	}

	// item 3 art: a warm impact spark at the hit point (additive-ish glow).
	private void HitSpark(Vector2 center)
	{
		if (BattleTheme.Tex("fx/hit_spark.png") is not { } tex) return;
		var size = new Vector2(96, 96);
		var spark = BattleTheme.Art(tex, center - size / 2f, size, TextureRect.StretchModeEnum.KeepAspectCentered);
		spark.PivotOffset = size / 2f;
		spark.Scale = new Vector2(0.5f, 0.5f);
		spark.Rotation = (GetInstanceId() % 8) * 0.4f; // vary orientation per spawn so repeats don't look stamped
		_overlayLayer.AddChild(spark);
		var t = CreateTween();
		t.TweenProperty(spark, "scale", new Vector2(1.25f, 1.25f), 0.18).SetTrans(Tween.TransitionType.Cubic);
		t.Parallel().TweenProperty(spark, "modulate:a", 0.0f, 0.18);
		t.TweenCallback(Callable.From(spark.QueueFree));
	}

	// item 3 art: 持盾吸收 marker — the shield sigil pops and drifts up, distinct from a real HP loss.
	private void ShieldPop(Vector2 center)
	{
		if (BattleTheme.Tex("fx/shield_glyph.png") is not { } tex)
		{
			FloatText(center + new Vector2(0, -8), "盾", BattleTheme.CostColor); // placeholder fallback
			return;
		}
		var size = new Vector2(68, 68);
		var glyph = BattleTheme.Art(tex, center - size / 2f, size, TextureRect.StretchModeEnum.KeepAspectCentered);
		glyph.PivotOffset = size / 2f;
		glyph.Scale = new Vector2(0.4f, 0.4f);
		var start = glyph.Position;
		_overlayLayer.AddChild(glyph);
		var t = CreateTween();
		t.TweenProperty(glyph, "scale", new Vector2(1.1f, 1.1f), 0.12).SetTrans(Tween.TransitionType.Back);
		t.TweenProperty(glyph, "position", start + new Vector2(0, -34), 0.4).SetTrans(Tween.TransitionType.Sine);
		t.Parallel().TweenProperty(glyph, "modulate:a", 0.0f, 0.4);
		t.TweenCallback(Callable.From(glyph.QueueFree));
	}

	// ---------- item 3/4/6: hit / death / face-damage reactions (shared by attacks and standalone events) ----------

	// item 3: white flash + knockback away from the blow + hit sfx + damage number. Shield absorption
	// reads blue with a 「盾」 float, clearly distinct from a real HP loss.
	private async Task ReactDamage(UnitDamagedEvent d, Vector2? from)
	{
		if (!_standees.TryGetValue(d.UnitEntityId, out var node)) return;
		if (d.ShieldAbsorbed)
		{
			_sfx.Play("attack");
			Flash(node, BattleTheme.CostColor);
			ShieldPop(Center(node)); // 蓝闪 + 盾纹章,与真实掉血区分
			SetStandeeStatuses(node, StandeeStatuses(false)); // 持盾被消耗 → 立刻清掉卡面指示器
			if (d.GuardRedirect) FloatBonusTag(Center(node) + new Vector2(0, 20), "守护-0"); // 守护单位被盾挡下
			await Delay(0.12);
			return;
		}
		// 守护 转移: the spared original target shows 守护-0 (a soft blue blink, no hit); the guardian that soaks
		// it shows 守护-<实际伤害> with full hit feedback. Mirrors the 架设+1 attribution tag the user asked for.
		if (d.GuardRedirect)
		{
			if (d.Amount > 0)
			{
				_sfx.Play("attack");
				Flash(node, Colors.White);
				HitSpark(Center(node));
				Vector2 gdir = from is { } gf && (Center(node) - gf).LengthSquared() > 1f ? (Center(node) - gf).Normalized() : new Vector2(0, 1);
				await Knockback(node, gdir * 7f);
			}
			else
			{
				Flash(node, BattleTheme.CostColor);
			}
			FloatBonusTag(Center(node) + new Vector2(0, d.Amount > 0 ? 0 : 20), $"守护-{d.Amount}");
			await Delay(0.1);
			return;
		}
		Flash(node, Colors.White);
		HitSpark(Center(node));
		Vector2 dir = from is { } f && (Center(node) - f).LengthSquared() > 1f ? (Center(node) - f).Normalized() : new Vector2(0, 1);
		await Knockback(node, dir * 7f);
		if (d.Amount > 0)
		{
			FloatNumber(Center(node), $"-{d.Amount}", BattleTheme.DangerColor, d.Amount);
			// 架设 second clause: EFFECT damage (order/skill/battlecry) deals +1 to bolted-down units — never
			// attacks. `from is null` is exactly the standalone (non-attack) path, so it distinguishes the two.
			// Surface WHY the number is 1 higher than the card's printed value.
			if (from is null && _emplacementUnits.Contains(d.UnitEntityId))
				FloatBonusTag(Center(node) + new Vector2(0, 20), "架设 +1");
		}
	}

	private async Task Knockback(Control node, Vector2 offset)
	{
		var home = node.Position;
		var t = CreateTween();
		t.TweenProperty(node, "position", home + offset, 0.05);
		t.TweenProperty(node, "position", home, 0.07).SetTrans(Tween.TransitionType.Sine);
		await ToSignal(t, Tween.SignalName.Finished);
	}

	// item 6: face damage. Breaking the ENEMY line (hitting their leader) is the reward beat — heavy shake +
	// full-screen red edge pulse; damage to your own leader is a lighter warning.
	private async Task ReactLeaderDamage(LeaderDamagedEvent ld, bool fromAttack)
	{
		_sfx.Play("leaderhit");
		var plate = LeaderPlate(ld.Seat);
		bool onOpponent = ld.Seat != ViewSeat;
		Flash(plate, BattleTheme.DangerColor);
		FloatNumber(Center(plate) + new Vector2(0, 24), $"-{ld.Amount}", BattleTheme.DangerColor, ld.Amount + 2);
		LeaderShake(plate, onOpponent ? 10f : 7f);
		if (fromAttack) EdgeFlash(onOpponent ? 0.85f : 0.55f); // 破线 red vignette pulse
		else ScreenShake(3f);                                  // standalone (tide / fatigue) shakes on its own
		await Delay(0.2);
	}

	// item 6: death — crumble (squash + spin + fade); the standee is then cleared by the next FullRender.
	private async Task ReactDeath(UnitDiedEvent dd)
	{
		if (!_standees.TryGetValue(dd.UnitEntityId, out var node)) return;
		_sfx.Play("death");
		node.PivotOffset = node.Size / 2f;
		var t = CreateTween();
		t.SetParallel(true);
		t.TweenProperty(node, "scale", new Vector2(1.15f, 0.55f), 0.22).SetTrans(Tween.TransitionType.Back);
		t.TweenProperty(node, "rotation", 0.5f, 0.22);
		t.TweenProperty(node, "modulate:a", 0.0f, 0.22);
		await ToSignal(t, Tween.SignalName.Finished);
	}

	// ---------- item 6/8: screen-space effects ----------

	private static Vector2 Center(Control c) => c.Position + c.Size / 2f;

	private Control LeaderPlate(int seat) => seat == ViewSeat ? (Control)_selfAvatar! : _oppLeaderBtn;

	private Vector2 AttackTargetCenter(AttackedEvent atk)
	{
		if (atk.TargetUnitId is int tid && _standees.TryGetValue(tid, out var tn))
			return Center(tn);
		if (atk.TargetLeaderSeat is int seat)
			return Center(LeaderPlate(seat));
		return new Vector2(BattleTheme.ScreenW / 2f, BattleTheme.ScreenH / 2f);
	}

	private bool AttackerHasRange(int entityId) =>
		_host.GetView(ViewSeat).Units.FirstOrDefault(u => u.EntityId == entityId)?.Keywords
			.Any(k => k.Keyword == Keyword.Range) ?? false;

	// A brief camera-style shake of the whole scene. Kills any prior shake so overlapping hits don't fight.
	private void ScreenShake(float px)
	{
		_shakeTween?.Kill();
		Position = Vector2.Zero;
		_shakeTween = CreateTween();
		for (int i = 0; i < 4; i++)
		{
			float f = 1f - i / 4f;
			_shakeTween.TweenProperty(this, "position", new Vector2((i % 2 == 0 ? px : -px) * f, (i % 2 == 0 ? -px : px) * f), 0.025);
		}
		_shakeTween.TweenProperty(this, "position", Vector2.Zero, 0.025);
	}

	private void LeaderShake(Control plate, float px)
	{
		var home = plate.Position;
		var t = CreateTween();
		for (int i = 0; i < 5; i++)
			t.TweenProperty(plate, "position", home + new Vector2(i % 2 == 0 ? px : -px, 0), 0.03);
		t.TweenProperty(plate, "position", home, 0.03);
	}

	// Full-screen red edge pulse — a vignette faked with a thick-bordered transparent frame (placeholder).
	private void EdgeFlash(float intensity)
	{
		var frame = new Panel { MouseFilter = MouseFilterEnum.Ignore, Modulate = new Color(1, 1, 1, 0) };
		frame.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		var style = new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0), BorderColor = new Color(0.82f, 0.24f, 0.18f, intensity) };
		style.BorderWidthLeft = style.BorderWidthRight = 120;
		style.BorderWidthTop = style.BorderWidthBottom = 90;
		frame.AddThemeStyleboxOverride("panel", style);
		_overlayLayer.AddChild(frame);
		var t = CreateTween();
		t.TweenProperty(frame, "modulate:a", 1.0f, 0.10);
		t.TweenProperty(frame, "modulate:a", 0.0f, 0.35);
		t.TweenCallback(Callable.From(frame.QueueFree));
	}

	// ---------- item 5: opponent card reveal ----------

	/// <summary>When the OPPONENT plays a card, show its face centre-screen (~1.2s, or click to skip) before
	/// it lands — otherwise a networked opponent's play, an order especially, is invisible to you.</summary>
	private async Task ShowOpponentCardReveal(CardPlayedEvent cp)
	{
		if (cp.Seat == ViewSeat || !_cards.TryGet(cp.CardId, out var def))
			return;

		var cardSize = new Vector2(360, 515);
		var root = new Control { MouseFilter = MouseFilterEnum.Stop };
		root.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		var dim = new ColorRect { Color = new Color(0, 0, 0, 0.42f), MouseFilter = MouseFilterEnum.Ignore };
		dim.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		root.AddChild(dim);

		var holder = new Control
		{
			Position = new Vector2((BattleTheme.ScreenW - cardSize.X) / 2f, (BattleTheme.ScreenH - cardSize.Y) / 2f - 30),
			Size = cardSize,
			PivotOffset = cardSize / 2f,
			Scale = new Vector2(0.82f, 0.82f),
			MouseFilter = MouseFilterEnum.Ignore,
		};
		holder.AddChild(BuildCardVisual(def, cardSize, compact: false, backing: true));
		root.AddChild(holder);

		var label = BattleTheme.MakeOutlinedLabel($"对手打出  {def.Name}", 30, BattleTheme.TextMain, HorizontalAlignment.Center);
		label.Position = new Vector2(0, holder.Position.Y - 70);
		label.Size = new Vector2(BattleTheme.ScreenW, 44);
		root.AddChild(label);

		_overlayLayer.AddChild(root);

		var skip = new System.Threading.Tasks.TaskCompletionSource();
		root.GuiInput += e => { if (e is InputEventMouseButton { Pressed: true }) skip.TrySetResult(); };

		var pop = CreateTween();
		pop.TweenProperty(holder, "scale", Vector2.One, 0.14).SetTrans(Tween.TransitionType.Back);

		await System.Threading.Tasks.Task.WhenAny(Delay(1.2), skip.Task);

		var outT = CreateTween();
		outT.TweenProperty(root, "modulate:a", 0.0f, 0.14);
		await ToSignal(outT, Tween.SignalName.Finished);
		root.QueueFree();
	}

	// ---------- item 8: turn-switch banner ----------

	// A turn-change banner sweeps in and fades. Fixed-view only — hotseat has the pass overlay already.
	private async Task ShowTurnBanner(int seat)
	{
		_sfx.Play("turnstart");
		bool mine = seat == ViewSeat;
		var banner = new Panel { MouseFilter = MouseFilterEnum.Ignore, Size = new Vector2(BattleTheme.ScreenW, 120), Modulate = new Color(1, 1, 1, 0) };
		banner.Position = new Vector2(0, BattleTheme.ScreenH / 2f - 60);
		var style = new StyleBoxFlat { BgColor = new Color(0.04f, 0.04f, 0.05f, 0.72f) };
		style.BorderColor = mine ? BattleTheme.Accent : BattleTheme.SeatColor1;
		style.BorderWidthTop = style.BorderWidthBottom = 3;
		banner.AddThemeStyleboxOverride("panel", style);
		var label = BattleTheme.MakeOutlinedLabel(mine ? "你的回合" : "对手回合", 52,
			mine ? BattleTheme.Accent : BattleTheme.TextMain, HorizontalAlignment.Center);
		label.Size = new Vector2(BattleTheme.ScreenW, 120);
		banner.AddChild(label);
		_overlayLayer.AddChild(banner);

		var t = CreateTween();
		t.TweenProperty(banner, "modulate:a", 1.0f, 0.14);
		t.TweenInterval(0.42);
		t.TweenProperty(banner, "modulate:a", 0.0f, 0.18);
		await ToSignal(t, Tween.SignalName.Finished);
		banner.QueueFree();
	}

	// 教团 on-cast flash: after an order is cast, pulse each of the caster's ally_order_played engines ember-orange.
	private async Task FlashOnCastEngines(int seat)
	{
		var ember = Color.FromHtml("ff7a3c");
		bool any = false;
		foreach (var uv in _host.GetView(ViewSeat).Units.Where(u => u.OwnerSeat == seat))
			if (_standees.TryGetValue(uv.EntityId, out var node)
				&& _cards.TryGet(uv.CardId, out var ud) && ud.Effects.Any(x => x.Trigger == "ally_order_played"))
			{ Flash(node, ember); any = true; }
		if (any) await Delay(0.14);
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

	// ---------- in-match menu (投降 / 暂停[后续] / 继续 / 查看牌组) ----------

	private void ToggleGameMenu()
	{
		_sfx.Play("button");
		if (_gameMenu is { } m && GodotObject.IsInstanceValid(m)) { CloseGameMenu(); return; }
		ShowGameMenu();
	}

	private void CloseGameMenu()
	{
		if (_gameMenu is { } m && GodotObject.IsInstanceValid(m)) m.QueueFree();
		_gameMenu = null;
	}

	/// <summary>A dim backdrop (click = close) plus a centered panel. Shared by the menu and its confirm/deck views.
	/// Tracked in <see cref="_gameMenu"/> so Esc / the menu button close whichever is showing.</summary>
	private Panel NewMenuOverlay(Vector2 panelSize)
	{
		CloseGameMenu();
		var dim = new ColorRect { Color = new Color(0.02f, 0.02f, 0.02f, 0.72f) };
		dim.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		_overlayLayer.AddChild(dim);
		_gameMenu = dim;

		var back = new Button { Flat = true };
		back.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		back.Pressed += CloseGameMenu;
		dim.AddChild(back);

		var panel = new Panel
		{
			Position = new Vector2((BattleTheme.ScreenW - panelSize.X) / 2f, (BattleTheme.ScreenH - panelSize.Y) / 2f),
			Size = panelSize,
			MouseFilter = MouseFilterEnum.Stop, // clicks on the panel must not close via the backdrop
		};
		panel.AddThemeStyleboxOverride("panel", BattleTheme.Box(BattleTheme.PanelDark, BattleTheme.Accent, 2, 16));
		dim.AddChild(panel);
		return panel;
	}

	private void ShowGameMenu()
	{
		bool over = _host.GetView(ViewSeat).Result != null;
		var panel = NewMenuOverlay(new Vector2(480, 512));

		var title = BattleTheme.MakeOutlinedLabel("菜 单", 44, BattleTheme.TextMain, HorizontalAlignment.Center);
		title.Position = new Vector2(0, 28); title.Size = new Vector2(480, 60);
		panel.AddChild(title);

		float by = 116f;
		Button Row(string text, Color color, System.Action onPressed)
		{
			var b = BattleTheme.MakeButton(new Vector2(60, by), new Vector2(360, 72), color, BattleTheme.Accent, 2, 12);
			b.Text = text;
			b.AddThemeFontSizeOverride("font_size", 28);
			b.Pressed += () => { _sfx.Play("button"); onPressed(); };
			panel.AddChild(b);
			by += 88f;
			return b;
		}

		Row("继续", BattleTheme.AccentSoft, CloseGameMenu);
		Row("查看牌组", BattleTheme.PanelDark, ShowDeckList);
		var surrender = Row("投降", BattleTheme.PanelDark, ConfirmSurrender);
		surrender.Disabled = over; // nothing to surrender once the match has ended
		Row("返回菜单", BattleTheme.PanelDark, () =>
		{
			if (_online && !over) { ConfirmLeaveOnline(); return; }
			CloseGameMenu();
			GetTree().ChangeSceneToFile("res://scenes/menu/Menu.tscn");
		});
	}

	/// <summary>返回菜单 mid-online-match: "离开 = 投降 = 看结算". The red 离开 concedes and STAYS in the
	/// scene — GameEnded flows through the pump to the win overlay, and the player leaves from there like
	/// any other finished match. No bypass path exists: the match always settles before the menu is
	/// reachable, so no server-side clock forfeit can chase the player into their next game on the shared
	/// lobby socket (the "sudden loss next match" bug).</summary>
	private void ConfirmLeaveOnline()
	{
		var panel = NewMenuOverlay(new Vector2(480, 360));

		var q = BattleTheme.MakeOutlinedLabel("还在对局中", 40, BattleTheme.DangerColor, HorizontalAlignment.Center);
		q.Position = new Vector2(0, 60); q.Size = new Vector2(480, 60);
		panel.AddChild(q);
		var sub = BattleTheme.MakeLabel("如果返回将会视为投降输掉对局,是否继续?", 22, BattleTheme.TextDim, HorizontalAlignment.Center);
		sub.Position = new Vector2(0, 132); sub.Size = new Vector2(480, 36);
		panel.AddChild(sub);

		var yes = BattleTheme.MakeButton(new Vector2(56, 220), new Vector2(180, 88), BattleTheme.DangerColor, BattleTheme.Accent, 2, 12);
		yes.Text = "离开"; yes.AddThemeFontSizeOverride("font_size", 28);
		yes.Pressed += () => { _sfx.Play("button"); CloseGameMenu(); LeaveAsConcede(); };
		panel.AddChild(yes);
		var no = BattleTheme.MakeButton(new Vector2(244, 220), new Vector2(180, 88), BattleTheme.PanelDark, BattleTheme.Accent, 2, 12);
		no.Text = "取消"; no.AddThemeFontSizeOverride("font_size", 28);
		no.Pressed += () => { _sfx.Play("button"); ShowGameMenu(); };
		panel.AddChild(no);
	}

	/// <summary>The 离开 action: concede, then wait in place for the settlement overlay (sub-second on a
	/// live connection — same path as 投降). Dead-socket escape hatch: once reconnect has permanently
	/// failed the concede can never reach the server NOR can GameEnded come back, so leave directly and
	/// let the server's new-match guard settle the forfeit — otherwise the player is locked in the scene.</summary>
	private void LeaveAsConcede()
	{
		if (_connFailed) { GetTree().ChangeSceneToFile("res://scenes/menu/Menu.tscn"); return; }
		ConcedeMatch();
	}

	private void ConfirmSurrender()
	{
		var panel = NewMenuOverlay(new Vector2(480, 360));

		var q = BattleTheme.MakeOutlinedLabel("确认投降?", 40, BattleTheme.DangerColor, HorizontalAlignment.Center);
		q.Position = new Vector2(0, 60); q.Size = new Vector2(480, 60);
		panel.AddChild(q);
		var sub = BattleTheme.MakeLabel(FixedView ? "本局将判负" : $"玩家{OfflineConcedeSeat() + 1} 判负", 22, BattleTheme.TextDim, HorizontalAlignment.Center);
		sub.Position = new Vector2(0, 132); sub.Size = new Vector2(480, 36);
		panel.AddChild(sub);

		var yes = BattleTheme.MakeButton(new Vector2(56, 220), new Vector2(180, 88), BattleTheme.DangerColor, BattleTheme.Accent, 2, 12);
		yes.Text = "投降"; yes.AddThemeFontSizeOverride("font_size", 28);
		yes.Pressed += () => { _sfx.Play("button"); CloseGameMenu(); ConcedeMatch(); };
		panel.AddChild(yes);
		var no = BattleTheme.MakeButton(new Vector2(244, 220), new Vector2(180, 88), BattleTheme.PanelDark, BattleTheme.Accent, 2, 12);
		no.Text = "取消"; no.AddThemeFontSizeOverride("font_size", 28);
		no.Pressed += () => { _sfx.Play("button"); ShowGameMenu(); };
		panel.AddChild(no);
	}

	/// <summary>Submit a concede for the surrendering seat (online → the human; hotseat → the player at
	/// the device). The resulting GameEnded flows through the pump to the win overlay.</summary>
	private void ConcedeMatch()
	{
		if (_online) { ConcedeOnline(); return; }
		Submit(new ConcedeCommand { Seat = OfflineConcedeSeat() }); // offline: routes through Apply → RunPlayback → win overlay
	}

	/// <summary>The seat that surrenders offline. Hotseat during 起手重抽 needs care: ActiveSeat stays
	/// FirstSeat all phase, but the player at the device is the one whose mulligan panel is up (or, on the
	/// pass overlay, the seat still owing a mulligan).</summary>
	private int OfflineConcedeSeat()
	{
		if (FixedView)
			return _humanSeat;
		if (InMulliganPhase())
			return _mulliganShownSeat >= 0 && MullPending(_mulliganShownSeat) ? _mulliganShownSeat
				: MullPending(ActiveSeat) ? ActiveSeat : 1 - ActiveSeat;
		return ActiveSeat;
	}

	/// <summary>查看牌组: the full deck the local viewer brought this match, grouped by card with copy counts.
	/// Click a card for its detail popup.</summary>
	private void ShowDeckList()
	{
		var panel = NewMenuOverlay(new Vector2(1200, 900));
		var title = BattleTheme.MakeOutlinedLabel("我 的 牌 组", 40, BattleTheme.TextMain, HorizontalAlignment.Center);
		title.Position = new Vector2(0, 20); title.Size = new Vector2(1200, 54);
		panel.AddChild(title);

		var cards = _deckCards[ViewSeat];
		if (cards is null || cards.Count == 0)
		{
			var none = BattleTheme.MakeLabel("牌组信息本局不可用", 28, BattleTheme.TextDim, HorizontalAlignment.Center);
			none.Position = new Vector2(0, 420); none.Size = new Vector2(1200, 44);
			panel.AddChild(none);
		}
		else
		{
			var scroll = new ScrollContainer { Position = new Vector2(24, 92), Size = new Vector2(1152, 720) };
			scroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
			panel.AddChild(scroll);
			var grid = new GridContainer { Columns = 6, SizeFlagsHorizontal = SizeFlags.ExpandFill };
			grid.AddThemeConstantOverride("h_separation", 14);
			grid.AddThemeConstantOverride("v_separation", 14);
			scroll.AddChild(grid);

			var faceSize = new Vector2(180, 252);
			foreach (var g in cards.GroupBy(id => id)
						 .Select(g => (Def: _cards.Get(g.Key), Count: g.Count()))
						 .OrderBy(x => x.Def.Cost).ThenBy(x => x.Def.Name, System.StringComparer.Ordinal))
			{
				var def = g.Def;
				var holder = new Button { CustomMinimumSize = faceSize, Size = faceSize, Flat = true };
				holder.AddThemeStyleboxOverride("normal", BattleTheme.Box(new Color(0, 0, 0, 0)));
				holder.AddChild(CardView.BuildFace(def, faceSize));
				if (g.Count > 1)
				{
					var badge = new Panel { Position = new Vector2(faceSize.X - 46, 4), Size = new Vector2(42, 34), MouseFilter = MouseFilterEnum.Ignore };
					badge.AddThemeStyleboxOverride("panel", BattleTheme.Box(new Color(0.05f, 0.05f, 0.05f, 0.85f), BattleTheme.Accent, 1, 8));
					var bl = BattleTheme.MakeOutlinedLabel($"×{g.Count}", 20, BattleTheme.TextMain, HorizontalAlignment.Center);
					bl.Size = new Vector2(42, 34);
					badge.AddChild(bl);
					holder.AddChild(badge);
				}
				holder.Pressed += () => { _sfx.Play("button"); CardView.ShowDetailPopup(this, def); };
				grid.AddChild(holder);
			}
		}

		var close = BattleTheme.MakeButton(new Vector2(500, 826), new Vector2(200, 60), BattleTheme.PanelDark, BattleTheme.Accent, 2, 12);
		close.Text = "返回"; close.AddThemeFontSizeOverride("font_size", 26);
		close.Pressed += () => { _sfx.Play("button"); ShowGameMenu(); };
		panel.AddChild(close);
	}

	// ---------- 起手重抽 (mulligan, docs/11 §6) ----------

	private bool InMulliganPhase()
	{
		var v = _host.GetView(0);
		return v.MulliganPending || v.OpponentMulliganPending;
	}

	/// <summary>Whether the given seat still owes a mulligan (read off seat 0's view).</summary>
	private bool MullPending(int seat) =>
		seat == 0 ? _host.GetView(0).MulliganPending : _host.GetView(0).OpponentMulliganPending;

	private void CloseMulligan()
	{
		if (_mulliganPanel is { } p && GodotObject.IsInstanceValid(p)) p.QueueFree();
		_mulliganPanel = null;
		_mulliganTimerLabel = null; // owned by the panel; freed with it
	}

	/// <summary>Server re-announced the mulligan clock (match_started / resync after a reconnect):
	/// adopt its count so the local 1s tick can't drift.</summary>
	private void OnMulliganTimer(int secs)
	{
		_mulliganTimerSecs = secs;
		if (_mulliganTimerLabel is { } l && GodotObject.IsInstanceValid(l)) l.Text = $"限时 {secs} 秒";
	}

	// --- offline (hotseat / vs-AI) ---

	private void BeginMulliganOffline()
	{
		_handLayer.Visible = false;
		_mulliganShownSeat = -1;
		AdvanceMulliganOffline();
	}

	private void AdvanceMulliganOffline()
	{
		if (!InMulliganPhase()) { EndMulliganOffline(); return; }

		// vs-AI: the host's MulliganAi heuristic picks the swaps (docs/11 §7); keep-all only as fallback.
		if (_vsAi && MullPending(_aiSeat))
		{
			var pick = _localHost?.SuggestCommand(_aiSeat) as MulliganCommand;
			_ = SubmitMulliganOffline(_aiSeat, (IReadOnlyList<int>?)pick?.ReplacedEntityIds ?? System.Array.Empty<int>());
			return;
		}

		// The human seat to prompt. Hotseat mulligans in seat order (FirstSeat == ActiveSeat during the phase).
		int seat = _vsAi ? _humanSeat : (MullPending(ActiveSeat) ? ActiveSeat : 1 - ActiveSeat);
		if (!MullPending(seat)) { EndMulliganOffline(); return; }

		if (!_vsAi && _mulliganShownSeat >= 0 && _mulliganShownSeat != seat)
		{
			ShowMulliganPassOverlay(seat); // hotseat: hand the device to the other player first
			return;
		}
		_mulliganShownSeat = seat;
		ShowMulliganPanel(seat, null, ids => { _ = SubmitMulliganOffline(seat, ids); });
	}

	private async Task SubmitMulliganOffline(int seat, IReadOnlyList<int> replaced)
	{
		CloseMulligan();
		await Apply(new MulliganCommand { Seat = seat, ReplacedEntityIds = replaced.ToList() });
		AdvanceMulliganOffline();
	}

	private void EndMulliganOffline()
	{
		CloseMulligan();
		_handLayer.Visible = true;
		FullRender();
		if (_vsAi)
		{
			if (ActiveSeat == _aiSeat && _host.GetView(0).Result == null) _ = RunAiTurn();
		}
		else
		{
			ShowPassOverlay(ActiveSeat); // hotseat: pass the device to FirstSeat for turn 1
		}
	}

	private void ShowMulliganPassOverlay(int seat)
	{
		CloseMulligan();
		_handLayer.Visible = false;
		var panel = BattleTheme.MakeButton(Vector2.Zero, new Vector2(BattleTheme.ScreenW, BattleTheme.ScreenH), new Color(0.03f, 0.03f, 0.03f, 0.95f), radius: 0);
		var msg = BattleTheme.MakeLabel($"轮到 {(seat == 0 ? "玩家1" : "玩家2")} 换牌\n\n点击继续", 44, BattleTheme.TextMain, HorizontalAlignment.Center);
		msg.Position = new Vector2(0, 420); msg.Size = new Vector2(BattleTheme.ScreenW, 240);
		panel.AddChild(msg);
		panel.Pressed += () =>
		{
			_mulliganShownSeat = seat;
			ShowMulliganPanel(seat, null, ids => { _ = SubmitMulliganOffline(seat, ids); });
		};
		_overlayLayer.AddChild(panel);
		_mulliganPanel = panel; // ShowMulliganPanel's CloseMulligan retires it
	}

	// --- online ---

	/// <summary>Reflect the current mulligan state online: show my selection panel while I owe a mulligan,
	/// a waiting notice once I've submitted, and tear the overlay down when the phase closes.</summary>
	private void RefreshMulliganUi()
	{
		if (!_online) return;
		var view = _host.GetView(_humanSeat);

		if (view.Result != null || (!view.MulliganPending && !view.OpponentMulliganPending))
		{
			if (_mulliganMode != 0) { _mulliganMode = 0; CloseMulligan(); _handLayer.Visible = true; FullRender(); }
			return;
		}

		if (view.MulliganPending)
		{
			if (_mulliganMode == 1) return; // already selecting — don't clobber the player's picks
			_mulliganMode = 1;
			ShowMulliganPanel(_humanSeat, _remoteHost?.MulliganSecondsLeft, ids =>
			{
				_mulliganMode = 2;
				ShowMulliganWaiting();
				SubmitOnline(new MulliganCommand { Seat = _humanSeat, ReplacedEntityIds = ids });
			});
		}
		else if (_mulliganMode != 2)
		{
			_mulliganMode = 2;
			ShowMulliganWaiting();
		}
	}

	private void ShowMulliganWaiting()
	{
		CloseMulligan();
		_handLayer.Visible = false;
		var dim = new ColorRect { Color = new Color(0.02f, 0.02f, 0.02f, 0.9f) };
		dim.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		_overlayLayer.AddChild(dim);
		_mulliganPanel = dim;
		var msg = BattleTheme.MakeOutlinedLabel("已确认 · 等待对手换牌…", 40, BattleTheme.Accent, HorizontalAlignment.Center);
		msg.Position = new Vector2(0, 470); msg.Size = new Vector2(BattleTheme.ScreenW, 60);
		dim.AddChild(msg);
	}

	// --- shared panel ---

	/// <summary>The 起手重抽 selection panel: the seat's opening hand as toggleable cards (tap = mark for
	/// replacement) plus a confirm. <paramref name="onConfirm"/> receives the chosen entity ids.</summary>
	private void ShowMulliganPanel(int seat, int? secondsLeft, System.Action<List<int>> onConfirm)
	{
		CloseMulligan();
		_handLayer.Visible = false;

		var hand = _host.GetView(seat).Self.Hand;
		var selected = new HashSet<int>();

		var dim = new ColorRect { Color = new Color(0.02f, 0.02f, 0.02f, 0.92f) };
		dim.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		_overlayLayer.AddChild(dim);
		_mulliganPanel = dim;

		string who = _vsAi || _online ? "起 手 换 牌" : (seat == 0 ? "玩家1 · 起手换牌" : "玩家2 · 起手换牌");
		var title = BattleTheme.MakeOutlinedLabel(who, 46, BattleTheme.TextMain, HorizontalAlignment.Center);
		title.Position = new Vector2(0, 84); title.Size = new Vector2(BattleTheme.ScreenW, 60);
		dim.AddChild(title);
		var sub = BattleTheme.MakeLabel("点击要换掉的牌,然后确认(仅一次机会)", 26, BattleTheme.TextDim, HorizontalAlignment.Center);
		sub.Position = new Vector2(0, 160); sub.Size = new Vector2(BattleTheme.ScreenW, 40);
		dim.AddChild(sub);
		if (secondsLeft is { } s)
		{
			var label = BattleTheme.MakeOutlinedLabel($"限时 {s} 秒", 24, BattleTheme.Accent, HorizontalAlignment.Center);
			label.Position = new Vector2(0, 206); label.Size = new Vector2(BattleTheme.ScreenW, 34);
			dim.AddChild(label);
			_mulliganTimerLabel = label;
			_mulliganTimerSecs = s;
			// Local 1s tick; the server clock stays authoritative (expiry auto-submits keep-all server-side,
			// and MulliganTimerReceived re-syncs the count after a reconnect).
			var tick = new Godot.Timer { WaitTime = 1.0, Autostart = true };
			tick.Timeout += () =>
			{
				_mulliganTimerSecs = Mathf.Max(0, _mulliganTimerSecs - 1);
				if (GodotObject.IsInstanceValid(label)) label.Text = $"限时 {_mulliganTimerSecs} 秒";
				if (_mulliganTimerSecs == 0) tick.Stop();
			};
			dim.AddChild(tick); // freed with the panel — the countdown dies with it
		}

		var faceSize = new Vector2(206, 288);
		int n = hand.Count;
		const float gap = 22f;
		float totalW = n * faceSize.X + (n - 1) * gap;
		float startX = Mathf.Max(20f, (BattleTheme.ScreenW - totalW) / 2f);
		const float y = 300f;

		var confirm = BattleTheme.MakeButton(new Vector2(BattleTheme.ScreenW / 2 - 200, 664), new Vector2(400, 84), BattleTheme.AccentSoft, BattleTheme.Accent, 2, 12);
		confirm.AddThemeFontSizeOverride("font_size", 30);
		void UpdateConfirm() => confirm.Text = selected.Count == 0 ? "确认 · 全部保留" : $"确认 · 换 {selected.Count} 张";
		UpdateConfirm();

		for (int i = 0; i < n; i++)
		{
			int id = hand[i].EntityId;
			var def = _cards.Get(hand[i].CardId);
			var holder = new Button { Position = new Vector2(startX + i * (faceSize.X + gap), y), Size = faceSize, Flat = true };
			holder.AddThemeStyleboxOverride("normal", BattleTheme.Box(new Color(0, 0, 0, 0)));
			holder.AddChild(CardView.BuildFace(def, faceSize));

			var mark = new Panel { Size = faceSize, MouseFilter = MouseFilterEnum.Ignore, Visible = false };
			mark.AddThemeStyleboxOverride("panel", BattleTheme.Box(new Color(0.7f, 0.2f, 0.15f, 0.42f), BattleTheme.DangerColor, 4, 10));
			var glyph = BattleTheme.MakeOutlinedLabel("换", 64, Colors.White, HorizontalAlignment.Center);
			glyph.Size = faceSize;
			mark.AddChild(glyph);
			holder.AddChild(mark);

			holder.Pressed += () =>
			{
				if (!selected.Add(id)) selected.Remove(id);
				mark.Visible = selected.Contains(id);
				UpdateConfirm();
			};
			dim.AddChild(holder);
		}

		confirm.Pressed += () => { _sfx.Play("button"); onConfirm(selected.ToList()); };
		dim.AddChild(confirm);
	}

	private void ShowWinOverlay(GameEndedEvent ended)
	{
		var panel = new ColorRect { Color = new Color(0.02f, 0.02f, 0.02f, 0.9f) };
		panel.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		_overlayLayer.AddChild(panel);

		string who;
		Color tint;
		if (ended.WinnerSeat < 0) { who = "平局"; tint = BattleTheme.TextMain; }
		else if (FixedView) { bool win = ended.WinnerSeat == _humanSeat; who = win ? "胜 利" : "败 北"; tint = win ? BattleTheme.Accent : BattleTheme.DangerColor; }
		else { who = $"{LeaderName(_host.GetView(ended.WinnerSeat).Self.LeaderId)} 获胜"; tint = BattleTheme.Accent; }

		// item 7: victory / defeat sting, in sync with the result screen. Hotseat always celebrates a winner.
		_sfx.Play(ended.WinnerSeat >= 0 && (!FixedView || ended.WinnerSeat == _humanSeat) ? "victory" : "defeat");

		// Result illustration behind the text (dimmed); a draw keeps the plain panel.
		bool defeat = FixedView && ended.WinnerSeat >= 0 && ended.WinnerSeat != _humanSeat;
		if (ended.WinnerSeat >= 0 &&
			BattleTheme.Tex(defeat ? "screens/result_defeat.png" : "screens/result_victory.png") is { } resultTex)
		{
			var illus = BattleTheme.Art(resultTex, Vector2.Zero, new Vector2(BattleTheme.ScreenW, BattleTheme.ScreenH));
			illus.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
			illus.Modulate = new Color(0.62f, 0.62f, 0.62f);
			panel.AddChild(illus);
		}

		var msg = BattleTheme.MakeOutlinedLabel(who, 64, tint, HorizontalAlignment.Center);
		msg.Position = new Vector2(0, 250);
		msg.Size = new Vector2(BattleTheme.ScreenW, 100);
		panel.AddChild(msg);

		// Match stats: rounds, line-breaks (推过底线打脸), leader damage taken.
		int rounds = (_host.GetView(0).TurnNumber + 1) / 2;
		int a = FixedView ? _humanSeat : 0, b = 1 - a;
		string Side(int seat) => FixedView ? (seat == _humanSeat ? "你" : "对手") : (seat == 0 ? "玩家1" : "玩家2");

		void StatLine(string text, float y, int size, Color color)
		{
			var l = BattleTheme.MakeOutlinedLabel(text, size, color, HorizontalAlignment.Center);
			l.Position = new Vector2(0, y);
			l.Size = new Vector2(BattleTheme.ScreenW, 40);
			panel.AddChild(l);
		}
		StatLine($"本 局 {rounds} 回 合", 380, 30, BattleTheme.TextMain);
		StatLine($"破线打脸    {Side(a)} {_lineBreaks[a]} 次        {Side(b)} {_lineBreaks[b]} 次", 432, 26, BattleTheme.Accent);
		StatLine($"领袖受创    {Side(a)} {_leaderDmg[a]}        {Side(b)} {_leaderDmg[b]}", 474, 26, BattleTheme.TextDim);

		// Ranked ELO line (C3): shown for online matches once rating_change is in. If the delta already
		// arrived (common — playback pacing lags the wire), animate now; otherwise OnRatingChange fills it.
		if (_online)
		{
			_ratingLabel = BattleTheme.MakeOutlinedLabel("", 34, BattleTheme.Accent, HorizontalAlignment.Center);
			_ratingLabel.Position = new Vector2(0, 520);
			_ratingLabel.Size = new Vector2(BattleTheme.ScreenW, 44);
			panel.AddChild(_ratingLabel);
			if (_ratingChange is { } rc) AnimateRating(_ratingLabel, rc);
		}

		// Rematch reloads the scene locally; online can't (it would reconnect / create a new room —
		// same-room rematch is N3), so online shows only "return to menu", centered.
		if (!_online)
		{
			var again = BattleTheme.MakeButton(new Vector2(BattleTheme.ScreenW / 2 - 320, 570), new Vector2(280, 80), BattleTheme.AccentSoft, BattleTheme.Accent, 2, 12);
			again.Text = "再来一局";
			again.AddThemeFontSizeOverride("font_size", 26);
			again.Pressed += () => { _sfx.Play("button"); GetTree().ReloadCurrentScene(); };
			panel.AddChild(again);
		}

		var menuX = _online ? BattleTheme.ScreenW / 2 - 140 : BattleTheme.ScreenW / 2 + 40;
		var menu = BattleTheme.MakeButton(new Vector2(menuX, 570), new Vector2(280, 80), BattleTheme.PanelDark, BattleTheme.Accent, 2, 12);
		menu.Text = "返回菜单";
		menu.AddThemeFontSizeOverride("font_size", 26);
		menu.Pressed += () => { _sfx.Play("button"); GetTree().ChangeSceneToFile("res://scenes/menu/Menu.tscn"); };
		panel.AddChild(menu);
	}

	/// <summary>rating_change from the shared Session (WS thread already marshalled here). Stash it, and if the
	/// result screen is already up, animate the line in — otherwise ShowWinOverlay picks it up on creation.</summary>
	private void OnRatingChange(RatingChange rc)
	{
		_ratingChange = rc;
		if (_ratingLabel is { } lbl && GodotObject.IsInstanceValid(lbl))
			AnimateRating(lbl, rc);
	}

	/// <summary>Count the rating up/down from Old to New over ~0.7s, color-coded by the sign of the delta.</summary>
	private void AnimateRating(Label label, RatingChange rc)
	{
		int delta = rc.New - rc.Old;
		string sign = delta >= 0 ? "+" : "";
		label.AddThemeColorOverride("font_color", delta >= 0 ? BattleTheme.HpColor : BattleTheme.DangerColor);
		label.Modulate = new Color(1, 1, 1, 0);
		var fade = CreateTween();
		fade.TweenProperty(label, "modulate:a", 1f, 0.3f);
		var count = CreateTween();
		count.TweenMethod(Callable.From<float>(v =>
		{
			if (GodotObject.IsInstanceValid(label))
				label.Text = $"排位评分  {Mathf.RoundToInt(v)}    {sign}{delta}";
		}), (float)rc.Old, (float)rc.New, 0.7f).SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
	}

	private void AccumulateStat(GameEvent e)
	{
		switch (e)
		{
			case AttackedEvent { TargetLeaderSeat: int s }: _lineBreaks[1 - s]++; break; // hitter is the other seat
			case LeaderDamagedEvent ld: _leaderDmg[ld.Seat] += ld.Amount; break;          // covers combat + fatigue + tide
		}
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

		// Card art (BattleTheme.Art keeps the ExpandMode-before-Size order — a raw initializer
		// here once let the texture's min size blow the image out of the panel).
		const float artH = 264f;
		var artPath = $"{BattleTheme.ArtRoot}/cards/{def.Id}.png";
		if (ResourceLoader.Exists(artPath))
		{
			_detailPanel.AddChild(BattleTheme.Art(GD.Load<Texture2D>(artPath), new Vector2(pad, pad), new Vector2(innerW, artH)));
		}
		else
		{
			_detailPanel.AddChild(new ColorRect { Color = faction.Darkened(0.2f), Position = new Vector2(pad, pad), Size = new Vector2(innerW, artH), MouseFilter = MouseFilterEnum.Ignore });
			var ph = BattleTheme.MakeOutlinedLabel(def.Name, 34, new Color(1, 1, 1, 0.85f), HorizontalAlignment.Center);
			ph.AutowrapMode = TextServer.AutowrapMode.Arbitrary;
			ph.Position = new Vector2(pad, pad);
			ph.Size = new Vector2(innerW, artH);
			_detailPanel.AddChild(ph);
		}

		// Name over the art's lower edge, on a soft dark strip.
		var nameStrip = new ColorRect { Color = new Color(0.05f, 0.045f, 0.04f, 0.62f), Position = new Vector2(pad, pad + artH - 46), Size = new Vector2(innerW, 46), MouseFilter = MouseFilterEnum.Ignore };
		_detailPanel.AddChild(nameStrip);
		var nameL = BattleTheme.MakeOutlinedLabel(def.Name, 26, BattleTheme.TextMain);
		nameL.Position = new Vector2(pad + 14, pad + artH - 44);
		nameL.Size = new Vector2(innerW * 0.6f, 42);
		_detailPanel.AddChild(nameL);
		var metaL = BattleTheme.MakeOutlinedLabel($"{RarityName(def.Rarity)} · {FactionName(def.Faction)} · 随从", 14, BattleTheme.TextDim, HorizontalAlignment.Right);
		metaL.Position = new Vector2(pad + innerW * 0.5f - 14, pad + artH - 42);
		metaL.Size = new Vector2(innerW * 0.5f, 38);
		_detailPanel.AddChild(metaL);

		// Stats row: the same gems as the cards, with captions.
		float y = pad + artH + 14;
		var stats = new (string Num, string Caption, Color Color, Texture2D? Gem)[]
		{
			(def.Cost.ToString(), "辉尘", BattleTheme.CostColor, _gemCost),
			(u.Atk.ToString(), "攻击", BattleTheme.AtkColor, _gemAtk),
			(u.CurrentHp.ToString(), $"生命 {u.CurrentHp}/{u.MaxHp}", u.CurrentHp < u.MaxHp ? BattleTheme.DangerColor : BattleTheme.HpColor, _gemHp),
		};
		float sx = pad + 6;
		foreach (var (num, caption, color, gemTex) in stats)
		{
			_detailPanel.AddChild(Pip(num, color, new Vector2(sx, y), gemTex, 46));
			var cap = BattleTheme.MakeLabel(caption, 17, BattleTheme.TextMain);
			cap.Position = new Vector2(sx + 54, y + 11);
			cap.Size = new Vector2(110, 24);
			_detailPanel.AddChild(cap);
			sx += 158;
		}
		y += 46 + 16;

		// Rules text on the same dark plate style as the hand cards.
		if (def.Text.Length > 0)
		{
			string bodyText = BattleTheme.BodyText(def.Text);
			float plateH = 26f + 26f * Mathf.Ceil(bodyText.Length / 26f);
			var plate = new Panel { Position = new Vector2(pad, y), Size = new Vector2(innerW, plateH), MouseFilter = MouseFilterEnum.Ignore };
			plate.AddThemeStyleboxOverride("panel", BattleTheme.Box(
				new Color(0.07f, 0.06f, 0.05f, 0.7f), new Color(0.62f, 0.5f, 0.3f, 0.45f), 1, 8));
			_detailPanel.AddChild(plate);
			var body = BattleTheme.MakeLabel(bodyText, 17, new Color(0.93f, 0.89f, 0.8f), HorizontalAlignment.Center);
			body.AddThemeFontOverride("font", BattleTheme.UiFontBold);
			body.AutowrapMode = TextServer.AutowrapMode.Arbitrary;
			body.VerticalAlignment = VerticalAlignment.Center;
			body.ClipContents = true;
			body.Position = new Vector2(pad + 12, y + 2);
			body.Size = new Vector2(innerW - 24, plateH - 4);
			_detailPanel.AddChild(body);
			y += plateH + 12;
		}

		foreach (var k in u.Keywords)
		{
			var kwl = BattleTheme.MakeLabel($"【{KeywordDisplayName(k)}】{BattleTheme.BodyText(KeywordDesc(k.Keyword))}", 15, BattleTheme.Accent);
			kwl.AutowrapMode = TextServer.AutowrapMode.Arbitrary;
			kwl.VerticalAlignment = VerticalAlignment.Top;
			kwl.ClipContents = true;
			kwl.Position = new Vector2(pad + 4, y);
			kwl.Size = new Vector2(innerW - 8, 44);
			_detailPanel.AddChild(kwl);
			y += 48;
		}

		// Faction lore pinned to the bottom — skipped when the content above needs the room.
		if (y < DetailH - 76)
		{
			var lore = BattleTheme.MakeLabel(FactionLore(def.Faction), 13, BattleTheme.TextDim);
			lore.AutowrapMode = TextServer.AutowrapMode.Arbitrary;
			lore.VerticalAlignment = VerticalAlignment.Bottom;
			lore.ClipContents = true;
			lore.Position = new Vector2(pad, DetailH - 66);
			lore.Size = new Vector2(innerW, 52);
			_detailPanel.AddChild(lore);
		}

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
		"duskweaver" => "黄昏教团",
		"undervault" => "掘世匠会",
		_ => "中立",
	};

	private static string FactionLore(string faction) => faction switch
	{
		"iron_vow" => "铁誓军团 —— 誓约骑士与堡垒工程师,断层战争中最后的正规军。以墙为盾,寸土不让。",
		"wildpack" => "荒野游群 —— 兽人与掠猎兽骑手,在断层荒原上以速度为生存法则。风过之处,防线洞开。",
		"duskweaver" => "黄昏教团 —— 焚火祭司与灰烬信徒,以格、行、列为祭坛的法术连锁者。误伤友军是代价,也是燃料。",
		"undervault" => "掘世匠会 —— 掘地矮人与蒸汽工程师,把阵型钉死成答案。架起炮台,隔墙点名。",
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
		Keyword.Taunt => "嘲讽",
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
		Keyword.Blessing => "福泽",
		Keyword.Guardian => "守护",
		_ => k.ToString(),
	};

	private static string KeywordDesc(Keyword k) => k switch
	{
		Keyword.Charge => "部署当回合即可移动与攻击。",
		Keyword.Assault => "部署当回合可攻击,但不能移动。",
		Keyword.Swift => "每回合可移动的格数提升。",
		Keyword.Range => "可攻击 N 步(横纵相加)内的任意敌人,越过其他随从;仅当目标能反击到你(在其射程/相邻内)时才吃反击。",
		Keyword.Taunt => "与其相邻的敌方随从必须优先攻击它。",
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
		Keyword.Blessing => "福泽:与其相邻的友方随从受到的伤害 -1(不含自身,可与坚守叠加)。",
		Keyword.Guardian => "守护:与其相邻的友方随从将要受到的伤害,转移到它身上承受(享受它自身的减伤)。",
		_ => "",
	};

	// ---------- tiny animation helpers ----------

	private async Task TweenTo(Control node, Vector2 target, double dur)
	{
		var t = CreateTween();
		t.TweenProperty(node, "position", target, dur).SetTrans(Tween.TransitionType.Sine);
		await ToSignal(t, Tween.SignalName.Finished);
	}

	private void Flash(Control node, Color color)
	{
		var t = CreateTween();
		node.Modulate = color;
		t.TweenProperty(node, "modulate", Colors.White, 0.25);
	}

	// A centred floating label (shield "盾", the 压力潮汐 line). `center` is the point to center on.
	private void FloatText(Vector2 center, string text, Color color)
	{
		var label = BattleTheme.MakeOutlinedLabel(text, 30, color, HorizontalAlignment.Center);
		label.Size = new Vector2(600, 44);
		label.Position = center - label.Size / 2f;
		_overlayLayer.AddChild(label);
		var start = label.Position;
		var t = CreateTween();
		t.SetParallel(true);
		t.TweenProperty(label, "position", start + new Vector2(0, -60), 0.6);
		t.TweenProperty(label, "modulate:a", 0.0f, 0.6);
		t.Chain().TweenCallback(Callable.From(label.QueueFree));
	}

	// item 4: a damage/heal number — pops in, floats up then settles (gravity), bigger for bigger hits.
	private void FloatNumber(Vector2 center, string text, Color color, int amount)
	{
		int size = Mathf.Clamp(28 + amount * 4, 28, 60);
		var label = BattleTheme.MakeOutlinedLabel(text, size, color, HorizontalAlignment.Center);
		label.Size = new Vector2(140, size + 16);
		label.Position = center - label.Size / 2f;
		label.PivotOffset = label.Size / 2f;
		label.Scale = new Vector2(0.5f, 0.5f);
		_overlayLayer.AddChild(label);
		var p0 = label.Position;
		var t = CreateTween();
		t.TweenProperty(label, "scale", new Vector2(1.15f, 1.15f), 0.10).SetTrans(Tween.TransitionType.Back);
		t.Parallel().TweenProperty(label, "position", p0 + new Vector2(0, -24), 0.10);
		t.TweenProperty(label, "scale", Vector2.One, 0.06);
		t.TweenProperty(label, "position", p0 + new Vector2(0, -10), 0.35).SetTrans(Tween.TransitionType.Sine); // settle
		t.Parallel().TweenProperty(label, "modulate:a", 0.0f, 0.35);
		t.TweenCallback(Callable.From(label.QueueFree));
	}

	// A small attribution tag beside a damage number (e.g. "架设 +1"), explaining a bonus the card face
	// doesn't print. Amber (fire) reads apart from the red damage number; offset right so they don't overlap.
	private void FloatBonusTag(Vector2 center, string text)
	{
		var label = BattleTheme.MakeOutlinedLabel(text, 20, BattleTheme.AtkColor, HorizontalAlignment.Center);
		label.Size = new Vector2(150, 30);
		label.Position = center - label.Size / 2f + new Vector2(52, 4);
		_overlayLayer.AddChild(label);
		var p0 = label.Position;
		var t = CreateTween();
		t.TweenProperty(label, "position", p0 + new Vector2(0, -28), 0.55).SetTrans(Tween.TransitionType.Sine);
		t.Parallel().TweenProperty(label, "modulate:a", 0.0f, 0.55);
		t.TweenCallback(Callable.From(label.QueueFree));
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

	/// <summary>Stat pip: number over a gem texture when available, else the flat colored number.</summary>
	private static Control Pip(string text, Color color, Vector2 pos, Texture2D? gem = null, int px = 38)
	{
		if (gem == null)
		{
			var label = BattleTheme.MakeLabel(text, 24, color, HorizontalAlignment.Center);
			label.Position = pos;
			label.Size = new Vector2(34, 34);
			return label;
		}
		var holder = new Control { Position = pos, Size = new Vector2(px, px), MouseFilter = MouseFilterEnum.Ignore };
		holder.AddChild(BattleTheme.Art(gem, Vector2.Zero, holder.Size, TextureRect.StretchModeEnum.KeepAspectCentered));
		var num = BattleTheme.MakeOutlinedLabel(text, Mathf.RoundToInt(px * 0.53f), Colors.White, HorizontalAlignment.Center);
		num.Size = holder.Size;
		holder.AddChild(num);
		return holder;
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
		"duskweaver" => Color.FromHtml("8b5fa6"), // dusk purple (教团)
		"undervault" => Color.FromHtml("b5883f"), // brass (匠会)
		_ => BattleTheme.TextDim,
	};

	// By SCREEN row: the viewer's deploy zone is always the bottom row, the enemy's the top.
	private static Color CellBaseColor(int screenRow) =>
		screenRow == 0 ? BattleTheme.HomeRowP0 : screenRow == BattleTheme.Rows - 1 ? BattleTheme.HomeRowP1 : BattleTheme.CellEmpty;

	// Over the painted table the cells go translucent so the board art shows through.
	private Color CellBase(int screenRow)
	{
		var c = CellBaseColor(screenRow);
		return _boardTex != null ? new Color(c.R, c.G, c.B, screenRow == 0 || screenRow == BattleTheme.Rows - 1 ? 0.6f : 0.42f) : c;
	}

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
				Keyword.HoldFast => "坚",
				Keyword.Trample => "踏",
				Keyword.CheapShot => "偷",
				// 持盾 is shown by the live status chip (SetStandeeStatuses), NOT here — the keyword can linger
				// after its charge is spent, so a static "盾" would lie about protection. Chip tracks ShieldActive.
				Keyword.Garrison => "防",
				Keyword.Leap => "跃",
				Keyword.PackTactics => "围",
				Keyword.Emplacement => "架",
				Keyword.Pierce => "贯",
				Keyword.Taunt => "嘲",
				Keyword.Blessing => "福",
				Keyword.Guardian => "护",
				_ => "",
			});
		return string.Join(" ", parts.Where(p => p.Length > 0));
	}

	// ---------- on-standee status indicators (buffs / debuffs) ----------

	/// <summary>One status a unit advertises on its card face.</summary>
	private readonly record struct StatusBadge(string Text, Color Bg, Color Border);

	private const string StatusStripName = "__status_strip";

	/// <summary>The statuses to show for a unit, computed from its LIVE view state (not the static keyword
	/// list). Extensible: new buffs/debuffs add a line here. Today only 持盾 (the ShieldActive charge).</summary>
	private static List<StatusBadge> StandeeStatuses(bool shieldActive)
	{
		var badges = new List<StatusBadge>();
		if (shieldActive)
			badges.Add(new StatusBadge("持盾", BattleTheme.ShieldStatusBg, BattleTheme.ShieldStatusBorder));
		return badges;
	}

	/// <summary>(Re)build a standee's status strip — a centred row of chips near the top, clear of the corner
	/// atk/hp pips. Removes any prior strip first so it can be called live mid-animation (shield pop/grant)
	/// as well as during a full render.</summary>
	private static void SetStandeeStatuses(Control standee, List<StatusBadge> badges)
	{
		if (standee.GetNodeOrNull(StatusStripName) is { } old) { standee.RemoveChild(old); old.QueueFree(); }
		if (badges.Count == 0)
			return;

		var size = new Vector2(BattleTheme.CellW - 14, BattleTheme.CellH - 14);
		const float chipH = 22f, gap = 3f;
		var widths = badges.Select(b => (float)(b.Text.Length * 15 + 12)).ToArray();
		float total = widths.Sum() + gap * (badges.Count - 1);

		var strip = new Control
		{
			Name = StatusStripName,
			Position = new Vector2((size.X - total) / 2f, 3f),
			Size = new Vector2(total, chipH),
			MouseFilter = MouseFilterEnum.Ignore,
		};
		float x = 0f;
		for (int i = 0; i < badges.Count; i++)
		{
			strip.AddChild(StatusChip(badges[i], new Vector2(x, 0), new Vector2(widths[i], chipH)));
			x += widths[i] + gap;
		}
		standee.AddChild(strip);
	}

	private static Control StatusChip(StatusBadge b, Vector2 pos, Vector2 size)
	{
		var chip = new Panel { Position = pos, Size = size, MouseFilter = MouseFilterEnum.Ignore };
		chip.AddThemeStyleboxOverride("panel", BattleTheme.Box(b.Bg, b.Border, 2, 7));
		var label = BattleTheme.MakeOutlinedLabel(b.Text, 13, BattleTheme.TextMain, HorizontalAlignment.Center);
		label.VerticalAlignment = VerticalAlignment.Center;
		label.Position = Vector2.Zero;
		label.Size = size;
		chip.AddChild(label);
		return chip;
	}

	private void Log(string message) => _logLabel.Text = message;
}
