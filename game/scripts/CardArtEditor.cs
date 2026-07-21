using Godot;
using HoldTheLine.Rules.Cards;

namespace HoldTheLine.Game;

/// <summary>Developer-facing visual editor for <see cref="CardArtFraming"/>.</summary>
public partial class CardArtEditor : Control
{
    private const float FaceW = 480f;
    private const float FaceH = 672f;
    private static readonly Vector2 FacePos = new(600, 208);
    private static readonly Vector2 FaceSize = new(FaceW, FaceH);
    private static readonly Vector2 FaceArtPos = FacePos + new Vector2(FaceW * 0.165f, FaceH * 0.152f);
    private static readonly Vector2 FaceArtSize = new(FaceW * 0.675f, FaceH * 0.536f);

    private CardDatabase _cards = null!;
    private CardDefinition _selected = null!;
    private Action? _onClose;
    private Control _faceHost = null!;
    private Control _dragPane = null!;
    private Panel _aperture = null!;
    private Vector2 _currentArtSize = FaceArtSize;
    private VBoxContainer _cardList = null!;
    private Label _cardHeading = null!, _values = null!, _status = null!;
    private HSlider _zoom = null!, _x = null!, _y = null!;
    private LineEdit _search = null!;
    private bool _syncing;
    private bool _dragging;

    public static void Open(Control host, CardDatabase cards, Action? onClose = null)
    {
        var editor = new CardArtEditor { _cards = cards, _onClose = onClose };
        host.AddChild(editor);
        editor.Build();
    }

    private void Build()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;

        var bg = new ColorRect { Color = new Color(0.025f, 0.03f, 0.032f, 0.985f), MouseFilter = MouseFilterEnum.Ignore };
        bg.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(bg);

        var title = BattleTheme.MakeTitle("插画取景台", 38, BattleTheme.TextMain);
        title.Position = new Vector2(42, 26); title.Size = new Vector2(420, 52);
        AddChild(title);
        var hint = BattleTheme.MakeLabel("拖动画面平移 · 滚轮缩放 · 配置按卡牌 ID 保存", 17, BattleTheme.TextDim);
        hint.Position = new Vector2(44, 78); hint.Size = new Vector2(620, 28);
        AddChild(hint);

        var close = MakeButton("关闭", new Vector2(1726, 28), new Vector2(148, 54), Close);
        AddChild(close);

        BuildCardRail();
        BuildPreview();
        BuildControls();

