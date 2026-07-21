# UI 美化专项资产清单(配套 docs/18-UI美化改版计划;资产已生成入库并 P0–P3 实装)

风格圣经:`style_bible.json` v1(沿用,不改风格)。本清单是**新增资产**,生成/验收规则与 `asset_checklist.md` 完全一致:

- 生成尺寸只用 gpt-image-2 支持的三档:1024×1024 / 1536×1024 / 1024×1536。
- UI 类不挂锚点图,靠提示词色板词;`negative` 作为验收否决表(出现文字/水印/3D 感即打回)。
- 每项 2–3 候选;**任何一项 roll 3 轮不合格 → 降级为程序绘制,不死磕**(图标类降级为字体图形,面板类降级为 StyleBoxFlat)。
- 原图存 `tools/art/generated/v1/<id>.png`;去背/裁切由 postprocess 输出到 `game/assets/art/ui/`(图标进 `ui/icons/`),不手工裁。
- 下方"成品提示词"已按 v1 组装规则(`global_style + composition[type] + art_prompt`)拼好,可直接使用;确认定稿后再把 §3 的 JSON 并入 `style_bible.json` 的 `extra_assets` 重跑 `generate_prompts.py` 归档。

## 1. AI 生成资产(15 项)

### A. 面板/底板(4 项)

| id | 类型 | 尺寸 | 用途 | 验收要点 |
|---|---|---|---|---|
| `panel_parchment` | ui_frame | 1024×1536 | 弹出面板底(人机对战、口令、确认框) | 边框磨损自然、**中心大面积素面**(要叠文字);九宫格拉伸安全(四边纹理均匀无大图案) |
| `header_banner` | ui_button | 1536×1024 | 面板/回合横幅标题条(后处理裁中段横条) | 严格左右对称;中心素面;装饰只在两端;缩到 600×80 摆标题字不抢戏 |
| `button_plate_round` | ui_icon | 1024×1024 | 圆形图标按钮底(领袖技能、关闭、设置) | 正圆、素面中心;与现有 `button_plate` 同一钢材质感;64px 下铆钉仍隐约可辨 |
| `cell_slot` | ui_frame | 1024×1024 | 棋盘部署格刻痕框(替代半透明圆角矩形) | **极低对比**——叠在 board_main 上似"印在桌面上的模板刷痕";中心全空;四角小刻度装饰;立牌放上后完全不抢 |

各项成品提示词(生成时直接整段使用):

**panel_parchment**
> tabletop wargame illustration, painterly oil brushwork with visible strokes, weathered battlefield field-journal aesthetic, muted cool grey and warm umber palette with luminous dust accents of faded teal and old gold, matte texture, grounded military-fantasy tone, high detail, game UI asset: flat front view, perfectly symmetrical ornamental design, isolated on plain neutral dark background, crisp clean edges suitable for nine-slice scaling, no text, the inner window area left plain and empty, aged field-journal parchment sheet panel with darkened worn edges, faint creases and stains, thin restrained old-gold corner mounts, plain uninterrupted center surface, warm pale umber

**header_banner**
> tabletop wargame illustration, painterly oil brushwork with visible strokes, weathered battlefield field-journal aesthetic, muted cool grey and warm umber palette with luminous dust accents of faded teal and old gold, matte texture, grounded military-fantasy tone, high detail, game UI asset: wide landscape button plate, flat front view, perfectly symmetrical, isolated on plain neutral dark background, crisp clean edges suitable for nine-slice scaling, plain uninterrupted center surface for programmatic text, simple uniform side rails and corners, restrained decoration only at the four corners, no crest, no wings, no protruding side ornaments, no text, a very wide horizontal engraved steel and dark wood title banner plate, subtle ember-gold trim lines along the rails, restrained rivets at both ends

**button_plate_round**
> tabletop wargame illustration, painterly oil brushwork with visible strokes, weathered battlefield field-journal aesthetic, muted cool grey and warm umber palette with luminous dust accents of faded teal and old gold, matte texture, grounded military-fantasy tone, high detail, game UI icon: single centered emblem, flat front view, isolated on plain neutral dark background, bold silhouette that stays readable at 64 pixels, no text, a round riveted weathered-steel button boss with a subtle ember-gold rim ring and a plain empty center disc

