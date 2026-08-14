#!/usr/bin/env python3
"""
Rebuild source/book_full.txt by reading page images with Grok vision.

Use when PDF embedded text is sparse/garbled (picture books, scans).

Requires XAI_API_KEY.

Usage (repo root):
  python scripts/two_stage_adaptation/transcribe_book_pages_grok.py --project BusterTheNoodleheadDog
"""
from __future__ import annotations

import argparse
import base64
import io
import json
import mimetypes
import os
import re
import time
import urllib.error
import urllib.request
from pathlib import Path
from typing import Any, Callable, Dict, List, Optional, Tuple

ROOT = Path(__file__).resolve().parents[2]
XAI_API_BASE = "https://api.x.ai/v1"

TRANSCRIBE_PROMPT = """You are transcribing a children's / illustrated book page.

Task: extract ALL readable printed text on this page (title, body, dialogue).
Rules:
- Preserve verse line breaks when it looks like rhyme/poetry.
- Fix obvious OCR-style noise only if the letters on the page are clear; otherwise write what you see.
- Do NOT invent story, paraphrase, or add scene descriptions.
- If the page is illustration-only with no readable words, output exactly: (illustration only)
- Output plain text only — no markdown, no JSON, no preamble.
"""


def _project_dir(project_id: Optional[str]) -> Path:
    if project_id:
        return ROOT / "projects" / project_id
    ws = ROOT / "projects" / "workspace.json"
    pid = "NickAndMe"
    if ws.is_file():
        try:
            pid = json.loads(ws.read_text(encoding="utf-8")).get("active_project") or pid
        except (json.JSONDecodeError, OSError):
            pass
    return ROOT / "projects" / str(pid)


