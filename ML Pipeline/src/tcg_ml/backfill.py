"""Reconstruct missing games.jsonl rows and metadata from exported logs.

A game only gets a games.jsonl row when GameResultLogger writes one (needs Unity's
OnGameOver or a timeout adjudication). Decision files imported/copied without their
games.jsonl — e.g. logs fetched from the server — leave winner metadata empty, so
`--winners-only` training silently drops them. The winner is recoverable from the
decisions alone:

  * Decisive game (ended before the turn limit): the LAST logged decision is the lethal
    blow, so its acting player_id is the winner.
  * Turn-limit game (last turn >= maxTurns - 1): no clean KO, so adjudicate by the higher
    score in the final snapshot; equal score is a Draw.

Rows written here carry `reconstructed: true` and an `end_reason` so they are easy to
tell apart from live GameResultLogger rows (and easy to remove:
`grep -v '"reconstructed":true'`).

Some older games.jsonl rows have winner/score data but no deck metadata. When the
matching Deckbuilder battle export is present, this module can enrich those existing
rows with `deck_a`, `deck_b`, `cards_drawn_a`, and `cards_drawn_b`. It deliberately
does not guess `brain_a`/`brain_b` profiles when that information is absent.
"""

from __future__ import annotations

import json
from pathlib import Path
from typing import Any, Iterator

_PLAYER_SIDE = {1: "A", 2: "B"}
_SUFFIX = "_decisions.jsonl"
_COMMON_TRAINERS = {
    "Potion", "Professor Oak", "Professor’s Research", "Professor Research", "Brock",
    "Irida", "Leaf", "Jasmine", "Rummage", "Ice Cream", "Chilly Pepper", "X-Speed",
    "Pokemon Center", "Net", "Poke Pill",
}


def _read_lines(path: Path) -> Iterator[str]:
    # Unity writes UTF-8 with a BOM; utf-8-sig strips it transparently.
    with path.open(encoding="utf-8-sig") as handle:
        for line in handle:
            line = line.strip()
            if line:
                yield line


def existing_game_ids(games_jsonl: Path) -> set[str]:
    ids: set[str] = set()
    if not games_jsonl.exists():
        return ids
    for line in _read_lines(games_jsonl):
        try:
            ids.add(json.loads(line).get("game_id"))
        except json.JSONDecodeError:
            continue
    ids.discard(None)
    return ids


def file_game_id(path: Path) -> str:
    name = path.name
    return name[: -len(_SUFFIX)] if name.endswith(_SUFFIX) else path.stem


def reconstruct(path: Path, max_turns: int) -> dict[str, Any] | None:
    records = []
    for line in _read_lines(path):
        try:
            records.append(json.loads(line))
        except json.JSONDecodeError:
            continue
    if not records:
        return None

    last = records[-1]
    last_turn = max((r.get("turn", 0) for r in records), default=0)
    snap = last.get("snapshot") or {}
    me = snap.get("MyState") or {}
    opp = snap.get("OpponentState") or {}

    # Final observed score per side, keyed by the snapshot's own PlayerId fields.
    score_by_pid: dict[int, int] = {}
    if me.get("PlayerId") is not None:
        score_by_pid[me["PlayerId"]] = me.get("Score", 0)
    if opp.get("PlayerId") is not None:
        score_by_pid[opp["PlayerId"]] = opp.get("Score", 0)
    score_a = score_by_pid.get(1, 0)
    score_b = score_by_pid.get(2, 0)

    decisive = last_turn < max_turns - 1
    if decisive:
        # The actor of the final logged decision landed the game-ending KO.
        winner_pid = last.get("player_id")
        winner = _PLAYER_SIDE.get(winner_pid, "Unknown")
        end_reason = "reconstructed_ko"
        # The lethal KO isn't in the pre-action snapshot, so credit the winner with it.
        if winner == "A":
            score_a += 1
        elif winner == "B":
            score_b += 1
    else:
        winner = "A" if score_a > score_b else "B" if score_b > score_a else "Draw"
        end_reason = "reconstructed_turn_limit"

    return {
        "game_id": file_game_id(path),
        "winner": winner,
        "end_reason": end_reason,
        "reconstructed": True,
        "turns": last_turn,
        "score_a": score_a,
        "score_b": score_b,
    }


