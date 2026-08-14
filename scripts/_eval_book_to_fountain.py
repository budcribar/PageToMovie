#!/usr/bin/env python3
"""
Run prompts/book_to_fountain.txt against sample books via xAI Grok.
Saves Fountain + structural/fidelity eval reports.

Usage (repo root, XAI_API_KEY set):
  python scripts/_eval_book_to_fountain.py --all
  python scripts/_eval_book_to_fountain.py --only "Gift of the Magi"
  python scripts/_eval_book_to_fountain.py --only Brick --rerun
"""
from __future__ import annotations

import argparse
import json
import os
import re
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path
from typing import Any, Dict, List, Optional, Tuple

ROOT = Path(__file__).resolve().parents[1]
PROMPT_PATH = ROOT / "prompts" / "book_to_fountain.txt"
OUT_DIR = ROOT / "projects" / "_prompt_eval"
FOUNTAIN_DIR = OUT_DIR / "fountain"
LOG_DIR = OUT_DIR / "logs"
XAI_API_BASE = "https://api.x.ai/v1"
MODEL = "grok-4.5"
MAX_BOOK_CHARS = 28_000  # production trims ~32k; leave room for system prompt

BOOKS = [
    {
        "id": "gift_of_the_magi",
        "title": "The Gift of the Magi",
        "path": Path(r"C:\Users\budcr\Downloads\The_Gift_of_the_Magi.txt"),
        "must_include": ["Della", "Jim", "hair", "watch", "comb", "chain"],
        "must_not_as_heading": ["STORY", "PAGE "],
        "kind_hint": "short_story",
    },
    {
        "id": "yellow_wallpaper",
        "title": "The Yellow Wallpaper",
        "path": Path(r"C:\Users\budcr\Downloads\The_Yellow_Wallpaper.txt"),
        "must_include": ["wallpaper", "yellow"],
        "must_not_as_heading": ["STORY", "PAGE "],
        "kind_hint": "short_story",
    },
    {
        "id": "tell_tale_heart",
        "title": "The Tell-Tale Heart",
        "path": Path(r"C:\Users\budcr\Downloads\tell-tale-heart.txt"),
        "must_include": ["heart", "eye"],
        "iconic": ["old man", "vulture", "police", "floor", "mad"],
        "must_not_as_heading": ["STORY", "PAGE "],
        "kind_hint": "short_story",
    },
    {
        "id": "brick_steel",
        "title": "Brick & Steel",
        "path": Path(r"C:\Users\budcr\Downloads\Brick-&-Steel.txt"),
        "must_include": ["Brick", "Steel"],
        "must_not_as_heading": ["STORY"],
        "kind_hint": "already_fountain",
    },
    {
        "id": "christmas_carol",
        "title": "A Christmas Carol",
        "path": Path(r"C:\Users\budcr\Downloads\A_Christmas_Carol.txt"),
        "must_include": ["Scrooge", "Marley", "Christmas", "Cratchit"],
        "iconic": ["Past", "Present", "Tiny Tim", "Yet to Come", "Future"],
        "must_not_as_heading": ["STORY", "PAGE "],
        "kind_hint": "novella",
    },
    {
        "id": "alice",
        "title": "Alice's Adventures in Wonderland",
        "path": Path(r"C:\Users\budcr\Downloads\Alices_Adventures_in_Wonderland.txt"),
        "must_include": ["Alice", "rabbit"],
        "iconic": ["Caterpillar", "Cheshire", "Hatter", "Queen"],
        "must_not_as_heading": ["STORY", "PAGE "],
        "kind_hint": "novel",
    },
    {
        "id": "jungle_book",
        "title": "The Jungle Book",
        "path": Path(r"C:\Users\budcr\Downloads\The_Jungle_Book.txt"),
        "must_include": ["Mowgli", "wolf"],
        "iconic": ["Bagheera", "Baloo", "Shere"],
        "must_not_as_heading": ["STORY", "PAGE "],
        "kind_hint": "novel",
    },
    {
        "id": "frankenstein",
        "title": "Frankenstein",
        "path": Path(r"C:\Users\budcr\Downloads\Frankenstein.txt"),
        "must_include": ["Frankenstein"],
        "iconic": ["creature", "monster", "laboratory", "Elizabeth", "Walton"],
        "must_not_as_heading": ["STORY", "PAGE "],
        "kind_hint": "novel",
    },
    {
        "id": "dracula",
        "title": "Dracula",
        "path": Path(r"C:\Users\budcr\Downloads\Dracula.txt"),
        "must_include": ["Dracula", "Harker"],
        "iconic": ["Mina", "Lucy", "Van Helsing", "castle"],
        "must_not_as_heading": ["STORY", "PAGE "],
        "kind_hint": "novel",
    },
]


