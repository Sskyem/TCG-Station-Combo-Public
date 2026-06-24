using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/// <summary>
/// Generuje listę legalnych akcji dla aktywnego gracza w danym momencie tury.
/// Czysta funkcja: stan → lista akcji. Nie modyfikuje stanu gry.
/// </summary>
public static class LegalActionGenerator
{
    public static List<GameAction> Generate(
        PlayerController player,
        PlayerController opponent,
        PlayerManager pm,
        bool includeFutureTurnActions = true)
    {
        var actions = new List<GameAction>();

        GeneratePlayBasic(player, actions);
        GenerateEvolve(player, pm, actions);
        GenerateAttachEnergy(player, pm, actions, includeFutureTurnActions);
        GenerateAttack(player, opponent, pm, actions, includeFutureTurnActions);
        GenerateRetreat(player, actions);
        GeneratePlayTrainer(player, actions);

        // EndTurn jest zawsze legalne
        actions.Add(GameAction.EndTurn());

        return actions;
    }

    // --- Zagranie Basic Pokemona ---
    private static void GeneratePlayBasic(PlayerController player, List<GameAction> actions)
    {
        bool hasActiveSlot = player.activePokemon == null;
        bool hasBenchSlot  = player.benchPokemons.Count < GameRulesConfig.Instance.benchSize;

        if (!hasActiveSlot && !hasBenchSlot) return;

        foreach (var card in player.hand)
        {
            if (card.baseData is PokemonData pd && pd.stage == 0)
                actions.Add(GameAction.PlayBasic(card));
        }
    }

    // --- Ewolucja ---
    private static void GenerateEvolve(PlayerController player, PlayerManager pm, List<GameAction> actions)
    {
        if (!player.canEvolve) return;

        foreach (var card in player.hand)
        {
            if (card.baseData is PokemonData pd && pd.stage > 0)
            {
                var targets = pm.GetEvolvableTargets(card, player);
                foreach (var t in targets)
                    actions.Add(GameAction.Evolve(card, t));
            }
        }
    }

    // --- Podpięcie energii ---
    private static void GenerateAttachEnergy(PlayerController player, PlayerManager pm, List<GameAction> actions, bool includeFutureTurnActions)
    {
        if (!player.canAddEnergy) return;

        EnergyZone zone = pm.GetEnergyZoneFor(player);
        if (zone == null || zone.currentEnergy == EnumPokemonType.None) return;

        if (player.activePokemon != null)
        {
            var activePd = player.activePokemon.baseData as PokemonData;
            bool activeNeedsEnergy = activePd?.attacks != null &&
                activePd.attacks.Any(atk => !CardActions.CanAffordAttack(player.activePokemon.pokemonLogic, atk));
            bool activeEvolutionNeedsEnergy = includeFutureTurnActions &&
                ActiveEvolutionCanUseOneAttachedEnergy(player, pm);
            if (activeNeedsEnergy || activeEvolutionNeedsEnergy)
            {
                var action = GameAction.AttachEnergy(player.activePokemon);
                action.label = BuildAttachEnergyLabel(player.activePokemon, isActive: true);
                actions.Add(action);
            }
        }

        foreach (var bench in player.benchPokemons)
        {
            var benchPd = bench.baseData as PokemonData;
            if (!NeedsEnergy(bench, benchPd)) continue;

            var action = GameAction.AttachEnergy(bench);
            action.label = BuildAttachEnergyLabel(bench, isActive: false);
            actions.Add(action);
        }

        if (includeFutureTurnActions)
            GenerateAttachEnergyForPlayableBasics(player, actions);
    }

    private static void GenerateAttachEnergyForPlayableBasics(PlayerController player, List<GameAction> actions)
    {
        bool hasActiveSlot = player.activePokemon == null;
        bool hasBenchSlot = player.benchPokemons.Count < GameRulesConfig.Instance.benchSize;
        if (!hasActiveSlot && !hasBenchSlot) return;

        foreach (var card in player.hand)
        {
            if (card.baseData is not PokemonData pd || pd.stage != 0) continue;
            if (!NeedsEnergy(card, pd)) continue;

            var action = GameAction.AttachEnergy(card);
            action.label = BuildAttachEnergyAfterPlayLabel(card, hasActiveSlot);
            actions.Add(action);
        }
    }

    private static bool NeedsEnergy(CardInstance card, PokemonData pokemonData)
    {
        return card?.pokemonLogic != null &&
               pokemonData?.attacks != null &&
               pokemonData.attacks.Any(atk => !CardActions.CanAffordAttack(card.pokemonLogic, atk));
    }

