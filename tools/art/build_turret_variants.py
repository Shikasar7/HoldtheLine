"""Build dynamic 工造炮台 card art and 4x3 standee variants.

Raw AI sources live under the ignored ``tools/art/generated/turret_v2`` folder.
The script removes their backgrounds, combines one upper assembly with one
chassis, mounts the result on the common standee base, and writes only the
runtime assets used by Godot.
"""

from __future__ import annotations

from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw
from rembg import new_session, remove


ROOT = Path(__file__).resolve().parents[2]
SRC = ROOT / "tools" / "art" / "generated" / "turret_v2"
OUT = ROOT / "game" / "assets" / "art"
BASE_SRC = ROOT / "tools" / "art" / "generated" / "v1" / "standee_base.png"
QA_OUT = ROOT / "tools" / "art" / "out" / "turret_v2_standee_contact.png"
QA_CARD_OUT = ROOT / "tools" / "art" / "out" / "turret_v2_card_contact.png"

CARD_SIZE = (512, 768)
STANDEE_SIZE = (512, 672)

UPPERS = {
    "": "core.png",
    "autoloader": "autoloader.png",
    "grand": "grand.png",
    "grand_autoloader": "grand_autoloader.png",
}
CHASSIS = {
    "": "core.png",
    "anchor": "anchor.png",
    "tracked": "tracked.png",
}


def cutout(image: Image.Image, session) -> Image.Image:
    return remove(image.convert("RGB"), session=session)


def upper_mask(size: tuple[int, int]) -> Image.Image:
    """Soft waist seam: upper art is authoritative above the rotating collar."""
    width, height = size
    alpha = np.zeros((height, width), dtype=np.uint8)
    solid = round(height * 0.52)
    end = round(height * 0.64)
    alpha[:solid, :] = 255
    for y in range(solid, end):
        alpha[y, :] = round(255 * (end - y) / (end - solid))
    return Image.fromarray(alpha, "L")


def combine(chassis: Image.Image, upper: Image.Image | None) -> Image.Image:
    result = chassis.copy()
    if upper is None:
        return result
    layer = upper.copy()
    layer.putalpha(Image.composite(layer.getchannel("A"), Image.new("L", layer.size), upper_mask(layer.size)))
    result.alpha_composite(layer)
    return result


def make_standee(figure: Image.Image, base: Image.Image) -> Image.Image:
    bbox = figure.getbbox()
    if bbox:
        figure = figure.crop(bbox)
    cw, ch = STANDEE_SIZE
    base = base.resize((round(cw * 0.92), round(cw * 0.92 * base.height / base.width)), Image.Resampling.LANCZOS)
    feet_y = ch - round(base.height * 0.45)
    max_h = feet_y - 8
    scale = min(max_h / figure.height, cw * 0.88 / figure.width)
    figure = figure.resize((round(figure.width * scale), round(figure.height * scale)), Image.Resampling.LANCZOS)
    canvas = Image.new("RGBA", STANDEE_SIZE)
    canvas.alpha_composite(base, ((cw - base.width) // 2, ch - base.height))
    canvas.alpha_composite(figure, ((cw - figure.width) // 2, feet_y - figure.height))
    return canvas


def state_name(upper: str, chassis: str) -> str:
    suffix = "_".join(part for part in (upper, chassis) if part)
    return "uv_turret_core" + (f"_{suffix}" if suffix else "")


def save_cards() -> None:
    card_dir = OUT / "cards"
    card_dir.mkdir(parents=True, exist_ok=True)
    for upper, source in UPPERS.items():
        name = state_name(upper, "")
        image = Image.open(SRC / source).convert("RGB").resize(CARD_SIZE, Image.Resampling.LANCZOS)
        image.save(card_dir / f"{name}.png", optimize=True)


def checker(size: tuple[int, int]) -> Image.Image:
    image = Image.new("RGB", size, "#242424")
    draw = ImageDraw.Draw(image)
    tile = 24
    for y in range(0, size[1], tile):
        for x in range(0, size[0], tile):
            if (x // tile + y // tile) % 2:
                draw.rectangle((x, y, x + tile - 1, y + tile - 1), fill="#353535")
    return image


def save_card_contact() -> None:
    thumb = (256, 384)
    contact = checker((thumb[0] * len(UPPERS), thumb[1]))
    draw = ImageDraw.Draw(contact)
    for i, upper in enumerate(UPPERS):
        name = state_name(upper, "")
        card = Image.open(OUT / "cards" / f"{name}.png").convert("RGB").resize(thumb, Image.Resampling.LANCZOS)
        x = i * thumb[0]
        contact.paste(card, (x, 0))
        draw.rectangle((x, 0, x + thumb[0] - 1, 25), fill="#111111")
        draw.text((x + 6, 6), upper or "base", fill="#e8d7ad")
    QA_CARD_OUT.parent.mkdir(parents=True, exist_ok=True)
    contact.save(QA_CARD_OUT, quality=92)


def main() -> None:
    missing = [path for path in [BASE_SRC, *(SRC / file for file in set(UPPERS.values()) | set(CHASSIS.values()))] if not path.exists()]
    if missing:
        raise SystemExit("Missing turret source art:\n" + "\n".join(map(str, missing)))

    save_cards()
    save_card_contact()
    session = new_session("u2net")
    cutouts = {
        source: cutout(Image.open(SRC / source), session)
        for source in set(UPPERS.values()) | set(CHASSIS.values())
    }
    base_cutout = cutout(Image.open(BASE_SRC), session)
    if bbox := base_cutout.getbbox():
        base_cutout = base_cutout.crop(bbox)

    standee_dir = OUT / "standees"
    standee_dir.mkdir(parents=True, exist_ok=True)
    rendered: list[tuple[str, Image.Image]] = []
    for upper_name, upper_file in UPPERS.items():
        for chassis_name, chassis_file in CHASSIS.items():
            upper = None if not upper_name else cutouts[upper_file]
            merged = combine(cutouts[chassis_file], upper)
            standee = make_standee(merged, base_cutout)
            name = state_name(upper_name, chassis_name)
            standee.save(standee_dir / f"{name}.png", optimize=True)
            rendered.append((name, standee))
            print(f"wrote {name}.png")

    thumb = (256, 336)
    contact = checker((thumb[0] * len(CHASSIS), thumb[1] * len(UPPERS)))
    draw = ImageDraw.Draw(contact)
    for i, (name, standee) in enumerate(rendered):
        x = (i % len(CHASSIS)) * thumb[0]
        y = (i // len(CHASSIS)) * thumb[1]
        small = standee.resize(thumb, Image.Resampling.LANCZOS)
        contact.paste(small, (x, y), small)
        draw.rectangle((x, y, x + thumb[0] - 1, y + 25), fill="#111111")
        draw.text((x + 6, y + 6), name.removeprefix("uv_turret_core") or "base", fill="#e8d7ad")
    QA_OUT.parent.mkdir(parents=True, exist_ok=True)
    contact.save(QA_OUT, quality=92)
    print(f"wrote {QA_OUT}")


if __name__ == "__main__":
    main()
