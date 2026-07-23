#!/usr/bin/env python3
"""Sync the NUMBERS in manual card-text overrides from each card's source ``text`` — without
touching a single formatting tag.

Background
----------
``card_text_formatting.json`` holds per-card BBCode. Entries carrying ``generated: true`` are owned
by ``generate_card_text_formatting.py`` (re-run it to refresh them). Entries WITHOUT that flag are
*manual overrides*: a developer hand-added colours / fonts / underlines / line breaks that the
generator must never clobber. The generator therefore skips them forever — which also means their
embedded numbers (stats, damage, keyword values, caps) stop tracking the card's data.

This tool closes that gap. For every manual override it lines up the visible digit runs in the
BBCode against the digit runs in the card's source ``game/data/cards/*.json`` ``text`` and, when the
structure matches, rewrites ONLY the differing digits — in place, in the file's exact byte encoding
(the C# editor writes ``\\u002B`` / ``\\uXXXX``; digits are plain ASCII, so we edit just those). All
tags, colours and layout are preserved verbatim.

Safety
------
* Default is a dry run (no flag): it reports what WOULD change; it writes nothing. ``--write`` applies.
* Only manual overrides are considered; generated entries and non-overridden cards are left alone.
* A card is synced only when its BBCode and its ``text`` expose the SAME number of digit runs. If the
  counts differ (a clause was added/removed, or a value was written as a Chinese numeral like 两), the
  card is reported as a CONFLICT and left untouched for a human to reconcile.
* ``--write`` re-parses the whole file as JSON afterwards and re-checks every touched card; if either
  verification fails it aborts without saving. The file is git-tracked, so ``git checkout`` reverts.

Usage
-----
    python tools/cards/sync_manual_numbers.py            # dry run (report only)
    python tools/cards/sync_manual_numbers.py --write    # apply
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
CARDS_DIR = ROOT / "game" / "data" / "cards"
FORMATTING = ROOT / "game" / "data" / "card_text_formatting.json"


def load_source_texts() -> dict[str, str]:
    texts: dict[str, str] = {}
    for path in sorted(CARDS_DIR.glob("*.json")):
        for card in json.loads(path.read_text(encoding="utf-8")):
            texts[card["id"]] = card.get("text", "")
    return texts


def source_digit_runs(text: str) -> list[str]:
    """Digit runs in the plain source text, in order. Signs are layout, not synced — only the
    magnitude (``\\d+``) is compared, so a +1 -> +2 buff is a single-digit edit that keeps its sign."""
    return re.findall(r"\d+", text)


def find_bbcode_literal(raw: str, card_id: str) -> tuple[int, int] | None:
    """Return (start, end) offsets of a card's BBCode string CONTENT (between the quotes) in the raw
    file text, or None. Walks the JSON string body so escaped quotes don't end it early."""
    m = re.search(r'"' + re.escape(card_id) + r'"\s*:\s*\{\s*"bbcode"\s*:\s*"', raw)
    if not m:
        return None
    start = i = m.end()
    while i < len(raw):
        c = raw[i]
        if c == "\\":
            i += 2
            continue
        if c == '"':
            return (start, i)
        i += 1
    return None


def visible_digit_runs(lit: str) -> list[tuple[int, int, str]]:
    """Digit runs in a BBCode literal's VISIBLE text, as (start, end, value) spans relative to lit.

    Skips ``[...]`` tags wholesale (their digits are markup: colours, font paths) and skips escape
    sequences (``\\uXXXX`` hex, ``\\n`` etc.) so only genuine on-screen digits are collected."""
    runs: list[tuple[int, int, str]] = []
    i, n = 0, len(lit)
    while i < n:
        c = lit[i]
        if c == "[":  # bbcode tag — plain-ASCII delimited; skip to its close
            j = lit.find("]", i)
            i = (j + 1) if j != -1 else n
            continue
        if c == "\\":  # escape sequence: \uXXXX (6) or \n \" \\ \/ (2)
            i += 6 if (i + 1 < n and lit[i + 1] == "u") else 2
            continue
        if c.isdigit():
            k = i
            while k < n and lit[k].isdigit():
                k += 1
            runs.append((i, k, lit[i:k]))
            i = k
            continue
        i += 1
    return runs


