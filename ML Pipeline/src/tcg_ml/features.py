from __future__ import annotations

import re
from dataclasses import dataclass
from typing import Any

import numpy as np

from .cards import CardCatalog
from .spec import FeatureSpec


_WILDCARD_ENERGY = "Dragon"
_COLORLESS_ENERGY = "Colorless"


def _clip(value: float, lo: float = -1.0, hi: float = 1.0) -> float:
    return max(lo, min(hi, value))


# --- Energy math: faithful port of AlgorithmBrain / CardActions (C# source of truth) -----
# Validated to 0.000% mismatch on `helps` and 0.039% on `becomes_ready` against the heuristic's
# logged `reasons` across 389k AttachEnergy candidates (scripts/validate_energy_port.py). The
# residual is per-Pokemon attackEnergyCostChange buffs (Slow) that the snapshot does not carry.

def _attack_costs(card: dict[str, Any] | None) -> list[list[str]]:
    if not isinstance(card, dict):
        return []
    attacks = card.get("attacks") if isinstance(card.get("attacks"), list) else []
    out: list[list[str]] = []
    for atk in attacks:
        if isinstance(atk, dict) and isinstance(atk.get("attackCost"), list):
            out.append([str(c) for c in atk["attackCost"]])
    return out


def _energy_missing_for_attack(equipped: dict[str, Any], cost: list[str], cost_change: int = 0) -> int:
    """Port of AlgorithmBrain.EnergyMissingForAttack (AlgorithmBrain.cs:2363)."""
    if not cost:
        return 0
    available = {k: int(v or 0) for k, v in (equipped or {}).items()}
    jokers = available.get(_WILDCARD_ENERGY, 0)
    available[_WILDCARD_ENERGY] = 0  # Dragon spent flexibly below

    missing_typed = 0
    for c in cost:
        if c == _COLORLESS_ENERGY:
            continue
        if available.get(c, 0) > 0:
            available[c] -= 1
        else:
            missing_typed += 1

    typed_covered = min(jokers, missing_typed)
    missing_typed -= typed_covered
    jokers -= typed_covered

    colorless_cost = sum(1 for c in cost if c == _COLORLESS_ENERGY)
    adjusted_colorless = max(0, colorless_cost + cost_change)
    remaining = sum(available.values()) + jokers
    missing_colorless = max(0, adjusted_colorless - remaining)
    return missing_typed + missing_colorless


def _energy_missing_min(equipped: dict[str, Any], attack_costs: list[list[str]]) -> int:
    """Min missing energy across a Pokemon's attacks. ``_NO_ATTACK`` when it has none."""
    if not attack_costs:
        return _NO_ATTACK
    return min(_energy_missing_for_attack(equipped, c) for c in attack_costs)


def _add_energy(equipped: dict[str, Any], energy_type: str) -> dict[str, int]:
    out = {k: int(v or 0) for k, v in (equipped or {}).items()}
    if energy_type and energy_type != "None":
        out[energy_type] = out.get(energy_type, 0) + 1
    return out


def _would_energy_help(equipped: dict[str, Any], attack_costs: list[list[str]], energy_type: str) -> bool:
    """Port of AlgorithmBrain.WouldEnergyHelp (AlgorithmBrain.cs:1813)."""
    equipped = {k: int(v or 0) for k, v in (equipped or {}).items()}
    total_have = sum(equipped.values())
    for cost in attack_costs:
        if not cost:
            continue
        if energy_type == _WILDCARD_ENERGY:
            if total_have < len(cost):
                return True
            continue
        if energy_type != _COLORLESS_ENERGY:
            needed = sum(1 for c in cost if c == energy_type)
            if needed > equipped.get(energy_type, 0):
                return True
        if any(c == _COLORLESS_ENERGY for c in cost):
            if total_have < len(cost):
                return True
    return False


_NO_ATTACK = 1 << 30  # sentinel mirroring C# int.MaxValue: "Pokemon has no usable attack"
_ENERGY_FIT_DIM = 7


def _norm(value: Any, div: float, signed: bool = False) -> float:
    try:
        result = float(value or 0) / div
    except (TypeError, ValueError):
        result = 0.0
    return _clip(result, -1.0 if signed else 0.0, 1.0)


def _one_hot(value: Any, choices: list[str]) -> list[float]:
    value_s = str(value or "")
    return [1.0 if value_s == choice else 0.0 for choice in choices]


def _stage_norm(stage: Any) -> float:
    if isinstance(stage, str):
        mapping = {"Basic": 0, "Stage1": 1, "Stage2": 2}
        return mapping.get(stage, 0) / 2.0
    try:
        return _clip(float(stage or 0) / 2.0, 0.0, 1.0)
    except (TypeError, ValueError):
        return 0.0


