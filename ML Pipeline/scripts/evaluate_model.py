from __future__ import annotations

import argparse
import json
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

from tcg_ml.cards import CardCatalog
from tcg_ml.dataset import (
    decision_is_by_winner,
    filter_winner_files,
    iter_training_examples_for_files,
    split_train_val_files,
)
from tcg_ml.features import FeatureEncoder
from tcg_ml.logs import iter_decision_files
from tcg_ml.model import ActionScorer, select_device, torch_device_report
from tcg_ml.paths import add_path_args, get_paths_from_args
from tcg_ml.spec import load_spec


def latest_model(models_dir: Path) -> Path:
    models = sorted(models_dir.glob("*.pt"), key=lambda p: p.stat().st_mtime, reverse=True)
    if not models:
        raise FileNotFoundError("No .pt model found in models/.")
    return models[0]


def main() -> int:
    parser = argparse.ArgumentParser(description="Evaluate a trained BC model on decision logs.")
    add_path_args(parser)
    parser.add_argument("--model", type=Path, default=None)
    parser.add_argument("--device", choices=["auto", "cpu", "cuda", "mps"], default="auto")
    parser.add_argument("--max-decisions", type=int, default=50000)
    parser.add_argument("--log-every", type=int, default=1000)
    parser.add_argument(
        "--split",
        choices=["all", "val"],
        default="all",
        help=(
            "'val' evaluates ONLY the held-out games the matching training run never saw "
            "(reconstructed from --seed/--val-ratio); 'all' scans every game (in-sample, "
            "so the number is optimistic). Use 'val' for an honest generalization estimate."
        ),
    )
    parser.add_argument(
        "--winners-only",
        action=argparse.BooleanOptionalAction,
        default=None,
        help=(
            "Filter to the winner's decisions. Default: inherit from the model sidecar when "
            "--split val (so the file set matches that run), else off. --no-winners-only forces off."
        ),
    )
    parser.add_argument(
        "--sources",
        nargs="+",
        default=None,
        metavar="SOURCE",
        help=(
            "Decision-log sources to evaluate on (Decisions/ subfolders, e.g. benchmark interactive "
            "legacy; 'all' for every source). Default: the model sidecar's training sources, so a "
            "--split val eval reconstructs the exact held-out games."
        ),
    )
    parser.add_argument("--seed", type=int, default=None, help="Split seed. Default: model sidecar, else 1234.")
    parser.add_argument("--val-ratio", type=float, default=None, help="Holdout fraction. Default: model sidecar, else 0.1.")
    parser.add_argument(
        "--max-games",
        type=int,
        default=None,
        help="Match a training run's --max-games so its exact split is reconstructed. Default: model sidecar, else 0 (all games).",
    )
    args = parser.parse_args()

    import torch

    paths = get_paths_from_args(args)
    paths.reports_dir.mkdir(parents=True, exist_ok=True)
    spec = load_spec(paths.feature_spec)
    catalog = CardCatalog.load(paths.cards_dir)
    encoder = FeatureEncoder(spec=spec, catalog=catalog)
    device_report = torch_device_report()
    device = select_device(args.device)
    print(
        "device_auto="
        + str(device_report.get("selected_auto_device"))
        + " requested="
        + str(args.device)
        + " selected="
        + str(device),
        flush=True,
    )

    model_path = args.model or latest_model(paths.models_dir)
    checkpoint = torch.load(model_path, map_location="cpu")
    model = ActionScorer(int(checkpoint["input_dim"])).module()
    model.load_state_dict(checkpoint["model_state"])
    model.to(device)
    model.eval()

    # Inherit split parameters from the model's training sidecar when not given, so a
    # `--split val` eval reconstructs the exact games that run held out without the user
    # re-typing seed / val-ratio / winners-only.
    meta: dict = {}
    meta_path = model_path.with_suffix(".json")
    if meta_path.exists():
        try:
            meta = json.loads(meta_path.read_text(encoding="utf-8-sig"))
        except (OSError, json.JSONDecodeError):
            meta = {}
    seed = args.seed if args.seed is not None else int(meta.get("seed", 1234))
    val_ratio = args.val_ratio if args.val_ratio is not None else float(meta.get("val_ratio", 0.1))
    max_games = args.max_games if args.max_games is not None else int(meta.get("max_games", 0))
    if args.winners_only is not None:
        winners_only = args.winners_only
    elif args.split == "val":
        winners_only = bool(meta.get("winners_only", False))
    else:
        winners_only = False

    # Resolve the file set the same way training did: same log sources, optional winner-file filter,
    # then the shared seeded game-level split. This guarantees `--split val` sees only held-out games.
    if args.sources and any(str(s).lower() == "all" for s in args.sources):
        sources = None
    elif args.sources:
        sources = list(args.sources)
    else:
        sources = meta.get("sources", None)  # null/absent (older models) == every source
    files = list(iter_decision_files(paths.decisions_dir, sources=sources))
    keep_record = None
    if winners_only:
        files, _ = filter_winner_files(files, paths.games_jsonl)
        keep_record = decision_is_by_winner
    train_files, val_files = split_train_val_files(files, seed, val_ratio, max_games)
    eval_files = val_files if args.split == "val" else train_files + val_files
    if not eval_files:
        raise RuntimeError(f"No games to evaluate for split={args.split} (seed={seed}, val_ratio={val_ratio}).")
    print(
        f"eval split={args.split} games={len(eval_files)} winners_only={winners_only} "
        f"seed={seed} val_ratio={val_ratio}",
        flush=True,
    )

    total = 0
    correct = 0
    exact_correct = 0
    top3_correct = 0
    exact_top3_correct = 0
    per_category: dict[str, dict[str, int]] = {}
    started = time.perf_counter()

    with torch.no_grad():
        for example in iter_training_examples_for_files(eval_files, paths.games_jsonl, encoder, keep_record):
            x = torch.as_tensor(example.x, dtype=torch.float32, device=device)
            logits = model(x).view(-1)
            pred = int(torch.argmax(logits).item())
            top3 = [int(i) for i in torch.topk(logits, k=min(3, logits.numel())).indices.tolist()]
            exact_hit = int(pred == example.y)
            exact_top3_hit = int(example.y in top3)
            expert_key = example.equivalence_keys[example.y]
            hit = int(example.equivalence_keys[pred] == expert_key)
            top3_hit = int(any(example.equivalence_keys[i] == expert_key for i in top3))

            total += 1
            correct += hit
            exact_correct += exact_hit
            top3_correct += top3_hit
            exact_top3_correct += exact_top3_hit
            bucket = per_category.setdefault(
                example.category,
                {"total": 0, "correct": 0, "exact_correct": 0, "top3_correct": 0, "exact_top3_correct": 0},
            )
            bucket["total"] += 1
            bucket["correct"] += hit
            bucket["exact_correct"] += exact_hit
            bucket["top3_correct"] += top3_hit
            bucket["exact_top3_correct"] += exact_top3_hit

            if args.log_every and total % args.log_every == 0:
                elapsed = time.perf_counter() - started
                print(
                    f"evaluated={total} accuracy={correct / max(1, total):.3f} "
                    f"top3={top3_correct / max(1, total):.3f} elapsed_s={elapsed:.1f}",
                    flush=True,
                )
            if args.max_decisions and total >= args.max_decisions:
                break

    per_category_report = {
        category: {
            "total": values["total"],
            "accuracy": values["correct"] / max(1, values["total"]),
            "exact_accuracy": values["exact_correct"] / max(1, values["total"]),
            "top3_accuracy": values["top3_correct"] / max(1, values["total"]),
            "exact_top3_accuracy": values["exact_top3_correct"] / max(1, values["total"]),
        }
        for category, values in sorted(per_category.items())
    }

    # Macro-average gives every category equal weight, so a near-deterministic category
    # (Attack ~0.99) can't inflate the headline number above the hard ones (AttachEnergy ~0.52).
    n_cat = max(1, len(per_category_report))
    macro_accuracy = sum(v["accuracy"] for v in per_category_report.values()) / n_cat
    macro_top3_accuracy = sum(v["top3_accuracy"] for v in per_category_report.values()) / n_cat
    exact_macro_accuracy = sum(v["exact_accuracy"] for v in per_category_report.values()) / n_cat
    exact_macro_top3_accuracy = sum(v["exact_top3_accuracy"] for v in per_category_report.values()) / n_cat

    report = {
        "model_path": str(model_path),
        "device": str(device),
        "split": args.split,
        "split_games": len(eval_files),
        "winners_only": winners_only,
        "seed": seed,
        "val_ratio": val_ratio,
        "evaluated_decisions": total,
        "accuracy": correct / max(1, total),
        "exact_accuracy": exact_correct / max(1, total),
        "top3_accuracy": top3_correct / max(1, total),
        "exact_top3_accuracy": exact_top3_correct / max(1, total),
        "macro_accuracy": macro_accuracy,
        "macro_top3_accuracy": macro_top3_accuracy,
        "exact_macro_accuracy": exact_macro_accuracy,
        "exact_macro_top3_accuracy": exact_macro_top3_accuracy,
        "per_category": per_category_report,
        "elapsed_s": time.perf_counter() - started,
    }

    report_path = paths.reports_dir / f"eval_{time.strftime('%Y%m%d_%H%M%S')}.json"
    with report_path.open("w", encoding="utf-8") as handle:
        json.dump(report, handle, indent=2)
    print(json.dumps({**report, "report_path": str(report_path)}, indent=2), flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
