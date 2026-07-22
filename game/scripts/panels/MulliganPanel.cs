using Godot;
using HoldTheLine.Rules.Cards;
using HoldTheLine.Rules.Hosting;

namespace HoldTheLine.Game;

/// <summary>
/// 起手重抽 overlay (docs/11 §6), offline + online — docs/22 批次E3: lifted out of BattleScene.
/// One panel class serves both flows; the mode differences are expressed through parameters:
/// offline (hotseat / vs-AI) drives <see cref="ShowSelect"/> / <see cref="ShowPassOverlay"/> directly,
/// online funnels every state change through <see cref="RefreshOnline"/>. Data flows in via
/// constructor/method arguments, results flow back via callbacks — the panel never reads BattleScene.
/// </summary>
public sealed class MulliganPanel
{
	private readonly Control _overlayLayer;
	private readonly Control _handLayer;
	private readonly SfxBank _sfx;
	private readonly CardDatabase _cards;

	private Control? _root;         // the overlay currently up (selection / waiting / pass), freed on Close
	private int _mode;              // online only: 0 none, 1 selecting, 2 waiting-for-opponent
	private Label? _timerLabel;     // countdown label on the online panel; freed with the panel
	private int _timerSecs;         // ticks down locally, re-synced by UpdateTimer (MulliganTimerReceived)

	/// <summary>Offline hotseat: seat whose panel is currently up (-1 none). Read by the scene's
	/// surrender logic (OfflineConcedeSeat) to know who is at the device during the phase.</summary>
	public int ShownSeat { get; set; } = -1;

	public MulliganPanel(Control overlayLayer, Control handLayer, SfxBank sfx, CardDatabase cards)
	{
		_overlayLayer = overlayLayer;
		_handLayer = handLayer;
		_sfx = sfx;
		_cards = cards;
	}

	public void Close()
	{
		if (_root is { } p && GodotObject.IsInstanceValid(p)) p.QueueFree();
		_root = null;
		_timerLabel = null; // owned by the panel; freed with it
	}

	/// <summary>Server re-announced the mulligan clock (match_started / resync after a reconnect):
	/// adopt its count so the local 1s tick can't drift.</summary>
	public void UpdateTimer(int secs)
	{
		_timerSecs = secs;
		if (_timerLabel is { } l && GodotObject.IsInstanceValid(l)) l.Text = $"限时 {secs} 秒";
	}

	// --- offline (hotseat) ---

	/// <summary>Hotseat: hand the device to the other player before their selection panel opens.
	/// <paramref name="onContinue"/> fires on click — the caller marks the seat and shows the panel.</summary>
	public void ShowPassOverlay(string seatDisplayName, System.Action onContinue)
	{
		Close();
		_handLayer.Visible = false;
		var panel = BattleTheme.MakeButton(Vector2.Zero, new Vector2(BattleTheme.ScreenW, BattleTheme.ScreenH), new Color(0.03f, 0.03f, 0.03f, 0.95f), radius: 0);
		var msg = BattleTheme.MakeLabel($"轮到 {seatDisplayName} 换牌\n\n点击继续", 44, BattleTheme.TextMain, HorizontalAlignment.Center);
		msg.Position = new Vector2(0, 420); msg.Size = new Vector2(BattleTheme.ScreenW, 240);
		panel.AddChild(msg);
		panel.Pressed += () => onContinue();
		_overlayLayer.AddChild(panel);
		_root = panel; // ShowSelect's Close retires it
	}

	// --- online ---

	/// <summary>Reflect the current mulligan state online: show my selection panel while I owe a mulligan,
	/// a waiting notice once I've submitted, and tear the overlay down when the phase closes
	/// (<paramref name="onPhaseClosed"/> lets the scene re-render; the hand strip is already restored).</summary>
	public void RefreshOnline(PlayerView view, int? secondsLeft, System.Action<List<int>> submit, System.Action onPhaseClosed)
	{
		if (view.Result != null || (!view.MulliganPending && !view.OpponentMulliganPending))
		{
			if (_mode != 0) { _mode = 0; Close(); _handLayer.Visible = true; onPhaseClosed(); }
			return;
		}

		if (view.MulliganPending)
		{
			if (_mode == 1) return; // already selecting — don't clobber the player's picks
			_mode = 1;
			ShowSelect(view.Self.Hand, "起 手 换 牌", secondsLeft, ids =>
			{
				_mode = 2;
				ShowWaiting();
				submit(ids);
			});
		}
		else if (_mode != 2)
		{
			_mode = 2;
			ShowWaiting();
		}
	}