**cell_slot**
> tabletop wargame illustration, painterly oil brushwork with visible strokes, weathered battlefield field-journal aesthetic, muted cool grey and warm umber palette with luminous dust accents of faded teal and old gold, matte texture, grounded military-fantasy tone, high detail, game UI asset: flat front view, perfectly symmetrical ornamental design, isolated on plain neutral dark background, crisp clean edges suitable for nine-slice scaling, no text, the inner window area left plain and empty, a square deployment slot frame stenciled into a weathered war-table surface, thin worn painted border with small corner tick marks, very low contrast, faded and scuffed, center completely empty

### B. 图标套件(11 项,统一规格)

规格:ui_icon,1024×1024 生成,去背后落 `game/assets/art/ui/icons/<id>.png`,实际使用 32–48px。
**成套验收(最重要)**:11 枚并排缩到 40px——①同一种"旧金属刻印"质感;②剪影互相可区分;③单色化后仍可辨。建议先生成 `icon_hand`、`icon_deck` 两枚定调,过图后把它们作为参考图挂着生成其余 9 枚(与宝石三连的成套做法相同)。

| id | 用途 | art_prompt(拼装尾段) |
|---|---|---|
| `icon_hand` | 资源条·手牌数 | a fanned trio of playing cards emblem stamped in worn steel |
| `icon_deck` | 资源条·牌库数 | a neat squared stack of cards emblem stamped in worn steel |
| `icon_dust` | 资源条·辉尘 | a rising swirl of luminous faded-teal dust sparks emblem, bold silhouette |
| `icon_vs_ai` | 主菜单·人机对战 | a clockwork automaton knight helmet emblem stamped in worn steel |
| `icon_hotseat` | 主菜单·双人热座 | two crossed war banners above a round table emblem stamped in worn steel |
| `icon_online` | 主菜单·联机对战 | a signal beacon tower radiating three arcs emblem stamped in worn steel |
| `icon_decks` | 主菜单·卡组管理 | a leather-bound card ledger with a buckled strap emblem stamped in worn steel |
| `icon_exit` | 主菜单·退出 | a weathered wooden signpost arrow emblem stamped in worn steel |
| `icon_swap` | 起手换牌 | two curved arrows chasing each other in a circle emblem stamped in worn steel |
| `icon_tide` | 潮汐/回合倒数 | a cresting wave over an hourglass emblem stamped in worn steel |
| `icon_settings` | 对战菜单/设置 | a heavy iron cogwheel emblem stamped in worn steel |

图标类成品提示词模板(替换尾段即可):

> tabletop wargame illustration, painterly oil brushwork with visible strokes, weathered battlefield field-journal aesthetic, muted cool grey and warm umber palette with luminous dust accents of faded teal and old gold, matte texture, grounded military-fantasy tone, high detail, game UI icon: single centered emblem, flat front view, isolated on plain neutral dark background, bold silhouette that stays readable at 64 pixels, no text, **{art_prompt}**

## 1.5 第二批:羊皮纸窗口升级件(✅ 2026-07-21 已全部生成并接入,rev4)

5 项全部入库接入:金 CTA 板(半分辨率九宫格)/小钢板(替代降采样)/横版羊皮纸(WindowPanel)/标题铭牌(WindowPanelTitled)/icon_edit(大厅改片)。原需求与提示词存档如下:

| id | 类型 | 尺寸 | 用途 | 验收要点 |
|---|---|---|---|---|
| `button_plate_gold` | ui_button | 1536×1024 | 每屏唯一主 CTA(开战/排位匹配/结束回合) | 亮暖金铜面(参考炉石"对战"按钮),素面中心;与钢板同族但一眼分清主次 |
| `icon_edit` | ui_icon | 1024×1024 | 卡组"改"小片(替代文字) | 羽笔+卡片浮雕;挂现有图标做参考图保持成套 |
| `button_plate_small` | ui_button | 1536×1024 | 小按钮/页签专用钢板 | 细单线边框+四角小铆钉,无大角饰;当前用大图降采样顶着,专图更锐 |
| `panel_parchment_wide` | ui_frame | 1536×1024 | 横版羊皮纸窗口(当前竖图横拉,角部比例略宽) | 同 panel_parchment 材质;金铜角托+细金属轨;中心大面积素面 |
| `window_title_plaque` | ui_button | 1536×1024 | 窗口标题牌(挂框顶,金字刻上面) | 深青铜圆角横牌+两颗安装铆钉;素面中心;类似"返回"金铭牌样例的气质 |

