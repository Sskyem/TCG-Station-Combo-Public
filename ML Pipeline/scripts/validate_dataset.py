from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

from tcg_ml.cards import CardCatalog
from tcg_ml.features import FeatureEncoder
from tcg_ml.logs import scan_dataset_incremental
from tcg_ml.paths import add_path_args, get_paths_from_args
from tcg_ml.spec import load_spec


def main() -> int:
    parser = argparse.ArgumentParser(description="Scan TCG Station ML decision logs.")
    add_path_args(parser)
    parser.add_argument("--json", action="store_true")
    args = parser.parse_args()

    paths = get_paths_from_args(args)
    spec = load_spec(paths.feature_spec)
    catalog = CardCatalog.load(paths.cards_dir)
    encoder = FeatureEncoder(spec=spec, catalog=catalog)
    # Same persistent per-file cache as the dashboard's /api/dataset/scan, so the CLI and the
    # dashboard stay consistent and only newly added decision files are parsed.
    stats = scan_dataset_incremental(
        paths.decisions_dir, paths.games_jsonl, cache_path=paths.runs_dir / "dataset_scan_cache.json"
    )
    stats.update({
        "cards_loaded": len(catalog),
        "state_dim": encoder.state_dim,
        "action_dim": encoder.action_dim,
        "input_dim": encoder.state_dim + encoder.action_dim,
    })

    if args.json:
        print(json.dumps(stats, indent=2, ensure_ascii=False))
    else:
        for key, value in stats.items():
            print(f"{key}: {value}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