def smart_excerpt(text: str, max_chars: int) -> str:
    """Keep start / middle / end so long novels still expose climax + resolution."""
    if len(text) <= max_chars:
        return text
    head_b = int(max_chars * 0.40)
    mid_b = int(max_chars * 0.28)
    tail_b = max_chars - head_b - mid_b - 240
    if tail_b < 2000:
        return text[:max_chars] + "\n\n[[Book text truncated for length.]]\n"
    mid_center = len(text) // 2
    mid_start = max(0, min(len(text) - mid_b, mid_center - mid_b // 2))
    return (
        text[:head_b].rstrip()
        + "\n\n[[… middle of book omitted for length …]]\n\n"
        + text[mid_start : mid_start + mid_b].strip()
        + "\n\n[[… later chapters omitted for length …]]\n\n"
        + text[-tail_b:].lstrip()
        + "\n\n[[Book excerpted (start/middle/end) — adapt a complete short film covering the full arc present across these parts. Do not invent missing chapters.]]\n"
    )


def strip_gutenberg(text: str) -> str:
    text = text.replace("\r\n", "\n").replace("\r", "\n")
    # Start after common START markers
    m = re.search(r"\*\*\*\s*START OF (THIS|THE) PROJECT GUTENBERG EBOOK[^*\n]*\*\*\*", text, re.I)
    if m:
        text = text[m.end() :]
    m = re.search(r"\*\*\*\s*END OF (THIS|THE) PROJECT GUTENBERG EBOOK", text, re.I)
    if m:
        text = text[: m.start()]
    # Drop long license tails if still present
    m = re.search(r"\nEnd of (the )?Project Gutenberg", text, re.I)
    if m:
        text = text[: m.start()]
    return text.strip()


def estimate_minutes(words: int, kind: str) -> int:
    if kind == "already_fountain":
        return 10
    if kind == "short_story":
        return max(8, min(25, words // 120))
    if kind == "novella":
        return max(15, min(45, words // 140))
    # novel — short-film adaptation band for our pipeline
    return max(20, min(40, words // 200 if words < 20000 else 35))


def load_prompt(minutes: int) -> str:
    body = PROMPT_PATH.read_text(encoding="utf-8")
    return body.replace("{{TOTAL_RUNTIME_MINUTES}}", str(minutes))


def build_user(title: str, author_hint: str, minutes: int, page_count: int, book: str) -> str:
    return "\n".join(
        [
            f"TOTAL_RUNTIME_MINUTES = {minutes}",
            "",
            f"Project title hint: {title}",
            f"Author hint: {author_hint}",
            f"Book page count (approx): {page_count}",
            "",
            "Write the Fountain screenplay only (see system prompt).",
            "Respect --- PAGE N --- markers for page tags on each scene when present.",
            "If there are no PAGE markers, omit page tags (do not invent pages).",
            "",
            "BOOK_TEXT:",
            book,
        ]
    )


def xai_chat(system: str, user: str, temperature: float = 0.2, timeout: int = 600) -> str:
    api_key = os.environ.get("XAI_API_KEY")
    if not api_key:
        raise RuntimeError("XAI_API_KEY is not set")
    payload = {
        "model": MODEL,
        "temperature": temperature,
        "messages": [
            {"role": "system", "content": system},
            {"role": "user", "content": user},
        ],
    }
    data = json.dumps(payload).encode("utf-8")
    req = urllib.request.Request(
        f"{XAI_API_BASE}/chat/completions",
        data=data,
        headers={
            "Authorization": f"Bearer {api_key}",
            "Content-Type": "application/json",
        },
        method="POST",
    )
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            raw = resp.read().decode("utf-8")
    except urllib.error.HTTPError as e:
        body = e.read().decode(errors="ignore") if hasattr(e, "read") else ""
        raise RuntimeError(f"xAI HTTP {e.code}: {body[:1200]}") from e
    result = json.loads(raw)
    choices = result.get("choices") or []
    if not choices:
        raise RuntimeError(f"No choices: {raw[:500]}")
    content = choices[0].get("message", {}).get("content") or ""
    if isinstance(content, list):
        parts = []
        for c in content:
            if isinstance(c, dict):
                parts.append(c.get("text") or "")
            else:
                parts.append(str(c))
        content = "\n".join(parts)
    return str(content).strip()


def strip_fences(text: str) -> str:
    text = text.strip()
    if text.startswith("```"):
        text = re.sub(r"^```(?:fountain|text|markdown)?\s*", "", text, flags=re.I)
        text = re.sub(r"\s*```\s*$", "", text)
    return text.strip()


def evaluate_fountain(fountain: str, book: Dict[str, Any], book_text: str) -> Dict[str, Any]:
    issues: List[str] = []
    notes: List[str] = []
    lines = fountain.replace("\r\n", "\n").split("\n")

    has_title = bool(re.search(r"(?im)^Title:\s*\S", fountain))
    scene_heads = [
        ln
        for ln in lines
        if re.match(r"^(INT|EXT|EST|I/E)[\./ ]", ln.strip(), re.I)
    ]
    bad_heads = [
        h
        for h in scene_heads
        if re.search(r"\bSTORY\b", h, re.I) or re.search(r"\bPAGE\s+\d+", h, re.I)
    ]
    chars = re.findall(r"(?m)^([A-Z][A-Z0-9 &'.\-]{1,40})$", fountain)
    # filter scene-like
    chars = [c for c in chars if not re.match(r"^(INT|EXT|EST|I/E)\b", c, re.I)]
    has_narrator = any(c == "NARRATOR" for c in chars)
    page_tags = len(re.findall(r"(?im)^=\s*pages?\s+\d+", fountain)) + len(
        re.findall(r"\[\[\s*page\s+\d+\s*\]\]", fountain, re.I)
    )
    has_page_markers_in_book = bool(re.search(r"---\s*PAGE\s+\d+\s*---", book_text, re.I))
    looks_json = fountain.strip().startswith("{") or '"schema_version"' in fountain[:500]
    dump_count = len(re.findall(r"(?im)^INT\.\s+STORY\s+-\s+PAGE\s+\d+", fountain))

    if not has_title:
        issues.append("missing_title_page")
    if looks_json:
        issues.append("emitted_json")
    if len(scene_heads) < 1:
        issues.append("no_scene_headings")
    if bad_heads:
        issues.append(f"bad_scene_headings:{len(bad_heads)}")
        notes.append("bad heads: " + " | ".join(bad_heads[:5]))
    if dump_count >= 2:
        issues.append("story_page_dump_pattern")
    if not chars and len(scene_heads) > 0:
        notes.append("no_character_cues_detected")
    if has_page_markers_in_book and page_tags == 0:
        issues.append("missing_page_tags_when_book_has_pages")
    if not has_page_markers_in_book and page_tags > 0:
        # Invented pages — soft issue for novels
        issues.append("invented_page_tags_without_book_markers")
    if len(fountain) < 200:
        issues.append("output_too_short")

    missing_keys = []
    for k in book.get("must_include") or []:
        if not re.search(re.escape(k), fountain, re.I):
            missing_keys.append(k)
    if missing_keys:
        issues.append("missing_fidelity_keys:" + ",".join(missing_keys))

    iconic = book.get("iconic") or []
    iconic_hits = [k for k in iconic if re.search(re.escape(k), fountain, re.I)]
    iconic_miss = [k for k in iconic if k not in iconic_hits]
    if iconic and len(iconic_hits) < max(1, (len(iconic) + 1) // 2):
        issues.append("weak_iconic_coverage:" + ",".join(iconic_miss))
        notes.append(f"iconic hits={iconic_hits} miss={iconic_miss}")
    elif iconic_miss:
        notes.append(f"iconic partial miss={iconic_miss}")

    # Dialogue-ish: character cue followed by non-empty line
    dialogue_blocks = len(re.findall(r"(?m)^[A-Z][A-Z0-9 &'.\-]{1,40}\n.+\n", fountain))

    return {
        "title_ok": has_title,
        "scene_heading_count": len(scene_heads),
        "scene_headings_sample": scene_heads[:12],
        "character_cues_sample": sorted(set(chars))[:20],
        "has_narrator": has_narrator,
        "page_tag_hits": page_tags,
        "book_has_page_markers": has_page_markers_in_book,
        "dialogue_blocks_est": dialogue_blocks,
        "char_count": len(fountain),
        "iconic_hits": iconic_hits if iconic else [],
        "iconic_miss": iconic_miss if iconic else [],
        "issues": issues,
        "notes": notes,
        "pass": len(issues) == 0
        or (
            # soft-pass: only invented page tags and nothing else critical
            issues == ["invented_page_tags_without_book_markers"]
        ),
        "hard_fail": any(
            i.startswith("missing_fidelity")
            or i
            in (
                "no_scene_headings",
                "emitted_json",
                "story_page_dump_pattern",
                "output_too_short",
                "bad_scene_headings",
            )
            or i.startswith("bad_scene")
            for i in issues
        ),
    }


def infer_author(text: str) -> str:
    m = re.search(r"(?im)^Author:\s*(.+)$", text[:4000])
    if m:
        return m.group(1).strip()[:80]
    m = re.search(r"(?i)by\s+([A-Z][A-Za-z .'\-]{3,60})", text[:2000])
    if m:
        return m.group(1).strip()[:80]
    return "(unknown)"


def run_one(book: Dict[str, Any], force: bool = False) -> Dict[str, Any]:
    FOUNTAIN_DIR.mkdir(parents=True, exist_ok=True)
    LOG_DIR.mkdir(parents=True, exist_ok=True)
    bid = book["id"]
    out_path = FOUNTAIN_DIR / f"{bid}.fountain"
    report_path = LOG_DIR / f"{bid}_eval.json"

    raw = book["path"].read_text(encoding="utf-8", errors="ignore")
    cleaned = strip_gutenberg(raw)
    words = len(re.findall(r"\S+", cleaned))
    minutes = estimate_minutes(words, book.get("kind_hint", "novel"))
    page_count = len(re.findall(r"---\s*PAGE\s+\d+\s*---", cleaned, re.I)) or max(1, words // 300)

    book_for_prompt = cleaned
    truncated = False
    if len(book_for_prompt) > MAX_BOOK_CHARS:
        book_for_prompt = smart_excerpt(book_for_prompt, MAX_BOOK_CHARS)
        truncated = True

    author = infer_author(raw)
    system = load_prompt(minutes)
    user = build_user(book["title"], author, minutes, page_count, book_for_prompt)

    print(f"\n=== {book['title']} ({bid}) words={words} min={minutes} truncated={truncated} ===", flush=True)

    if out_path.is_file() and not force:
        fountain = out_path.read_text(encoding="utf-8")
        print(f"  using cached {out_path.name}", flush=True)
    else:
        t0 = time.time()
        print(f"  calling {MODEL}…", flush=True)
        try:
            fountain = strip_fences(xai_chat(system, user, temperature=0.2))
        except Exception as e:
            report = {
                "id": bid,
                "title": book["title"],
                "error": str(e),
                "hard_fail": True,
                "pass": False,
            }
            report_path.write_text(json.dumps(report, indent=2), encoding="utf-8")
            print(f"  ERROR: {e}", flush=True)
            return report
        elapsed = time.time() - t0
        out_path.write_text(fountain + ("\n" if not fountain.endswith("\n") else ""), encoding="utf-8")
        print(f"  wrote {out_path.name} ({len(fountain)} chars) in {elapsed:.0f}s", flush=True)

    eval_r = evaluate_fountain(fountain, book, cleaned)
    report = {
        "id": bid,
        "title": book["title"],
        "path": str(book["path"]),
        "words": words,
        "minutes_target": minutes,
        "truncated": truncated,
        "fountain_path": str(out_path),
        "fountain_chars": len(fountain),
        "eval": eval_r,
        "pass": eval_r.get("pass"),
        "hard_fail": eval_r.get("hard_fail"),
        "issues": eval_r.get("issues"),
    }
    report_path.write_text(json.dumps(report, indent=2), encoding="utf-8")
    status = "PASS" if eval_r.get("pass") and not eval_r.get("hard_fail") else (
        "HARD_FAIL" if eval_r.get("hard_fail") else "SOFT_ISSUES"
    )
    print(f"  {status}: issues={eval_r.get('issues')}", flush=True)
    if eval_r.get("scene_headings_sample"):
        print("  heads:", "; ".join(eval_r["scene_headings_sample"][:6]), flush=True)
    return report


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--all", action="store_true")
    ap.add_argument("--only", action="append", default=[], help="substring match on id/title")
    ap.add_argument("--rerun", action="store_true", help="force re-call model even if cached")
    args = ap.parse_args()

    books = BOOKS
    if args.only:
        sel = []
        for b in BOOKS:
            if any(o.lower() in b["id"].lower() or o.lower() in b["title"].lower() for o in args.only):
                sel.append(b)
        books = sel
    elif not args.all:
        print("Pass --all or --only NAME", file=sys.stderr)
        return 2

    if not PROMPT_PATH.is_file():
        print(f"Missing prompt {PROMPT_PATH}", file=sys.stderr)
        return 2

    reports = []
    for b in books:
        if not b["path"].is_file():
            print(f"MISSING book file: {b['path']}", file=sys.stderr)
            reports.append({"id": b["id"], "error": "missing_file", "hard_fail": True})
            continue
        reports.append(run_one(b, force=args.rerun))

    summary = {
        "prompt": str(PROMPT_PATH),
        "model": MODEL,
        "results": [
            {
                "id": r.get("id"),
                "title": r.get("title"),
                "pass": r.get("pass"),
                "hard_fail": r.get("hard_fail"),
                "issues": r.get("issues") or r.get("eval", {}).get("issues"),
                "error": r.get("error"),
            }
            for r in reports
        ],
    }
    summary_path = LOG_DIR / "summary.json"
    summary_path.write_text(json.dumps(summary, indent=2), encoding="utf-8")
    print("\n=== SUMMARY ===", flush=True)
    for r in summary["results"]:
        print(
            f"  {r['id']}: pass={r['pass']} hard={r['hard_fail']} issues={r['issues']} err={r.get('error')}",
            flush=True,
        )
    print(f"Wrote {summary_path}", flush=True)
    return 0 if not any(r.get("hard_fail") for r in reports) else 1


if __name__ == "__main__":
    raise SystemExit(main())
