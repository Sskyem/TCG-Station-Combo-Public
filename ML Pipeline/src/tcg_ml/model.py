from __future__ import annotations

import platform


def _probe_torch_device(torch, device_type: str) -> tuple[bool, str | None]:
    try:
        x = torch.empty((1,), device=device_type)
        _ = (x + 1).item()
        if device_type == "cuda":
            torch.cuda.synchronize()
        return True, None
    except Exception as exc:
        return False, str(exc)


def torch_device_report() -> dict:
    import torch

    report: dict = {
        "python": platform.python_version(),
        "platform": platform.platform(),
        "machine": platform.machine(),
        "torch_version": torch.__version__,
        "selected_auto_device": "cpu",
        "devices": {
            "cpu": {"available": True, "usable": True, "reason": None},
            "cuda": {"available": False, "usable": False, "reason": None, "device_count": 0, "device_name": None},
            "mps": {"available": False, "usable": False, "reason": None, "built": False},
        },
    }

    cuda = report["devices"]["cuda"]
    try:
        cuda["available"] = bool(torch.cuda.is_available())
        cuda["device_count"] = int(torch.cuda.device_count()) if cuda["available"] else 0
        cuda["device_name"] = torch.cuda.get_device_name(0) if cuda["available"] else None
        if cuda["available"]:
            cuda["usable"], cuda["reason"] = _probe_torch_device(torch, "cuda")
        else:
            cuda["reason"] = "torch.cuda.is_available() is false"
    except Exception as exc:
        cuda["reason"] = str(exc)

    mps = report["devices"]["mps"]
    try:
        backend = getattr(torch.backends, "mps", None)
        mps["built"] = bool(backend is not None and backend.is_built())
        mps["available"] = bool(backend is not None and backend.is_available())
        if mps["available"]:
            mps["usable"], mps["reason"] = _probe_torch_device(torch, "mps")
        elif backend is None:
            mps["reason"] = "torch.backends.mps is missing"
        elif not mps["built"]:
            mps["reason"] = "PyTorch was not built with MPS support"
        else:
            mps["reason"] = "torch.backends.mps.is_available() is false"
    except Exception as exc:
        mps["reason"] = str(exc)

    if cuda["usable"]:
        report["selected_auto_device"] = "cuda"
    elif mps["usable"]:
        report["selected_auto_device"] = "mps"
    return report


def select_device(requested: str):
    import torch

    requested = (requested or "auto").lower()
    report = torch_device_report()
    devices = report["devices"]
    if requested == "auto":
        # Prefer usable CUDA, then usable Apple Silicon MPS, then CPU.
        return torch.device(str(report["selected_auto_device"]))
    if requested == "cuda" and not devices["cuda"]["usable"]:
        raise RuntimeError(f"CUDA requested but unusable: {devices['cuda']['reason']}")
    if requested == "mps" and not devices["mps"]["usable"]:
        raise RuntimeError(f"MPS requested but unusable: {devices['mps']['reason']}")
    return torch.device(requested)


class ActionScorer:
    def __init__(self, input_dim: int):
        import torch
        from torch import nn

        self.net = nn.Sequential(
            nn.Linear(input_dim, 512),
            nn.ReLU(),
            nn.Dropout(0.2),
            nn.Linear(512, 256),
            nn.ReLU(),
            nn.Dropout(0.2),
            nn.Linear(256, 64),
            nn.ReLU(),
            nn.Linear(64, 1),
        )

    def module(self):
        return self.net