class Result:
    def __init__(self) -> None:
        self.synced: list[tuple[str, list[tuple[str, str]]]] = []   # card_id, [(old, new), ...]
        self.clean: list[str] = []
        self.conflict: list[tuple[str, list[str], list[str]]] = []  # card_id, bb_runs, src_runs
        self.no_source: list[str] = []
        self.edits: list[tuple[int, int, str]] = []                 # (abs_start, abs_end, new_digits)


def analyze(raw: str, data: dict, sources: dict[str, str]) -> Result:
    r = Result()
    for card_id, entry in data.items():
        if entry.get("generated", False):
            continue  # owned by generate_card_text_formatting.py
        src = sources.get(card_id)
        if src is None or not src.strip():
            r.no_source.append(card_id)
            continue
        span = find_bbcode_literal(raw, card_id)
        if span is None:
            r.no_source.append(card_id)
            continue
        lit_start, lit_end = span
        lit = raw[lit_start:lit_end]
        bb_runs = visible_digit_runs(lit)
        src_runs = source_digit_runs(src)

        bb_values = [v for _, _, v in bb_runs]
        if len(bb_values) != len(src_runs):
            r.conflict.append((card_id, bb_values, src_runs))
            continue

        changes = [(bb_values[i], src_runs[i]) for i in range(len(bb_values)) if bb_values[i] != src_runs[i]]
        if not changes:
            r.clean.append(card_id)
            continue
        r.synced.append((card_id, changes))
        for (s, e, val), new in zip(bb_runs, src_runs):
            if val != new:
                r.edits.append((lit_start + s, lit_start + e, new))
    return r


def apply_edits(raw: str, edits: list[tuple[int, int, str]]) -> str:
    for start, end, new in sorted(edits, key=lambda t: t[0], reverse=True):
        raw = raw[:start] + new + raw[end:]
    return raw


def main() -> int:
    ap = argparse.ArgumentParser(description="Sync numbers from source text into manual BBCode overrides.")
    ap.add_argument("--write", action="store_true", help="apply the changes (default: dry-run report)")
    args = ap.parse_args()

    raw = FORMATTING.read_text(encoding="utf-8")
    data = json.loads(raw)
    sources = load_source_texts()
    r = analyze(raw, data, sources)

    manual_total = sum(1 for e in data.values() if not e.get("generated", False))
    print(f"manual overrides: {manual_total}  |  sync: {len(r.synced)}  clean: {len(r.clean)}  "
          f"conflict: {len(r.conflict)}  no-source: {len(r.no_source)}")

    if r.synced:
        print("\n-- would sync (numbers only, formatting preserved) --")
        for card_id, changes in r.synced:
            pretty = ", ".join(f"{o}->{n}" for o, n in changes)
            print(f"  {card_id}: {pretty}")

    if r.conflict:
        print("\n-- conflict (digit-run count differs; left untouched, needs a manual look) --")
        for card_id, bb, src in r.conflict:
            print(f"  {card_id}: bbcode[{','.join(bb) or '-'}] vs text[{','.join(src) or '-'}]")

    if not args.write:
        print("\n(dry run -- nothing written. re-run with --write to apply.)")
        return 0

    if not r.edits:
        print("\nnothing to write.")
        return 0

    new_raw = apply_edits(raw, r.edits)

    # verify: still valid JSON, and every synced card now matches its source digit runs
    try:
        new_data = json.loads(new_raw)
    except json.JSONDecodeError as ex:
        print(f"\nABORT: edit produced invalid JSON ({ex}); nothing written.", file=sys.stderr)
        return 1
    for card_id, _ in r.synced:
        # compare on the parsed (unescaped) string: tags are stripped, visible digits are plain
        got = re.findall(r"\d+", strip_tags(new_data[card_id]["bbcode"]))
        want = source_digit_runs(sources[card_id])
        if got != want:
            print(f"\nABORT: post-check mismatch on {card_id} (got {got}, want {want}); nothing written.",
                  file=sys.stderr)
            return 1

    FORMATTING.write_text(new_raw, encoding="utf-8")
    print(f"\nwrote {FORMATTING.relative_to(ROOT)} -- {len(r.synced)} cards, {len(r.edits)} number(s) updated.")
    return 0


def strip_tags(bbcode: str) -> str:
    """Visible text of an unescaped BBCode string (tags removed) — for the post-write verification."""
    return re.sub(r"\[[^\]]*\]", "", bbcode)


if __name__ == "__main__":
    raise SystemExit(main())