成品提示词(整段直接用):

**button_plate_gold**
> tabletop wargame illustration, painterly oil brushwork with visible strokes, weathered battlefield field-journal aesthetic, muted cool grey and warm umber palette with luminous dust accents of faded teal and old gold, matte texture, grounded military-fantasy tone, high detail, game UI asset: wide landscape button plate, flat front view, perfectly symmetrical, isolated on plain neutral dark background, crisp clean edges suitable for nine-slice scaling, plain uninterrupted center surface for programmatic text, simple uniform side rails and corners, restrained decoration only at the four corners, no crest, no wings, no protruding side ornaments, no text, a wide rectangular polished brass and warm gold button plate with riveted edges and a subtle raised bevel, bright gold face catching soft light

**icon_edit**
> tabletop wargame illustration, painterly oil brushwork with visible strokes, weathered battlefield field-journal aesthetic, muted cool grey and warm umber palette with luminous dust accents of faded teal and old gold, matte texture, grounded military-fantasy tone, high detail, game UI icon: single centered emblem, flat front view, isolated on plain neutral dark background, bold silhouette that stays readable at 64 pixels, no text, a quill pen crossing a playing card emblem stamped in worn steel

**button_plate_small**
> tabletop wargame illustration, painterly oil brushwork with visible strokes, weathered battlefield field-journal aesthetic, muted cool grey and warm umber palette with luminous dust accents of faded teal and old gold, matte texture, grounded military-fantasy tone, high detail, game UI asset: wide landscape button plate, flat front view, perfectly symmetrical, isolated on plain neutral dark background, crisp clean edges suitable for nine-slice scaling, plain uninterrupted center surface for programmatic text, simple uniform side rails and corners, restrained decoration only at the four corners, no crest, no wings, no protruding side ornaments, no text, a compact plain weathered steel button plate with one thin border line and four small corner rivets, minimal decoration

**panel_parchment_wide**
> tabletop wargame illustration, painterly oil brushwork with visible strokes, weathered battlefield field-journal aesthetic, muted cool grey and warm umber palette with luminous dust accents of faded teal and old gold, matte texture, grounded military-fantasy tone, high detail, game UI asset: flat front view, perfectly symmetrical ornamental design, isolated on plain neutral dark background, crisp clean edges suitable for nine-slice scaling, no text, the inner window area left plain and empty, a wide landscape aged parchment sheet window panel framed by ornate weathered gold-bronze corner brackets and thin metal rails, darkened worn edges, faint creases, plain uninterrupted parchment center, warm pale umber

**window_title_plaque**
> tabletop wargame illustration, painterly oil brushwork with visible strokes, weathered battlefield field-journal aesthetic, muted cool grey and warm umber palette with luminous dust accents of faded teal and old gold, matte texture, grounded military-fantasy tone, high detail, game UI asset: wide landscape button plate, flat front view, perfectly symmetrical, isolated on plain neutral dark background, crisp clean edges suitable for nine-slice scaling, plain uninterrupted center surface for programmatic text, simple uniform side rails and corners, restrained decoration only at the four corners, no crest, no wings, no protruding side ornaments, no text, an engraved dark bronze title plaque with rounded ends, a thin old-gold border line and two mounting rivets

## 2. 非 AI 资产(下载,2 项)

