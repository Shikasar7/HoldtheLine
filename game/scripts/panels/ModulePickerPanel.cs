using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using HoldTheLine.Rules.Cards;
using HoldTheLine.Rules.Commands;
using HoldTheLine.Rules.Hosting;

namespace HoldTheLine.Game;

/// <summary>
/// 掘世匠会 模块选择弹层 (docs/20) — a one-shot modal for the two module plays that pick an IN-装 module rather
/// than a board target: 满位顶替 (choose which module to 报废, <see cref="ShowScrap"/>) and 镜像工坊 (choose which
/// module to copy, <see cref="ShowMirror"/>; a 开关类 copy warns 无增益, §S9b). Each tile maps to a pre-built legal
/// <see cref="PlayCardCommand"/> (carrying ReplacedModuleCardId / TargetModuleCardId); clicking it hands that
/// command back via <paramref name="submit"/>. The panel never reads the scene. Modeled on <see cref="SacrificePanel"/>.
/// </summary>
public static class ModulePickerPanel
{
    /// <summary>满位顶替 (docs/20 §S2): the turret is full (5/5); pick an in-装 module to scrap for the new one.
    /// Candidates each carry a distinct ReplacedModuleCardId.</summary>
    public static bool ShowScrap(Control overlayLayer, CardDatabase cards, SfxBank sfx, PlayerView view,
        IReadOnlyList<Command> candidates, Action<Command> submit)
    {
        var entries = candidates.OfType<PlayCardCommand>()
            .Where(p => p.ReplacedModuleCardId is not null)
            .Select(p => new Entry(p.ReplacedModuleCardId!, p, false))
            .DistinctBy(e => e.ModuleId).ToList();
        if (entries.Count == 0) return false;
        Show(overlayLayer, cards, sfx, "炮台已满 · 选择报废模块", "报废件仍留在历史池,可被战地重构取回。", entries, submit);
        return true;
    }

    /// <summary>镜像工坊 (docs/20 §S9b): copy one in-装 module (无视同名唯一). Candidates each carry a distinct
    /// TargetModuleCardId. A 开关类 module (无数值加成) gets a 无增益 warning — the copy still装, but adds nothing.</summary>
    public static bool ShowMirror(Control overlayLayer, CardDatabase cards, SfxBank sfx, PlayerView view,
        IReadOnlyList<Command> candidates, Action<Command> submit)
    {
        var entries = candidates.OfType<PlayCardCommand>()
            .Where(p => p.TargetModuleCardId is not null)
            .Select(p => new Entry(p.TargetModuleCardId!, p, IsSwitchOnly(cards, p.TargetModuleCardId!)))
            .DistinctBy(e => e.ModuleId).ToList();
        if (entries.Count == 0) return false;
        Show(overlayLayer, cards, sfx, "镜像工坊 · 选择复制模块", "复制一件在装模块并装配(无视同名唯一)。", entries, submit);
        return true;
    }

    /// <summary>A module whose spec has no numeric contribution — copying it via 镜像 grants no benefit (开关不叠, §S9b).</summary>
    private static bool IsSwitchOnly(CardDatabase cards, string moduleId) =>
        cards.TryGet(moduleId, out var def) && def.Module is { } m
            && m.Atk == 0 && m.Hp == 0 && m.Range == 0 && m.Move == 0;

    private readonly record struct Entry(string ModuleId, Command Command, bool Warn);