	private void ShowWaiting()
	{
		Close();
		_handLayer.Visible = false;
		var dim = new ColorRect { Color = new Color(0.02f, 0.02f, 0.02f, 0.9f) };
		dim.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
		_overlayLayer.AddChild(dim);
		_root = dim;
		var msg = BattleTheme.MakeOutlinedLabel("已确认 · 等待对手换牌…", 40, BattleTheme.Accent, HorizontalAlignment.Center);
		msg.Position = new Vector2(0, 470); msg.Size = new Vector2(BattleTheme.ScreenW, 60);
		dim.AddChild(msg);
	}

	// --- shared panel ---

	/// <summary>The 起手重抽 selection panel: the seat's opening hand as toggleable cards (tap = mark for
	/// replacement) plus a confirm. <paramref name="onConfirm"/> receives the chosen entity ids.</summary>
	public void ShowSelect(IReadOnlyList<CardInHandView> hand, string title, int? secondsLeft, System.Action<List<int>> onConfirm)
	{
		Close();
		_handLayer.Visible = false;

		var selected = new HashSet<int>();

		var dim = new ColorRect { Color = new Color(0.02f, 0.02f, 0.02f, 0.92f) };
		dim.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
		_overlayLayer.AddChild(dim);
		_root = dim;

		var titleLabel = BattleTheme.MakeTitle(title, 52, BattleTheme.TextMain, HorizontalAlignment.Center);
		titleLabel.Position = new Vector2(0, 80); titleLabel.Size = new Vector2(BattleTheme.ScreenW, 64);
		dim.AddChild(titleLabel);
		var sub = BattleTheme.MakeLabel("点击要换掉的牌,然后确认(仅一次机会)", 26, BattleTheme.TextDim, HorizontalAlignment.Center);
		sub.Position = new Vector2(0, 160); sub.Size = new Vector2(BattleTheme.ScreenW, 40);
		dim.AddChild(sub);
		if (secondsLeft is { } s)
		{
			var label = BattleTheme.MakeOutlinedLabel($"限时 {s} 秒", 24, BattleTheme.Accent, HorizontalAlignment.Center);
			label.Position = new Vector2(0, 206); label.Size = new Vector2(BattleTheme.ScreenW, 34);
			dim.AddChild(label);
			_timerLabel = label;
			_timerSecs = s;
			// Local 1s tick; the server clock stays authoritative (expiry auto-submits keep-all server-side,
			// and MulliganTimerReceived re-syncs the count after a reconnect).
			var tick = new Godot.Timer { WaitTime = 1.0, Autostart = true };
			tick.Timeout += () =>
			{
				_timerSecs = Mathf.Max(0, _timerSecs - 1);
				if (GodotObject.IsInstanceValid(label)) label.Text = $"限时 {_timerSecs} 秒";
				if (_timerSecs == 0) tick.Stop();
			};
			dim.AddChild(tick); // freed with the panel — the countdown dies with it
		}

		var faceSize = new Vector2(206, 288);
		int n = hand.Count;
		const float gap = 22f;
		float totalW = n * faceSize.X + (n - 1) * gap;
		float startX = Mathf.Max(20f, (BattleTheme.ScreenW - totalW) / 2f);
		const float y = 300f;

		// rev5: back to the teal plate — gold read as out-of-place here (user feedback), and the side icon
		// made the label look off-center.
		var confirm = BattleTheme.MakeButton(new Vector2(BattleTheme.ScreenW / 2 - 200, 664), new Vector2(400, 84), BattleTheme.AccentSoft, BattleTheme.Accent, 2, 12, textured: true);
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

			var mark = new Panel { Size = faceSize, MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
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
}
