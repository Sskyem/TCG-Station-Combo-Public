from __future__ import annotations

import os
from dataclasses import dataclass
from pathlib import Path


@dataclass(frozen=True)
class ProjectPaths:
    root: Path
    build_root: Path | None
    python_dir: Path
    ml_dir: Path
    cards_dir: Path
    decks_dir: Path
    logs_root: Path
    logs_ml_dir: Path
    decisions_dir: Path
    games_jsonl: Path
    feature_spec: Path
    models_dir: Path
    runs_dir: Path
    reports_dir: Path


def _env_path(name: str) -> Path | None:
    value = os.environ.get(name)
    return Path(value).expanduser().resolve() if value else None


def _optional_path(value) -> Path | None:
    return Path(value).expanduser() if value is not None else None


def find_pipeline_root(start: Path | None = None) -> Path:
    current = (start or Path(__file__)).resolve()
    for candidate in [current, *current.parents]:
        if (candidate / "feature_spec.json").exists() and (candidate / "src" / "tcg_ml").exists():
            return candidate
    raise RuntimeError("Could not find ML Pipeline root from current path.")


def find_game_root(start: Path | None = None) -> Path:
    current = (start or Path(__file__)).resolve()
    for candidate in [current, *current.parents]:
        if (candidate / "Assets").exists() and (candidate / "Cards").exists():
            return candidate
        sibling = candidate / "TCG_Station"
        if (sibling / "Assets").exists() and (sibling / "Cards").exists():
            return sibling
    raise RuntimeError(
        "Could not find TCG_Station root. Pass --root or set TCG_GAME_ROOT."
    )


def get_paths(
    root: Path | None = None,
    *,
    ml_root: Path | None = None,
    build_root: Path | None = None,
    logs_dir: Path | None = None,
    cards_dir: Path | None = None,
    decks_dir: Path | None = None,
) -> ProjectPaths:
    pipeline_root = (_optional_path(ml_root) or _env_path("TCG_ML_ROOT") or find_pipeline_root()).resolve()
    game_root = (_optional_path(root) or _env_path("TCG_GAME_ROOT") or find_game_root()).resolve()
    resolved_build_root = (_optional_path(build_root) or _env_path("TCG_BUILD_ROOT"))
    if resolved_build_root is not None:
        resolved_build_root = resolved_build_root.resolve()

    resolved_logs_ml = _optional_path(logs_dir) or _env_path("TCG_LOGS_DIR")
    if resolved_logs_ml is None:
        base_for_logs = resolved_build_root or game_root
        resolved_logs_ml = base_for_logs / "Logs Export" / "ML"
    resolved_logs_ml = resolved_logs_ml.resolve()
    logs_root = resolved_logs_ml.parent

    resolved_cards = _optional_path(cards_dir) or _env_path("TCG_CARDS_DIR")
    if resolved_cards is None:
        build_cards = resolved_build_root / "Cards" if resolved_build_root is not None else None
        resolved_cards = build_cards if build_cards is not None and build_cards.exists() else game_root / "Cards"
    resolved_cards = resolved_cards.resolve()

    resolved_decks = _optional_path(decks_dir) or _env_path("TCG_DECKS_DIR")
    if resolved_decks is None:
        build_decks = resolved_build_root / "Decks" if resolved_build_root is not None else None
        resolved_decks = build_decks if build_decks is not None and build_decks.exists() else game_root / "Decks"
    resolved_decks = resolved_decks.resolve()

    return ProjectPaths(
        root=game_root,
        build_root=resolved_build_root,
        python_dir=pipeline_root,
        ml_dir=pipeline_root,
        cards_dir=resolved_cards,
        decks_dir=resolved_decks,
        logs_root=logs_root,
        logs_ml_dir=resolved_logs_ml,
        decisions_dir=resolved_logs_ml / "Decisions",
        games_jsonl=resolved_logs_ml / "games.jsonl",
        feature_spec=pipeline_root / "feature_spec.json",
        models_dir=pipeline_root / "models",
        runs_dir=pipeline_root / "runs",
        reports_dir=pipeline_root / "reports",
    )


def add_path_args(parser) -> None:
    parser.add_argument("--ml-root", type=Path, default=None, help="ML Pipeline root. Env: TCG_ML_ROOT.")
    parser.add_argument("--root", type=Path, default=None, help="Unity TCG_Station root. Env: TCG_GAME_ROOT.")
    parser.add_argument("--build-root", type=Path, default=None, help="Built game folder. Env: TCG_BUILD_ROOT.")
    parser.add_argument("--logs-dir", type=Path, default=None, help="Logs Export/ML directory. Env: TCG_LOGS_DIR.")
    parser.add_argument("--cards-dir", type=Path, default=None, help="Cards directory. Env: TCG_CARDS_DIR.")
    parser.add_argument("--decks-dir", type=Path, default=None, help="Decks directory. Env: TCG_DECKS_DIR.")


def get_paths_from_args(args) -> ProjectPaths:
    return get_paths(
        getattr(args, "root", None),
        ml_root=getattr(args, "ml_root", None),
        build_root=getattr(args, "build_root", None),
        logs_dir=getattr(args, "logs_dir", None),
        cards_dir=getattr(args, "cards_dir", None),
        decks_dir=getattr(args, "decks_dir", None),
    )
