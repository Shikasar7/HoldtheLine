using Godot;
using HoldTheLine.Rules.Cards;

namespace HoldTheLine.Game;

/// <summary>
/// In-match menu overlay (继续 / 查看牌组 / 投降 / 返回菜单) with its confirm sheets and the deck-list
/// view — docs/22 批次E3: lifted out of BattleScene. All views share one tracked backdrop (Esc / the
/// menu button close whichever is showing); scaffolding comes from <see cref="OverlayKit"/>. Data in
/// via constructor funcs (evaluated at open time), consequences out via callbacks.
/// </summary>
public sealed class GameMenuPanel
{
	private readonly Control _overlayLayer;
	private readonly Control _popupHost; // scene control that hosts card detail popups (CardView.ShowDetailPopup)
	private readonly SfxBank _sfx;
	private readonly CardDatabase _cards;
	private readonly bool _online;
	private readonly System.Func<bool> _isMatchOver;
	private readonly System.Func<string> _surrenderSubText;                 // 投降 confirm sub line (seat-aware offline)
	private readonly System.Func<IReadOnlyList<string>?> _deckCards;        // the local viewer's decklist, for 查看牌组
	private readonly System.Action _onSurrender;                            // confirmed 投降
	private readonly System.Action _onLeaveAsConcede;                       // confirmed 离开 mid-online-match (= concede)
	private readonly System.Action _onExitToMenu;                           // plain 返回菜单 (offline / match already over)

	private Control? _root; // whichever menu view is showing; freed on Close

	public GameMenuPanel(Control overlayLayer, Control popupHost, SfxBank sfx, CardDatabase cards, bool online,
		System.Func<bool> isMatchOver, System.Func<string> surrenderSubText, System.Func<IReadOnlyList<string>?> deckCards,
		System.Action onSurrender, System.Action onLeaveAsConcede, System.Action onExitToMenu)
	{
		_overlayLayer = overlayLayer;
		_popupHost = popupHost;
		_sfx = sfx;
		_cards = cards;
		_online = online;
		_isMatchOver = isMatchOver;
		_surrenderSubText = surrenderSubText;
		_deckCards = deckCards;
		_onSurrender = onSurrender;
		_onLeaveAsConcede = onLeaveAsConcede;
		_onExitToMenu = onExitToMenu;
	}

	public bool IsOpen => _root is { } m && GodotObject.IsInstanceValid(m);

	public void Toggle()
	{
		_sfx.Play("button");
		if (IsOpen) { Close(); return; }
		Show();
	}

	public void Close()
	{
		if (_root is { } m && GodotObject.IsInstanceValid(m)) m.QueueFree();
		_root = null;
	}

	/// <summary>One shared backdrop per view: opening a view closes the previous one, and the backdrop
	/// click (OverlayKit's back button) closes the whole menu.</summary>
	private Panel NewOverlay(Vector2 panelSize)
	{
		Close();
		var panel = OverlayKit.Overlay(_overlayLayer, panelSize, out var root, onBackdrop: Close);
		_root = root;
		return panel;
	}

	public void Show()
	{
		bool over = _isMatchOver();
		// rev3: no title — four buttons, evenly inset within the leather frame's inner field (the frame border
		// is ~74px; the old 60px inset let plates overlap it).
		var panel = NewOverlay(new Vector2(480, 460));

		float by = 78f;
		Button Row(string text, Color color, System.Action onPressed)
		{
			var b = BattleTheme.MakeButton(new Vector2(84, by), new Vector2(312, 64), color, BattleTheme.Accent, 2, 12, textured: true);
			b.Text = text;
			b.AddThemeFontSizeOverride("font_size", 26);
			b.Pressed += () => { _sfx.Play("button"); onPressed(); };
			panel.AddChild(b);
			by += 80f;
			return b;
		}

		Row("继续", BattleTheme.AccentSoft, Close);
		Row("查看牌组", BattleTheme.PanelDark, ShowDeckList);
		var surrender = Row("投降", BattleTheme.PanelDark, ConfirmSurrender);
		surrender.Disabled = over; // nothing to surrender once the match has ended
		Row("返回菜单", BattleTheme.PanelDark, () =>
		{
			if (_online && !over) { ConfirmLeaveOnline(); return; }
			Close();
			_onExitToMenu();
		});
	}