def _deck_id(deck: Any) -> str | None:
    if not isinstance(deck, dict):
        return None
    deck_id = deck.get("deck_id")
    return deck_id if isinstance(deck_id, str) and deck_id else None


def _load_battle_metadata(deckbuilder_dir: Path) -> dict[str, dict[str, Any]]:
    metadata: dict[str, dict[str, Any]] = {}
    if not deckbuilder_dir.exists():
        return metadata
    for path in sorted(deckbuilder_dir.glob("battle_*.json")):
        try:
            with path.open(encoding="utf-8-sig") as handle:
                record = json.load(handle)
        except (OSError, json.JSONDecodeError):
            continue

        game_id = record.get("battle_id") if isinstance(record, dict) else None
        if not isinstance(game_id, str) or not game_id:
            game_id = path.stem

        deck_a = _deck_id(record.get("deck_a"))
        deck_b = _deck_id(record.get("deck_b"))
        if not deck_a or not deck_b:
            continue

        row: dict[str, Any] = {
            "deck_a": deck_a,
            "deck_b": deck_b,
        }
        drawn_a = record.get("drawn_cards_a")
        drawn_b = record.get("drawn_cards_b")
        if isinstance(drawn_a, list):
            row["cards_drawn_a"] = len(drawn_a)
        if isinstance(drawn_b, list):
            row["cards_drawn_b"] = len(drawn_b)
        metadata[game_id] = row
    return metadata


def enrich_existing_metadata(games_jsonl: Path, deckbuilder_dir: Path,
                             apply: bool = False) -> dict[str, Any]:
    """Fill missing deck/card-draw metadata in existing games.jsonl rows.

    The source is Logs Export/Deckbuilder/battle_*.json. Existing games.jsonl values are
    left untouched; only missing fields are added.
    """
    if not games_jsonl.exists():
        return {
            "games_rows": 0,
            "battle_metadata": 0,
            "matched_rows": 0,
            "updated_rows": 0,
            "written": 0,
        }

    battle_metadata = _load_battle_metadata(deckbuilder_dir)
    rows: list[dict[str, Any]] = []
    games_rows = 0
    matched_rows = 0
    updated_rows = 0

    for line in _read_lines(games_jsonl):
        try:
            row = json.loads(line)
        except json.JSONDecodeError:
            continue
        if not isinstance(row, dict):
            continue

        games_rows += 1
        game_id = row.get("game_id")
        meta = battle_metadata.get(game_id) if isinstance(game_id, str) else None
        if meta:
            matched_rows += 1
            changed = False
            for key, value in meta.items():
                if row.get(key) in (None, ""):
                    row[key] = value
                    changed = True
            if changed:
                row["metadata_backfilled"] = True
                updated_rows += 1
        rows.append(row)

    written = 0
    if apply and updated_rows:
        with games_jsonl.open("w", encoding="utf-8") as handle:
            for row in rows:
                handle.write(json.dumps(row, ensure_ascii=False) + "\n")
        written = updated_rows

    return {
        "games_rows": games_rows,
        "battle_metadata": len(battle_metadata),
        "matched_rows": matched_rows,
        "updated_rows": updated_rows,
        "written": written,
    }


def _project_root_from_games(games_jsonl: Path) -> Path:
    # <root>/Logs Export/ML/games.jsonl
    try:
        return games_jsonl.resolve().parents[2]
    except IndexError:
        return Path.cwd()


def _load_card_names(cards_dir: Path) -> dict[str, str]:
    names: dict[str, str] = {}
    if not cards_dir.exists():
        return names
    for path in cards_dir.rglob("*.json"):
        try:
            record = json.loads(path.read_text(encoding="utf-8-sig"))
        except (OSError, json.JSONDecodeError):
            continue
        card_id = record.get("cardId") or path.stem
        card_name = record.get("cardName")
        if isinstance(card_id, str) and isinstance(card_name, str) and card_name:
            names[card_id] = card_name
    return names