        _selected = _cards.All.OrderBy(c => c.Faction).ThenBy(c => c.Cost).ThenBy(c => c.Name, StringComparer.Ordinal).First();
        Select(_selected);
    }

    private void BuildCardRail()
    {
        var panel = PanelAt(new Vector2(36, 124), new Vector2(500, 910), BattleTheme.PanelDark);
        AddChild(panel);
        var label = BattleTheme.MakeOutlinedLabel("选择卡牌", 22, BattleTheme.Accent);
        label.Position = new Vector2(20, 18); label.Size = new Vector2(200, 32); panel.AddChild(label);

        _search = new LineEdit { PlaceholderText = "按名称或 ID 筛选", Position = new Vector2(20, 62), Size = new Vector2(460, 48) };
        _search.AddThemeFontSizeOverride("font_size", 18);
        _search.TextChanged += _ => RebuildList();
        panel.AddChild(_search);

        var scroll = new ScrollContainer { Position = new Vector2(18, 126), Size = new Vector2(464, 760) };
        scroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
        panel.AddChild(scroll);
        _cardList = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _cardList.AddThemeConstantOverride("separation", 4);
        scroll.AddChild(_cardList);
        RebuildList();
    }

    private void RebuildList()
    {
        foreach (Node child in _cardList.GetChildren()) child.QueueFree();
        string needle = _search.Text.Trim();
        foreach (var def in _cards.All
                     .Where(c => needle.Length == 0 || c.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)
                         || c.Id.Contains(needle, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(c => c.Faction).ThenBy(c => c.Cost).ThenBy(c => c.Name, StringComparer.Ordinal))
        {
            var local = def;
            var b = MakeButton($"{FactionMark(def.Faction)}  {def.Cost}费  {def.Name}", Vector2.Zero, new Vector2(448, 42), () => Select(local));
            b.Alignment = HorizontalAlignment.Left;
            b.AddThemeFontSizeOverride("font_size", 17);
            b.TooltipText = def.Id;
            _cardList.AddChild(b);
        }
    }

    private void BuildPreview()
    {
        var stage = PanelAt(new Vector2(568, 124), new Vector2(544, 910), new Color(0.045f, 0.047f, 0.044f, 1f));
        AddChild(stage);
        _cardHeading = BattleTheme.MakeOutlinedLabel("", 24, BattleTheme.TextMain, HorizontalAlignment.Center);
        _cardHeading.Position = new Vector2(16, 16); _cardHeading.Size = new Vector2(512, 38); stage.AddChild(_cardHeading);

        _faceHost = new Control { Position = FacePos - stage.Position, Size = FaceSize, MouseFilter = MouseFilterEnum.Ignore };
        stage.AddChild(_faceHost);

        // An invisible input pane exactly over the frame's illustration aperture.
        _dragPane = new Control
        {
            Position = FaceArtPos - stage.Position,
            Size = FaceArtSize,
            MouseDefaultCursorShape = CursorShape.Drag,
            TooltipText = "拖拽平移插画；滚轮缩放",
            MouseFilter = MouseFilterEnum.Stop,
        };
        _dragPane.GuiInput += OnArtInput;
        stage.AddChild(_dragPane);

        _aperture = new Panel { Position = FaceArtPos - stage.Position, Size = FaceArtSize, MouseFilter = MouseFilterEnum.Ignore };
        var outline = new StyleBoxFlat { BgColor = Colors.Transparent, BorderColor = new Color(0.83f, 0.66f, 0.31f, 0.7f) };
        outline.SetBorderWidthAll(2); _aperture.AddThemeStyleboxOverride("panel", outline);
        stage.AddChild(_aperture);
    }

    private void BuildControls()
    {
        var panel = PanelAt(new Vector2(1144, 124), new Vector2(730, 910), BattleTheme.PanelDark);
        AddChild(panel);
        var heading = BattleTheme.MakeOutlinedLabel("取景参数", 26, BattleTheme.Accent);
        heading.Position = new Vector2(34, 28); heading.Size = new Vector2(300, 38); panel.AddChild(heading);
        var explain = BattleTheme.MakeLabel("1.00× 是铺满窗口，可在 0.50×–3.00× 间缩放。插画位于卡框下层；\n超出中央窗口的部分由当前阵营卡框遮住，未覆盖的位置保持为空。", 18, BattleTheme.TextDim);
        explain.Position = new Vector2(34, 78); explain.Size = new Vector2(650, 70); panel.AddChild(explain);

        _zoom = AddSlider(panel, "缩放", 170, CardArtFraming.MinZoom, CardArtFraming.MaxZoom, 0.01);
        _x = AddSlider(panel, "水平位置", 270, -1, 1, 0.01);
        _y = AddSlider(panel, "垂直位置", 370, -1, 1, 0.01);
        _zoom.ValueChanged += _ => ApplyControls();
        _x.ValueChanged += _ => ApplyControls();
        _y.ValueChanged += _ => ApplyControls();

        _values = BattleTheme.MakeOutlinedLabel("", 18, BattleTheme.TextMain);
        _values.Position = new Vector2(36, 454); _values.Size = new Vector2(640, 36); panel.AddChild(_values);

        var reset = MakeButton("重置这张", new Vector2(34, 526), new Vector2(210, 58), ResetSelected);
        panel.AddChild(reset);
        var save = MakeButton("保存全部取景", new Vector2(264, 526), new Vector2(250, 58), Save);
        BattleTheme.SetButtonBg(save, BattleTheme.AccentSoft);
        panel.AddChild(save);

        _status = BattleTheme.MakeOutlinedLabel("", 18, BattleTheme.HpColor);
        _status.Position = new Vector2(36, 606); _status.Size = new Vector2(650, 60); panel.AddChild(_status);

        var note = BattleTheme.MakeLabel($"保存位置\n{CardArtFraming.ResourcePath}\n\n快捷操作\n拖拽：平移\n滚轮：缩放\n双击：重置当前卡", 17, BattleTheme.TextDim);
        note.Position = new Vector2(36, 700); note.Size = new Vector2(650, 180); panel.AddChild(note);
    }

    private HSlider AddSlider(Control parent, string caption, float y, double min, double max, double step)
    {
        var label = BattleTheme.MakeOutlinedLabel(caption, 20, BattleTheme.TextMain);
        label.Position = new Vector2(34, y); label.Size = new Vector2(150, 32); parent.AddChild(label);
        var slider = new HSlider { Position = new Vector2(184, y), Size = new Vector2(500, 38), MinValue = min, MaxValue = max, Step = step };
        parent.AddChild(slider);
        return slider;
    }

    private void Select(CardDefinition def)
    {
        _selected = def;
        var f = CardArtFraming.Get(def.Id);
        _syncing = true;
        _zoom.Value = f.Zoom; _x.Value = f.OffsetX; _y.Value = f.OffsetY;
        _syncing = false;
        _status.Text = "";
        RefreshPreview();
    }

    private void ApplyControls()
    {
        if (_syncing || _selected == null) return;
        CardArtFraming.Set(_selected.Id, new CardArtFrame
        {
            Zoom = (float)_zoom.Value,
            OffsetX = (float)_x.Value,
            OffsetY = (float)_y.Value,
        });
        _status.Text = "已修改，记得保存";
        RefreshPreview();
    }

    private void RefreshPreview()
    {
        foreach (Node child in _faceHost.GetChildren()) child.QueueFree();
        _faceHost.AddChild(CardView.BuildFace(_selected, FaceSize));
        var frameTex = BattleTheme.Tex($"ui/frame_{_selected.Faction}.png") ?? BattleTheme.Tex("ui/frame_neutral.png");
        if (frameTex != null)
        {
            var bounds = CardView.FrameWindowBounds(frameTex);
            var localPos = bounds.Position * FaceSize;
            _currentArtSize = bounds.Size * FaceSize;
            _dragPane.Position = _faceHost.Position + localPos;
            _dragPane.Size = _currentArtSize;
            _aperture.Position = _faceHost.Position + localPos;
            _aperture.Size = _currentArtSize;
        }
        var f = CardArtFraming.Get(_selected.Id);
        _cardHeading.Text = $"{_selected.Name}  ·  {_selected.Id}";
        _values.Text = $"缩放 {f.Zoom:0.00}×     水平 {f.OffsetX:+0.00;-0.00;0.00}     垂直 {f.OffsetY:+0.00;-0.00;0.00}";
    }

    private void OnArtInput(InputEvent e)
    {
        if (e is InputEventMouseButton mb)
        {
            if (mb.ButtonIndex == MouseButton.Left)
            {
                _dragging = mb.Pressed;
                if (mb.DoubleClick) ResetSelected();
            }
            else if (mb.Pressed && mb.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
            {
                _zoom.Value = Mathf.Clamp((float)_zoom.Value + (mb.ButtonIndex == MouseButton.WheelUp ? 0.05f : -0.05f),
                    CardArtFraming.MinZoom, CardArtFraming.MaxZoom);
            }
            AcceptEvent();
        }
        else if (e is InputEventMouseMotion motion && _dragging)
        {
            _x.Value = Mathf.Clamp((float)_x.Value + motion.Relative.X * 2f / _currentArtSize.X, -1f, 1f);
            _y.Value = Mathf.Clamp((float)_y.Value + motion.Relative.Y * 2f / _currentArtSize.Y, -1f, 1f);
            AcceptEvent();
        }
    }

    private void ResetSelected()
    {
        CardArtFraming.Reset(_selected.Id);
        Select(_selected);
        _status.Text = "已重置，记得保存";
    }

    private void Save()
    {
        if (CardArtFraming.Save(out var error))
        {
            _status.AddThemeColorOverride("font_color", BattleTheme.HpColor);
            _status.Text = "✓ 已保存，所有卡面界面立即使用新取景";
        }
        else
        {
            _status.AddThemeColorOverride("font_color", BattleTheme.DangerColor);
            _status.Text = $"保存失败：{error}";
        }
    }

    private void Close()
    {
        _onClose?.Invoke();
        QueueFree();
    }

    private static Panel PanelAt(Vector2 pos, Vector2 size, Color color)
    {
        var panel = new Panel { Position = pos, Size = size };
        panel.AddThemeStyleboxOverride("panel", BattleTheme.Box(color, new Color(0.48f, 0.39f, 0.24f, 0.8f), 1, 10));
        return panel;
    }

    private static Button MakeButton(string text, Vector2 pos, Vector2 size, Action pressed)
    {
        var b = BattleTheme.MakeButton(pos, size, BattleTheme.PanelDark, BattleTheme.Accent, 1, 7, textured: true);
        b.Text = text; b.CustomMinimumSize = size; b.AddThemeFontSizeOverride("font_size", 20); b.Pressed += pressed;
        return b;
    }

    private static string FactionMark(string faction) => faction switch
    {
        "iron_vow" => "铁誓",
        "wildpack" => "游群",
        "duskweaver" => "教团",
        "undervault" => "匠会",
        _ => "中立",
    };
}
