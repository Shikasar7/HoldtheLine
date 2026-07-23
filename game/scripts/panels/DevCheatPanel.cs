using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using HoldTheLine.Rules.Cards;

namespace HoldTheLine.Game;

/// <summary>
/// 开发者测试修改器面板 (dev-only) — 战斗内 Ctrl+Alt+0 切换 (见 <see cref="BattleScene"/>)。两项作弊：
///   • 一键回费 — 把当前操作方的辉尘补满。
///   • 从牌库取牌 — 点选牌库里任意一张，直接进手牌 (手牌满 9 时拒绝)。
///
/// 纯 UI + push 模型：面板不主动拉数据。<see cref="BattleScene"/> 用 <see cref="SetDeck"/> 灌入牌库列表，
/// 用 <see cref="SetStatus"/> 写状态行。这样单机(同步)与联机(异步，服务器回包后才拿到牌库)共用同一套界面：
/// 联机时牌库先显示"加载中…"，收到 dev_deck_list 后再 SetDeck。按钮点击走 onRefill/onTutor 回调。
/// </summary>
public sealed class DevCheatPanel
{
    private const float PanelW = 660f;
    private const float PanelH = 800f;

    private readonly Control _host;
    private readonly SfxBank _sfx;
    private readonly CardDatabase _cards;

    private Control? _root;
    private VBoxContainer? _list;
    private Label? _status;
    private Action? _onRefill;
    private Action<int>? _onTutor;

    public bool IsOpen => _root != null;

    public DevCheatPanel(Control host, SfxBank sfx, CardDatabase cards)
    {
        _host = host;
        _sfx = sfx;
        _cards = cards;
    }

    /// <summary>Open if closed, close if open. On open the deck list starts empty ("加载中…"); the caller
    /// pushes it via <see cref="SetDeck"/> (immediately offline, on the server reply online).</summary>
    public void Toggle(Action onRefill, Action<int> onTutor)
    {
        if (IsOpen) { Close(); return; }
        Open(onRefill, onTutor);
    }

    private void Open(Action onRefill, Action<int> onTutor)
    {
        _onRefill = onRefill;
        _onTutor = onTutor;

        var panel = OverlayKit.Overlay(_host, new Vector2(PanelW, PanelH), out var root, onBackdrop: Close);
        _root = root;

        var title = BattleTheme.MakeOutlinedLabel("开发者修改器 · 测试", 34, BattleTheme.Accent, HorizontalAlignment.Center);
        title.Position = new Vector2(0, 22);
        title.Size = new Vector2(PanelW, 46);
        panel.AddChild(title);

        var hint = BattleTheme.MakeLabel("Ctrl+Alt+0 或点击空白处关闭 · 联机功能由服务器开关控制", 18, BattleTheme.TextDim, HorizontalAlignment.Center);
        hint.Position = new Vector2(0, 70);
        hint.Size = new Vector2(PanelW, 26);
        panel.AddChild(hint);

        var refill = BattleTheme.MakeButton(new Vector2(60, 112), new Vector2(PanelW - 120, 62), BattleTheme.AccentSoft, BattleTheme.Accent, 2, 12, textured: true);
        refill.Text = "⚡ 一键回费 (辉尘补满)";
        refill.AddThemeFontSizeOverride("font_size", 26);
        refill.Pressed += () => { _sfx.Play("button"); _onRefill?.Invoke(); };
        panel.AddChild(refill);

        _status = BattleTheme.MakeLabel("", 19, BattleTheme.CostColor, HorizontalAlignment.Center);
        _status.Position = new Vector2(0, 184);
        _status.Size = new Vector2(PanelW, 26);
        panel.AddChild(_status);

        var listTitle = BattleTheme.MakeLabel("牌库 — 点击一张加入手牌", 21, BattleTheme.TextDim, HorizontalAlignment.Center);
        listTitle.Position = new Vector2(0, 218);
        listTitle.Size = new Vector2(PanelW, 28);
        panel.AddChild(listTitle);

        var scroll = new ScrollContainer
        {
            Position = new Vector2(40, 254),
            Size = new Vector2(PanelW - 80, PanelH - 254 - 84),
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        panel.AddChild(scroll);
        _list = new VBoxContainer { CustomMinimumSize = new Vector2(PanelW - 80, 0) };
        _list.AddThemeConstantOverride("separation", 6);
        scroll.AddChild(_list);

        var close = BattleTheme.MakeButton(new Vector2((PanelW - 200) / 2f, PanelH - 68), new Vector2(200, 54), BattleTheme.PanelDark, BattleTheme.Accent, 2, 12, textured: true);
        close.Text = "关闭";
        close.AddThemeFontSizeOverride("font_size", 24);
        close.Pressed += () => { _sfx.Play("button"); Close(); };
        panel.AddChild(close);

        SetPlaceholder("加载中…");
    }

    /// <summary>Set the status line under the buttons. Safe to call while closed (ignored).</summary>
    public void SetStatus(string text)
    {
        if (_status is { } s) s.Text = text;
    }

    /// <summary>(Re)populate the deck list. Safe to call while closed (ignored). Sorted by cost then name;
    /// clicking a row invokes onTutor(EntityId).</summary>
    public void SetDeck(IReadOnlyList<(int EntityId, string CardId)> deck)
    {
        if (_list is null)
            return;
        ClearList();

        var rows = deck
            .Select(d => (d.EntityId, Def: _cards.TryGet(d.CardId, out var def) ? def : null, d.CardId))
            .OrderBy(r => r.Def?.Cost ?? 99)
            .ThenBy(r => r.Def?.Name ?? r.CardId)
            .ToList();

        if (rows.Count == 0)
        {
            SetPlaceholder("牌库已空");
            return;
        }

        foreach (var r in rows)
        {
            string name = r.Def?.Name ?? r.CardId;
            int cost = r.Def?.Cost ?? 0;
            string type = TypeLabel(r.Def?.Type);
            var btn = BattleTheme.MakeButton(Vector2.Zero, new Vector2(PanelW - 96, 48), BattleTheme.PanelDark, BattleTheme.AccentSoft, 1, 8);
            btn.CustomMinimumSize = new Vector2(PanelW - 96, 48);
            btn.Text = $"   {cost} 费    {name}    · {type}";
            btn.Alignment = HorizontalAlignment.Left;
            btn.AddThemeFontSizeOverride("font_size", 20);
            int id = r.EntityId;
            btn.Pressed += () => { _sfx.Play("button"); _onTutor?.Invoke(id); };
            _list.AddChild(btn);
        }
    }

    public void Close()
    {
        _root?.QueueFree();
        _root = null;
        _list = null;
        _status = null;
        _onRefill = null;
        _onTutor = null;
    }

    private void SetPlaceholder(string text)
    {
        if (_list is null)
            return;
        ClearList();
        var label = BattleTheme.MakeLabel(text, 20, BattleTheme.TextDim, HorizontalAlignment.Center);
        label.CustomMinimumSize = new Vector2(PanelW - 96, 40);
        _list.AddChild(label);
    }

    // RemoveChild is immediate (QueueFree defers), so clearing then re-adding never shows a duplicated frame.
    private void ClearList()
    {
        if (_list is null)
            return;
        foreach (var child in _list.GetChildren().ToArray())
        {
            _list.RemoveChild(child);
            child.QueueFree();
        }
    }

    private static string TypeLabel(CardType? t) => t switch
    {
        CardType.Unit => "单位",
        CardType.Order => "指令",
        CardType.Structure => "阵地",
        CardType.Equipment => "装备",
        _ => "?",
    };
}
