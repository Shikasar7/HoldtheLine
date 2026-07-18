"""Crop a transparent source to its alpha bounds and fit it to an exact canvas."""

from __future__ import annotations

import argparse
from pathlib import Path

from PIL import Image


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("input", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--width", type=int, required=True)
    parser.add_argument("--height", type=int, required=True)
    parser.add_argument("--padding", type=float, default=0.08)
    parser.add_argument("--stretch", action="store_true")
    args = parser.parse_args()

    image = Image.open(args.input).convert("RGBA")
    alpha = image.getchannel("A")
    bbox = alpha.getbbox()
    if bbox is None:
        raise SystemExit(f"no visible pixels in {args.input}")
    image = image.crop(bbox)

    usable_w = max(1, round(args.width * (1 - 2 * args.padding)))
    usable_h = max(1, round(args.height * (1 - 2 * args.padding)))
    if args.stretch:
        size = (usable_w, usable_h)
    else:
        scale = min(usable_w / image.width, usable_h / image.height)
        size = (max(1, round(image.width * scale)), max(1, round(image.height * scale)))
    image = image.resize(size, Image.Resampling.LANCZOS)

    canvas = Image.new("RGBA", (args.width, args.height), (0, 0, 0, 0))
    offset = ((args.width - image.width) // 2, (args.height - image.height) // 2)
    canvas.alpha_composite(image, offset)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    canvas.save(args.output)


if __name__ == "__main__":
    main()
