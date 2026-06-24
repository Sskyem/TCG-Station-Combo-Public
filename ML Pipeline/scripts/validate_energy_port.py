"""Validate a Python port of AlgorithmBrain's energy math against logged C# `reasons`.

We do NOT trust the Python re-derivation blindly: AlgorithmBrain logs, per AttachEnergy
candidate, human-readable reason strings that encode the exact booleans the heuristic
computed (`helps`, `becomes_ready`). This script recomputes those booleans in Python from
the snapshot (equipped energy + card attack costs + the energy type being attached) and
diffs them against the logged reasons across the whole dataset. Zero mismatches => the port
faithfully reproduces the C# truth and can be wired into features.py.

Ground-truth reason substrings (see AlgorithmBrain.ScoreAttachEnergy):
  helps          : "energy type advances an attack cost"
  wrong type     : "energy type does not advance a normal attack"
  becomes_ready  : "bench becomes ready" | "active attacks this turn after attach"
                   | "ramp attack becomes ready"
"""
from __future__ import annotations

import io
import json
import sys
import argparse
from collections import Counter
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

from tcg_ml.cards import CardCatalog
from tcg_ml.paths import add_path_args, get_paths_from_args

WILDCARD = "Dragon"
COLORLESS = "Colorless"


# --- Python port of AlgorithmBrain / CardActions energy math ---------------------

def energy_missing_for_attack(equipped: dict, cost: list[str], cost_change: int = 0) -> int:
    """Port of AlgorithmBrain.EnergyMissingForAttack (AlgorithmBrain.cs:2363)."""
    if not cost:
        return 0
    available = {k: int(v or 0) for k, v in (equipped or {}).items()}
    jokers = available.get(WILDCARD, 0)
    available[WILDCARD] = 0  # Dragon spent flexibly below

    missing_typed = 0
    for c in cost:
        if c == COLORLESS:
            continue
        if available.get(c, 0) > 0:
            available[c] -= 1
        else:
            missing_typed += 1

    typed_covered = min(jokers, missing_typed)
    missing_typed -= typed_covered
    jokers -= typed_covered

    colorless_cost = sum(1 for c in cost if c == COLORLESS)
    adjusted_colorless = max(0, colorless_cost + cost_change)
    remaining = sum(available.values()) + jokers
    missing_colorless = max(0, adjusted_colorless - remaining)
    return missing_typed + missing_colorless


def energy_missing_min(equipped: dict, attack_costs: list[list[str]], cost_change: int = 0) -> int:
    """Min missing across all attacks. Port of EnergyMissingNow / -AfterAttach min loop."""
    if not attack_costs:
        return 1 << 30  # int.MaxValue sentinel: no attacks => never "ready"
    return min(energy_missing_for_attack(equipped, c, cost_change) for c in attack_costs)


def add_energy(equipped: dict, energy_type: str) -> dict:
    out = {k: int(v or 0) for k, v in (equipped or {}).items()}
    if energy_type and energy_type != "None":
        out[energy_type] = out.get(energy_type, 0) + 1
    return out


def would_energy_help(equipped: dict, attack_costs: list[list[str]], energy_type: str) -> bool:
    """Port of AlgorithmBrain.WouldEnergyHelp (AlgorithmBrain.cs:1813)."""
    equipped = {k: int(v or 0) for k, v in (equipped or {}).items()}
    total_have = sum(equipped.values())
    for cost in attack_costs:
        if not cost:
            continue
        if energy_type == WILDCARD:
            if total_have < len(cost):
                return True
            continue
        if energy_type != COLORLESS:
            needed = sum(1 for c in cost if c == energy_type)
            have = equipped.get(energy_type, 0)
            if needed > have:
                return True
        if any(c == COLORLESS for c in cost):
            if total_have < len(cost):
                return True
    return False


# --- Validation harness ----------------------------------------------------------

