from __future__ import annotations

import argparse
import platform
import shutil
import subprocess
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))


def run(cmd: list[str]) -> None:
    print(" ".join(cmd), flush=True)
    subprocess.check_call(cmd)


def has_cuda() -> bool:
    if shutil.which("nvidia-smi") is None:
        return False
    result = subprocess.run(["nvidia-smi"], capture_output=True)
    return result.returncode == 0


def environment_kind() -> str:
    if has_cuda():
        return "cuda"
    if platform.system() == "Darwin" and platform.machine() == "arm64":
        return "apple_silicon"
    return "cpu"


def main() -> int:
    parser = argparse.ArgumentParser(description="Install or repair ML dashboard dependencies.")
    parser.add_argument("--profile", choices=["cpu", "gpu", "auto"], default="auto")
    args = parser.parse_args()

    profile = args.profile
    if profile == "auto":
        kind = environment_kind()
        profile = "gpu" if kind == "cuda" else "cpu"
        print(f"Auto-detected environment: {kind}; setup profile: {profile}", flush=True)

    run([sys.executable, "-m", "pip", "install", "--upgrade", "pip"])
    if profile == "gpu":
        run([
            sys.executable,
            "-m",
            "pip",
            "install",
            "torch",
            "torchvision",
            "torchaudio",
            "--index-url",
            "https://download.pytorch.org/whl/cu128",
        ])
        run([sys.executable, "-m", "pip", "install", "fastapi", "uvicorn[standard]", "pydantic", "numpy"])
    else:
        run([sys.executable, "-m", "pip", "install", "-r", "requirements.txt"])
    try:
        from tcg_ml.model import torch_device_report

        print("Torch device report:", flush=True)
        print(torch_device_report(), flush=True)
    except Exception as exc:
        print(f"Torch device report unavailable: {exc}", flush=True)
    print("Environment setup complete.", flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
