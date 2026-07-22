using Godot;

namespace HoldTheLine.Game;

/// <summary>
/// docs/22 批次B2/E3 UiKit: shared modal scaffolding for in-battle overlay panels.
/// <see cref="Overlay"/> is the dim-backdrop + centered-framed-panel skeleton (the old
/// BattleScene.NewMenuOverlay); <see cref="Confirm"/> is the standard two-button confirm sheet built
/// on top of it. MenuScene/DeckScene/CardView keep their own scaffolding for now (批次E3 scope).
/// </summary>
public static class OverlayKit
{
	/// <summary>A dim backdrop (click = <paramref name="onBackdrop"/>) plus a centered panel.
	/// <paramref name="skin"/> overrides the panel stylebox; the default is the leather ledger frame
	/// (docs/18 §4.2) with the flat panel as fallback when the art is missing. Returns the centered
	/// panel; <paramref name="root"/> is the full-screen backdrop the caller tracks and frees.</summary>
	public static Panel Overlay(Control host, Vector2 panelSize, out Control root, System.Action? onBackdrop = null, StyleBox? skin = null)
	{
		var dim = new ColorRect { Color = new Color(0.02f, 0.02f, 0.02f, 0.72f) };
		dim.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
		host.AddChild(dim);
		root = dim;

		var back = new Button { Flat = true };
		back.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
		if (onBackdrop is not null) back.Pressed += () => onBackdrop();
		dim.AddChild(back);

		var panel = new Panel
		{
			Position = new Vector2((BattleTheme.ScreenW - panelSize.X) / 2f, (BattleTheme.ScreenH - panelSize.Y) / 2f),
			Size = panelSize,
			MouseFilter = Control.MouseFilterEnum.Stop, // clicks on the panel must not close via the backdrop
		};
		// docs/18 §4.2: a leather ledger frame when the art is present, else the flat panel.
		panel.AddThemeStyleboxOverride("panel", skin ?? (StyleBox?)BattleTheme.LeatherPanel() ?? BattleTheme.Box(BattleTheme.PanelDark, BattleTheme.Accent, 2, 16));
		dim.AddChild(panel);
		return panel;
	}

	/// <summary>The standard 480×360 confirm sheet: outlined danger title, dim sub line, a danger action
	/// button plus a steel 取消. Sfx belongs to the callers — bake it into <paramref name="onYes"/> /
	/// <paramref name="onNo"/>. Returns the backdrop root for the caller to track/free.</summary>
	public static Control Confirm(Control host, string title, string sub, string yesText,
		System.Action onYes, System.Action onNo, System.Action? onBackdrop = null)
	{
		var panel = Overlay(host, new Vector2(480, 360), out var root, onBackdrop);

		var q = BattleTheme.MakeOutlinedLabel(title, 40, BattleTheme.DangerColor, HorizontalAlignment.Center);
		q.Position = new Vector2(0, 60); q.Size = new Vector2(480, 60);
		panel.AddChild(q);
		var subLabel = BattleTheme.MakeLabel(sub, 22, BattleTheme.TextDim, HorizontalAlignment.Center);
		subLabel.Position = new Vector2(0, 132); subLabel.Size = new Vector2(480, 36);
		panel.AddChild(subLabel);

		var yes = BattleTheme.MakeButton(new Vector2(84, 230), new Vector2(150, 64), BattleTheme.DangerColor, BattleTheme.Accent, 2, 12, textured: true);
		yes.Text = yesText; yes.AddThemeFontSizeOverride("font_size", 28);
		yes.Pressed += () => onYes();
		panel.AddChild(yes);
		var no = BattleTheme.MakeButton(new Vector2(246, 230), new Vector2(150, 64), BattleTheme.PanelDark, BattleTheme.Accent, 2, 12, textured: true);
		no.Text = "取消"; no.AddThemeFontSizeOverride("font_size", 28);
		no.Pressed += () => onNo();
		panel.AddChild(no);

		return root;
	}
}
