using Godot;
using HoldTheLine.Net.Protocol;
using HoldTheLine.Rules.Events;

namespace HoldTheLine.Game;

/// <summary>
/// 结算面板 (win / defeat / draw overlay + match stats + ranked ELO line) — docs/22 批次E3: lifted out
/// of BattleScene. The panel owns the rating_change stash: the delta may arrive before or after the
/// overlay is up (playback pacing lags the wire), so <see cref="OnRatingChange"/> fills or defers, and
/// <see cref="Show"/> picks a stashed delta up on creation. Data in via arguments, exits via callbacks.
/// </summary>
public sealed class MatchEndPanel
{
	private readonly Control _host;         // scene node: parents tweens (AnimateRating) exactly as before
	private readonly Control _overlayLayer;
	private readonly SfxBank _sfx;
	private readonly bool _online;

	private RatingChange? _ratingChange; // latest rating_change (may arrive before or after the win overlay)
	private Label? _ratingLabel;         // rating line on the result screen, filled/animated once the delta is known

	public MatchEndPanel(Control host, Control overlayLayer, SfxBank sfx, bool online)
	{
		_host = host;
		_overlayLayer = overlayLayer;
		_sfx = sfx;
		_online = online;
	}

	public void Show(GameEndedEvent ended, bool fixedView, int humanSeat, System.Func<int, string> seatLeaderName,
		int rounds, IReadOnlyList<int> lineBreaks, IReadOnlyList<int> leaderDmg,
		System.Action onRematch, System.Action onExitToMenu)
	{
		var panel = new ColorRect { Color = new Color(0.02f, 0.02f, 0.02f, 0.9f) };
		panel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
		_overlayLayer.AddChild(panel);

		string who;
		Color tint;
		if (ended.WinnerSeat < 0) { who = "平局"; tint = BattleTheme.TextMain; }
		else if (fixedView) { bool win = ended.WinnerSeat == humanSeat; who = win ? "胜 利" : "败 北"; tint = win ? BattleTheme.Accent : BattleTheme.DangerColor; }
		else { who = $"{seatLeaderName(ended.WinnerSeat)} 获胜"; tint = BattleTheme.Accent; }

		// item 7: victory / defeat sting, in sync with the result screen. Hotseat always celebrates a winner.
		_sfx.Play(ended.WinnerSeat >= 0 && (!fixedView || ended.WinnerSeat == humanSeat) ? "victory" : "defeat");

		// Result illustration behind the text (dimmed); a draw keeps the plain panel.
		bool defeat = fixedView && ended.WinnerSeat >= 0 && ended.WinnerSeat != humanSeat;
		if (ended.WinnerSeat >= 0 &&
			BattleTheme.Tex(defeat ? "screens/result_defeat.png" : "screens/result_victory.png") is { } resultTex)
		{
			var illus = BattleTheme.Art(resultTex, Vector2.Zero, new Vector2(BattleTheme.ScreenW, BattleTheme.ScreenH));
			illus.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
			illus.Modulate = new Color(0.62f, 0.62f, 0.62f);
			panel.AddChild(illus);
		}

		var msg = BattleTheme.MakeTitle(who, 88, tint, HorizontalAlignment.Center);
		msg.Position = new Vector2(0, 232);
		msg.Size = new Vector2(BattleTheme.ScreenW, 120);
		panel.AddChild(msg);

		// Match stats: rounds, line-breaks (推过底线打脸), leader damage taken.
		int a = fixedView ? humanSeat : 0, b = 1 - a;
		string Side(int seat) => fixedView ? (seat == humanSeat ? "你" : "对手") : (seat == 0 ? "玩家1" : "玩家2");

		void StatLine(string text, float y, int size, Color color)
		{
			var l = BattleTheme.MakeOutlinedLabel(text, size, color, HorizontalAlignment.Center);
			l.Position = new Vector2(0, y);
			l.Size = new Vector2(BattleTheme.ScreenW, 40);
			panel.AddChild(l);
		}
		StatLine($"本 局 {rounds} 回 合", 380, 30, BattleTheme.TextMain);
		StatLine($"破线打脸    {Side(a)} {lineBreaks[a]} 次        {Side(b)} {lineBreaks[b]} 次", 432, 26, BattleTheme.Accent);
		StatLine($"领袖受创    {Side(a)} {leaderDmg[a]}        {Side(b)} {leaderDmg[b]}", 474, 26, BattleTheme.TextDim);

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
			var again = BattleTheme.MakeButton(new Vector2(BattleTheme.ScreenW / 2 - 320, 570), new Vector2(280, 80), BattleTheme.AtkColor, BattleTheme.Accent, 2, 12, textured: true);
			again.Text = "再来一局";
			again.AddThemeFontSizeOverride("font_size", 26);
			again.Pressed += () => { _sfx.Play("button"); onRematch(); };
			panel.AddChild(again);
		}

		var menuX = _online ? BattleTheme.ScreenW / 2 - 140 : BattleTheme.ScreenW / 2 + 40;
		var menu = BattleTheme.MakeButton(new Vector2(menuX, 570), new Vector2(280, 80), BattleTheme.PanelDark, BattleTheme.Accent, 2, 12, textured: true);
		menu.Text = "返回菜单";
		menu.AddThemeFontSizeOverride("font_size", 26);
		menu.Pressed += () => { _sfx.Play("button"); onExitToMenu(); };
		panel.AddChild(menu);
	}

	/// <summary>rating_change from the shared Session (WS thread already marshalled here). Stash it, and if the
	/// result screen is already up, animate the line in — otherwise Show picks it up on creation.</summary>
	public void OnRatingChange(RatingChange rc)
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
		var fade = _host.CreateTween();
		fade.TweenProperty(label, "modulate:a", 1f, 0.3f);
		var count = _host.CreateTween();
		count.TweenMethod(Callable.From<float>(v =>
		{
			if (GodotObject.IsInstanceValid(label))
				label.Text = $"排位评分  {Mathf.RoundToInt(v)}    {sign}{delta}";
		}), (float)rc.Old, (float)rc.New, 0.7f).SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
	}
}
