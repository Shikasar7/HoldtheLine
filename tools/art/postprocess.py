"""美术后处理:tools/art/generated/v1 → game/assets/art。

按资产类型分流(类型查 out/prompts.json):
  unit    rembg 去背 → 立牌(主体+统一底座合成)  + 完整竖版卡面(不裁切)
  order   完整横版卡面(不裁切;结算图 result_* 除外,整图拷到 screens/)
  leader  512 圆形裁切
  ui_*    卡框/按钮板泛洪抠透明;宝石/纹章/底座 rembg 抠出;纹理/卡背直拷
  board / key_art  直拷

用法:
  python postprocess.py            # 全量
  python postprocess.py --ids wp_pup gem_cost
  python postprocess.py --cards-only  # 只更新卡面,不跑 rembg/立牌/UI
"""

from __future__ import annotations

import argparse
import json
from pathlib import Path

import numpy as np
from PIL import Image, ImageFilter
from rembg import new_session, remove

TOOLS_DIR = Path(__file__).parent
SRC = TOOLS_DIR / "generated" / "v1"
OUT = TOOLS_DIR.parent.parent / "game" / "assets" / "art"
PROMPTS = TOOLS_DIR / "out" / "prompts.json"

UNIT_FACE = (512, 768)   # 原图 2:3,保留完整构图
ORDER_FACE = (768, 512)  # 原图 3:2,保留完整构图
LEADER_SIZE = 512
STANDEE_CANVAS = (512, 672)

_session = None


def rembg_full(img: Image.Image) -> Image.Image:
    """rembg 去背,保持原尺寸。"""
    global _session
    if _session is None:
        _session = new_session("u2net")
    return remove(img, session=_session)


def cutout(img: Image.Image) -> Image.Image:
    """rembg 去背 → 裁到主体包围盒。"""
    result = rembg_full(img)
    bbox = result.getbbox()
    return result.crop(bbox) if bbox else result


