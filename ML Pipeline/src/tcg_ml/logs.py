from __future__ import annotations

import json
import os
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Iterable, Iterator

# orjson (Rust) parses these logs ~3-5x faster than the stdlib; it is optional, the
# stdlib json is a drop-in fallback when it is not installed.
try:
    import orjson  # type: ignore[import-not-found]

    def _loads(line: str) -> Any:
        return orjson.loads(line)

    _JSON_ERRORS: tuple[type[Exception], ...] = (orjson.JSONDecodeError,)
except ImportError:
    _loads = json.loads
    _JSON_ERRORS = (json.JSONDecodeError,)


@dataclass
class DecisionEnvelope:
    path: Path
    line_no: int
    record: dict[str, Any]
    game: dict[str, Any] | None


def iter_jsonl(path: Path) -> Iterator[tuple[int, dict[str, Any]]]:
    try:
        with path.open("r", encoding="utf-8-sig") as handle:
            for line_no, line in enumerate(handle, start=1):
                line = line.strip()
                if not line or line == "[]":
                    continue
                try:
                    data = _loads(line)
                except _JSON_ERRORS:
                    continue
                if isinstance(data, dict):
                    yield line_no, data
    except OSError:
        return


def load_games(games_jsonl: Path) -> dict[str, dict[str, Any]]:
    games: dict[str, dict[str, Any]] = {}
    for _, record in iter_jsonl(games_jsonl):
        game_id = record.get("game_id")
        if game_id:
            games[str(game_id)] = record
    return games


# Files directly under the Decisions/ root (written before the per-context split, or pulled from the
# SMB server) belong to this pseudo-source. New logs go into named subfolders ("benchmark",
# "interactive", …) so training can include/exclude a context without mixing them.
ROOT_SOURCE = "legacy"
_DECISION_GLOB = "*_decisions.jsonl"


def file_source(path: Path, decisions_dir: Path) -> str:
    """Source label for a decision file = its full subfolder path under decisions_dir (POSIX, e.g.
    "benchmark", "interactive/algorithm_vs_ml", "received"), or ROOT_SOURCE when the file sits
    directly in the Decisions/ root."""
    try:
        rel = path.resolve().relative_to(decisions_dir.resolve())
    except (ValueError, OSError):
        return ROOT_SOURCE
    parts = rel.parts[:-1]  # drop the filename
    return "/".join(parts) if parts else ROOT_SOURCE


def _source_selected(src: str, wanted: set[str]) -> bool:
    """A file's source matches the selection if it equals a wanted label or sits under it, so
    selecting "interactive" includes every "interactive/<matchup>" bucket (hierarchical)."""
    return any(src == w or src.startswith(w + "/") for w in wanted)


def iter_decision_files(decisions_dir: Path, sources: Iterable[str] | None = None) -> list[Path]:
    """Decision files under decisions_dir, recursing into per-context subfolders.

    ``sources`` optionally restricts the result to specific source labels (full subfolder paths such
    as "benchmark" or "interactive/algorithm_vs_ml", or ROOT_SOURCE for files in the Decisions/ root).
    Selection is hierarchical — a parent label includes all of its children. ``None`` (default) returns
    every file — used by stats/replay/analysis so they always see all contexts; training passes a set.
    """
    if not decisions_dir.exists():
        return []
    wanted = set(sources) if sources is not None else None
    out: list[Path] = []
    for p in decisions_dir.rglob(_DECISION_GLOB):
        if not (p.is_file() and p.stat().st_size > 3):
            continue
        if wanted is not None and not _source_selected(file_source(p, decisions_dir), wanted):
            continue
        out.append(p)
    return sorted(out)


def decision_sources(decisions_dir: Path) -> dict[str, int]:
    """Map each available source label to its decision-file count (for UI selection / stats)."""
    counts: dict[str, int] = {}
    if not decisions_dir.exists():
        return counts
    for p in decisions_dir.rglob(_DECISION_GLOB):
        if not (p.is_file() and p.stat().st_size > 3):
            continue
        src = file_source(p, decisions_dir)
        counts[src] = counts.get(src, 0) + 1
    return counts