def attack_costs_for(card: dict | None) -> list[list[str]]:
    if not isinstance(card, dict):
        return []
    attacks = card.get("attacks") if isinstance(card.get("attacks"), list) else []
    out = []
    for atk in attacks:
        if isinstance(atk, dict) and isinstance(atk.get("attackCost"), list):
            out.append([str(c) for c in atk["attackCost"]])
    return out


def reason_flags(reasons: list[str]) -> dict:
    text = " | ".join(reasons or [])
    helps = "energy type advances an attack cost" in text
    wrong = "energy type does not advance a normal attack" in text
    becomes_ready = (
        "bench becomes ready" in text
        or "active attacks this turn after attach" in text
        or "ramp attack becomes ready" in text
    )
    return {"helps": helps, "wrong": wrong, "becomes_ready": becomes_ready}


def main() -> int:
    parser = argparse.ArgumentParser(description="Validate energy feature parity against logged C# reasons.")
    add_path_args(parser)
    args = parser.parse_args()

    paths = get_paths_from_args(args)
    catalog = CardCatalog.load(paths.cards_dir)
    files = sorted(paths.decisions_dir.glob("*_decisions.jsonl"))

    n_cand = 0
    mism_helps = 0
    mism_ready = 0
    # collect a few mismatch examples for inspection
    examples_helps: list = []
    examples_ready: list = []

    for f in files:
        for line in io.open(f, encoding="utf-8-sig"):
            line = line.strip()
            if not line:
                continue
            try:
                rec = json.loads(line)
            except json.JSONDecodeError:
                continue
            if rec.get("category") != "AttachEnergy":
                continue
            ms = rec.get("snapshot", {}).get("MyState", {}) or {}
            energy_type = str(ms.get("AvailableEnergy") or "None")
            # index board pokemon by InstanceId -> equipped energy
            by_id = {}
            act = ms.get("ActivePokemon")
            if isinstance(act, dict):
                by_id[act.get("InstanceId")] = act
            for b in ms.get("Bench") or []:
                if isinstance(b, dict):
                    by_id[b.get("InstanceId")] = b

            for s in rec.get("scores", []):
                if not isinstance(s, dict):
                    continue
                label = str(s.get("label") or "")
                if not label.startswith("AttachEnergy"):
                    continue
                tid = s.get("target_instance_id")
                pkmn = by_id.get(tid)
                if pkmn is None:
                    continue  # can't validate without the live target
                name = pkmn.get("Name")
                card = catalog.get_by_name(name)
                costs = attack_costs_for(card)
                equipped = pkmn.get("EnergyEquipped") or {}

                py_helps = would_energy_help(equipped, costs, energy_type)
                missing_before = energy_missing_min(equipped, costs)
                missing_after = energy_missing_min(add_energy(equipped, energy_type), costs)
                py_ready = (missing_before > 0) and (missing_after == 0)

                gt = reason_flags(s.get("reasons"))
                n_cand += 1

                if py_helps != gt["helps"]:
                    mism_helps += 1
                    if len(examples_helps) < 8:
                        examples_helps.append((name, energy_type, equipped, costs, py_helps, gt))
                if py_ready != gt["becomes_ready"]:
                    mism_ready += 1
                    if len(examples_ready) < 8:
                        examples_ready.append(
                            (name, energy_type, equipped, costs, missing_before, missing_after, py_ready, gt)
                        )

    print(f"validated AttachEnergy candidates (with live target): {n_cand}")
    print(f"helps mismatch:         {mism_helps}  ({100*mism_helps/max(1,n_cand):.3f}%)")
    print(f"becomes_ready mismatch: {mism_ready}  ({100*mism_ready/max(1,n_cand):.3f}%)")
    if examples_helps:
        print("\n--- helps mismatch examples ---")
        for name, et, eq, costs, py, gt in examples_helps:
            print(f"  {name} +{et} eq={eq} costs={costs} py_helps={py} gt={gt}")
    if examples_ready:
        print("\n--- becomes_ready mismatch examples ---")
        for name, et, eq, costs, mb, ma, py, gt in examples_ready:
            print(f"  {name} +{et} eq={eq} costs={costs} miss {mb}->{ma} py_ready={py} gt={gt}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
