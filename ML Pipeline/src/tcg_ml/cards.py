from __future__ import annotations

import json
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Iterable


def _load_json(path: Path) -> dict[str, Any] | None:
    try:
        with path.open("r", encoding="utf-8-sig") as handle:
            data = json.load(handle)
        return data if isinstance(data, dict) else None
    except (OSError, json.JSONDecodeError):
        return None


def iter_card_files(cards_dir: Path) -> Iterable[Path]:
    for path in sorted(cards_dir.rglob("*.json")):
        if any(part.lower() == "backup" for part in path.parts):
            continue
        yield path


@dataclass
class CardCatalog:
    by_name: dict[str, dict[str, Any]]
    by_id: dict[str, dict[str, Any]]

    @classmethod
    def load(cls, cards_dir: Path) -> "CardCatalog":
        by_name: dict[str, dict[str, Any]] = {}
        by_id: dict[str, dict[str, Any]] = {}
        for path in iter_card_files(cards_dir):
            data = _load_json(path)
            if not data:
                continue
            name = str(data.get("cardName") or "").strip()
            card_id = str(data.get("cardId") or "").strip()
            if not name:
                continue
            data["_source_path"] = str(path)
            by_name.setdefault(name.lower(), data)
            if card_id:
                by_id.setdefault(card_id.lower(), data)
        return cls(by_name=by_name, by_id=by_id)

    def get_by_name(self, name: str | None) -> dict[str, Any] | None:
        if not name:
            return None
        return self.by_name.get(str(name).strip().lower())

    def __len__(self) -> int:
        return len(self.by_name)
