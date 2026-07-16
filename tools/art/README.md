# AI 美术流水线(tools/art)

规划见 [docs/03 §9](../../docs/03-PC原型实现计划.md)。人工环节只有一个:四选一筛选。

## 文件

| 文件 | 作用 |
|------|------|
| `style_bible.json` | 风格圣经:全局风格块、按卡型的构图规则、三阵营色彩/元素词表、负面提示词、探索包选卡、锚点图列表。**所有风格调整只改这里。** |
| `generate_prompts.py` | 提示词生成器:卡牌/领袖 JSON + 风格圣经 → 完整提示词 |
| `out/prompts.json` | 全量提示词(生成产物,不入库) |
| `out/exploration_pack.md` | 风格探索包,贴给 ChatGPT 手动出图用 |

## 当前阶段:风格探索(风格圣经 v1-draft)

1. `python generate_prompts.py`
2. 把 `out/exploration_pack.md` 里 5 段提示词逐个贴给 ChatGPT,每个出 2~4 变体
3. 并排看:像不像同一个游戏?不满意 → 改 `style_bible.json` 的风格块 → 重新生成 → 再试
4. 定稿后:version 改为 `v1`,选 3~5 张锚点图存入 `anchors/` 目录并登记到 `anchors.images`

## 后续(待建)

- `batch_generate.py`:经 ComfyUI HTTP API 批量出图(Flux.1-dev fp8 + 锚点图风格参考),每卡 4 候选
- `postprocess.py`:rembg 去背 → 立牌裁剪 + 底座合成 → 卡面 4:3 裁切 → 按 ID 输出到 `game/assets/cards/`
- Godot 侧按 ID 加载,缺图回退几何占位
