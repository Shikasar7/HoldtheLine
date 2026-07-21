# AI 美术流水线(tools/art)

规划见 [docs/03 §9](../../docs/03-（已实现）PC原型实现计划.md)。人工环节只有一个:四选一筛选。

## 文件

| 文件 | 作用 |
|------|------|
| `asset_checklist.md` | **原型版全量资产清单与验收标准**(67 项,gpt-image-2 生成) |
| `style_bible.json` | 风格圣经:全局风格块、按卡型的构图规则、三阵营色彩/元素词表、负面提示词、探索包选卡、锚点图列表。**所有风格调整只改这里。** |
| `generate_prompts.py` | 提示词生成器:卡牌/领袖 JSON + 风格圣经 → 完整提示词 |
| `out/prompts.json` | 全量提示词(生成产物,不入库) |
| `out/exploration_pack.md` | 风格探索包,贴给 ChatGPT 手动出图用 |

## 当前阶段:批量生成(风格圣经 v1 已锁定,锚点图在 anchors/)

按 `asset_checklist.md` 的顺序和验收标准,用 `out/prompts.json` 的提示词 + 3 张锚点图作参考,gpt-image-2 逐批生成,产物存 `generated/v1/<id>.png`。

## 后处理(已建):postprocess.py

`python postprocess.py`(全量)或 `--ids <id>...`。依赖 `pip install rembg onnxruntime opencv-python-headless pillow`。
按 prompts.json 的 type 分流:unit → rembg 去背+底座合成立牌 & 完整缩放为 512×768 卡面;order → 完整缩放为 768×512 卡面;
leader → 512 圆裁;卡框/按钮板 → 泛洪抠透明(固定范围,种子=四角+插画窗中心);宝石/纹章 → rembg 抠出缩 256。
输出 `game/assets/art/{cards,standees,leaders,board,ui,screens}/<id>.png`。

只更新卡面可运行 `python postprocess.py --cards-only`，不会触发 rembg，也不会重建立牌/UI。卡面不再预裁切，最终构图由开发模式的「插画取景」逐卡保存。

Godot 侧已接入(BattleTheme.Tex 按 ID 加载,缺图自动回退几何占位):棋盘底图、立牌、手牌卡框+插画+宝石、
领袖头像、卡背、结束回合按钮板、结算图、主菜单 Key Art+纹章。重新生成美术后只需重跑 postprocess + 编辑器重扫。

## 后续(待建)

- `batch_generate.py`:正式版经 ComfyUI HTTP API 批量出图(Flux.1-dev fp8 + 锚点图风格参考)