def iter_decisions(decisions_dir: Path, games: dict[str, dict[str, Any]] | None = None) -> Iterator[DecisionEnvelope]:
    games = games or {}
    for path in iter_decision_files(decisions_dir):
        for line_no, record in iter_jsonl(path):
            game_id = record.get("game_id")
            yield DecisionEnvelope(path=path, line_no=line_no, record=record, game=games.get(str(game_id)))


# Terminal / cross-category records that are logged for analysis and Replay but are NOT
# behavioral-cloning training targets (they have no real candidate set to choose from).
# "GameEnd" carries the final winning state (incl. the deciding KO); "TurnMeta" is legacy.
NON_TRAINABLE_CATEGORIES = {"GameEnd", "TurnMeta"}


def usable_decision(record: dict[str, Any]) -> tuple[bool, str]:
    if str(record.get("category") or "") in NON_TRAINABLE_CATEGORIES:
        return False, "non-trainable category"
    scores = record.get("scores")
    chosen = record.get("chosen_label")
    snapshot = record.get("snapshot")
    if not chosen:
        return False, "missing chosen_label"
    if not isinstance(scores, list) or not scores:
        return False, "missing scores"
    if not isinstance(snapshot, dict):
        return False, "missing snapshot"
    if str(chosen) == "(skip)":
        return True, "ok"
    labels = [str(item.get("label")) for item in scores if isinstance(item, dict)]
    if str(chosen) not in labels:
        return False, "chosen_label not found in scores"
    return True, "ok"


def index_decision_files(decisions_dir: Path) -> list[tuple[Path, str, tuple[int, int]]]:
    """One rglob+stat pass over the decision logs: (path, source_label, (size, mtime_ns)) per
    file, sorted by path. Resolves decisions_dir once instead of per file (file_source resolves
    on every call, which is far too slow for 50k+ files)."""
    if not decisions_dir.exists():
        return []
    root = decisions_dir.resolve()
    out: list[tuple[Path, str, tuple[int, int]]] = []
    for p in root.rglob(_DECISION_GLOB):
        try:
            st = p.stat()
        except OSError:
            continue
        if not p.is_file() or st.st_size <= 3:
            continue
        parts = p.relative_to(root).parts[:-1]
        src = "/".join(parts) if parts else ROOT_SOURCE
        out.append((p, src, (st.st_size, st.st_mtime_ns)))
    out.sort(key=lambda item: item[0])
    return out


def scan_decision_file(path: Path) -> dict[str, Any]:
    """Mergeable scan aggregate for one decision file.

    Deliberately independent of games.jsonl and of the file's source label, so the result
    stays valid as games.jsonl grows and can be cached per file (decision files are immutable
    once a battle ends). ``game_id_counts`` (record game_id -> record count) lets the merge
    step compute records-without-game-metadata against the CURRENT games.jsonl.
    """
    total = 0
    usable = 0
    non_trainable = 0
    reasons: dict[str, int] = {}
    categories: dict[str, int] = {}
    game_id_counts: dict[str, int] = {}

    for _, record in iter_jsonl(path):
        total += 1
        gid = str(record.get("game_id"))
        game_id_counts[gid] = game_id_counts.get(gid, 0) + 1
        ok, reason = usable_decision(record)
        if ok:
            usable += 1
            category = str(record.get("category") or "Unknown")
            keys = [category]
            if str(record.get("chosen_label")) == "(skip)":
                keys.append(f"{category}:skip")
                # Split skips: a skip where at least one candidate was NOT blocked means a
                # legal action was available and the bot deliberately passed (e.g. chose to
                # attack instead of retreating). A skip where every candidate is blocked is a
                # forced skip — there was no real decision to make. Distinguishing the two
                # matters for the Retreat category in particular (an active that can KO blocks
                # all retreat candidates, so it lands here as a forced skip, not a real "pass").
                scores = record.get("scores")
                had_legal = isinstance(scores, list) and any(
                    isinstance(s, dict) and not s.get("blocked", False) for s in scores
                )
                if had_legal:
                    keys.append(f"{category}:skip_legal")
            for k in keys:
                categories[k] = categories.get(k, 0) + 1
        else:
            # Non-trainable records (TurnMeta, GameEnd) are intentionally excluded — they are
            # not malformed. Track them separately so they don't inflate the "invalid" counter.
            if reason == "non-trainable category":
                non_trainable += 1
            else:
                reasons[reason] = reasons.get(reason, 0) + 1

    return {
        "total": total,
        "usable": usable,
        "non_trainable": non_trainable,
        "reasons": reasons,
        "categories": categories,
        "game_id_counts": game_id_counts,
    }


