#!/usr/bin/env python3
"""Read capability default model ids from models_catalog.json.

Scripts cannot import SupportedModelCatalog. Do not freeze a model id here —
callers pass capability ids (vision, chat, …) and we return
capabilities[].defaultModelId from the catalog file.
"""
from __future__ import annotations

import json
from pathlib import Path
from typing import Optional

_SCRIPTS_DIR = Path(__file__).resolve().parent
REPO_ROOT = _SCRIPTS_DIR.parent
DEFAULT_CATALOG = REPO_ROOT / "host" / "PageToMovie.Core" / "config" / "models_catalog.json"


def catalog_default_model_id(
    *capability_ids: str,
    catalog_path: Optional[Path] = None,
) -> Optional[str]:
    """First non-empty capabilities[].defaultModelId for the given capability ids.

    Typical: catalog_default_model_id("vision", "chat") — Vision, then Chat.
    """
    wanted = [c.strip().lower() for c in capability_ids if c and str(c).strip()]
    if not wanted:
        return None
    path = catalog_path or DEFAULT_CATALOG
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError, TypeError):
        return None
    by_id: dict[str, str] = {}
    for cap in data.get("capabilities") or []:
        if not isinstance(cap, dict):
            continue
        cid = str(cap.get("id") or "").strip().lower()
        mid = str(cap.get("defaultModelId") or "").strip()
        if cid and mid:
            by_id[cid] = mid
    for cap in wanted:
        hit = by_id.get(cap)
        if hit:
            return hit
    return None


def require_catalog_default_model_id(*capability_ids: str) -> str:
    mid = catalog_default_model_id(*capability_ids)
    if not mid:
        names = ", ".join(capability_ids) or "(none)"
        raise RuntimeError(
            f"No defaultModelId in models_catalog.json for capability [{names}]. "
            "Set capabilities[].defaultModelId or pass --model / STAGE1_MODEL."
        )
    return mid


if __name__ == "__main__":
    vision = catalog_default_model_id("vision", "chat")
    chat = catalog_default_model_id("chat")
    if not vision or not chat:
        raise SystemExit("catalog missing Vision/Chat defaultModelId")
    print(f"vision={vision}")
    print(f"chat={chat}")
