from __future__ import annotations

"""
Reconstruct missing games.jsonl winner rows and metadata from exported logs.

Why this exists: a game only gets a games.jsonl row when GameResultLogger writes one,
which needs Unity's BattleManager.OnGameOver (or, since the watchdog fix, a timeout
adjudication). Decision files that were imported/copied without their games.jsonl — or
produced by a run where the result writer didn't save — leave winner metadata empty, so
`--winners-only` training silently drops them.

The reconstruction core lives in `tcg_ml.backfill`; this script is the CLI wrapper.
The dashboard log-sync runs the same `backfill()` automatically after fetching logs.

Usage:
  python scripts/backfill_games.py                         # dry-run: print what WOULD be written
  python scripts/backfill_games.py --apply                  # append missing rows to games.jsonl
  python scripts/backfill_games.py --enrich-existing --apply # fill deck metadata from Deckbuilder exports
  python scripts/backfill_games.py --infer-decks-from-decisions --apply # infer deck metadata from snapshots
"""

import argparse
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

from tcg_ml.backfill import backfill, enrich_existing_metadata, infer_existing_decks_from_decisions
from tcg_ml.paths import add_path_args, get_paths_from_args


def main() -> int:
    parser = argparse.ArgumentParser(description="Backfill games.jsonl winners from decision logs.")
    add_path_args(parser)
    parser.add_argument("--apply", action="store_true", help="Append missing rows to games.jsonl (default: dry-run).")
    parser.add_argument("--max-turns", type=int, default=45, help="Turn limit used by the run (GameRulesConfig.maxTurns).")
    parser.add_argument("--enrich-existing", action="store_true",
                        help="Also fill missing deck/card-draw metadata in existing games.jsonl rows from Logs Export/Deckbuilder.")
    parser.add_argument("--infer-decks-from-decisions", action="store_true",
                        help="Infer missing deck_a/deck_b in existing games.jsonl rows from decision snapshots and Decks/*.json.")
    parser.add_argument("--deckbuilder-dir", type=Path, default=None,
                        help="Directory containing battle_*.json deckbuilder exports (default: configured Logs Export/Deckbuilder).")
    parser.add_argument("--infer-max-lines", type=int, default=160,
                        help="Maximum decision records to scan per file when inferring deck metadata.")
    parser.add_argument("--infer-min-margin", type=int, default=10,
                        help="Required score gap between the best and second-best deck inference.")
    args = parser.parse_args()

    paths = get_paths_from_args(args)
    if not paths.decisions_dir.exists() or not any(paths.decisions_dir.rglob("*_decisions.jsonl")):
        print(f"No decision files in {paths.decisions_dir}")
        return 1

    stats = backfill(paths.decisions_dir, paths.games_jsonl, max_turns=args.max_turns, apply=args.apply)
    c = stats["winners"]
    r = stats["end_reasons"]
    print(f"decision_files={stats['decision_files']} already_in_games={stats['already_in_games']} "
          f"to_reconstruct={stats['to_reconstruct']}")
    print(f"  winners A={c['A']} B={c['B']} Draw={c['Draw']} Unknown={c['Unknown']} "
          f"(usable A/B for winners-only: {stats['usable_ab']})")
    print(f"  end_reason ko={r['reconstructed_ko']} turn_limit={r['reconstructed_turn_limit']}")

    if not args.apply:
        print("dry-run: no missing winner rows written. Re-run with --apply to append these rows to games.jsonl.")
    else:
        print(f"appended {stats['written']} reconstructed rows to {paths.games_jsonl} "
              f"at {time.strftime('%Y-%m-%d %H:%M:%S')}")

    if args.enrich_existing:
        deckbuilder_dir = args.deckbuilder_dir or (paths.logs_root / "Deckbuilder")
        meta = enrich_existing_metadata(paths.games_jsonl, deckbuilder_dir, apply=args.apply)
        print(f"metadata_enrichment battle_metadata={meta['battle_metadata']} games_rows={meta['games_rows']} "
              f"matched_rows={meta['matched_rows']} updated_rows={meta['updated_rows']}")
        if not args.apply:
            print("dry-run: no existing rows enriched. Re-run with --apply to write metadata updates.")
        else:
            print(f"updated {meta['written']} existing rows in {paths.games_jsonl}")

    if args.infer_decks_from_decisions:
        meta = infer_existing_decks_from_decisions(
            paths.games_jsonl,
            paths.decisions_dir,
            root=paths.root,
            apply=args.apply,
            max_lines=args.infer_max_lines,
            min_margin=args.infer_min_margin,
        )
        print(f"deck_inference decision_files={meta['decision_files']} games_rows={meta['games_rows']} "
              f"candidate_rows={meta['candidate_rows']} updated_rows={meta['updated_rows']} "
              f"ambiguous_or_unknown={meta['ambiguous_or_unknown']}")
        if not args.apply:
            print("dry-run: no inferred deck metadata written. Re-run with --apply to update games.jsonl.")
        else:
            print(f"inferred deck metadata for {meta['written']} existing rows in {paths.games_jsonl}")

    if not args.apply:
        return 0

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