    private static string BuildAttachEnergyLabel(CardInstance pokemon, bool isActive)
    {
        string name = pokemon.baseData?.cardName ?? "?";
        string position = isActive ? "ACTIVE" : "bench";
        int currentEnergy = pokemon.pokemonLogic?.energyEquipped != null
            ? pokemon.pokemonLogic.energyEquipped.Values.Sum()
            : 0;
        var pd = pokemon.baseData as PokemonData;
        int minCost = pd?.attacks != null && pd.attacks.Count > 0
            ? pd.attacks.Min(atk => atk.attackCost?.Count ?? 0)
            : 0;
        string missing = FormatBestMissingEnergy(pokemon, pd);
        return $"AttachEnergy(to {name} [{position} — {currentEnergy}/{minCost} total energy for cheapest attack{missing}])";
    }

    private static string BuildAttachEnergyAfterPlayLabel(CardInstance pokemon, bool wouldBecomeActive)
    {
        string name = pokemon.baseData?.cardName ?? "?";
        string position = wouldBecomeActive ? "ACTIVE after PlayBasic" : "bench after PlayBasic";
        int currentEnergy = pokemon.pokemonLogic?.energyEquipped != null
            ? pokemon.pokemonLogic.energyEquipped.Values.Sum()
            : 0;
        var pd = pokemon.baseData as PokemonData;
        int minCost = pd?.attacks != null && pd.attacks.Count > 0
            ? pd.attacks.Min(atk => atk.attackCost?.Count ?? 0)
            : 0;
        string missing = FormatBestMissingEnergy(pokemon, pd);
        return $"AttachEnergy(to {name} [ONLY AFTER PlayBasic({name}); {position} — {currentEnergy}/{minCost} total energy for cheapest attack{missing}])";
    }

    private static string FormatBestMissingEnergy(CardInstance pokemon, PokemonData pokemonData)
    {
        if (pokemon?.pokemonLogic?.energyEquipped == null ||
            pokemonData?.attacks == null ||
            pokemonData.attacks.Count == 0)
        {
            return "";
        }

        List<EnumPokemonType> bestMissing = null;
        int bestMissingCount = int.MaxValue;
        int bestCost = int.MaxValue;

        foreach (var attack in pokemonData.attacks)
        {
            var missing = GetMissingEnergyForAttack(
                pokemon.pokemonLogic.energyEquipped,
                attack,
                pokemon.pokemonLogic.tempBuffsData.attackEnergyCostChange);
            int cost = attack.attackCost?.Count ?? 0;

            if (bestMissing == null ||
                missing.Count < bestMissingCount ||
                (missing.Count == bestMissingCount && cost < bestCost))
            {
                bestMissing = missing;
                bestMissingCount = missing.Count;
                bestCost = cost;
            }
        }

        if (bestMissing == null || bestMissing.Count == 0)
            return "; cheapest attack payable";

        return $"; missing {FormatMissingEnergy(bestMissing)}";
    }

    private static List<EnumPokemonType> GetMissingEnergyForAttack(
        Dictionary<EnumPokemonType, int> equipped,
        AttackData attack,
        int attackEnergyCostChange)
    {
        var missing = new List<EnumPokemonType>();
        if (attack == null)
            return missing;
        var attackCost = attack.attackCost ?? new List<EnumPokemonType>();

        var available = equipped != null
            ? new Dictionary<EnumPokemonType, int>(equipped)
            : new Dictionary<EnumPokemonType, int>();

        int jokers = available.TryGetValue(EnumPokemonType.Dragon, out int dragonCount) ? dragonCount : 0;
        available[EnumPokemonType.Dragon] = 0;

        foreach (var cost in attackCost)
        {
            if (cost == EnumPokemonType.Colorless) continue;

            if (available.TryGetValue(cost, out int count) && count > 0)
            {
                available[cost]--;
            }
            else if (jokers > 0)
            {
                jokers--;
            }
            else
            {
                missing.Add(cost);
            }
        }

        int colorlessCost = attackCost.Count(c => c == EnumPokemonType.Colorless);
        int adjustedColorless = Mathf.Max(0, colorlessCost + attackEnergyCostChange);
        int remainingEnergy = available.Values.Sum() + jokers;
        int missingColorless = adjustedColorless - remainingEnergy;
        for (int i = 0; i < missingColorless; i++)
            missing.Add(EnumPokemonType.Colorless);

        return missing;
    }

    private static string FormatMissingEnergy(List<EnumPokemonType> missing)
    {
        return string.Join(", ",
            missing.GroupBy(t => t)
                .Select(g =>
                {
                    string name = g.Key == EnumPokemonType.Colorless
                        ? "any energy for Colorless"
                        : g.Key.ToString();
                    return g.Count() == 1 ? name : $"{g.Count()}x{name}";
                }));
    }