_SCAN_CACHE_VERSION = 1


def _load_scan_cache(cache_path: Path | None) -> dict[str, Any]:
    if cache_path is None:
        return {}
    try:
        with cache_path.open("r", encoding="utf-8") as handle:
            data = json.load(handle)
    except (OSError, json.JSONDecodeError):
        return {}
    if not isinstance(data, dict) or data.get("version") != _SCAN_CACHE_VERSION:
        return {}
    files = data.get("files")
    return files if isinstance(files, dict) else {}


def _save_scan_cache(cache_path: Path | None, files: dict[str, Any]) -> None:
    if cache_path is None:
        return
    try:
        cache_path.parent.mkdir(parents=True, exist_ok=True)
        tmp = cache_path.with_suffix(cache_path.suffix + ".tmp")
        with tmp.open("w", encoding="utf-8") as handle:
            json.dump({"version": _SCAN_CACHE_VERSION, "files": files}, handle, separators=(",", ":"))
        os.replace(tmp, cache_path)
    except OSError:
        return  # cache is an optimization; a failed save must never break the scan


def scan_dataset_incremental(
    decisions_dir: Path, games_jsonl: Path, cache_path: Path | None = None
) -> dict[str, Any]:
    """Dataset statistics with a persistent per-file cache.

    Decision files are write-once (one file == one game), so each file's aggregate is reused
    as long as its (size, mtime_ns) stamp matches; only new/changed files are parsed. With
    ``cache_path`` set the cache survives process restarts, turning a rescan after a batch of
    new games from a full multi-GB parse into seconds. Entries for files that disappeared are
    dropped, so deletions shrink the totals. Output shape is identical to the full scan.
    """
    games = load_games(games_jsonl)
    entries = index_decision_files(decisions_dir)
    cache = _load_scan_cache(cache_path)
    new_cache: dict[str, Any] = {}

    total = 0
    usable = 0
    non_trainable = 0
    reasons: dict[str, int] = {}
    categories: dict[str, int] = {}
    # Per-top-level-source category breakdown (benchmark / interactive / received / legacy) so the
    # dashboard can show a chart per context in addition to the combined one.
    source_breakdown: dict[str, dict[str, int]] = {}
    sources: dict[str, int] = {}
    missing_game_meta = 0

    for path, src, stamp in entries:
        sources[src] = sources.get(src, 0) + 1
        key = str(path)
        cached = cache.get(key)
        if (
            isinstance(cached, dict)
            and list(cached.get("stamp") or ()) == [stamp[0], stamp[1]]
            and isinstance(cached.get("agg"), dict)
        ):
            agg = cached["agg"]
        else:
            agg = scan_decision_file(path)
        new_cache[key] = {"stamp": [stamp[0], stamp[1]], "agg": agg}

        total += int(agg.get("total") or 0)
        usable += int(agg.get("usable") or 0)
        non_trainable += int(agg.get("non_trainable") or 0)
        for k, v in (agg.get("reasons") or {}).items():
            reasons[k] = reasons.get(k, 0) + int(v)
        file_categories = agg.get("categories") or {}
        top = src.split("/", 1)[0]
        sc = source_breakdown.setdefault(top, {})
        for k, v in file_categories.items():
            categories[k] = categories.get(k, 0) + int(v)
            sc[k] = sc.get(k, 0) + int(v)
        for gid, count in (agg.get("game_id_counts") or {}).items():
            if gid not in games:
                missing_game_meta += int(count)

    _save_scan_cache(cache_path, new_cache)

    truly_invalid = total - usable - non_trainable
    return {
        "decision_files": len(entries),
        "sources": sources,
        "games_with_metadata": len(games),
        "decision_records": total,
        "usable_decisions": usable,
        "non_trainable_records": non_trainable,
        "invalid_decisions": truly_invalid,
        "invalid_reasons": reasons,
        "categories": categories,
        "source_breakdown": source_breakdown,
        "records_without_game_metadata": missing_game_meta,
    }


def scan_dataset(decisions_dir: Path, games_jsonl: Path) -> dict[str, Any]:
    return scan_dataset_incremental(decisions_dir, games_jsonl, cache_path=None)