def _load_deck_signatures(root: Path) -> dict[str, dict[str, Any]]:
    card_names = _load_card_names(root / "Cards")
    signatures: dict[str, dict[str, Any]] = {}
    for path in sorted((root / "Decks").glob("*.json")):
        try:
            record = json.loads(path.read_text(encoding="utf-8-sig"))
        except (OSError, json.JSONDecodeError):
            continue
        deck_name = record.get("deckName") or path.stem
        if not isinstance(deck_name, str) or not deck_name:
            continue
        names = set()
        for card in record.get("cards") or []:
            if isinstance(card, dict):
                name = card_names.get(card.get("cardId"))
                if name:
                    names.add(name)
        if names:
            signatures[deck_name] = {
                "names": names,
                "energy": tuple(record.get("energyTypes") or []),
            }
    return signatures


def _add_snapshot_names(state: Any, target: set[str]) -> None:
    if not isinstance(state, dict):
        return
    pokemon: list[dict[str, Any]] = []
    active = state.get("ActivePokemon")
    if isinstance(active, dict):
        pokemon.append(active)
    pokemon.extend(b for b in (state.get("Bench") or []) if isinstance(b, dict))
    for pkm in pokemon:
        name = pkm.get("Name")
        if isinstance(name, str) and name:
            target.add(name)
        for evo in pkm.get("PossibleEvolutions") or []:
            if isinstance(evo, dict) and isinstance(evo.get("Name"), str):
                target.add(evo["Name"])
    for card in state.get("Hand") or []:
        if isinstance(card, dict) and isinstance(card.get("Name"), str):
            target.add(card["Name"])


def _rank_decks(names: set[str], energy: tuple[Any, ...],
                decks: dict[str, dict[str, Any]]) -> list[tuple[int, str, int]]:
    signal = {n for n in names if n not in _COMMON_TRAINERS} or set(names)
    ranked: list[tuple[int, str, int]] = []
    for deck, info in decks.items():
        deck_names = info["names"]
        intersection = len(signal & deck_names)
        missing = len(signal - deck_names)
        energy_bonus = 1 if energy and tuple(energy) == info["energy"] else 0
        score = intersection * 10 + energy_bonus - missing * 2
        ranked.append((score, deck, missing))
    ranked.sort(reverse=True)
    return ranked


def _infer_decks_from_decision_file(path: Path, decks: dict[str, dict[str, Any]],
                                    max_lines: int, min_score: int,
                                    min_margin: int) -> tuple[str | None, str | None]:
    names = {1: set(), 2: set()}
    energy: dict[int, tuple[Any, ...]] = {1: (), 2: ()}

    line_count = 0
    for line in _read_lines(path):
        if line_count >= max_lines:
            break
        try:
            record = json.loads(line)
        except json.JSONDecodeError:
            continue
        line_count += 1
        snapshot = record.get("snapshot") or {}
        for key in ("MyState", "OpponentState"):
            state = snapshot.get(key) or {}
            pid = state.get("PlayerId")
            if pid not in (1, 2):
                continue
            _add_snapshot_names(state, names[pid])
            pool = state.get("DeckEnergyPool")
            if pool:
                energy[pid] = tuple(pool)

    def choose(pid: int) -> str | None:
        ranked = _rank_decks(names[pid], energy[pid], decks)
        if not ranked or ranked[0][0] < min_score:
            return None
        margin = ranked[0][0] - (ranked[1][0] if len(ranked) > 1 else -999)
        if margin < min_margin:
            return None
        return ranked[0][1]

    return choose(1), choose(2)


