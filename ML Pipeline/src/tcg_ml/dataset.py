from __future__ import annotations

import random
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Callable, Iterator

import numpy as np

from .features import FeatureEncoder
from .logs import DecisionEnvelope, iter_decisions, iter_jsonl, load_games, usable_decision


@dataclass
class TrainingExample:
    game_id: str
    category: str
    x: np.ndarray
    y: int
    labels: list[str]
    equivalence_keys: list[tuple]


# Decision logs use integer player_id (1, 2); games.jsonl reports winner as "A"/"B".
# Player 1 is side A, player 2 is side B (verified: score_a tracks player 1, score_b player 2).
_PLAYER_SIDE = {1: "A", 2: "B"}


def decision_is_by_winner(record: dict[str, Any], game: dict[str, Any] | None) -> bool:
    """True if the player who made this decision won the game.

    Requires game-level metadata (games.jsonl) with a valid winner; decisions from
    games without a recorded winner are treated as non-winning (dropped under a
    winners-only filter). This keeps behavioral cloning focused on winning play.
    """
    if not isinstance(game, dict):
        return False
    winner = game.get("winner")
    if winner not in ("A", "B"):
        return False
    side = _PLAYER_SIDE.get(record.get("player_id"))
    return side == winner


_BRAIN_PROFILE_PREFIX = "Algorithm:"


def decision_profile(record: dict[str, Any], game: dict[str, Any] | None) -> str | None:
    """AlgorithmBrain profile of the player who made this decision, e.g. "Ramp".

    Derived from games.jsonl ``brain_a``/``brain_b`` (format ``Algorithm:<Profile>``) via the
    decision's ``player_id``. Returns None when the side is unknown, the brain is not an
    AlgorithmBrain, or game metadata is missing.
    """
    if not isinstance(game, dict):
        return None
    side = _PLAYER_SIDE.get(record.get("player_id"))
    if side is None:
        return None
    brain = game.get("brain_a") if side == "A" else game.get("brain_b")
    if not isinstance(brain, str) or not brain.startswith(_BRAIN_PROFILE_PREFIX):
        return None
    return brain[len(_BRAIN_PROFILE_PREFIX):]


def make_keep_predicate(
    winners_only: bool = False,
    profile: str | list[str] | None = None,
) -> Callable[[dict[str, Any], dict[str, Any] | None], bool] | None:
    """Compose per-decision filters into a single ``(record, game) -> bool`` predicate.

    Returns None when no filter is requested (so the caller can skip predicate overhead).
    ``profile`` keeps only decisions made by an AlgorithmBrain running one of the given
    profiles (case-insensitive); accepts a single string or a list of strings. Use it to
    train one model per profile or to keep training on specific experts when the dataset
    mixes profiles.
    """
    if isinstance(profile, str):
        normed: set[str] | None = {profile.strip().lower()} if profile.strip() else None
    elif isinstance(profile, (list, tuple)):
        normed = {p.strip().lower() for p in profile if isinstance(p, str) and p.strip()} or None
    else:
        normed = None

    if not winners_only and normed is None:
        return None

    def keep(record: dict[str, Any], game: dict[str, Any] | None) -> bool:
        if winners_only and not decision_is_by_winner(record, game):
            return False
        if normed is not None:
            p = decision_profile(record, game)
            if p is None or p.lower() not in normed:
                return False
        return True

    return keep


def file_game_id(path: Path) -> str:
    """Game id from a ``<game_id>_decisions.jsonl`` filename (one file == one game)."""
    name = path.name
    suffix = "_decisions.jsonl"
    return name[: -len(suffix)] if name.endswith(suffix) else path.stem


def winner_game_ids(games_jsonl: Path) -> set[str]:
    """Game ids that have a recorded A/B winner in games.jsonl."""
    games = load_games(games_jsonl)
    return {gid for gid, g in games.items() if g.get("winner") in ("A", "B")}


def filter_winner_files(files: list[Path], games_jsonl: Path) -> tuple[list[Path], int]:
    """Keep only decision files whose game has a recorded A/B winner.

    Returns (kept_files, total_games_with_winner). Order is preserved so a downstream
    seeded shuffle stays reproducible.
    """
    won = winner_game_ids(games_jsonl)
    return [p for p in files if file_game_id(p) in won], len(won)


def split_train_val_files(
    files: list[Path], seed: int, val_ratio: float, max_games: int = 0
) -> tuple[list[Path], list[Path]]:
    """Game-level shuffle + holdout split, shared by training and evaluation.

    Uses ``random.Random(seed)`` which yields the SAME order as a module-level
    ``random.seed(seed)`` + ``random.shuffle``, so an evaluation run can reconstruct the
    exact validation games a training run held out — given the same seed, val_ratio,
    max_games and (winner-filtered) file list. This is what makes a true held-out eval
    possible without persisting the split.
    """
    shuffled = list(files)
    random.Random(seed).shuffle(shuffled)
    if max_games:
        shuffled = shuffled[:max_games]
    n_val = int(len(shuffled) * val_ratio) if len(shuffled) > 1 else 0
    return shuffled[n_val:], shuffled[:n_val]


