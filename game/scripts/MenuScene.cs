using Godot;

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

        var title = BattleTheme.MakeLabel("守 线", 96, BattleTheme.TextMain, HorizontalAlignment.Center);
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

        AddButton("双人热座对战", new Vector2(660, 672), BattleTheme.AccentSoft, StartHotseat);
        AddButton("退出", new Vector2(660, 792), BattleTheme.PanelDark, () => GetTree().Quit());
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
