# 卡面文案工具链

卡面/规则详情里显示的 BBCode 文案由两个脚本维护。它们都以 **每张卡的源头 `text`**
（`game/data/cards/*.json` 里每个卡对象的 `text` 字段，手写散文）为内容真源，产物写入
`game/data/card_text_formatting.json`（`卡id -> { bbcode, generated? }`）。

渲染规则（`game/scripts/CardTextFormatting.cs`）：某卡有 override 就用其 `bbcode`，否则回退到纯 `text`（无配色）。

```
game/data/cards/*.json 的 text（手写，含数值/关键词的散文描述）
        │
        ├── generate_card_text_formatting.py  ── 排版：给关键词/伤害/数值套色、断行
        │                                         → 写 generated 条目
        │
        └── sync_manual_numbers.py            ── 只把 text 里的数字回填进「手动」条目，
                                                  不碰任何标签/颜色/字体/换行
        ▼
game/data/card_text_formatting.json → 卡面渲染
```

> **没有「数据 → 文案」的自动生成**：`text` 是手写的，改卡的数值/关键词时要**手动把 `text` 一起改**。
> 两个脚本只负责「排版」和「把 text 的数字同步到手动格式里」，都不会替你造句。

---

## `generated` 标记：谁归谁管

- **`"generated": true`** —— 由 `generate_card_text_formatting.py` 生成，重跑它会刷新。
- **无该字段（手动 override）** —— 开发者手写/在游戏内卡面编辑器改过（一改就掉标记）。
  生成器**永远跳过**它，以保住你加的颜色、字体、下划线、换行等美化。
  代价：它内嵌的数字不再随卡数据变化 —— 这正是 `sync_manual_numbers.py` 要补的洞。

---

## `sync_manual_numbers.py` —— 只同步数字，不动格式

把每张**手动** override 卡的源头 `text` 里的数字，就地回填进它的 BBCode：**只改数字**，
其余字节（标签、颜色、字体、换行，以及文件的 C# ASCII 转义 `\uXXXX` / `+`）一律不动，
因此和游戏内 C# 卡面编辑器共存、diff 最小。

```bash
python tools/cards/sync_manual_numbers.py            # 干跑：只报告要改什么，不写盘（默认）
python tools/cards/sync_manual_numbers.py --write    # 应用
```

判定逻辑（按卡）：对齐 = BBCode 可见数字与 `text` 数字**逐个一一对应**。

- **sync**：对应但有值变了 → 只重写差异的那几位数字。
- **clean**：对应且值全一致 → 无需改动（以后 `text` 改数值会自动同步）。
- **conflict**：数字**个数对不上** → **不动，只报警**。常见两因：
  1. 卡面用了中文数字（如「两次」而 `text` 是「2 次」）；
  2. 卡面**有意精简/改写**了 `text`（例：关键词值靠角标显示、正文不重复；把 `+0/+1` 改写成「回复 1 点」）。
     这类保持手动是对的，需要人工判断，脚本不猜。
- **no-source**：该卡在 `game/data/cards` 里没有 `text`（或没找到）。

`--write` 落盘后会重新 `json.loads` 整份文件、并逐张复核数字，任一校验失败即中止不写。
文件受 git 跟踪，可 `git checkout -- game/data/card_text_formatting.json` 回退。

---

## `generate_card_text_formatting.py` —— 从 text 套排版

读取所有卡的 `text`，给关键词（黄）、伤害词（红）、数值 `+N/+N`（青）套色、按句断行，
生成 `generated` 条目；**手动条目原样保留**。

```bash
python tools/cards/generate_card_text_formatting.py            # 干跑：打印 manual/generated/empty 计数
python tools/cards/generate_card_text_formatting.py --write    # 写盘
```

> **⚠ 编码坑**：本脚本落盘用 `ensure_ascii=False`（原始 UTF-8），而当前 `card_text_formatting.json`
> 是游戏内 C# 编辑器写的 **ASCII 转义**（`\uXXXX` 大写、`+` 转 `+`）。跑一次 `--write` 会把整份文件
> 翻成 UTF-8 编码 → 巨大 diff，且下次 C# 编辑器保存又翻回去。**只在确需刷新大量 generated 条目时**用它，
> 并接受一次性重排；日常改数值请优先走 `sync_manual_numbers.py`（就地改、零编码扰动）。

---

## 日常工作流：改一张卡

1. 在 `game/data/cards/<阵营>.json` 里改这张卡的**数值字段 + `text` 描述**（两者保持一致）。
2. 同步数字进手动格式：
   ```bash
   python tools/cards/sync_manual_numbers.py           # 预览
   python tools/cards/sync_manual_numbers.py --write    # 应用
   ```
3. 若该卡是 `generated`（非手动），改动会在你下次跑生成器时刷新；或按需精修成手动 override。