def _xai_responses(payload: Dict[str, Any], timeout: int = 180) -> Dict[str, Any]:
    api_key = (os.environ.get("XAI_API_KEY") or "").strip()
    if not api_key:
        raise RuntimeError(
            "XAI_API_KEY is not set. Required for Grok vision transcription."
        )
    data = json.dumps(payload).encode("utf-8")
    req = urllib.request.Request(
        f"{XAI_API_BASE}/responses",
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
        raise RuntimeError(f"xAI HTTP {e.code}: {body[:800]}") from e
    except Exception as e:
        raise RuntimeError(f"xAI request failed: {e}") from e
    return json.loads(raw)


def _extract_response_text(result: Dict[str, Any]) -> str:
    if isinstance(result.get("output_text"), str) and result["output_text"].strip():
        return result["output_text"].strip()
    output = result.get("output")
    if isinstance(output, list):
        texts: List[str] = []
        for item in output:
            if not isinstance(item, dict):
                continue
            content = item.get("content")
            if isinstance(content, list):
                for part in content:
                    if isinstance(part, dict) and part.get("type") in (
                        "output_text",
                        "text",
                    ):
                        if part.get("text"):
                            texts.append(part["text"])
            elif isinstance(content, str):
                texts.append(content)
        if texts:
            return "\n".join(texts).strip()
    choices = result.get("choices") or []
    if choices:
        msg = (choices[0].get("message") or {}).get("content")
        if isinstance(msg, str):
            return msg.strip()
    return json.dumps(result)[:1500]


def _load_image_data_uri(path: Path, *, max_edge: int = 1600) -> str:
    """Load image, downscale large pages, return data URI."""
    raw = path.read_bytes()
    mime = mimetypes.guess_type(str(path))[0] or "image/jpeg"
    try:
        from PIL import Image

        im = Image.open(io.BytesIO(raw))
        im = im.convert("RGB")
        w, h = im.size
        edge = max(w, h)
        if edge > max_edge:
            scale = max_edge / float(edge)
            im = im.resize(
                (max(1, int(w * scale)), max(1, int(h * scale))),
                Image.Resampling.LANCZOS,
            )
        buf = io.BytesIO()
        im.save(buf, format="JPEG", quality=85, optimize=True)
        raw = buf.getvalue()
        mime = "image/jpeg"
    except Exception:
        pass
    b64 = base64.b64encode(raw).decode("ascii")
    return f"data:{mime};base64,{b64}"


def collect_page_images(source: Path) -> List[Tuple[int, Path]]:
    """
    Prefer one image per page: embedded figure first, else rendered still.
    Returns sorted list of (page_number, path).
    """
    img_dir = source / "book_images"
    manifest_path = img_dir / "manifest.json"
    by_page: Dict[int, Dict[str, Path]] = {}

    if manifest_path.is_file():
        try:
            man = json.loads(manifest_path.read_text(encoding="utf-8"))
            for row in man.get("images") or []:
                page = int(row.get("page") or 0)
                if page <= 0:
                    continue
                rel = row.get("path") or ""
                # paths are relative to source/
                p = source / rel if not Path(rel).is_absolute() else Path(rel)
                if not p.is_file():
                    p = img_dir / Path(rel).name
                if not p.is_file():
                    continue
                kind = str(row.get("kind") or "")
                slot = by_page.setdefault(page, {})
                if kind == "embedded":
                    slot["embedded"] = p
                else:
                    slot.setdefault("rendered", p)
        except (json.JSONDecodeError, OSError, TypeError, ValueError):
            pass

    if not by_page and img_dir.is_dir():
        for p in sorted(img_dir.glob("embedded_p*.jpg")) + sorted(
            img_dir.glob("embedded_p*.png")
        ):
            m = re.search(r"embedded_p(\d+)", p.name, re.I)
            if m:
                by_page.setdefault(int(m.group(1)), {})["embedded"] = p
        for p in sorted(img_dir.glob("page_*.png")) + sorted(img_dir.glob("page_*.jpg")):
            m = re.search(r"page_(\d+)", p.name, re.I)
            if m:
                by_page.setdefault(int(m.group(1)), {}).setdefault("rendered", p)

    out: List[Tuple[int, Path]] = []
    for page in sorted(by_page):
        slot = by_page[page]
        path = slot.get("embedded") or slot.get("rendered")
        if path:
            out.append((page, path))
    return out


def transcribe_page(
    image_path: Path,
    *,
    page: int,
    model: str,
) -> str:
    data_uri = _load_image_data_uri(image_path)
    payload = {
        "model": model,
        "input": [
            {
                "role": "user",
                "content": [
                    {
                        "type": "input_image",
                        "image_url": data_uri,
                        "detail": "high",
                    },
                    {
                        "type": "input_text",
                        "text": f"Page {page} of the book.\n\n{TRANSCRIBE_PROMPT}",
                    },
                ],
            }
        ],
    }
    result = _xai_responses(payload, timeout=180)
    text = _extract_response_text(result)
    # strip accidental fences
    text = re.sub(r"^```(?:\w+)?\s*", "", text.strip())
    text = re.sub(r"\s*```$", "", text).strip()
    return text


def transcribe_book_pages(
    *,
    project_id: Optional[str] = None,
    model: str = "grok-4.5",
    force: bool = False,
    max_pages: int = 0,
    progress_cb: Optional[Callable[[Dict[str, Any]], None]] = None,
) -> Dict[str, Any]:
    """
    Write source/book_full.txt from page images via Grok vision.
    Backs up previous book_full.txt when replacing.
    """
    def progress(event: str, **kwargs: Any) -> None:
        if progress_cb:
            progress_cb({"event": event, **kwargs})

    project = _project_dir(project_id)
    source = project / "source"
    source.mkdir(parents=True, exist_ok=True)
    book_txt = source / "book_full.txt"
    pages = collect_page_images(source)
    if not pages:
        raise FileNotFoundError(
            f"No page images under {source / 'book_images'}. "
            "Run PDF extract first so embedded/rendered pages exist."
        )
    if max_pages and max_pages > 0:
        pages = pages[:max_pages]

    progress(
        "start",
        message=f"Grok vision transcription: {len(pages)} page image(s), model={model}",
        chunk=0,
        total=len(pages),
    )

    if book_txt.is_file() and not force:
        # still allow overwrite when called from prepare with force
        pass

    if book_txt.is_file():
        bak = source / f"book_full.txt.bak_pre_vision_{time.strftime('%Y%m%d_%H%M%S')}"
        bak.write_text(book_txt.read_text(encoding="utf-8", errors="ignore"), encoding="utf-8")
    else:
        bak = None

    parts: List[str] = []
    page_results: List[Dict[str, Any]] = []
    for i, (page_num, img_path) in enumerate(pages):
        progress(
            "page_start",
            message=f"Transcribing page {page_num} ({img_path.name})",
            chunk=i + 1,
            total=len(pages),
            page=page_num,
        )
        t0 = time.time()
        try:
            text = transcribe_page(img_path, page=page_num, model=model)
            err = None
        except Exception as e:
            text = ""
            err = str(e)[:400]
            progress(
                "page_error",
                message=f"Page {page_num} failed: {err}",
                chunk=i + 1,
                total=len(pages),
                page=page_num,
            )
        elapsed = time.time() - t0
        body = text.strip()
        if not body:
            body = "(illustration only)" if not err else f"(transcription failed: {err})"
        parts.append(f"--- PAGE {page_num} ---\n{body}")
        page_results.append(
            {
                "page": page_num,
                "image": str(img_path.name),
                "chars": len(body),
                "elapsed_sec": round(elapsed, 2),
                "error": err,
            }
        )
        progress(
            "page_done",
            message=f"Page {page_num} done ({len(body)} chars, {elapsed:.1f}s)",
            chunk=i + 1,
            total=len(pages),
            page=page_num,
            chars=len(body),
        )

    full = "\n\n".join(parts) + "\n"
    book_txt.write_text(full, encoding="utf-8")

    summary: Dict[str, Any] = {
        "ok": True,
        "project": project.name,
        "book_full": str(book_txt),
        "backup": str(bak) if bak else None,
        "pages": len(pages),
        "text_chars": len(full),
        "text_words": len(full.split()),
        "model": model,
        "method": "grok_vision",
        "page_results": page_results,
        "failed_pages": sum(1 for r in page_results if r.get("error")),
    }
    progress("done", message=f"Wrote {book_txt}", chunk=len(pages), total=len(pages), **summary)
    return summary


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--project", default=None)
    ap.add_argument("--model", default=os.environ.get("STAGE1_MODEL", "grok-4.5"))
    ap.add_argument("--force", action="store_true")
    ap.add_argument("--max-pages", type=int, default=0)
    args = ap.parse_args()
    try:
        summary = transcribe_book_pages(
            project_id=args.project,
            model=args.model,
            force=args.force,
            max_pages=args.max_pages,
        )
    except Exception as e:
        print(f"[Error] {e}")
        return 1
    print(
        f"[Success] vision → book_full.txt pages={summary['pages']} "
        f"chars={summary['text_chars']} failed={summary['failed_pages']}"
    )
    print(f"  {summary['book_full']}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