    // --- Atak ---
    private static void GenerateAttack(PlayerController player, PlayerController opponent, PlayerManager pm, List<GameAction> actions, bool includeFutureTurnActions)
    {
        // Gracz 2 nie może atakować w turze 1
        bool firstTurnBlock = TurnManager.Instance.turnCounter == 1 && player.playerId == 2;
        if (firstTurnBlock) return;

        if (player.activePokemon == null || opponent.activePokemon == null) return;

        var activePokemon = player.activePokemon.pokemonLogic;
        if (!activePokemon.tempBuffsData.canAttack) return;

        var pd = player.activePokemon.baseData as PokemonData;
        if (pd?.attacks == null) return;

        var attackLabels = new Dictionary<int, List<string>>();

        for (int i = 0; i < pd.attacks.Count; i++)
        {
            AttackData attack = pd.attacks[i];
            if (!CardActions.CanPayAttackAdditionalCosts(player, attack))
                continue;

            if (CardActions.CanAffordAttack(activePokemon, attack))
                AddAttackLabel(attackLabels, i, BuildAttackLabel(player.activePokemon, attack, "now"));
            else if (includeFutureTurnActions && CanAffordAttackAfterAttach(player, pm, attack))
                AddAttackLabel(attackLabels, i, BuildAttackLabel(player.activePokemon, attack, "after AttachEnergy; use before Evolve"));
        }

        if (includeFutureTurnActions)
            AddAttackLabelsAfterActiveEvolution(player, pm, attackLabels);

        foreach (var kv in attackLabels.OrderBy(kv => kv.Key))
        {
            var action = GameAction.Attack(kv.Key);
            action.label = $"Attack[{kv.Key}] ({string.Join(" OR ", kv.Value)})";
            actions.Add(action);
        }
    }

    private static bool CanAffordAttackAfterAttach(PlayerController player, PlayerManager pm, AttackData attack)
    {
        if (attack == null || !player.canAddEnergy || player.activePokemon?.pokemonLogic == null)
        {
            return false;
        }

        EnergyZone zone = pm.GetEnergyZoneFor(player);
        if (zone == null || zone.currentEnergy == EnumPokemonType.None)
        {
            return false;
        }

        Pokemon pokemon = player.activePokemon.pokemonLogic;
        var available = new Dictionary<EnumPokemonType, int>(pokemon.energyEquipped);
        if (available.ContainsKey(zone.currentEnergy))
        {
            available[zone.currentEnergy]++;
        }
        else
        {
            available[zone.currentEnergy] = 1;
        }

        int jokers = available.TryGetValue(EnumPokemonType.Dragon, out int dragonCount) ? dragonCount : 0;
        available[EnumPokemonType.Dragon] = 0; // Dragon is a joker, spent flexibly below

        var attackCost = attack.attackCost ?? new List<EnumPokemonType>();
        foreach (var cost in attackCost)
        {
            if (cost == EnumPokemonType.Colorless) continue;
            if (available.TryGetValue(cost, out int count) && count > 0)
                available[cost]--;
            else if (jokers > 0)
                jokers--;
            else
                return false;
        }

        int colorlessCost = attackCost.Count(c => c == EnumPokemonType.Colorless);
        int adjustedColorless = Mathf.Max(0, colorlessCost + pokemon.tempBuffsData.attackEnergyCostChange);
        int totalRemaining = available.Values.Sum() + jokers;
        return totalRemaining >= adjustedColorless;
    }

    private static void AddAttackLabelsAfterActiveEvolution(PlayerController player, PlayerManager pm, Dictionary<int, List<string>> attackLabels)
    {
        if (!player.canEvolve || player.activePokemon == null) return;

        foreach (var card in player.hand)
        {
            if (card.baseData is not PokemonData evolvedData || evolvedData.attacks == null) continue;
            if (!CanEvolveActiveWith(card, player, pm)) continue;

            for (int i = 0; i < evolvedData.attacks.Count; i++)
            {
                AttackData attack = evolvedData.attacks[i];
                if (CanAffordFutureEvolutionAttack(player, pm, attack, allowAttach: false))
                    AddAttackLabel(attackLabels, i, BuildAttackLabel(card, attack, "after Evolve"));
                else if (CanAffordFutureEvolutionAttack(player, pm, attack, allowAttach: true))
                    AddAttackLabel(attackLabels, i, BuildAttackLabel(card, attack, "after AttachEnergy + Evolve"));
            }
        }
    }

    private static void AddAttackLabel(Dictionary<int, List<string>> attackLabels, int attackIndex, string label)
    {
        if (!attackLabels.TryGetValue(attackIndex, out var labels))
        {
            labels = new List<string>();
            attackLabels[attackIndex] = labels;
        }

        if (!labels.Contains(label))
            labels.Add(label);
    }

    private static string BuildAttackLabel(CardInstance attacker, AttackData attack, string timing)
    {
        string attackerName = attacker?.baseData?.cardName ?? "?";
        string attackName = attack?.attackName ?? "?";
        return $"{attackerName}.{attackName} {timing}";
    }