    private static void Show(Control overlayLayer, CardDatabase cards, SfxBank sfx,
        string title, string subtitle, List<Entry> entries, Action<Command> submit)
    {
        const float pw = 1180, tilesTop = 176, tileH = 118, tileGapY = 14;
        const int perRow = 3;
        int rows = (entries.Count + perRow - 1) / perRow;
        float tilesBottom = tilesTop + rows * tileH + (rows - 1) * tileGapY;
        float cancelY = tilesBottom + 26;
        float ph = cancelY + 64 + 78;
        float px = (BattleTheme.ScreenW - pw) / 2f, py = (BattleTheme.ScreenH - ph) / 2f;
        var bronze = Color.FromHtml("8f7247");
        var charred = Color.FromHtml("2a2520");
        var warnCol = Color.FromHtml("c9a24b");

        var overlay = new Button { Name = "__module_panel", Flat = true };
        overlay.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        overlay.AddThemeStyleboxOverride("normal", BattleTheme.Box(new Color(0, 0, 0, 0.72f), null, 0, 0));

        void Close()
        {
            if (overlayLayer.GetNodeOrNull("__module_panel") is { } p) { overlayLayer.RemoveChild(p); p.QueueFree(); }
        }

        var panel = new Panel
        {
            Position = new Vector2(px, py),
            Size = new Vector2(pw, ph),
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        panel.AddThemeStyleboxOverride("panel", (StyleBox?)BattleTheme.ParchmentPanel() ?? BattleTheme.Box(BattleTheme.PanelDark, BattleTheme.Accent, 3, 12));
        overlay.AddChild(panel);

        var plaque = BattleTheme.TitlePlaque(new Vector2(220, 24), new Vector2(pw - 440, 92));
        bool hasPlaque = plaque is not null;
        if (plaque is not null) panel.AddChild(plaque);
        var titleLbl = hasPlaque
            ? BattleTheme.MakeOutlinedLabel(title, 30, BattleTheme.TextMain, HorizontalAlignment.Center)
            : BattleTheme.MakeLabel(title, 30, BattleTheme.InkMain, HorizontalAlignment.Center);
        titleLbl.AddThemeFontOverride("font", BattleTheme.HeadingFont);
        titleLbl.Position = new Vector2(90, 44); titleLbl.Size = new Vector2(pw - 180, 44);
        panel.AddChild(titleLbl);

        var sub = BattleTheme.MakeLabel(subtitle, 18, BattleTheme.InkDim, HorizontalAlignment.Center);
        sub.Position = new Vector2(90, 122); sub.Size = new Vector2(pw - 180, 28);
        panel.AddChild(sub);

        const float tw = 356, gapX = 20;
        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            cards.TryGet(e.ModuleId, out var md);
            int col = i % perRow, row = i / perRow;
            int rowCount = Math.Min(perRow, entries.Count - row * perRow);
            float rowW = rowCount * tw + (rowCount - 1) * gapX;
            float bx = (pw - rowW) / 2f + col * (tw + gapX), by = tilesTop + row * (tileH + tileGapY);
            var b = BattleTheme.MakeButton(new Vector2(bx, by), new Vector2(tw, tileH), charred, e.Warn ? warnCol : bronze, e.Warn ? 3 : 2, 10);

            var artFrame = new Panel { Position = new Vector2(12, 12), Size = new Vector2(78, 92), MouseFilter = Control.MouseFilterEnum.Ignore };
            artFrame.AddThemeStyleboxOverride("panel", BattleTheme.Box(new Color(0.08f, 0.07f, 0.06f, 1f), bronze, 2, 6));
            b.AddChild(artFrame);
            if (BattleTheme.Tex($"cards/{e.ModuleId}.png") is { } art)
                b.AddChild(CardView.ArtWindow(art, e.ModuleId, new Vector2(16, 16), new Vector2(70, 84)));

            var nameLbl = BattleTheme.MakeOutlinedLabel(md?.Name ?? e.ModuleId, 22, BattleTheme.TextMain, HorizontalAlignment.Left);
            nameLbl.Position = new Vector2(106, 14); nameLbl.Size = new Vector2(tw - 118, 30);
            b.AddChild(nameLbl);
            var desc = BattleTheme.MakeLabel(BattleTheme.BodyText(md?.Text ?? ""), 15, BattleTheme.TextDim, HorizontalAlignment.Left);
            desc.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            desc.Position = new Vector2(106, 44); desc.Size = new Vector2(tw - 118, 44);
            b.AddChild(desc);
            if (e.Warn)
            {
                var warnLbl = BattleTheme.MakeLabel("镜像无增益(开关类)", 14, warnCol, HorizontalAlignment.Left);
                warnLbl.AddThemeFontOverride("font", BattleTheme.UiFontBold);
                warnLbl.Position = new Vector2(106, 90); warnLbl.Size = new Vector2(tw - 118, 22);
                b.AddChild(warnLbl);
            }

            var cmd = e.Command;
            b.Pressed += () => { sfx.Play("button"); Close(); submit(cmd); };
            panel.AddChild(b);
        }

        var cancel = BattleTheme.MakeButton(new Vector2(pw / 2f - 130, cancelY), new Vector2(260, 62), BattleTheme.PanelDark, bronze, 2, 10, textured: true);
        cancel.Text = "取消"; cancel.AddThemeFontSizeOverride("font_size", 22);
        cancel.Pressed += () => { sfx.Play("button"); Close(); };
        panel.AddChild(cancel);

        overlayLayer.AddChild(overlay);
    }
}