| 项 | 来源 | 说明 |
|---|---|---|
| 思源宋体 SC Heavy(`SourceHanSerifSC-Heavy.otf`) | Adobe/Google 官方仓库,OFL 免费商用 | 标题 Logo 字/结算大字。**取 SC 子集版**控制包体;落 `game/assets/fonts/` |
| 思源宋体 SC Bold(`SourceHanSerifSC-Bold.otf`) | 同上 | H1/H2 面板标题、回合横幅 |

## 3. style_bible.json 合入块(定稿后并入 extra_assets,重跑 generate_prompts.py 归档)

```json
[
  { "id": "panel_parchment", "type": "ui_frame", "faction": null,
    "art_prompt": "aged field-journal parchment sheet panel with darkened worn edges, faint creases and stains, thin restrained old-gold corner mounts, plain uninterrupted center surface, warm pale umber" },
  { "id": "header_banner", "type": "ui_button", "faction": null,
    "art_prompt": "a very wide horizontal engraved steel and dark wood title banner plate, subtle ember-gold trim lines along the rails, restrained rivets at both ends" },
  { "id": "button_plate_round", "type": "ui_icon", "faction": null,
    "art_prompt": "a round riveted weathered-steel button boss with a subtle ember-gold rim ring and a plain empty center disc" },
  { "id": "cell_slot", "type": "ui_frame", "faction": null,
    "art_prompt": "a square deployment slot frame stenciled into a weathered war-table surface, thin worn painted border with small corner tick marks, very low contrast, faded and scuffed, center completely empty" },
  { "id": "icon_hand", "type": "ui_icon", "faction": null,
    "art_prompt": "a fanned trio of playing cards emblem stamped in worn steel" },
  { "id": "icon_deck", "type": "ui_icon", "faction": null,
    "art_prompt": "a neat squared stack of cards emblem stamped in worn steel" },
  { "id": "icon_dust", "type": "ui_icon", "faction": null,
    "art_prompt": "a rising swirl of luminous faded-teal dust sparks emblem, bold silhouette" },
  { "id": "icon_vs_ai", "type": "ui_icon", "faction": null,
    "art_prompt": "a clockwork automaton knight helmet emblem stamped in worn steel" },
  { "id": "icon_hotseat", "type": "ui_icon", "faction": null,
    "art_prompt": "two crossed war banners above a round table emblem stamped in worn steel" },
  { "id": "icon_online", "type": "ui_icon", "faction": null,
    "art_prompt": "a signal beacon tower radiating three arcs emblem stamped in worn steel" },
  { "id": "icon_decks", "type": "ui_icon", "faction": null,
    "art_prompt": "a leather-bound card ledger with a buckled strap emblem stamped in worn steel" },
  { "id": "icon_exit", "type": "ui_icon", "faction": null,
    "art_prompt": "a weathered wooden signpost arrow emblem stamped in worn steel" },
  { "id": "icon_swap", "type": "ui_icon", "faction": null,
    "art_prompt": "two curved arrows chasing each other in a circle emblem stamped in worn steel" },
  { "id": "icon_tide", "type": "ui_icon", "faction": null,
    "art_prompt": "a cresting wave over an hourglass emblem stamped in worn steel" },
  { "id": "icon_settings", "type": "ui_icon", "faction": null,
    "art_prompt": "a heavy iron cogwheel emblem stamped in worn steel" }
]
```

## 4. 接入去向速查(生成完交给客户端接入时用)

| 资产 | 接入点 |
|---|---|
| `panel_parchment` / `header_banner` | `BattleTheme.MakePanel` 面板工厂 + 各面板标题条(P1/P2) |
| `button_plate_round` | 领袖技能按钮、关闭/设置图标按钮(P2) |
| `cell_slot` | `BattleScene` 棋盘格渲染(P2) |
| `icon_hand/deck/dust` | 对战顶栏/底栏资源胶囊(P2) |
| `icon_vs_ai/hotseat/online/decks/exit` | 主菜单按钮左侧(P1) |
| `icon_swap` | 起手换牌确认按钮(P2) |
| `icon_tide` | 回合横幅潮汐 pip 区(P2,衔接 docs/17) |
| `icon_settings` | 对战 Esc 菜单按钮(P2) |
| 思源宋体 Heavy/Bold | `BattleTheme` 字体常量(P0) |
