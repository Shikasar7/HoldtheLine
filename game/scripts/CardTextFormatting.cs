using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Godot;

namespace HoldTheLine.Game;

/// <summary>Presentation-only, per-card BBCode used by card faces and rules-detail panels.</summary>
public sealed record CardTextFormat
{
    public string Bbcode { get; init; } = "";
    /// <summary>True for entries produced by the bulk style generator. Any editor change replaces the
    /// record with a manual override so future generator runs preserve the developer's adjustment.</summary>
    public bool Generated { get; init; }
}

public static partial class CardTextFormatting
{
    public const string ResourcePath = "res://data/card_text_formatting.json";
    public const string RulesWhite = "#ede5d7";
    public const string KeywordYellow = "#e0b24a";
    public const string AccentTeal = "#55c7b8";
    public const string DangerRed = "#df7467";
    public const string BuffGreen = "#87c878";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
    };

    private static Dictionary<string, CardTextFormat>? _formats;

    /// <summary>Returns an override when one exists, otherwise the card's original plain description.</summary>
    public static string GetBbcode(string cardId, string fallbackText)
    {
        EnsureLoaded();
        return _formats!.TryGetValue(cardId, out var format) ? format.Bbcode : fallbackText;
    }

    public static bool HasOverride(string cardId)
    {
        EnsureLoaded();
        return _formats!.ContainsKey(cardId);
    }

    public static bool IsGenerated(string cardId)
    {
        EnsureLoaded();
        return _formats!.TryGetValue(cardId, out var format) && format.Generated;
    }

    public static void Set(string cardId, string bbcode, string fallbackText)
    {
        EnsureLoaded();
        string normalized = bbcode.Replace("\r\n", "\n").Trim();
        if (normalized == fallbackText.Trim()) _formats!.Remove(cardId);
        else _formats![cardId] = new CardTextFormat { Bbcode = normalized };
    }

    public static void Reset(string cardId)
    {
        EnsureLoaded();
        _formats!.Remove(cardId);
    }

    public static bool Save(out string error)
    {
        EnsureLoaded();
        try
        {
            string path = ProjectSettings.GlobalizePath(ResourcePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(_formats, JsonOptions) + System.Environment.NewLine);
            error = "";
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static RichTextLabel MakeRichLabel(string cardId, string fallbackText, int fontSize,
        Color defaultColor, HorizontalAlignment alignment = HorizontalAlignment.Center)
    {
        string content = GetBbcode(cardId, fallbackText);
        var label = new RichTextLabel
        {
            BbcodeEnabled = true,
            Text = Align(content, alignment),
            FitContent = false,
            ScrollActive = false,
            AutowrapMode = TextServer.AutowrapMode.Arbitrary,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        label.AddThemeFontOverride("normal_font", BattleTheme.UiFont);
        label.AddThemeFontOverride("bold_font", BattleTheme.UiFontBold);
        label.AddThemeFontOverride("italics_font", BattleTheme.UiFontItalic);
        label.AddThemeFontOverride("bold_italics_font", BattleTheme.UiFontBoldItalic);
        label.AddThemeFontSizeOverride("normal_font_size", fontSize);
        label.AddThemeFontSizeOverride("bold_font_size", fontSize);
        label.AddThemeFontSizeOverride("italics_font_size", fontSize);
        label.AddThemeFontSizeOverride("bold_italics_font_size", fontSize);
        label.AddThemeColorOverride("default_color", defaultColor);
        return label;
    }

    public static string PlainText(string bbcode) => BbcodeTag().Replace(bbcode, "");

    private static string Align(string text, HorizontalAlignment alignment) => alignment switch
    {
        HorizontalAlignment.Left => text,
        HorizontalAlignment.Right => $"[right]{text}[/right]",
        _ => $"[center]{text}[/center]",
    };

    private static void EnsureLoaded()
    {
        if (_formats != null) return;
        _formats = new Dictionary<string, CardTextFormat>(StringComparer.Ordinal);
        try
        {
            if (!Godot.FileAccess.FileExists(ResourcePath)) return;
            var loaded = JsonSerializer.Deserialize<Dictionary<string, CardTextFormat>>(
                Godot.FileAccess.GetFileAsString(ResourcePath), JsonOptions);
            if (loaded != null)
                _formats = new Dictionary<string, CardTextFormat>(loaded, StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            GD.PushWarning($"CardTextFormatting: could not load {ResourcePath}: {ex.Message}");
        }
    }

    [GeneratedRegex(@"\[(?:/?[a-zA-Z_]+)(?:=[^\]]*)?\]")]
    private static partial Regex BbcodeTag();
}