def choose_label_index(scores: list[dict], chosen_label: str, chosen_target_instance_id: int | None = None) -> int | None:
    if chosen_target_instance_id is not None and chosen_target_instance_id >= 0:
        for index, score in enumerate(scores):
            if not isinstance(score, dict):
                continue
            if score.get("blocked"):
                continue
            if str(score.get("label")) != str(chosen_label):
                continue
            if int(score.get("target_instance_id", -1) or -1) == chosen_target_instance_id:
                return index

    candidates = []
    for index, score in enumerate(scores):
        if not isinstance(score, dict):
            continue
        if score.get("blocked"):
            continue
        if str(score.get("label")) == str(chosen_label):
            candidates.append((int(score.get("score") or 0), index))
    if candidates:
        return sorted(candidates, reverse=True)[0][1]
    for index, score in enumerate(scores):
        if isinstance(score, dict) and str(score.get("label")) == str(chosen_label):
            return index
    return None


def candidate_equivalence_key(candidate: dict, category: str) -> tuple:
    """Key for behaviorally equivalent choices used by evaluation diagnostics.

    Exact target identity matters for board targets such as AttachEnergy/Retreat, because
    two same-name Pokemon can have different HP/energy/readiness. For PlayBasic, duplicate
    same-name cards in hand are interchangeable: playing either copy creates the same board.
    """
    label = str(candidate.get("label") or "")
    blocked = bool(candidate.get("blocked", False))
    if str(category) == "PlayBasic" and label.startswith("PlayBasic("):
        return ("PlayBasic", label, blocked)
    return ("Exact", label, int(candidate.get("target_instance_id", -1) or -1), blocked)


def candidates_equivalent(left: dict, right: dict, category: str) -> bool:
    return candidate_equivalence_key(left, category) == candidate_equivalence_key(right, category)


def envelope_to_example(env: DecisionEnvelope, encoder: FeatureEncoder) -> TrainingExample | None:
    record = env.record
    ok, _ = usable_decision(record)
    if not ok:
        return None
    scores = [s for s in record["scores"] if isinstance(s, dict)]
    scores.append({"label": "(skip)", "score": 0, "blocked": False, "reasons": ["synthetic no-action candidate"]})
    chosen_target = int(record.get("chosen_target_instance_id", -1) or -1)
    chosen_index = choose_label_index(scores, str(record["chosen_label"]), chosen_target)
    if chosen_index is None:
        return None

    snapshot = record["snapshot"]
    state = encoder.state_vector(snapshot)
    actions = []
    labels = []
    equivalence_keys = []
    category = str(record.get("category") or "")
    for index, score in enumerate(scores):
        label = str(score.get("label") or "")
        labels.append(label)
        equivalence_keys.append(candidate_equivalence_key(score, category))
        action = encoder.action_vector(
            label=label,
            category=category,
            ordinal=index,
            candidate_count=len(scores),
            target_instance_id=int(score.get("target_instance_id", -1) or -1),
            snapshot=snapshot,
        )
        actions.append(np.concatenate([state, action]).astype(np.float32))
    return TrainingExample(
        game_id=str(record.get("game_id") or ""),
        category=category,
        x=np.vstack(actions),
        y=chosen_index,
        labels=labels,
        equivalence_keys=equivalence_keys,
    )


def iter_training_examples(decisions_dir: Path, games_jsonl: Path, encoder: FeatureEncoder) -> Iterator[TrainingExample]:
    games = load_games(games_jsonl)
    for env in iter_decisions(decisions_dir, games):
        example = envelope_to_example(env, encoder)
        if example is not None:
            yield example


def iter_training_examples_for_files(
    files: list[Path],
    games_jsonl: Path,
    encoder: FeatureEncoder,
    keep_record: Callable[[dict[str, Any], dict[str, Any] | None], bool] | None = None,
) -> Iterator[TrainingExample]:
    """Yield examples only from the given decision files (one file == one game).

    Used for game-level sampling and a game-level train/val split, so decisions from
    the same game never straddle the split (no state leakage between train and val).

    ``keep_record`` is an optional per-decision predicate ``(record, game) -> bool``;
    records for which it returns False are skipped (e.g. winners-only training via
    :func:`decision_is_by_winner`).
    """
    games = load_games(games_jsonl)
    for path in files:
        for line_no, record in iter_jsonl(path):
            game = games.get(str(record.get("game_id")))
            if keep_record is not None and not keep_record(record, game):
                continue
            env = DecisionEnvelope(path=path, line_no=line_no, record=record, game=game)
            example = envelope_to_example(env, encoder)
            if example is not None:
                yield example
