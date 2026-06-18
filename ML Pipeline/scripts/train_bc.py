from __future__ import annotations

import argparse
import json
import os
import random
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

from tcg_ml.cards import CardCatalog
from tcg_ml.dataset import (
    filter_winner_files,
    iter_training_examples_for_files,
    make_keep_predicate,
    split_train_val_files,
)
from tcg_ml.features import FeatureEncoder
from tcg_ml.logs import ROOT_SOURCE, decision_sources, iter_decision_files
from tcg_ml.model import ActionScorer, select_device, torch_device_report
from tcg_ml.paths import add_path_args, get_paths_from_args
from tcg_ml.spec import load_spec


def main() -> int:
    parser = argparse.ArgumentParser(description="Train baseline behavioral cloning model.")
    add_path_args(parser)
    parser.add_argument("--device", choices=["auto", "cpu", "cuda", "mps"], default="auto")
    parser.add_argument(
        "--from-model",
        type=Path,
        default=None,
        metavar="PATH",
        help=(
            "Fine-tune from an existing checkpoint instead of starting from scratch. "
            "Loads weights and input_dim from PATH (a .pt file). The feature spec must match "
            "the current dataset. Use a lower --lr (e.g. 1e-5) for fine-tuning. "
            "Example: --from-model models/bc_20260601_220135.pt --lr 1e-5 --winners-only"
        ),
    )
    parser.add_argument(
        "--max-games",
        type=int,
        default=0,
        help="Train on this many randomly-sampled games (decision files). 0 = use all games.",
    )
    parser.add_argument(
        "--max-decisions",
        type=int,
        default=0,
        help="Optional hard safety cap on total decisions loaded (memory guard). 0 = no cap.",
    )
    parser.add_argument(
        "--winners-only",
        action="store_true",
        help="Train only on decisions made by the player who won the game (needs games.jsonl winner).",
    )
    parser.add_argument(
        "--profile",
        nargs="+",
        default=None,
        metavar="PROFILE",
        help=(
            "Train only on decisions made by an AlgorithmBrain running one of these profiles "
            "(e.g. Standard Ramp). Multiple profiles allowed. Reads games.jsonl brain_a/brain_b. "
            "Use it to train one model per profile or to keep training on specific experts "
            "when the dataset mixes profiles."
        ),
    )
    parser.add_argument(
        "--sources",
        nargs="+",
        default=None,
        metavar="SOURCE",
        help=(
            "Which decision-log sources to train on. A source is a subfolder of "
            "Logs Export/ML/Decisions/ — 'benchmark' (Algorithm-vs-Algorithm runs), 'interactive' "
            "(watchable AI-vs-AI / Human-vs-AI), or 'legacy' (older files in the Decisions/ root). "
            "Use 'all' for every source. Default: every non-legacy source present, so old root logs "
            "are excluded unless you ask for them with --sources legacy (or all)."
        ),
    )
    parser.add_argument(
        "--grad-accum",
        type=int,
        default=1,
        help=(
            "Accumulate gradients over this many decisions before optimizer.step(). Examples are "
            "ragged (variable candidate count) so true batching is awkward; accumulation gives a "
            "larger effective batch — smoother, faster training. 1 = original per-decision SGD."
        ),
    )
    parser.add_argument("--epochs", type=int, default=3)
    parser.add_argument("--lr", type=float, default=1e-4)
    parser.add_argument("--val-ratio", type=float, default=0.1)
    parser.add_argument("--seed", type=int, default=1234)
    parser.add_argument("--log-every", type=int, default=1000)
    parser.add_argument(
        "--patience",
        type=int,
        default=4,
        help=(
            "Early stopping: stop after this many epochs without val_loss improvement. 0 = disabled. "
            "Default 4 — val_loss on a small val set is noisy, so a single bad epoch must not stop "
            "training. The model on disk is always the BEST epoch, never the last one."
        ),
    )
    args = parser.parse_args()

    import signal

    import torch

    random.seed(args.seed)
    torch.manual_seed(args.seed)

    # PyTorch defaulted to 2 intra-op threads on this hardware; use ~3/4 of the cores
    # for CPU math, keeping headroom so the dashboard server and OS stay responsive.
    # Note: on a pure-CPU device this changes float reduction order, so metrics can
    # drift in the last decimal places; on MPS/CUDA results are unaffected.
    cpu_threads = max(1, ((os.cpu_count() or 4) * 3) // 4)
    torch.set_num_threads(cpu_threads)
    print(f"cpu_threads={cpu_threads} cores={os.cpu_count()}", flush=True)

    # Graceful stop: the dashboard's "Stop training" sends an interrupt (CTRL_BREAK on
    # Windows / SIGTERM elsewhere) instead of hard-killing us, so we can save the model
    # learned so far before exiting cleanly.
    stop_requested = {"flag": False}

    def request_stop(signum, _frame):
        if not stop_requested["flag"]:
            print(f"stop_requested signal={signum} — finishing current step, then saving and exiting.", flush=True)
        stop_requested["flag"] = True

    for sig_name in ("SIGINT", "SIGTERM", "SIGBREAK"):
        sig = getattr(signal, sig_name, None)
        if sig is not None:
            try:
                signal.signal(sig, request_stop)
            except (ValueError, OSError):
                pass

    paths = get_paths_from_args(args)
    paths.models_dir.mkdir(parents=True, exist_ok=True)
    paths.runs_dir.mkdir(parents=True, exist_ok=True)

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
    print("device_report=" + json.dumps(device_report, sort_keys=True), flush=True)

    # Sample whole games (one decision file == one game), seeded for reproducibility.
    # This avoids the chronological bias of a decision-prefix cut and keeps every
    # selected game's full decision mix (including rare late-game Attack/Retreat steps).
    # Resolve which log sources (Decisions/ subfolders) to train on. Default keeps benchmark and
    # interactive logs unmixed from the legacy root logs: train on every non-legacy source present.
    available_sources = decision_sources(paths.decisions_dir)
    if args.sources and any(str(s).lower() == "all" for s in args.sources):
        selected_sources = None  # every source, incl. legacy root
    elif args.sources:
        selected_sources = list(args.sources)
    else:
        selected_sources = [s for s in available_sources if s != ROOT_SOURCE]
        if not selected_sources:
            raise RuntimeError(
                "No per-context decision logs found (e.g. Decisions/benchmark, Decisions/interactive). "
                f"Available sources: {available_sources or 'none'}. Pass --sources legacy to train on "
                "the Decisions/ root logs, or --sources all for everything."
            )
    print(
        f"sources={'all' if selected_sources is None else selected_sources} "
        f"available={available_sources}",
        flush=True,
    )

    all_files = list(iter_decision_files(paths.decisions_dir, sources=selected_sources))
    if not all_files:
        raise RuntimeError(
            f"No decision files found for sources={'all' if selected_sources is None else selected_sources}. "
            f"Available: {available_sources or 'none'}."
        )

    # Winners-only: keep only games with a recorded A/B winner, so the game count / split
    # below are meaningful (games without metadata contribute nothing) and the per-record
    # predicate drops the losing player's decisions within those games.
    # Non-trainable categories (TurnMeta, GameEnd) are excluded downstream by
    # usable_decision (logs.NON_TRAINABLE_CATEGORIES), so no predicate is needed for them.
    if args.winners_only:
        before = len(all_files)
        all_files, games_with_winner = filter_winner_files(all_files, paths.games_jsonl)
        print(f"winners_only=1 games_with_winner={games_with_winner} files_kept={len(all_files)}/{before}", flush=True)
        if not all_files:
            raise RuntimeError("winners-only: no decision files have a recorded winner in games.jsonl.")

    # Per-record predicate composing winners-only and profile filters (None = keep all records).
    # Profile filtering is per-decision (a game can mix two seat profiles), so it is not a file filter.
    keep_record = make_keep_predicate(winners_only=args.winners_only, profile=args.profile)

    profile_str = ",".join(args.profile) if args.profile else "-"
    print(f"filters: winners_only={args.winners_only} profile={profile_str}", flush=True)

    # Game-level train/val split (shared with evaluate_model so a held-out eval can
    # reconstruct the exact validation games). Validation holds out whole games, so no
    # decision from a training game ever appears in validation (prevents state leakage).
    train_files, val_files = split_train_val_files(all_files, args.seed, args.val_ratio, args.max_games)
    all_files = train_files + val_files
    print(f"selected_games={len(all_files)} train_games={len(train_files)} val_games={len(val_files)}", flush=True)

    load_started = time.perf_counter()

    def load(file_list, cap: int, label: str) -> list:
        out: list = []
        for example in iter_training_examples_for_files(file_list, paths.games_jsonl, encoder, keep_record):
            out.append(example)
            if args.log_every and len(out) % args.log_every == 0:
                elapsed = time.perf_counter() - load_started
                print(f"loaded_{label}_decisions={len(out)} elapsed_s={elapsed:.1f}", flush=True)
            if cap and len(out) >= cap:
                break
        return out

    train_examples = load(train_files, args.max_decisions, "train")
    val_examples = load(val_files, 0, "val")
    if not train_examples:
        raise RuntimeError("No usable training examples found.")
    if not val_examples:
        # Single-game or tiny dataset: carve a small validation slice from train.
        random.shuffle(train_examples)
        cut = max(1, int(len(train_examples) * args.val_ratio))
        val_examples = train_examples[:cut]
        train_examples = train_examples[cut:] or train_examples
    examples = train_examples + val_examples

    input_dim = int(train_examples[0].x.shape[1])

    if args.from_model is not None:
        # Fine-tuning: load weights from an existing checkpoint.
        # The checkpoint's input_dim must match the current feature set.
        ckpt = torch.load(args.from_model, map_location="cpu")
        ckpt_input_dim = int(ckpt["input_dim"])
        if ckpt_input_dim != input_dim:
            raise RuntimeError(
                f"--from-model input_dim mismatch: checkpoint has {ckpt_input_dim}, "
                f"current feature set has {input_dim}. "
                "Make sure you use the same Cards/ and feature spec."
            )
        model = ActionScorer(input_dim).module()
        model.load_state_dict(ckpt["model_state"])
        model.to(device)
        print(f"fine_tuning from={args.from_model} input_dim={input_dim}", flush=True)
    else:
        model = ActionScorer(input_dim).module().to(device)

    optimizer = torch.optim.Adam(model.parameters(), lr=args.lr, weight_decay=1e-5)
    loss_fn = torch.nn.CrossEntropyLoss()

    run_name = time.strftime("bc_%Y%m%d_%H%M%S")
    grad_accum = max(1, args.grad_accum)

    def run_epoch(items, train: bool, epoch: int):
        model.train(train)
        total_loss = 0.0
        correct = 0
        total = 0
        per_cat: dict[str, list[int]] = {}
        phase = "train" if train else "val"
        started = time.perf_counter()
        pending = 0  # backward passes accumulated since the last optimizer.step()

        # On MPS/CUDA every .item() stalls the pipeline, and the old loop did two per
        # decision (argmax + loss). Predictions and losses stay on-device and are drained
        # in ONE transfer per flush window. Python-side accumulation runs over the same
        # float32 values in the same order, so all reported metrics stay bit-identical.
        flush_every = args.log_every if args.log_every else 1000
        pend_preds: list = []
        pend_losses: list = []
        pend_items: list = []

        def flush_pending() -> None:
            nonlocal total_loss, correct, total
            if not pend_items:
                return
            preds = torch.stack(pend_preds).view(-1).cpu().tolist()
            losses = torch.stack(pend_losses).view(-1).cpu().tolist()
            for it, pred_raw, loss_val in zip(pend_items, preds, losses):
                pred = int(pred_raw)
                hit = int(it.equivalence_keys[pred] == it.equivalence_keys[it.y])
                total_loss += float(loss_val)
                correct += hit
                total += 1
                bucket = per_cat.setdefault(it.category, [0, 0])
                bucket[0] += hit
                bucket[1] += 1
            pend_preds.clear()
            pend_losses.clear()
            pend_items.clear()

        if train:
            optimizer.zero_grad(set_to_none=True)
        for step, item in enumerate(items, start=1):
            x = torch.as_tensor(item.x, dtype=torch.float32, device=device)
            y = torch.as_tensor([item.y], dtype=torch.long, device=device)
            with torch.set_grad_enabled(train):
                logits = model(x).view(1, -1)
                loss = loss_fn(logits, y)
                if train:
                    # Divide by grad_accum so the summed gradients equal the MEAN over the
                    # virtual batch — equivalent to a real batch of grad_accum examples.
                    (loss / grad_accum).backward()
                    pending += 1
                    if pending >= grad_accum:
                        optimizer.step()
                        optimizer.zero_grad(set_to_none=True)
                        pending = 0
            pend_preds.append(logits.detach().argmax(dim=1))
            pend_losses.append(loss.detach())
            pend_items.append(item)
            if len(pend_items) >= flush_every:
                flush_pending()
            if stop_requested["flag"]:
                break
            if train and args.log_every and step % args.log_every == 0:
                elapsed = time.perf_counter() - started
                print(
                    f"epoch={epoch}/{args.epochs} phase={phase} step={step}/{len(items)} "
                    f"loss={total_loss / max(1, total):.4f} acc={correct / max(1, total):.3f} elapsed_s={elapsed:.1f}",
                    flush=True,
                )
        # Flush a trailing partial virtual batch so its gradients aren't discarded.
        if train and pending > 0:
            optimizer.step()
            optimizer.zero_grad(set_to_none=True)
        flush_pending()
        avg_loss = total_loss / max(1, total)
        acc = correct / max(1, total)
        per_cat_acc = {k: v[0] / max(1, v[1]) for k, v in per_cat.items()}
        return avg_loss, acc, per_cat_acc

    model_path = paths.models_dir / f"{run_name}.pt"
    meta_path = model_path.with_suffix(".json")

    # Card/deck patch the dataset was generated under. The dashboard's "Download patch"
    # button writes current_patch.json; null means no patch was applied via the dashboard
    # on this machine (or the model predates patch tracking).
    patch_ts = None
    patch_no = None
    patch_marker = paths.ml_dir / "current_patch.json"
    if patch_marker.exists():
        try:
            marker = json.loads(patch_marker.read_text(encoding="utf-8"))
            if isinstance(marker, dict):
                patch_ts = marker.get("patch_ts")
                patch_no = marker.get("patch_no")
        except (OSError, ValueError):
            patch_ts = None
            patch_no = None

    # Lightweight sidecar metadata so the dashboard can show how each model was trained
    # without loading the heavy .pt. Updated in place after every epoch.
    train_meta = {
        "run": run_name,
        "winners_only": bool(args.winners_only),
        "profile": args.profile,
        # Persisted so evaluate_model can reconstruct the exact same file set (and thus the same
        # held-out val split). null == every source (legacy behaviour for older models).
        "sources": selected_sources,
        "grad_accum": int(args.grad_accum),
        "max_games": int(args.max_games),
        "from_model": str(args.from_model) if args.from_model else None,
        "training_stage": "fine_tune" if args.from_model else "training",
        "patch_no": patch_no,
        "patch_ts": patch_ts,
        "games_selected": len(all_files),
        "train_games": len(train_files),
        "val_games": len(val_files),
        "train_examples": len(train_examples),
        "val_examples": len(val_examples),
        "epochs_requested": args.epochs,
        "seed": args.seed,
        "val_ratio": args.val_ratio,
        "device": str(device),
        "completed_epochs": 0,
        "best_epoch": 0,
        "val_acc": None,
        "val_loss": None,
        "val_macro_acc": None,
        "val_accuracy_by_category": None,
        # Per-epoch curves so the dashboard can plot loss/accuracy from the sidecar alone,
        # even on a machine that never saw this run's train_*.log (e.g. a model synced over
        # from another session/machine). Source of truth for the Metrics tile when no log exists.
        "history": {"epochs": [], "train_loss": [], "train_acc": [], "val_loss": [], "val_acc": [], "val_macro_acc": []},
    }

    def write_meta() -> None:
        """Persist the sidecar JSON without touching the .pt (so the best weights stay)."""
        try:
            meta_path.write_text(json.dumps(train_meta, indent=2), encoding="utf-8")
        except OSError:
            pass

    def save_checkpoint(reason: str) -> None:
        torch.save(
            {
                "model_state": model.state_dict(),
                "input_dim": input_dim,
                "state_dim": encoder.state_dim,
                "action_dim": encoder.action_dim,
                "spec": spec.raw,
            },
            model_path,
        )
        try:
            meta_path.write_text(json.dumps(train_meta, indent=2), encoding="utf-8")
        except OSError:
            pass
        print(f"saved_model path={model_path} reason={reason}", flush=True)

    best_val = float("inf")
    best_epoch = 0
    epochs_no_improve = 0
    completed_epochs = 0
    stopped_early = False
    saved_best = False
    for epoch in range(1, args.epochs + 1):
        train_loss, train_acc, _ = run_epoch(train_examples, train=True, epoch=epoch)
        if stop_requested["flag"]:
            # Interrupted mid-epoch. Only overwrite the saved model if we never managed
            # to save a validated best yet — otherwise the best epoch stays on disk.
            if not saved_best:
                save_checkpoint(f"stop during epoch {epoch} (no validated best yet)")
                saved_best = True
            else:
                print(f"stop during epoch {epoch}: keeping best epoch {best_epoch} (val_loss={best_val:.4f})", flush=True)
            break
        val_loss, val_acc, per_cat = run_epoch(val_examples, train=False, epoch=epoch)
        completed_epochs = epoch
        # Macro-average treats every category equally, so the near-deterministic Attack
        # (~0.99) can't mask the hard decisions (e.g. AttachEnergy ~0.52) like the
        # decision-weighted micro accuracy (val_acc) does.
        macro_acc = sum(per_cat.values()) / max(1, len(per_cat))
        improved = val_loss < best_val - 1e-6
        print(
            f"epoch={epoch} train_loss={train_loss:.4f} train_acc={train_acc:.3f} "
            f"val_loss={val_loss:.4f} val_acc={val_acc:.3f} val_macro_acc={macro_acc:.3f}",
            flush=True,
        )
        print("val_accuracy_by_category=" + json.dumps(per_cat, sort_keys=True), flush=True)
        train_meta["completed_epochs"] = epoch
        hist = train_meta["history"]
        hist["epochs"].append(epoch)
        hist["train_loss"].append(round(train_loss, 4))
        hist["train_acc"].append(round(train_acc, 4))
        hist["val_loss"].append(round(val_loss, 4))
        hist["val_acc"].append(round(val_acc, 4))
        hist["val_macro_acc"].append(round(macro_acc, 4))
        # Save ONLY when validation improves: the model on disk is always the best epoch,
        # never a later (worse) one. This is the whole point of early stopping — the old
        # code overwrote the best with the last epoch right before stopping.
        if improved:
            best_val = val_loss
            best_epoch = epoch
            epochs_no_improve = 0
            train_meta.update({
                "best_epoch": epoch,
                "val_acc": round(val_acc, 4),
                "val_loss": round(val_loss, 4),
                "val_macro_acc": round(macro_acc, 4),
                "val_accuracy_by_category": {k: round(v, 4) for k, v in sorted(per_cat.items())},
            })
            save_checkpoint(f"best epoch {epoch} val_loss={val_loss:.4f}")
            saved_best = True
        else:
            epochs_no_improve += 1
            print(
                f"no_improve epoch={epoch} epochs_no_improve={epochs_no_improve}/{args.patience} "
                f"best_epoch={best_epoch} best_val_loss={best_val:.4f}",
                flush=True,
            )
            write_meta()  # keep completed_epochs current even on non-improving epochs
            if args.patience and epochs_no_improve >= args.patience:
                stopped_early = True
                print(f"early_stopping epoch={epoch} epochs_no_improve={epochs_no_improve} patience={args.patience} best_epoch={best_epoch}", flush=True)
                break
        if stop_requested["flag"]:
            break

    # Guarantee a model file exists even if stopped before the first epoch finished.
    if not model_path.exists():
        save_checkpoint("final fallback")
    write_meta()

    summary = {
        "run": run_name,
        "model_path": str(model_path),
        "device": str(device),
        "winners_only": bool(args.winners_only),
        "profile": args.profile,
        "games_selected": len(all_files),
        "train_games": len(train_files),
        "val_games": len(val_files),
        "examples": len(examples),
        "train_examples": len(train_examples),
        "val_examples": len(val_examples),
        "input_dim": input_dim,
        "completed_epochs": completed_epochs,
        "requested_epochs": args.epochs,
        "best_epoch": best_epoch,
        "best_val_loss": None if best_val == float("inf") else round(best_val, 4),
        "val_macro_acc": train_meta.get("val_macro_acc"),
        "stopped_early": stopped_early,
        "stopped_by_user": stop_requested["flag"],
    }
    print(json.dumps(summary, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