    private static bool ActiveEvolutionCanUseOneAttachedEnergy(PlayerController player, PlayerManager pm)
    {
        if (!player.canEvolve || !player.canAddEnergy || player.activePokemon == null) return false;
        EnergyZone zone = pm.GetEnergyZoneFor(player);
        if (zone == null || zone.currentEnergy == EnumPokemonType.None) return false;

        foreach (var card in player.hand)
        {
            if (card.baseData is not PokemonData evolvedData || evolvedData.attacks == null) continue;
            if (!CanEvolveActiveWith(card, player, pm)) continue;
            foreach (var attack in evolvedData.attacks)
            {
                if (!CanAffordFutureEvolutionAttack(player, pm, attack, allowAttach: false) &&
                    CanAffordFutureEvolutionAttack(player, pm, attack, allowAttach: true))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool CanEvolveActiveWith(CardInstance evolutionCard, PlayerController player, PlayerManager pm)
    {
        var targets = pm.GetEvolvableTargets(evolutionCard, player);
        return targets != null && targets.Contains(player.activePokemon);
    }

    private static bool CanAffordFutureEvolutionAttack(PlayerController player, PlayerManager pm, AttackData attack, bool allowAttach)
    {
        if (attack == null) return false;
        if (player.activePokemon?.pokemonLogic == null) return false;

        var available = new Dictionary<EnumPokemonType, int>(player.activePokemon.pokemonLogic.energyEquipped);

        if (allowAttach)
        {
            if (!player.canAddEnergy) return false;
            EnergyZone zone = pm.GetEnergyZoneFor(player);
            if (zone == null || zone.currentEnergy == EnumPokemonType.None) return false;
            available[zone.currentEnergy] = available.TryGetValue(zone.currentEnergy, out int count) ? count + 1 : 1;
        }

        return CanPayAttackCost(available, attack, player.activePokemon.pokemonLogic.tempBuffsData.attackEnergyCostChange);
    }

    private static bool CanPayAttackCost(Dictionary<EnumPokemonType, int> available, AttackData attack, int attackEnergyCostChange)
    {
        var attackCost = attack?.attackCost ?? new List<EnumPokemonType>();
        int jokers = available.TryGetValue(EnumPokemonType.Dragon, out int dragonCount) ? dragonCount : 0;
        available[EnumPokemonType.Dragon] = 0;

        foreach (var cost in attackCost)
        {
            if (cost == EnumPokemonType.Colorless) continue;
            if (available.TryGetValue(cost, out int count) && count > 0)
                available[cost]--;
            else if (jokers > 0)
                jokers--;
            else
                return false;
        }

        int colorlessCost = attackCost.Count(c => c == EnumPokemonType.Colorless);
        int adjustedColorless = Mathf.Max(0, colorlessCost + attackEnergyCostChange);
        int totalRemaining = available.Values.Sum() + jokers;
        return totalRemaining >= adjustedColorless;
    }

    // --- Retreat ---
    private static void GenerateRetreat(PlayerController player, List<GameAction> actions)
    {
        if (player.activePokemon == null || player.benchPokemons.Count == 0) return;
        if (player.usedManualRetreatThisTurn) return;
        if (!player.activePokemon.pokemonLogic.tempBuffsData.canRetreat) return;

        var pd = player.activePokemon.baseData as PokemonData;
        if (pd == null) return;

        int cost = Mathf.Max(0, pd.retreatCost + player.retreatEnergyCostChange);
        int totalEnergy = player.activePokemon.pokemonLogic.energyEquipped.Values.Sum();

        if (totalEnergy < cost) return;

        foreach (var bench in player.benchPokemons)
            actions.Add(GameAction.Retreat(bench));
    }

    // --- Zagraj Trainera ---
    private static void GeneratePlayTrainer(PlayerController player, List<GameAction> actions)
    {
        foreach (var card in player.hand)
        {
            if (card.baseData is not TrainerData td) continue;

            switch (td.trainerSubType)
            {
                case EnumTrainerSubType.Item:
                    if (player.canUseItems)
                        actions.Add(GameAction.PlayTrainer(card));
                    break;

                case EnumTrainerSubType.Supporter:
                    if (player.canUseSupporters && !player.usedSupporterThisTurn)
                        actions.Add(GameAction.PlayTrainer(card));
                    break;

                case EnumTrainerSubType.Tool:
                    if (player.canUseTools)
                        actions.Add(GameAction.PlayTrainer(card));
                    break;

                case EnumTrainerSubType.Stadium:
                    if (player.canUseStadiums)
                        actions.Add(GameAction.PlayTrainer(card));
                    break;
            }
        }
    }
}