def black_to_alpha(img: Image.Image, size: tuple[int, int] = (512, 512)) -> Image.Image:
    """把纯黑底特效转成直通 alpha，并等比放入固定透明画布。"""
    rgb = np.array(img.convert("RGB"), dtype=np.float32)
    peak = rgb.max(axis=2)
    alpha = np.clip((peak - 8.0) * (255.0 / 247.0), 0, 255).astype(np.uint8)
    scale = np.maximum(peak[..., None], 1.0)
    color = np.clip(rgb * (255.0 / scale), 0, 255).astype(np.uint8)
    rgba = np.dstack((color, alpha))
    effect = Image.fromarray(rgba, "RGBA")
    bbox = effect.getbbox()
    if bbox:
        effect = effect.crop(bbox)
    effect.thumbnail((round(size[0] * 0.9), round(size[1] * 0.9)), Image.LANCZOS)
    canvas = Image.new("RGBA", size, (0, 0, 0, 0))
    canvas.alpha_composite(effect, ((size[0] - effect.width) // 2,
                                    (size[1] - effect.height) // 2))
    return canvas


def flood_transparent(img: Image.Image, seeds: list[tuple[int, int]], tol: int = 14) -> Image.Image:
    """把与种子点同色的连通平坦区域(外围背景/卡框内窗)变为透明。"""
    import cv2

    rgb = np.array(img.convert("RGB"))
    h, w = rgb.shape[:2]
    keep = np.full((h, w), 255, dtype=np.uint8)
    for sx, sy in seeds:
        mask = np.zeros((h + 2, w + 2), dtype=np.uint8)
        cv2.floodFill(rgb.copy(), mask, (sx, sy), (0, 0, 0),
                      loDiff=(tol,) * 3, upDiff=(tol,) * 3,
                      flags=cv2.FLOODFILL_MASK_ONLY | cv2.FLOODFILL_FIXED_RANGE | 8)
        keep[mask[1:-1, 1:-1] == 1] = 0
    alpha = Image.fromarray(keep).filter(ImageFilter.GaussianBlur(1.2))
    out = img.convert("RGBA")
    out.putalpha(alpha)
    return out


def save(img: Image.Image, rel: str) -> None:
    path = OUT / rel
    path.parent.mkdir(parents=True, exist_ok=True)
    img.save(path, optimize=True)
    print(f"  -> {rel}  {img.size}")


def unit_face(img: Image.Image) -> Image.Image:
    """竖版单位图完整缩放,最终取景交给游戏内逐卡取景台。"""
    return img.resize(UNIT_FACE, Image.LANCZOS)


def order_face(img: Image.Image) -> Image.Image:
    """横版指令图完整缩放,不再预裁切为固定窗口比例。"""
    return img.resize(ORDER_FACE, Image.LANCZOS)


def make_standee(fig: Image.Image, base: Image.Image) -> Image.Image:
    """主体立于统一底座之上;画布竖版,供场上按高度缩放。"""
    cw, ch = STANDEE_CANVAS
    base = base.resize((int(cw * 0.92), int(cw * 0.92 * base.height / base.width)), Image.LANCZOS)
    feet_y = ch - int(base.height * 0.45)  # 脚踩在底座椭圆中心
    max_h = feet_y - 8
    scale = min(max_h / fig.height, cw * 0.86 / fig.width)
    fig = fig.resize((int(fig.width * scale), int(fig.height * scale)), Image.LANCZOS)

    canvas = Image.new("RGBA", STANDEE_CANVAS, (0, 0, 0, 0))
    canvas.alpha_composite(base, ((cw - base.width) // 2, ch - base.height))
    canvas.alpha_composite(fig, ((cw - fig.width) // 2, feet_y - fig.height))
    return canvas


def circle_crop(img: Image.Image, size: int) -> Image.Image:
    img = img.resize((size, size), Image.LANCZOS).convert("RGBA")
    yy, xx = np.mgrid[:size, :size]
    r = size / 2
    dist = np.sqrt((xx - r + 0.5) ** 2 + (yy - r + 0.5) ** 2)
    alpha = np.clip((r - dist) * 4, 0, 255).astype(np.uint8)  # 2px 抗锯齿边
    img.putalpha(Image.fromarray(alpha))
    return img


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--ids", nargs="*")
    parser.add_argument("--cards-only", action="store_true",
                        help="只重建 unit/order 卡面,跳过 rembg、立牌和其他资产")
    args = parser.parse_args()

    prompts = json.loads(PROMPTS.read_text(encoding="utf-8"))
    base = None
    if not args.cards_only:
        base = cutout(Image.open(SRC / "standee_base.png"))
        save(base.resize((256, int(256 * base.height / base.width)), Image.LANCZOS), "ui/standee_base.png")

    for pid, meta in prompts.items():
        if args.ids and pid not in args.ids:
            continue
        kind = meta["type"]
        if args.cards_only and (kind not in ("unit", "order") or pid.startswith("result_")):
            continue
        src = SRC / f"{pid}.png"
        if not src.exists():
            print(f"!! missing {pid}")
            continue
        img = Image.open(src).convert("RGB")
        print(pid, kind)

        if kind == "unit":
            save(unit_face(img), f"cards/{pid}.png")
            if not args.cards_only:
                full = rembg_full(img)
                bbox = full.getbbox() or (0, 0, img.width, img.height)
                save(make_standee(full.crop(bbox), base), f"standees/{pid}.png")
        elif kind == "order":
            if pid.startswith("result_"):
                save(img, f"screens/{pid}.png")
            else:
                save(order_face(img), f"cards/{pid}.png")
        elif kind == "leader":
            save(circle_crop(img, LEADER_SIZE), f"leaders/{pid}.png")
        elif kind == "board":
            save(img, "board/board_main.png")
        elif kind == "key_art":
            save(img, "screens/key_art_main.png")
        elif kind in ("ui_frame", "ui_button"):  # 卡框/按钮板:外围+内窗抠透明
            w, h = img.size
            seeds = [(2, 2), (w - 3, 2), (2, h - 3), (w - 3, h - 3)]
            if pid.startswith("frame_"):
                seeds.append((w // 2, int(h * 0.38)))  # 插画窗中心
            result = flood_transparent(img, seeds)
            if pid == "button_plate" and (bbox := result.getbbox()) is not None:
                result = result.crop(bbox)  # 按钮板裁到内容,便于铺满按钮
            save(result, f"ui/{pid}.png")
        elif kind == "ui_icon":
            icon = cutout(img)
            icon.thumbnail((256, 256), Image.LANCZOS)
            save(icon, f"ui/{pid}.png")
        elif kind == "ui_texture":
            save(img, f"ui/{pid}.png")
        elif kind == "fx":
            save(black_to_alpha(img), f"fx/{pid}.png")
        elif kind == "card_back":
            save(img.resize((512, 768), Image.LANCZOS), f"ui/{pid}.png")
        elif kind == "prop":
            if pid != "standee_base":
                save(img.resize((512, 512), Image.LANCZOS), f"cards/{pid}.png")
        else:
            print(f"!! unhandled type {kind} for {pid}")


if __name__ == "__main__":
    main()
