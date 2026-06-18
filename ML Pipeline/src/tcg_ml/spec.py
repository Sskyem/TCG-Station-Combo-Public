from __future__ import annotations

import json
from dataclasses import dataclass
from pathlib import Path
from typing import Any


@dataclass(frozen=True)
class FeatureSpec:
    raw: dict[str, Any]

    @property
    def effect_types(self) -> list[str]:
        return list(self.raw["effect_types"])

    @property
    def targets(self) -> list[str]:
        return list(self.raw["targets"])

    @property
    def pokemon_types(self) -> list[str]:
        return list(self.raw["pokemon_types"])

    @property
    def special_conditions(self) -> list[str]:
        return list(self.raw["special_conditions"])

    @property
    def action_types(self) -> list[str]:
        return list(self.raw["action_types"])

    @property
    def attack_k(self) -> int:
        return int(self.raw["attack_K"])

    @property
    def trainer_k(self) -> int:
        return int(self.raw["trainer_K"])

    def norm(self, name: str) -> float:
        return float(self.raw["normalization"][name])


def load_spec(path: Path) -> FeatureSpec:
    with path.open("r", encoding="utf-8-sig") as handle:
        return FeatureSpec(json.load(handle))
