"""Quick CPU/CUDA/MPS training throughput benchmark for the BC model.

Loads a fixed slice of the dataset once, then times identical train steps on
each available device. Isolates device compute from the shared (CPU-bound)
feature-extraction cost so the comparison reflects the training loop itself.
"""
from __future__ import annotations

import sys
import time
import argparse
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

from tcg_ml.cards import CardCatalog
from tcg_ml.dataset import iter_training_examples
from tcg_ml.features import FeatureEncoder
from tcg_ml.model import ActionScorer, torch_device_report
from tcg_ml.paths import add_path_args, get_paths_from_args
from tcg_ml.spec import load_spec


def time_device(device_str: str, examples, input_dim: int, steps: int) -> float:
    import torch

    device = torch.device(device_str)
    model = ActionScorer(input_dim).module().to(device)
    optimizer = torch.optim.Adam(model.parameters(), lr=1e-4)
    loss_fn = torch.nn.CrossEntropyLoss()
    model.train(True)

    # Warm-up (CUDA lazily compiles/loads kernels on first use).
    for item in examples[: min(50, len(examples))]:
        x = torch.as_tensor(item.x, dtype=torch.float32, device=device)
        y = torch.as_tensor([item.y], dtype=torch.long, device=device)
        loss = loss_fn(model(x).view(1, -1), y)
        optimizer.zero_grad(set_to_none=True)
        loss.backward()
        optimizer.step()
    if device.type == "cuda":
        torch.cuda.synchronize()
    elif device.type == "mps" and hasattr(torch, "mps") and hasattr(torch.mps, "synchronize"):
        torch.mps.synchronize()

    started = time.perf_counter()
    done = 0
    while done < steps:
        for item in examples:
            x = torch.as_tensor(item.x, dtype=torch.float32, device=device)
            y = torch.as_tensor([item.y], dtype=torch.long, device=device)
            loss = loss_fn(model(x).view(1, -1), y)
            optimizer.zero_grad(set_to_none=True)
            loss.backward()
            optimizer.step()
            done += 1
            if done >= steps:
                break
    if device.type == "cuda":
        torch.cuda.synchronize()
    elif device.type == "mps" and hasattr(torch, "mps") and hasattr(torch.mps, "synchronize"):
        torch.mps.synchronize()
    return time.perf_counter() - started


def main() -> int:
    parser = argparse.ArgumentParser(description="Benchmark ML training throughput on this device.")
    add_path_args(parser)
    parser.add_argument("n_load", nargs="?", type=int, default=4000)
    parser.add_argument("steps", nargs="?", type=int, default=8000)
    args = parser.parse_args()

    n_load = args.n_load
    steps = args.steps
    paths = get_paths_from_args(args)
    spec = load_spec(paths.feature_spec)
    catalog = CardCatalog.load(paths.cards_dir)
    encoder = FeatureEncoder(spec=spec, catalog=catalog)

    print(f"loading up to {n_load} examples ...", flush=True)
    examples = []
    for ex in iter_training_examples(paths.decisions_dir, paths.games_jsonl, encoder):
        examples.append(ex)
        if len(examples) >= n_load:
            break
    input_dim = int(examples[0].x.shape[1])
    print(f"loaded={len(examples)} input_dim={input_dim} steps_per_device={steps}", flush=True)

    cpu_s = time_device("cpu", examples, input_dim, steps)
    print(f"CPU : {cpu_s:.2f}s  ({steps / cpu_s:,.0f} steps/s)", flush=True)

    report = torch_device_report()
    timings = {"cpu": cpu_s}

    cuda = report["devices"]["cuda"]
    if cuda["usable"]:
        print(f"CUDA: {cuda['device_name']}", flush=True)
        cuda_s = time_device("cuda", examples, input_dim, steps)
        timings["cuda"] = cuda_s
        print(f"CUDA: {cuda_s:.2f}s  ({steps / cuda_s:,.0f} steps/s)", flush=True)
    else:
        print(f"CUDA unavailable: {cuda['reason']}", flush=True)

    mps = report["devices"]["mps"]
    if mps["usable"]:
        mps_s = time_device("mps", examples, input_dim, steps)
        timings["mps"] = mps_s
        print(f"MPS : {mps_s:.2f}s  ({steps / mps_s:,.0f} steps/s)", flush=True)
    else:
        print(f"MPS unavailable: {mps['reason']}", flush=True)

    fastest, fastest_s = min(timings.items(), key=lambda kv: kv[1])
    slowest_s = max(timings.values())
    print(f"=> {fastest} is fastest ({slowest_s / max(1e-9, fastest_s):.2f}x vs slowest tested)", flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
