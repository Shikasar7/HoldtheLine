using Godot;

namespace HoldTheLine.Game;

/// <summary>
/// Layout constants, the placeholder palette, and small factory helpers for the battle UI.
/// Placeholder art per plan §8: solid panels + labels, no external assets. "War-map table" tone.
/// </summary>
public static class BattleTheme
{
    public const int Cols = 5;
    public const int Rows = 4;

    public const float CellW = 150f;
    public const float CellH = 128f;
    public const float Gap = 10f;

    public const float ScreenW = 1920f;
    public const float ScreenH = 1080f;

    public const float BoardW = Cols * CellW + (Cols - 1) * Gap;   // 790
    public const float BoardH = Rows * CellH + (Rows - 1) * Gap;   // 542
    public const float BoardLeft = (ScreenW - BoardW) / 2f;        // 565
    public const float BoardTop = 196f;

    // Palette (辉尘 teal is the only "magic" accent).
    public static readonly Color Background = Color.FromHtml("221f1a");
    public static readonly Color CellEmpty = Color.FromHtml("39342c");
    public static readonly Color HomeRowP0 = Color.FromHtml("2f3a44"); // player deploy row tint
    public static readonly Color HomeRowP1 = Color.FromHtml("43322a"); // opponent deploy row tint
    public static readonly Color SeatColor0 = Color.FromHtml("5a86b0"); // player — steel blue
    public static readonly Color SeatColor1 = Color.FromHtml("b06a4a"); // opponent — ochre red
    public static readonly Color Accent = Color.FromHtml("37b0a0");     // 辉尘 highlight
    public static readonly Color AccentSoft = Color.FromHtml("2a6f68");
    public static readonly Color AtkColor = Color.FromHtml("e0b24a");
    public static readonly Color HpColor = Color.FromHtml("6fae5a");
    public static readonly Color CostColor = Color.FromHtml("54a0c8");
    public static readonly Color TextMain = Color.FromHtml("ece4d6");
    public static readonly Color TextDim = Color.FromHtml("9a9283");
    public static readonly Color DangerColor = Color.FromHtml("d05a4a");
    public static readonly Color PanelDark = Color.FromHtml("2c2822");

    // ---------- fonts (bundled Source Han Sans SC, OFL; falls back to system YaHei if missing) ----------

    public static readonly Font UiFont = LoadUiFont("res://assets/fonts/SourceHanSansSC-Regular.otf", 400);
    public static readonly Font UiFontBold = LoadUiFont("res://assets/fonts/SourceHanSansSC-Bold.otf", 700);

    private static Font LoadUiFont(string path, int weight) =>
        ResourceLoader.Exists(path)
            ? GD.Load<Font>(path)
            : new SystemFont { FontNames = ["Microsoft YaHei UI", "Microsoft YaHei"], FontWeight = weight };

    /// <summary>Strip the trailing 。 when the text is a single sentence (ability one-liners read cleaner bare).</summary>
    public static string BodyText(string text) =>
        text.EndsWith('。') && text.IndexOf('。') == text.Length - 1 ? text[..^1] : text;

    // ---------- AI art loading (missing file → null → caller falls back to flat placeholder) ----------

    public const string ArtRoot = "res://assets/art";

    public static Texture2D? Tex(string relPath)
    {
        string path = $"{ArtRoot}/{relPath}";
        return ResourceLoader.Exists(path) ? GD.Load<Texture2D>(path) : null;
    }

    public static TextureRect Art(Texture2D tex, Vector2 pos, Vector2 size,
        TextureRect.StretchModeEnum stretch = TextureRect.StretchModeEnum.KeepAspectCovered)
    {
        // ExpandMode must be set BEFORE Size: while the default (KeepSize) is active,
        // the texture dictates the minimum size and a smaller Size assignment gets clamped.
        return new TextureRect
        {
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = stretch,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Texture = tex,
            Position = pos,
            Size = size,
        };
    }