	/// <summary>返回菜单 mid-online-match: "离开 = 投降 = 看结算". The red 离开 concedes and STAYS in the
	/// scene — GameEnded flows through the pump to the win overlay, and the player leaves from there like
	/// any other finished match. No bypass path exists: the match always settles before the menu is
	/// reachable, so no server-side clock forfeit can chase the player into their next game on the shared
	/// lobby socket (the "sudden loss next match" bug).</summary>
	private void ConfirmLeaveOnline()
	{
		Close();
		_root = OverlayKit.Confirm(_overlayLayer, "还在对局中", "如果返回将会视为投降输掉对局,是否继续?", "离开",
			onYes: () => { _sfx.Play("button"); Close(); _onLeaveAsConcede(); },
			onNo: () => { _sfx.Play("button"); Show(); },
			onBackdrop: Close);
	}

	private void ConfirmSurrender()
	{
		Close();
		_root = OverlayKit.Confirm(_overlayLayer, "确认投降?", _surrenderSubText(), "投降",
			onYes: () => { _sfx.Play("button"); Close(); _onSurrender(); },
			onNo: () => { _sfx.Play("button"); Show(); },
			onBackdrop: Close);
	}

	/// <summary>查看牌组: the full deck the local viewer brought this match, grouped by card with copy counts.
	/// Click a card for its detail popup.</summary>
	private void ShowDeckList()
	{
		// rev4: cards live INSIDE the leather frame's inner field (~74px border) — the old 24px inset let the
		// grid ride over the frame art (user 图1).
		var panel = NewOverlay(new Vector2(1200, 900));
		var title = BattleTheme.MakeTitle("我 的 牌 组", 34, BattleTheme.AtkColor);
		title.Position = new Vector2(0, 18); title.Size = new Vector2(1200, 48);
		panel.AddChild(title);

		var cards = _deckCards();
		if (cards is null || cards.Count == 0)
		{
			var none = BattleTheme.MakeLabel("牌组信息本局不可用", 28, BattleTheme.TextDim, HorizontalAlignment.Center);
			none.Position = new Vector2(0, 420); none.Size = new Vector2(1200, 44);
			panel.AddChild(none);
		}
		else
		{
			var scroll = new ScrollContainer { Position = new Vector2(84, 90), Size = new Vector2(1032, 654) };
			scroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
			panel.AddChild(scroll);
			var grid = new GridContainer { Columns = 5, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
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
					var badge = new Panel { Position = new Vector2(faceSize.X - 46, 4), Size = new Vector2(42, 34), MouseFilter = Control.MouseFilterEnum.Ignore };
					badge.AddThemeStyleboxOverride("panel", BattleTheme.Box(new Color(0.05f, 0.05f, 0.05f, 0.85f), BattleTheme.Accent, 1, 8));
					var bl = BattleTheme.MakeOutlinedLabel($"×{g.Count}", 20, BattleTheme.TextMain, HorizontalAlignment.Center);
					bl.Size = new Vector2(42, 34);
					badge.AddChild(bl);
					holder.AddChild(badge);
				}
				holder.Pressed += () => { _sfx.Play("button"); CardView.ShowDetailPopup(_popupHost, def); };
				grid.AddChild(holder);
			}
		}

		var close = BattleTheme.MakeButton(new Vector2(500, 760), new Vector2(200, 56), BattleTheme.PanelDark, BattleTheme.Accent, 2, 12, textured: true);
		close.Text = "返回"; close.AddThemeFontSizeOverride("font_size", 26);
		close.Pressed += () => { _sfx.Play("button"); Show(); };
		panel.AddChild(close);
	}
}