def infer_existing_decks_from_decisions(games_jsonl: Path, decisions_dir: Path,
                                        root: Path | None = None, apply: bool = False,
                                        max_lines: int = 160, min_score: int = 8,
                                        min_margin: int = 10) -> dict[str, Any]:
    """Infer missing deck_a/deck_b from decision snapshots and local Decks/*.json.

    This is a recovery tool for legacy rows that have winner/score metadata but no deck
    metadata. Inferred rows are marked with deck_metadata_inferred=true.
    """
    project_root = root or _project_root_from_games(games_jsonl)
    decks = _load_deck_signatures(project_root)
    if not games_jsonl.exists() or not decisions_dir.exists() or not decks:
        return {
            "games_rows": 0,
            "decision_files": 0,
            "candidate_rows": 0,
            "updated_rows": 0,
            "ambiguous_or_unknown": 0,
            "written": 0,
        }

    files = {file_game_id(p): p for p in decisions_dir.rglob("*" + _SUFFIX)}
    rows: list[dict[str, Any]] = []
    games_rows = candidate_rows = updated_rows = ambiguous = 0
    for line in _read_lines(games_jsonl):
        try:
            row = json.loads(line)
        except json.JSONDecodeError:
            continue
        if not isinstance(row, dict):
            continue
        games_rows += 1
        game_id = row.get("game_id")
        path = files.get(game_id) if isinstance(game_id, str) else None
        if path and (not row.get("deck_a") or not row.get("deck_b")):
            candidate_rows += 1
            deck_a, deck_b = _infer_decks_from_decision_file(
                path, decks, max_lines=max_lines, min_score=min_score, min_margin=min_margin
            )
            if deck_a and deck_b:
                if not row.get("deck_a"):
                    row["deck_a"] = deck_a
                if not row.get("deck_b"):
                    row["deck_b"] = deck_b
                if not row.get("brain_a"):
                    row["brain_a"] = "Algorithm:Standard"
                    row["profile_metadata_assumed"] = True
                if not row.get("brain_b"):
                    row["brain_b"] = "Algorithm:Standard"
                    row["profile_metadata_assumed"] = True
                row["deck_metadata_inferred"] = True
                updated_rows += 1
            else:
                ambiguous += 1
        rows.append(row)

    written = 0
    if apply and updated_rows:
        with games_jsonl.open("w", encoding="utf-8") as handle:
            for row in rows:
                handle.write(json.dumps(row, ensure_ascii=False) + "\n")
        written = updated_rows

    return {
        "games_rows": games_rows,
        "decision_files": len(files),
        "candidate_rows": candidate_rows,
        "updated_rows": updated_rows,
        "ambiguous_or_unknown": ambiguous,
        "written": written,
    }


def backfill(decisions_dir: Path, games_jsonl: Path, max_turns: int = 45,
             apply: bool = False) -> dict[str, Any]:
    """Reconstruct winner rows for decision files missing from games.jsonl.

    Returns a stats dict. When apply=True, appends the new rows to games.jsonl.
    """
    have = existing_game_ids(games_jsonl)
    # Recurse into per-context subfolders (benchmark/, interactive/<matchup>/, received/, …) so
    # logs that don't sit in the Decisions/ root are still backfilled.
    files = sorted(decisions_dir.rglob("*" + _SUFFIX)) if decisions_dir.exists() else []
    rows: list[dict[str, Any]] = []
    skipped_existing = 0
    counts = {"A": 0, "B": 0, "Draw": 0, "Unknown": 0}
    reasons = {"reconstructed_ko": 0, "reconstructed_turn_limit": 0}
    for path in files:
        if file_game_id(path) in have:
            skipped_existing += 1
            continue
        row = reconstruct(path, max_turns)
        if row is None:
            continue
        rows.append(row)
        counts[row["winner"]] = counts.get(row["winner"], 0) + 1
        reasons[row["end_reason"]] = reasons.get(row["end_reason"], 0) + 1

    written = 0
    if apply and rows:
        games_jsonl.parent.mkdir(parents=True, exist_ok=True)
        with games_jsonl.open("a", encoding="utf-8") as handle:
            for row in rows:
                handle.write(json.dumps(row) + "\n")
        written = len(rows)

    return {
        "decision_files": len(files),
        "already_in_games": skipped_existing,
        "to_reconstruct": len(rows),
        "written": written,
        "winners": counts,
        "usable_ab": counts["A"] + counts["B"],
        "end_reasons": reasons,
        "rows": rows,
    }