    /// <summary>Bold label readable on top of artwork (dark outline).</summary>
    public static Label MakeOutlinedLabel(string text, int size, Color color, HorizontalAlignment align = HorizontalAlignment.Left)
    {
        var label = MakeLabel(text, size, color, align);
        label.AddThemeFontOverride("font", UiFontBold);
        label.AddThemeColorOverride("font_outline_color", new Color(0.08f, 0.07f, 0.05f, 0.92f));
        label.AddThemeConstantOverride("outline_size", 6);
        return label;
    }

    public static Vector2 CellPos(int col, int row) =>
        new(BoardLeft + col * (CellW + Gap), BoardTop + (Rows - 1 - row) * (CellH + Gap));

    public static Color SeatColor(int seat) => seat == 0 ? SeatColor0 : SeatColor1;

    public static Label MakeLabel(string text, int size, Color color, HorizontalAlignment align = HorizontalAlignment.Left)
    {
        var label = new Label { Text = text };
        label.AddThemeFontOverride("font", UiFont);
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_color", color);
        label.HorizontalAlignment = align;
        label.VerticalAlignment = VerticalAlignment.Center;
        label.MouseFilter = Control.MouseFilterEnum.Ignore;
        return label;
    }

    public static StyleBoxFlat Box(Color bg, Color? border = null, int borderWidth = 0, int radius = 8)
    {
        var sb = new StyleBoxFlat
        {
            BgColor = bg,
            CornerRadiusTopLeft = radius,
            CornerRadiusTopRight = radius,
            CornerRadiusBottomLeft = radius,
            CornerRadiusBottomRight = radius,
        };
        if (border is { } b && borderWidth > 0)
        {
            sb.BorderColor = b;
            sb.SetBorderWidthAll(borderWidth);
        }
        return sb;
    }

    /// <summary>A clickable region styled as a flat panel (used for cells, standees, cards, leaders).</summary>
    public static Button MakeButton(Vector2 pos, Vector2 size, Color bg, Color? border = null, int borderWidth = 0, int radius = 8)
    {
        var btn = new Button
        {
            Position = pos,
            Size = size,
            Flat = false, // draw the overridden "normal" stylebox (Flat=true suppresses it)
            ClipText = false,
        };
        var normal = Box(bg, border, borderWidth, radius);
        btn.AddThemeStyleboxOverride("normal", normal);
        btn.AddThemeStyleboxOverride("hover", Box(bg.Lightened(0.06f), border, borderWidth, radius));
        btn.AddThemeStyleboxOverride("pressed", Box(bg.Darkened(0.06f), border, borderWidth, radius));
        btn.AddThemeStyleboxOverride("focus", Box(bg, border, borderWidth, radius));
        btn.AddThemeStyleboxOverride("disabled", Box(bg.Darkened(0.35f), (border ?? bg).Darkened(0.35f), borderWidth, radius));
        btn.AddThemeFontOverride("font", UiFontBold);
        btn.AddThemeColorOverride("font_color", TextMain);
        btn.AddThemeColorOverride("font_disabled_color", TextDim);
        btn.AddThemeColorOverride("font_outline_color", new Color(0.08f, 0.07f, 0.05f, 0.85f));
        btn.AddThemeConstantOverride("outline_size", 4);
        return btn;
    }

    public static void SetButtonBg(Button btn, Color bg, Color? border = null, int borderWidth = 0, int radius = 8)
    {
        btn.AddThemeStyleboxOverride("normal", Box(bg, border, borderWidth, radius));
        btn.AddThemeStyleboxOverride("hover", Box(bg.Lightened(0.06f), border, borderWidth, radius));
        btn.AddThemeStyleboxOverride("pressed", Box(bg.Darkened(0.06f), border, borderWidth, radius));
        btn.AddThemeStyleboxOverride("focus", Box(bg, border, borderWidth, radius));
        btn.AddThemeStyleboxOverride("disabled", Box(bg.Darkened(0.35f), (border ?? bg).Darkened(0.35f), borderWidth, radius));
    }
}