def _effect_slots(effects: list[dict[str, Any]] | None, k: int, spec: FeatureSpec) -> list[float]:
    effects = [e for e in (effects or []) if isinstance(e, dict)]
    effects = sorted(effects, key=lambda e: (str(e.get("cardEffectType") or ""), str(e.get("cardEffectTarget") or "")))
    out: list[float] = []
    for index in range(k):
        effect = effects[index] if index < len(effects) else None
        out.append(1.0 if effect else 0.0)
        out.extend(_one_hot(effect.get("cardEffectType") if effect else None, spec.effect_types))
        out.extend(_one_hot(effect.get("cardEffectTarget") if effect else None, spec.targets))
        out.append(_norm(effect.get("effectAmount") if effect else 0, spec.norm("amount_div"), signed=True))
    return out


@dataclass
class FeatureEncoder:
    spec: FeatureSpec
    catalog: CardCatalog

    def card_static(self, card: dict[str, Any] | None) -> np.ndarray:
        card = card or {}
        card_type = str(card.get("cardType") or "")
        is_pokemon = card_type == "Pokemon"
        is_trainer = card_type == "Trainer"
        attacks = card.get("attacks") if isinstance(card.get("attacks"), list) else []
        attack = attacks[0] if attacks and isinstance(attacks[0], dict) else {}
        attack_cost = attack.get("attackCost") if isinstance(attack.get("attackCost"), list) else []
        cost_counts = [attack_cost.count(t) / self.spec.norm("attack_cost_div") for t in self.spec.pokemon_types]
        effects = attack.get("effects") if isinstance(attack.get("effects"), list) else []
        trainer_effects = card.get("effects") if isinstance(card.get("effects"), list) else []

        values: list[float] = []
        values.extend([1.0 if is_pokemon else 0.0, 1.0 if is_trainer else 0.0])
        values.append(_norm(card.get("hp"), self.spec.norm("hp_div")))
        values.extend(_one_hot(card.get("type"), self.spec.pokemon_types))
        values.append(_stage_norm(card.get("stage")))
        values.append(_norm(card.get("retreatCost"), self.spec.norm("retreat_div")))
        values.append(1.0 if card.get("evolvesFrom") else 0.0)
        values.extend(_one_hot(card.get("trainerSubType"), ["Item", "Supporter", "Tool", "Stadium"]))

        values.append(_norm(attack.get("damage"), self.spec.norm("damage_div")))
        values.extend(cost_counts)
        values.append(_norm(len(attack_cost), self.spec.norm("attack_cost_div")))
        values.append(_norm(len(effects), max(1.0, float(self.spec.attack_k))))
        values.append(1.0 if not effects else 0.0)
        values.extend(_effect_slots(effects, self.spec.attack_k, self.spec))

        values.append(1.0 if card.get("trainerSubType") == "Supporter" else 0.0)
        values.extend(_effect_slots(trainer_effects, self.spec.trainer_k, self.spec))
        return np.asarray(values, dtype=np.float32)

    @property
    def card_dim(self) -> int:
        return int(self.card_static(None).shape[0])

    def pokemon_live(self, pokemon: dict[str, Any] | None, is_active: bool) -> np.ndarray:
        pokemon = pokemon or {}
        energy = pokemon.get("EnergyEquipped") if isinstance(pokemon.get("EnergyEquipped"), dict) else {}
        energy_total = sum(float(v or 0) for v in energy.values())
        values: list[float] = []
        values.append(_norm(pokemon.get("CurrentHp"), max(1.0, float(pokemon.get("MaxHp") or 1))))
        values.extend([_norm(energy.get(t), 5.0) for t in self.spec.pokemon_types])
        values.append(_norm(energy_total, self.spec.norm("energy_total_div")))
        values.extend(_one_hot(pokemon.get("SpecialCondition"), self.spec.special_conditions))
        values.append(1.0 if pokemon.get("IsPoisoned") else 0.0)
        values.append(1.0 if pokemon.get("IsBurned") else 0.0)
        values.append(_norm(pokemon.get("TurnPlacedOnBoard"), self.spec.norm("turn_div")))
        values.append(1.0 if pokemon.get("CanEvolve") else 0.0)
        values.append(1.0 if is_active else 0.0)
        values.append(_norm(len(pokemon.get("PossibleEvolutions") or []), 3.0))
        return np.asarray(values, dtype=np.float32)

    @property
    def live_dim(self) -> int:
        return int(self.pokemon_live(None, False).shape[0])

    def pokemon_vector(self, pokemon: dict[str, Any] | None, is_active: bool) -> np.ndarray:
        card = self.catalog.get_by_name(pokemon.get("Name") if pokemon else None)
        return np.concatenate([self.card_static(card), self.pokemon_live(pokemon, is_active)])

    @property
    def pokemon_dim(self) -> int:
        return self.card_dim + self.live_dim

    def _pool(self, vectors: list[np.ndarray]) -> tuple[np.ndarray, np.ndarray]:
        if not vectors:
            zeros = np.zeros(self.pokemon_dim, dtype=np.float32)
            return zeros, zeros
        matrix = np.vstack(vectors)
        return matrix.mean(axis=0).astype(np.float32), matrix.max(axis=0).astype(np.float32)

    def player_vector(self, player: dict[str, Any] | None, include_hand_cards: bool) -> np.ndarray:
        player = player or {}
        active = self.pokemon_vector(player.get("ActivePokemon"), is_active=True)
        bench_vectors = [self.pokemon_vector(p, is_active=False) for p in (player.get("Bench") or []) if isinstance(p, dict)]
        bench_mean, bench_max = self._pool(bench_vectors)

        hand_cards = []
        if include_hand_cards:
            for entry in player.get("Hand") or []:
                card = self.catalog.get_by_name(entry.get("Name") if isinstance(entry, dict) else None)
                hand_cards.append(self.card_static(card))
        hand_mean = np.vstack(hand_cards).mean(axis=0).astype(np.float32) if hand_cards else np.zeros(self.card_dim, dtype=np.float32)

        pool = player.get("DeckEnergyPool") if isinstance(player.get("DeckEnergyPool"), list) else []
        pool_dist = [pool.count(t) / max(1.0, float(len(pool))) for t in self.spec.pokemon_types]

        scalar = np.asarray([
            _norm(player.get("Score"), self.spec.norm("score_div")),
            _norm(player.get("HandCount"), self.spec.norm("hand_count_div")),
            _norm(player.get("DeckCount"), self.spec.norm("deck_count_div")),
            _norm(player.get("DiscardCount"), self.spec.norm("deck_count_div")),
            _norm(len(player.get("Bench") or []), 3.0),
            1.0 if player.get("CanAddEnergy") else 0.0,
            1.0 if player.get("UsedSupporterThisTurn") else 0.0,
            _norm(player.get("AttackDamageBonus"), 100.0, signed=True),
            _norm(player.get("AttackCostChange"), 5.0, signed=True),
            _norm(player.get("RetreatCostChange"), 5.0, signed=True),
        ], dtype=np.float32)

        energies = np.asarray(
            _one_hot(player.get("AvailableEnergy"), self.spec.pokemon_types)
            + _one_hot(player.get("NextEnergy"), self.spec.pokemon_types)
            + pool_dist,
            dtype=np.float32,
        )
        return np.concatenate([active, bench_mean, bench_max, hand_mean, scalar, energies])

    def state_vector(self, snapshot: dict[str, Any]) -> np.ndarray:
        my_state = self.player_vector(snapshot.get("MyState"), include_hand_cards=True)
        opp_state = self.player_vector(snapshot.get("OpponentState"), include_hand_cards=False)
        global_values = np.asarray([
            _norm(snapshot.get("TurnNumber"), self.spec.norm("turn_div")),
            1.0 if snapshot.get("ActivePlayerId") == (snapshot.get("MyState") or {}).get("PlayerId") else 0.0,
        ], dtype=np.float32)
        return np.concatenate([global_values, my_state, opp_state])

    @property
    def state_dim(self) -> int:
        return int(self.state_vector({}).shape[0])

    def find_pokemon_by_instance(self, snapshot: dict[str, Any] | None, instance_id: int) -> tuple[dict[str, Any] | None, bool]:
        """Locate a board Pokemon by its snapshot InstanceId. Returns (pokemon, is_active)."""
        if not snapshot or instance_id is None or instance_id < 0:
            return None, False
        for side in ("MyState", "OpponentState"):
            player = snapshot.get(side) or {}
            active = player.get("ActivePokemon")
            if isinstance(active, dict) and active.get("InstanceId") == instance_id:
                return active, True
            for bench in player.get("Bench") or []:
                if isinstance(bench, dict) and bench.get("InstanceId") == instance_id:
                    return bench, False
        return None, False

    def _energy_fit(
        self,
        action_type: str,
        target_pokemon: dict[str, Any] | None,
        snapshot: dict[str, Any] | None,
    ) -> np.ndarray:
        """Per-candidate 'does attaching THIS energy to THIS target advance an attack?' signals.

        AttachEnergy hinges on a 3-way interaction — energy type being attached (state) ×
        target attack cost (card) × target current energy (live) — spread across distant feature
        blocks that a flat MLP scores poorly. We precompute it here (validated port of the
        heuristic's energy math) so the model sees the comparison directly. AttachEnergy-only;
        zeros for every other action type and when there is no live target.

        The target card is resolved from the live Pokemon's Name (not the action label): the
        label is ``AttachEnergy(to <Name>)``, which the generic target-name extractor returns as
        ``"to <Name>"`` — so a label-based card lookup would miss. The board target's Name is exact.
        """
        if action_type != "AttachEnergy" or target_pokemon is None:
            return np.zeros(_ENERGY_FIT_DIM, dtype=np.float32)

        my_state = snapshot.get("MyState") if isinstance(snapshot, dict) else None
        energy_type = str((my_state or {}).get("AvailableEnergy") or "None")
        equipped = target_pokemon.get("EnergyEquipped") if isinstance(target_pokemon.get("EnergyEquipped"), dict) else {}
        costs = _attack_costs(self.catalog.get_by_name(target_pokemon.get("Name")))

        missing_before = _energy_missing_min(equipped, costs)
        missing_after = _energy_missing_min(_add_energy(equipped, energy_type), costs)
        has_attack = bool(costs)
        ready_before = has_attack and missing_before == 0
        ready_after = has_attack and missing_after == 0
        becomes_ready = (not ready_before) and ready_after
        improved = has_attack and (not ready_before) and missing_after < missing_before
        helps = _would_energy_help(equipped, costs, energy_type)

        def _miss_norm(m: int) -> float:
            return _norm(0 if m == _NO_ATTACK else m, 6.0)

        return np.asarray([
            1.0 if becomes_ready else 0.0,
            1.0 if improved else 0.0,
            1.0 if helps else 0.0,
            1.0 if ready_before else 0.0,   # already attack-ready: this attach is largely redundant
            _miss_norm(missing_before),
            _miss_norm(missing_after),
            1.0 if has_attack else 0.0,
        ], dtype=np.float32)

    def action_vector(
        self,
        label: str,
        category: str | None = None,
        ordinal: int = 0,
        candidate_count: int = 1,
        target_instance_id: int = -1,
        snapshot: dict[str, Any] | None = None,
    ) -> np.ndarray:
        action_type = category or _infer_action_type(label)
        target_name = _extract_target_name(label)
        target_card = self.catalog.get_by_name(target_name)
        attack_index = _extract_attack_index(label)
        values: list[float] = []
        values.extend(_one_hot(action_type if action_type in self.spec.action_types else "Other", self.spec.action_types))
        values.append(_norm(ordinal, 20.0))
        values.append(_norm(candidate_count, 20.0))
        values.append(_norm(attack_index, 4.0))

        # Live-state of the board target, looked up by stable InstanceId. This disambiguates
        # same-name candidates (active vs bench copy) and exposes the energy/readiness signals
        # AttachEnergy / Retreat / Evolve decisions actually depend on. Zeros when no board target.
        target_pokemon, target_is_active = self.find_pokemon_by_instance(snapshot, target_instance_id)
        target_found = 1.0 if target_pokemon is not None else 0.0
        live = self.pokemon_live(target_pokemon, target_is_active)
        energy_fit = self._energy_fit(
            action_type if action_type in self.spec.action_types else "Other",
            target_pokemon,
            snapshot,
        )

        return np.concatenate([
            np.asarray(values, dtype=np.float32),
            self.card_static(target_card),
            np.asarray([target_found], dtype=np.float32),
            live,
            energy_fit,
        ])

    @property
    def action_dim(self) -> int:
        return int(self.action_vector("").shape[0])


def _infer_action_type(label: str) -> str:
    if (label or "").strip() == "(skip)":
        return "Skip"
    match = re.match(r"([A-Za-z]+)", label or "")
    return match.group(1) if match else "Other"


def _extract_attack_index(label: str) -> int:
    match = re.search(r"Attack\[(\d+)\]", label or "")
    return int(match.group(1)) if match else 0


def _extract_target_name(label: str) -> str | None:
    text = label or ""
    patterns = [
        r"\(([^)]+)\)",
        r"\bto\s+([A-Za-z0-9' ._-]+?)(?:\s+\[|$)",
        r"\binto\s+([A-Za-z0-9' ._-]+?)(?:\s+\[|$)",
    ]
    for pattern in patterns:
        match = re.search(pattern, text)
        if match:
            # AttachEnergy/Retreat labels read "(to <Name>)"; the paren pattern captures
            # "to <Name>". Strip the leading "to "/"into " so the card lookup gets the bare
            # name (PlayBasic "(<Name>)" has no prefix and is unaffected). Without this the
            # target's card_static block stayed all-zero for AttachEnergy and Retreat.
            return re.sub(r"^(?:to|into)\s+", "", match.group(1).strip())
    return None
