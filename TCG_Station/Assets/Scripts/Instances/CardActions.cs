using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using System.Collections;

public class CardActions : MonoBehaviour
{
    #region Singleton
    public static CardActions Instance { get; private set; }

    void Awake()
    {
        if (Instance == null) Instance = this;
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    void Start()
    {
        if (playerManager == null)
            playerManager = PlayerManager.Instance;
    }

    #endregion

    public static event Action<PlayerController, List<CardInstance>> OnCardDrewFromEffect;

    private GameManager gameManager;
    public PlayerManager playerManager;
    public BattleManager battleManager;
    public BoardVisualizer boardVisualizer;

    [Header("Effect Visual Delays")]
    public bool useEffectDelays = true;
    public float effectDelay = 0.4f;

    // Each multiattack hit shows its own damage number and HP countdown (damageTextDisplayDuration).
    // The generic effectDelay (0.4s) is shorter than that animation, so hits visually merged into one.
    // Wait at least the full damage-display window between hits so each strike reads separately.
    private float MultiattackHitDelay()
    {
        float hpAnimation = GameRulesConfig.Instance != null
            ? GameRulesConfig.Instance.damageTextDisplayDuration
            : effectDelay;
        return Mathf.Max(effectDelay, hpAnimation);
    }

    private class AttackEffectContext
    {
        public int damageDealtToEnemyPokemon;
    }


    public bool ExecuteCardEffects(PlayerController sourcePlayer, List<EffectData> effects)
    {
        if (effects == null || effects.Count == 0) return false;
        StartCoroutine(ExecuteCardEffectsCoroutine(sourcePlayer, null, effects, null, null));
        return true;
    }

    // POSZCZEGÓLNE AKCJE

    public List<CardInstance> DrawCard(PlayerController player, int amount, bool triggerEvent = true)
    {
        List<CardInstance> drawnCards = new List<CardInstance>();

        for (int i = 0; i < amount; i++)
        {
            if (player.deck.Count > 0)
            {
                CardInstance drawnCard = player.deck[0];
                player.deck.RemoveAt(0);
                player.hand.Add(drawnCard);
                drawnCards.Add(drawnCard);
            }
            else
            {
                Debug.LogWarning($"Deck of player {player.playerId} is empty!");
                break;
            }
        }

        player.cardsDrawnThisGame += drawnCards.Count;

        Debug.Log($"<color=blue>EFEKT:</color> Player {player.playerId} drew {amount} card(s).");
        if (triggerEvent) OnCardDrewFromEffect?.Invoke(player, drawnCards);

        return drawnCards;
    }




    public static bool HealPokemon(Pokemon target, int amount)
    {
        if (target == null) return false;

        int hpBefore = target.currentHp;
        int maxHp = target.pokemonData.hp;
        Debug.Log($"<color=blue>EFEKT:</color> Attempting to heal {amount} HP on {target.pokemonData.cardName} (Current HP: {hpBefore}/{maxHp})");

        if (amount <= 0)
        {
            Debug.Log($"<color=blue>EFEKT:</color> Heal amount is zero or negative for {target.pokemonData.cardName}, skipping.");
            return false;
        }

        if (hpBefore >= maxHp)
        {
            Debug.Log($"<color=blue>EFEKT:</color> Heal amount is zero because {target.pokemonData.cardName} is already at full HP.");
            return false;
        }

        int finalAmount = Mathf.Min(amount, maxHp - hpBefore);
        if (finalAmount != amount)
        {
            Debug.Log($"<color=blue>EFEKT:</color> Heal amount adjusted to prevent overheal. Final heal amount: {finalAmount}");
        }

        target.currentHp += finalAmount;
        Debug.Log($"<color=blue>EFEKT:</color> Healed {target.pokemonData.cardName} for {finalAmount}. HP: {hpBefore}->{target.currentHp}/{maxHp}.");

        BattleManager.NotifyHpChanged(target);

        return true;
    }

    public static void RemoveSpecificCardFromHand(PlayerController playerController, CardInstance cardToRemove)
    {
        if (playerController.hand.Contains(cardToRemove))
        {
            playerController.hand.Remove(cardToRemove);
        }
    }

    public static void DiscardRandomCardFromHand(PlayerController playerController, int amount)
    {
        for (int i = 0; i < amount; i++)
        {
            if (playerController.hand.Count == 0) return;

            int index = UnityEngine.Random.Range(0, playerController.hand.Count);
            CardInstance cardToDiscard = playerController.hand[index];

            playerController.hand.RemoveAt(index);
            playerController.discardPile.Add(cardToDiscard);
            PlayerManager.NotifyCardDiscardedFromHand(cardToDiscard, playerController.playerId);

            Debug.Log($"<color=orange>DISCARD:</color> Discarded card: {cardToDiscard.baseData.cardId} (Instance: {cardToDiscard.instanceId})");
        }
    }

    // --- ATAKI ---

    /// <summary>
    /// Dragon energy is a joker: a single Dragon energy pays ANY energy requirement
    /// (typed or Colorless), unlike normal typed energy which only pays its own typed slot.
    /// This is the single source of truth for "which energy is the wildcard".
    /// </summary>
    public static bool IsWildcardEnergy(EnumPokemonType energyType) => energyType == EnumPokemonType.Dragon;

    /// <summary>
    /// Checks whether the Pokemon has enough energy for the attack.
    /// Typed costs require the exact type or a Dragon joker; Colorless costs can be paid by any remaining energy.
    /// attackEnergyCostChange adds/removes extra Colorless cost, including for printed free attacks.
    /// </summary>
    public static bool CanAffordAttack(Pokemon pokemon, AttackData attack)
    {
        if (pokemon == null || attack == null) return false;

        var available = new Dictionary<EnumPokemonType, int>(pokemon.energyEquipped);
        int jokers = available.TryGetValue(EnumPokemonType.Dragon, out int dragonCount) ? dragonCount : 0;
        available[EnumPokemonType.Dragon] = 0; // jokers are spent flexibly below

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

    /// <summary>
    /// Zwraca dodatni deficyt energii dla ataku, który może zostać zmniejszony przez podany typ energii.
    /// 0 oznacza, że ta energia nic nie daje temu Pokemonowi.
    /// </summary>
    private static int GetAttackEnergyNeed(AttackData attack, Dictionary<EnumPokemonType, int> equipped, EnumPokemonType energyType)
    {
        if (attack?.attackCost == null || attack.attackCost.Count == 0 || equipped == null)
            return 0;

        // A Dragon joker can satisfy any remaining slot, so it helps whenever anything is unpaid.
        if (IsWildcardEnergy(energyType))
            return Mathf.Max(0, attack.attackCost.Count - equipped.Values.Sum());

        int typedNeed = 0;

        if (energyType != EnumPokemonType.Colorless)
        {
            int neededOfType = attack.attackCost.Count(cost => cost == energyType);
            int haveOfType = equipped.TryGetValue(energyType, out int count) ? count : 0;
            typedNeed = Mathf.Max(0, neededOfType - haveOfType);
        }

        int totalCost = attack.attackCost.Count;
        int totalHave = equipped.Values.Sum();
        int totalNeed = Mathf.Max(0, totalCost - totalHave);

        if (typedNeed > 0)
            return totalNeed;

        bool hasColorlessSlot = attack.attackCost.Any(cost => cost == EnumPokemonType.Colorless);
        return hasColorlessSlot && totalNeed > 0 ? totalNeed : 0;
    }

    /// <summary>
    /// Wybiera ławkowego Pokemona, który najbardziej potrzebuje tej energii do któregoś ze swoich ataków.
    /// Ignoruje cele, którym energia nic realnie nie daje (np. atak za 0).
    /// </summary>
    private static CardInstance ChooseBenchEnergyRampTarget(PlayerController sourcePlayer, EnumPokemonType energyType)
    {
        if (sourcePlayer == null || sourcePlayer.benchPokemons == null || sourcePlayer.benchPokemons.Count == 0)
            return null;

        PlayerController opponent = PlayerManager.Instance.player1 == sourcePlayer
            ? PlayerManager.Instance.player2
            : PlayerManager.Instance.player1;
        int opponentMaxHp = GetOpponentMaxCurrentHp(opponent);

        CardInstance discardTarget = ChooseStrategicDiscardRampTarget(sourcePlayer, energyType);
        if (discardTarget != null)
            return discardTarget;

        CardInstance betterRampEngine = ChooseBetterRampEngineTarget(sourcePlayer, energyType);
        if (betterRampEngine != null)
            return betterRampEngine;

        CardInstance scalingTarget = ChooseScalingDamageRampTarget(sourcePlayer, energyType, opponentMaxHp);
        if (scalingTarget != null)
            return scalingTarget;

        CardInstance bestTarget = null;
        int bestNeed = 0;

        foreach (var benchCard in sourcePlayer.benchPokemons)
        {
            var pokemonData = benchCard?.baseData as PokemonData;
            var equipped = benchCard?.pokemonLogic?.energyEquipped;
            if (pokemonData?.attacks == null || equipped == null) continue;

            int need = pokemonData.attacks
                .Select(attack => GetAttackEnergyNeed(attack, equipped, energyType))
                .DefaultIfEmpty(0)
                .Max();

            if (need > bestNeed)
            {
                bestNeed = need;
                bestTarget = benchCard;
            }
        }

        if (bestTarget != null) return bestTarget;

        // All bench Pokemon already have required energy.
        // Prefer Psychic-effect targets that are not yet "fully loaded" (see IsPsychicSatisfied).
        // Break remaining ties by highest (max attack cost + retreat cost).
        return sourcePlayer.benchPokemons
            .Where(c => c?.baseData is PokemonData)
            .OrderByDescending(c => (HasScalingDamageLine(c, sourcePlayer) && !IsScalingDamageSatisfied(c, sourcePlayer, opponentMaxHp)) ? 1 : 0)
            .ThenByDescending(c => {
                var pd = (PokemonData)c.baseData;
                int maxAtk = pd.attacks?.Select(a => a.attackCost?.Count ?? 0).DefaultIfEmpty(0).Max() ?? 0;
                return maxAtk + pd.retreatCost;
            })
            .FirstOrDefault() ?? sourcePlayer.benchPokemons[0];
    }

    private static CardInstance ChooseStrategicDiscardRampTarget(PlayerController sourcePlayer, EnumPokemonType energyType)
    {
        if (energyType == EnumPokemonType.None)
            return null;

        return sourcePlayer.benchPokemons
            .Where(c => StrategicDiscardLineNeedsEnergy(c, sourcePlayer, energyType))
            .OrderBy(c => GetMinEnergyMissingAfterAttach(c, energyType))
            .ThenByDescending(c => GetStrategicDiscardAttackCeiling(c, sourcePlayer))
            .ThenBy(c => c.pokemonLogic?.energyEquipped?.Values.Sum() ?? 0)
            .FirstOrDefault();
    }

    private static bool StrategicDiscardLineNeedsEnergy(CardInstance card, PlayerController sourcePlayer, EnumPokemonType energyType)
    {
        if (card?.pokemonLogic == null || energyType == EnumPokemonType.None)
            return false;

        return GetCurrentAndFuturePokemonData(card, sourcePlayer).Any(pd =>
            pd.attacks != null &&
            pd.attacks.Any(atk =>
                GetEnergyDiscardAmount(atk) > 0 &&
                EstimatePrintedAttackCeiling(atk) >= 90 &&
                StrategicDiscardAttackBenefitsFromEnergy(card, atk, energyType)));
    }

    private static bool StrategicDiscardAttackBenefitsFromEnergy(CardInstance card, AttackData attack, EnumPokemonType energyType)
    {
        if (AttackNeedsTypedEnergy(attack, energyType))
            return true;

        // Colorless and the Dragon joker do not advance a specific typed slot here, but both
        // still help as a reserve buffer for the future discard-attacker coming online.
        if (energyType != EnumPokemonType.Colorless && !IsWildcardEnergy(energyType))
            return false;

        int wantedReserve = (attack.attackCost?.Count ?? 0) + GetEnergyDiscardAmount(attack);
        int currentTotal = card.pokemonLogic.energyEquipped?.Values.Sum() ?? 0;
        return currentTotal < wantedReserve;
    }

    private static bool AttackNeedsTypedEnergy(AttackData attack, EnumPokemonType energyType)
    {
        return attack?.attackCost != null &&
               attack.attackCost.Any(cost => cost == energyType);
    }

    private static int GetStrategicDiscardAttackCeiling(CardInstance card, PlayerController sourcePlayer)
    {
        return GetCurrentAndFuturePokemonData(card, sourcePlayer)
            .Where(pd => pd.attacks != null)
            .SelectMany(pd => pd.attacks)
            .Where(atk => GetEnergyDiscardAmount(atk) > 0)
            .Select(EstimatePrintedAttackCeiling)
            .DefaultIfEmpty(0)
            .Max();
    }

    private static int GetEnergyDiscardAmount(AttackData attack)
    {
        return attack?.effects?
            .Where(e => e.cardEffectType == EnumCardEffectType.EnergyDiscard)
            .Select(e => Mathf.Max(0, e.effectAmount))
            .DefaultIfEmpty(0)
            .Sum() ?? 0;
    }

    private static int EstimatePrintedAttackCeiling(AttackData attack)
    {
        if (attack == null) return 0;

        int damage = attack.damage;
        if (attack.effects == null) return damage;

        foreach (EffectData effect in attack.effects)
        {
            if (effect.cardEffectType == EnumCardEffectType.PowerUp && effect.effectAmount > 0)
                damage += effect.effectAmount * 5;
        }

        return damage;
    }

    private static CardInstance ChooseBetterRampEngineTarget(PlayerController sourcePlayer, EnumPokemonType energyType)
    {
        CardInstance active = sourcePlayer.activePokemon;
        int activeRampAmount = GetMaxEnergyRampAmount(active);
        if (activeRampAmount <= 0) return null;

        return sourcePlayer.benchPokemons
            .Where(c => HasEnergyRampAttack(c))
            .Where(c => GetMaxEnergyRampAmount(c) > activeRampAmount)
            .Where(c => !HasEnergyForAnyAttack(c))
            .Where(c => EnergyHelpsAttackCost(c, energyType))
            .OrderByDescending(GetMaxEnergyRampAmount)
            .ThenBy(c => GetMinEnergyMissingAfterAttach(c, energyType))
            .FirstOrDefault();
    }

    private static CardInstance ChooseScalingDamageRampTarget(PlayerController sourcePlayer, EnumPokemonType energyType, int opponentMaxHp)
    {
        if (opponentMaxHp <= 0) return null;

        return sourcePlayer.benchPokemons
            .Where(c => HasScalingDamageLine(c, sourcePlayer))
            .Where(c => !IsScalingDamageSatisfied(c, sourcePlayer, opponentMaxHp))
            .Where(c => ScalingDamageEnergyStillHelps(c, sourcePlayer, energyType, opponentMaxHp))
            .OrderBy(c => GetScalingDamageEnergyDeficit(c, sourcePlayer, opponentMaxHp))
            .ThenByDescending(c => GetBestScalingDamageRequirement(c, sourcePlayer, opponentMaxHp))
            .FirstOrDefault();
    }

    private static bool HasScalingDamageLine(CardInstance card, PlayerController sourcePlayer)
    {
        return GetCurrentAndFuturePokemonData(card, sourcePlayer).Any(HasScalingDamageEffect);
    }

    private static bool HasScalingDamageEffect(PokemonData pd)
    {
        if (pd?.attacks == null) return false;
        foreach (var atk in pd.attacks)
        {
            if (atk.effects == null) continue;
            foreach (var ef in atk.effects)
                if (ef.cardEffectType == EnumCardEffectType.Psychic ||
                    ef.cardEffectType == EnumCardEffectType.PowerUp)
                    return true;
        }
        return false;
    }

    private static bool HasEnergyRampAttack(CardInstance card)
    {
        var pd = card?.baseData as PokemonData;
        return pd?.attacks != null && pd.attacks.Any(atk =>
            atk.effects != null &&
            atk.effects.Any(e => e.cardEffectType == EnumCardEffectType.EnergyRamp));
    }

    private static int GetMaxEnergyRampAmount(CardInstance card)
    {
        var pd = card?.baseData as PokemonData;
        if (pd?.attacks == null) return 0;

        return pd.attacks
            .Where(atk => atk.effects != null)
            .Select(atk => atk.effects
                .Where(e => e.cardEffectType == EnumCardEffectType.EnergyRamp)
                .Select(e => Mathf.Max(0, e.effectAmount))
                .DefaultIfEmpty(0)
                .Sum())
            .DefaultIfEmpty(0)
            .Max();
    }

    private static bool HasEnergyForAnyAttack(CardInstance card)
    {
        var pd = card?.baseData as PokemonData;
        if (card?.pokemonLogic == null || pd?.attacks == null) return false;
        return pd.attacks.Any(atk => CanAffordAttack(card.pokemonLogic, atk));
    }

    private static bool EnergyHelpsAttackCost(CardInstance card, EnumPokemonType energyType)
    {
        return GetMinEnergyMissingAfterAttach(card, energyType) < GetMinEnergyMissing(card);
    }

    private static int GetMinEnergyMissingAfterAttach(CardInstance card, EnumPokemonType energyType)
    {
        var pd = card?.baseData as PokemonData;
        if (pd?.attacks == null || card?.pokemonLogic?.energyEquipped == null) return int.MaxValue;

        Dictionary<EnumPokemonType, int> equipped = new Dictionary<EnumPokemonType, int>(card.pokemonLogic.energyEquipped);
        if (energyType != EnumPokemonType.None)
        {
            if (!equipped.ContainsKey(energyType))
                equipped[energyType] = 0;
            equipped[energyType]++;
        }

        return pd.attacks
            .Select(atk => GetEnergyMissingForAttack(equipped, atk, card.pokemonLogic.tempBuffsData.attackEnergyCostChange))
            .DefaultIfEmpty(int.MaxValue)
            .Min();
    }

    private static int GetMinEnergyMissing(CardInstance card)
    {
        var pd = card?.baseData as PokemonData;
        if (pd?.attacks == null || card?.pokemonLogic?.energyEquipped == null) return int.MaxValue;

        return pd.attacks
            .Select(atk => GetEnergyMissingForAttack(card.pokemonLogic.energyEquipped, atk, card.pokemonLogic.tempBuffsData.attackEnergyCostChange))
            .DefaultIfEmpty(int.MaxValue)
            .Min();
    }

    private static int GetEnergyMissingForAttack(Dictionary<EnumPokemonType, int> equipped, AttackData attack, int attackEnergyCostChange)
    {
        if (attack == null) return 0;
        var attackCost = attack.attackCost ?? new List<EnumPokemonType>();

        var available = new Dictionary<EnumPokemonType, int>(equipped ?? new Dictionary<EnumPokemonType, int>());
        int jokers = available.TryGetValue(EnumPokemonType.Dragon, out int dragonCount) ? dragonCount : 0;
        available[EnumPokemonType.Dragon] = 0; // jokers are spent flexibly below
        int missingTyped = 0;

        foreach (EnumPokemonType cost in attackCost)
        {
            if (cost == EnumPokemonType.Colorless) continue;
            if (available.TryGetValue(cost, out int count) && count > 0)
                available[cost]--;
            else
                missingTyped++;
        }

        // Dragon jokers cover unpaid typed slots first, then spill into Colorless below.
        int typedCoveredByJoker = Mathf.Min(jokers, missingTyped);
        missingTyped -= typedCoveredByJoker;
        jokers -= typedCoveredByJoker;

        int colorlessCost = attackCost.Count(c => c == EnumPokemonType.Colorless);
        int adjustedColorless = Mathf.Max(0, colorlessCost + attackEnergyCostChange);
        int missingColorless = Mathf.Max(0, adjustedColorless - (available.Values.Sum() + jokers));
        return missingTyped + missingColorless;
    }

    private static bool IsScalingDamageSatisfied(CardInstance card, PlayerController sourcePlayer, int opponentMaxHp)
    {
        return GetScalingDamageEnergyDeficit(card, sourcePlayer, opponentMaxHp) <= 0;
    }

    private static bool ScalingDamageEnergyStillHelps(CardInstance card, PlayerController sourcePlayer, EnumPokemonType energyType, int opponentMaxHp)
    {
        if (energyType == EnumPokemonType.None) return false;
        int before = GetScalingDamageEnergyDeficit(card, sourcePlayer, opponentMaxHp);
        if (before <= 0) return false;

        int after = Mathf.Max(0, before - 1);
        return after < before;
    }

    private static int GetScalingDamageEnergyDeficit(CardInstance card, PlayerController sourcePlayer, int opponentMaxHp)
    {
        int required = GetBestScalingDamageRequirement(card, sourcePlayer, opponentMaxHp);
        if (required == int.MaxValue) return int.MaxValue;

        int totalEnergy = card.pokemonLogic.energyEquipped?.Values.Sum() ?? 0;
        return Mathf.Max(0, required - totalEnergy);
    }

    private static int GetBestScalingDamageRequirement(CardInstance card, PlayerController sourcePlayer, int opponentMaxHp)
    {
        if (card?.pokemonLogic == null || opponentMaxHp <= 0) return int.MaxValue;

        int best = int.MaxValue;
        foreach (PokemonData pd in GetCurrentAndFuturePokemonData(card, sourcePlayer))
        {
            if (pd?.attacks == null) continue;
            foreach (AttackData atk in pd.attacks)
                best = Mathf.Min(best, GetScalingDamageRequirement(atk, opponentMaxHp));
        }

        return best;
    }

    private static int GetScalingDamageRequirement(AttackData attack, int opponentMaxHp)
    {
        if (attack?.effects == null) return int.MaxValue;

        int attackCost = attack.attackCost?.Count ?? 0;
        int best = int.MaxValue;
        foreach (EffectData effect in attack.effects)
        {
            if (effect.cardEffectType == EnumCardEffectType.PowerUp && effect.effectAmount > 0)
            {
                int missingDamage = Mathf.Max(0, opponentMaxHp - attack.damage);
                int energyForKo = Mathf.CeilToInt(missingDamage / (float)effect.effectAmount);
                best = Mathf.Min(best, Mathf.Max(attackCost, energyForKo) + 2);
            }
            else if (effect.cardEffectType == EnumCardEffectType.Psychic)
            {
                best = Mathf.Min(best, attackCost + 2);
            }
        }

        return best;
    }

    private static IEnumerable<PokemonData> GetCurrentAndFuturePokemonData(CardInstance card, PlayerController sourcePlayer)
    {
        if (card?.baseData is not PokemonData current) yield break;

        List<PokemonData> ownedPokemon = GetOwnedPokemonData(sourcePlayer)
            .GroupBy(p => p.cardName)
            .Select(g => g.First())
            .ToList();

        yield return current;
        foreach (PokemonData candidate in ownedPokemon)
        {
            if (candidate.cardName == current.cardName) continue;
            if (IsSameEvolutionLineOrFuture(candidate, current, ownedPokemon))
                yield return candidate;
        }
    }

    private static bool IsSameEvolutionLineOrFuture(PokemonData candidate, PokemonData current, List<PokemonData> ownedPokemon)
    {
        string parent = candidate.evolvesFrom;
        while (!string.IsNullOrEmpty(parent))
        {
            if (parent == current.cardName) return true;
            PokemonData parentData = ownedPokemon.FirstOrDefault(p => p.cardName == parent);
            parent = parentData?.evolvesFrom;
        }

        return false;
    }

    private static List<PokemonData> GetOwnedPokemonData(PlayerController sourcePlayer)
    {
        IEnumerable<CardInstance> cards =
            (sourcePlayer.activePokemon != null ? new[] { sourcePlayer.activePokemon } : Enumerable.Empty<CardInstance>())
            .Concat(sourcePlayer.benchPokemons ?? Enumerable.Empty<CardInstance>())
            .Concat(sourcePlayer.hand ?? Enumerable.Empty<CardInstance>())
            .Concat(sourcePlayer.deck ?? Enumerable.Empty<CardInstance>())
            .Concat(sourcePlayer.discardPile ?? Enumerable.Empty<CardInstance>());

        return cards
            .Where(c => c?.baseData is PokemonData)
            .Select(c => c.baseData as PokemonData)
            .ToList();
    }

    private static int GetOpponentMaxCurrentHp(PlayerController opponent)
    {
        if (opponent == null) return 0;
        int max = 0;
        if (opponent.activePokemon?.pokemonLogic != null)
            max = Mathf.Max(max, opponent.activePokemon.pokemonLogic.currentHp);
        foreach (var b in opponent.benchPokemons)
            if (b?.pokemonLogic != null)
                max = Mathf.Max(max, b.pokemonLogic.currentHp);
        return max;
    }

    /// <summary>
    /// Wybiera najlepszy cel zamiany dla AI:
    /// preferuje ławkowego Pokemona, który może już atakować, a w tej grupie bierze najwyższy damage.
    /// </summary>
    private static CardInstance ChooseBestSwapSelfTarget(PlayerController sourcePlayer)
    {
        if (sourcePlayer == null || sourcePlayer.benchPokemons == null || sourcePlayer.benchPokemons.Count == 0)
            return null;

        CardInstance bestTarget = null;
        int bestDamage = int.MinValue;

        foreach (var benchCard in sourcePlayer.benchPokemons)
        {
            var pokemon = benchCard?.pokemonLogic;
            var pokemonData = benchCard?.baseData as PokemonData;
            if (pokemon == null || pokemonData?.attacks == null) continue;

            bool canAttack = pokemonData.attacks.Any(attack => CanAffordAttack(pokemon, attack));
            if (!canAttack) continue;

            int maxDamage = pokemonData.attacks
                .Where(attack => CanAffordAttack(pokemon, attack))
                .Select(attack => attack.damage)
                .DefaultIfEmpty(0)
                .Max();

            if (bestTarget == null || maxDamage > bestDamage)
            {
                bestTarget = benchCard;
                bestDamage = maxDamage;
            }
        }

        return bestTarget ?? sourcePlayer.benchPokemons[0];
    }

    /// <summary>
    /// Wykonuje atak: sprawdzenie energii -> obrażenia -> efekty (coroutine).
    /// Zwraca false, gdy atak nie został wykonany i tura nie powinna być zużyta.
    /// </summary>
    public static bool ExecuteAttack(CardInstance attackerCard, PlayerController attackerOwner,
                                     CardInstance defenderCard, PlayerController defenderOwner,
                                     AttackData attack)
    {
        if (!CanAffordAttack(attackerCard.pokemonLogic, attack))
        {
            Debug.LogWarning($"[Attack] {attackerCard.baseData.cardName}: niewystarczająca energia.");
            return false;
        }

        int discardCost = GetDiscardCost(attackerOwner, attack.effects);
        if (attackerOwner.hand.Count < discardCost)
        {
            Debug.Log($"[Attack] {attackerCard.baseData.cardName}: not enough cards in hand to pay discard cost ({discardCost}). Attack does nothing.");
            return false;
        }

        if (discardCost > 0)
        {
            DiscardRandomCardFromHand(attackerOwner, discardCost);
        }

        // Some attacks force the opponent to switch their Active BEFORE the hit lands (e.g. Noivern's
        // Dominating Echo). The swap must resolve first so both the damage and any EnemyActivePokemon
        // effects (Confuse, Paralyze) strike the newly-promoted Pokemon, not the one that left.
        bool hasSwapEnemy = attack.effects != null &&
                            attack.effects.Any(e => e.cardEffectType == EnumCardEffectType.SwapEnemy);
        if (hasSwapEnemy)
        {
            TrySwapEnemy(defenderOwner);
            defenderCard = defenderOwner.activePokemon;
        }

        if (defenderCard?.pokemonLogic == null)
        {
            Debug.Log($"[Attack] {attackerCard.baseData.cardName}: no defender to damage after swap.");
            return false;
        }

        int attackDamage = GetModifiedAttackDamage(attack.damage, attackerCard.pokemonLogic, defenderCard.pokemonLogic, attack.effects);
        var context = new AttackEffectContext();

        // Each hit notifies its own HP change so every hit (including the first) shows its damage on the UI.
        context.damageDealtToEnemyPokemon += BattleManager.Instance.ApplyAttackDamage(
            attackerCard.pokemonLogic, attackerOwner,
            defenderCard.pokemonLogic, defenderOwner,
            attackDamage, attack.attackName,
            notifyHpChanged: true
        );

        // SwapEnemy was already resolved above (pre-damage); strip it so the effect coroutine
        // doesn't swap a second time. All other effects still resolve post-damage as before.
        List<EffectData> remainingEffects = hasSwapEnemy
            ? attack.effects.Where(e => e.cardEffectType != EnumCardEffectType.SwapEnemy).ToList()
            : attack.effects;

        Instance.StartCoroutine(Instance.FinishAttackCoroutine(
            attackerCard, attackerOwner, defenderCard, defenderOwner, attack, remainingEffects, context, attackDamage));
        return true;
    }

    private IEnumerator FinishAttackCoroutine(CardInstance attackerCard, PlayerController attackerOwner,
                                               CardInstance defenderCard, PlayerController defenderOwner,
                                               AttackData attack, List<EffectData> effectsToRun,
                                               AttackEffectContext context, int attackDamage)
    {
        if (effectsToRun != null && effectsToRun.Count > 0)
            yield return ExecuteCardEffectsCoroutine(attackerOwner, attackerCard, effectsToRun, attack, context, attackDamage);
    }

    private IEnumerator ExecuteCardEffectsCoroutine(
        PlayerController sourcePlayer,
        CardInstance sourceCard,
        List<EffectData> effects,
        AttackData currentAttack,
        AttackEffectContext attackContext,
        int resolvedAttackDamage = 0)
    {
        PlayerController opponent = BattleManager.Instance.GetOpponent(sourcePlayer);
        Pokemon sourcePokemon = sourceCard?.pokemonLogic;

        if (currentAttack != null)
        {
            foreach (var effect in effects.Where(e => e.cardEffectType == EnumCardEffectType.SwapEnemy))
            {
                TrySwapEnemy(opponent);
                if (useEffectDelays)
                    yield return new WaitForSeconds(effectDelay);
            }
        }

        foreach (var effect in effects)
        {
            if (currentAttack != null &&
                (effect.cardEffectType == EnumCardEffectType.SwapEnemy ||
                 effect.cardEffectType == EnumCardEffectType.SwapSelf ||
                 effect.cardEffectType == EnumCardEffectType.LeechLife))
            {
                continue;
            }

            PlayerController targetPlayer;
            Pokemon targetPokemon;

            switch (effect.cardEffectTarget)
            {
                case EnumCardEffectTarget.Self:
                case EnumCardEffectTarget.ActivePokemon:
                    targetPlayer = sourcePlayer;
                    targetPokemon = sourcePlayer.activePokemon?.pokemonLogic;
                    break;
                case EnumCardEffectTarget.Opponent:
                case EnumCardEffectTarget.EnemyActivePokemon:
                    targetPlayer = opponent;
                    targetPokemon = opponent?.activePokemon?.pokemonLogic;
                    break;
                case EnumCardEffectTarget.BenchPokemon:
                    targetPlayer = sourcePlayer;
                    targetPokemon = null;
                    break;
                case EnumCardEffectTarget.EnemyBenchPokemon:
                    targetPlayer = opponent;
                    targetPokemon = null;
                    break;
                default:
                    Debug.LogWarning($"[CardActions] Unknown cardEffectTarget '{effect.cardEffectTarget}' for effect '{effect.cardEffectType}' — skipping targeted application.");
                    targetPlayer = null;
                    targetPokemon = null;
                    break;
            }

            switch (effect.cardEffectType)
            {
                case EnumCardEffectType.None:
                    break;

                case EnumCardEffectType.DealDamage:
                    if (targetPokemon != null && targetPlayer != null)
                    {
                        int dealt = BattleManager.Instance.ApplyEffectDamage(targetPokemon, targetPlayer, effect.effectAmount);
                        AddEnemyDamage(attackContext, sourcePlayer, targetPlayer, dealt);
                    }
                    break;

                case EnumCardEffectType.Heal:
                    if (targetPokemon != null)
                    {
                        if (effect.effectAmount < 0 && targetPlayer != null)
                        {
                            int dealt = BattleManager.Instance.ApplyEffectDamage(targetPokemon, targetPlayer, -effect.effectAmount);
                            AddEnemyDamage(attackContext, sourcePlayer, targetPlayer, dealt);
                        }
                        else
                        {
                            HealPokemon(targetPokemon, effect.effectAmount);
                        }
                    }
                    break;

                case EnumCardEffectType.BenchHeal:
                    if (targetPlayer != null)
                        foreach (var bench in targetPlayer.benchPokemons)
                            if (bench?.pokemonLogic != null)
                            {
                                if (effect.effectAmount < 0)
                                {
                                    int dealt = BattleManager.Instance.ApplyEffectDamage(bench.pokemonLogic, targetPlayer, -effect.effectAmount);
                                    AddEnemyDamage(attackContext, sourcePlayer, targetPlayer, dealt);
                                }
                                else
                                {
                                    bool healed = HealPokemon(bench.pokemonLogic, effect.effectAmount);
                                    if (healed)
                                    {
                                        Debug.Log($"[BenchHeal] Healed bench Pokemon {bench.baseData.cardName} for player {targetPlayer.playerId}.");
                                    }
                                }
                            }
                    break;

                case EnumCardEffectType.BenchDmg:
                    if (targetPlayer != null)
                        foreach (var bench in new List<CardInstance>(targetPlayer.benchPokemons))
                            if (bench?.pokemonLogic != null)
                            {
                                int dealt = BattleManager.Instance.ApplyEffectDamage(bench.pokemonLogic, targetPlayer, effect.effectAmount);
                                AddEnemyDamage(attackContext, sourcePlayer, targetPlayer, dealt);
                            }
                    break;

                case EnumCardEffectType.DmgTakenRed:
                    if (targetPokemon != null)
                    {
                        int amount = Mathf.Abs(effect.effectAmount);
                        // Multiple reduction sources in the same turn stack additively.
                        targetPokemon.tempBuffsData.SetAttackDamageTakenReduction(
                            targetPokemon.tempBuffsData.takeLessDamageFromAttacksBuff + amount);
                        BattleManager.Instance.NotifyPokemonStatusChanged(targetPokemon);
                    }
                    break;

                case EnumCardEffectType.EnergyRamp:
                {
                    EnumPokemonType energyType = sourcePokemon != null ? sourcePokemon.pokemonData.type : EnumPokemonType.None;
                    if (energyType != EnumPokemonType.None && sourcePlayer.benchPokemons.Count > 0)
                    {
                        CardInstance targetBench = null;

                        if (sourcePlayer.playerType == EnumPlayerType.Human)
                        {
                            bool selectionStarted = playerManager.PrepareToSelectBenchPokemon(
                                sourcePlayer,
                                EnumSelectionAction.EnergyRamp,
                                selected => targetBench = selected);

                            if (selectionStarted)
                            {
                                yield return new WaitUntil(() => targetBench != null);
                            }
                        }
                        else
                        {
                            targetBench = ChooseBenchEnergyRampTarget(sourcePlayer, energyType);
                        }

                        if (targetBench?.pokemonLogic != null)
                        {
                            playerManager.AddEnergyToPokemon(targetBench, energyType, effect.effectAmount);
                            UnityEngine.Debug.Log($"[EnergyRamp] Added {energyType} energy to {targetBench.baseData.cardName}.");
                        }
                        else
                        {
                            UnityEngine.Debug.Log("[EnergyRamp] No valid bench target — energy not attached.");
                        }
                    }
                    break;
                }

                case EnumCardEffectType.EnergyDiscard:
                {
                    if (targetPokemon != null)
                    {
                        var dict = targetPokemon.energyEquipped;
                        for (int i = 0; i < effect.effectAmount; i++)
                        {
                            int totalEnergy = dict.Values.Sum();
                            if (totalEnergy == 0) break;

                            // Weight by energy count, not by type: 6 Psychic + 1 Fire => 6/7 vs 1/7.
                            int pick = UnityEngine.Random.Range(0, totalEnergy);
                            EnumPokemonType randomType = EnumPokemonType.None;
                            foreach (var kv in dict)
                            {
                                if (kv.Value <= 0) continue;
                                pick -= kv.Value;
                                if (pick < 0) { randomType = kv.Key; break; }
                            }

                            dict[randomType]--;
                            if (dict[randomType] <= 0)
                                dict.Remove(randomType);
                        }

                        if (targetPlayer?.activePokemon != null)
                            PlayerManager.NotifyEnergyChanged(targetPlayer.activePokemon);
                    }
                    break;
                }

                case EnumCardEffectType.DrawCard:
                    if (targetPlayer != null)
                        DrawCard(targetPlayer, effect.effectAmount);
                    break;

                case EnumCardEffectType.DiscardHand:
                    // Attack discard is paid before damage. Trainer discard effects still resolve here.
                    if (currentAttack == null && targetPlayer != null)
                        DiscardRandomCardFromHand(targetPlayer, GetDiscardAmount(targetPlayer, effect.effectAmount));
                    break;

                case EnumCardEffectType.Multiattack:
                    if (currentAttack != null && sourcePokemon != null && targetPlayer != null)
                    {
                        for (int i = 1; i < effect.effectAmount; i++)
                        {
                            // Delay between hits long enough for the previous hit's damage number and
                            // HP drop to finish animating, otherwise consecutive hits blur into one.
                            if (useEffectDelays)
                                yield return new WaitForSeconds(MultiattackHitDelay());

                            if (!CanContinueMultiattack(targetPokemon, targetPlayer))
                            {
                                yield return WaitForMultiattackReplacement(targetPlayer);
                                targetPokemon = targetPlayer?.activePokemon?.pokemonLogic;
                            }

                            if (!CanContinueMultiattack(targetPokemon, targetPlayer))
                            {
                                Debug.Log($"[Multiattack] Stopping extra hits: target is no longer valid.");
                                break;
                            }

                            int dealt = BattleManager.Instance.ApplyAttackDamage(
                                sourcePokemon, sourcePlayer,
                                targetPokemon, targetPlayer,
                                resolvedAttackDamage, notifyHpChanged: true);
                            AddEnemyDamage(attackContext, sourcePlayer, targetPlayer, dealt);
                        }
                    }
                    break;

                case EnumCardEffectType.Counterattack:
                    if (sourcePokemon != null)
                    {
                        sourcePokemon.tempBuffsData.counterAttackDamage = effect.effectAmount;
                        BattleManager.Instance.NotifyPokemonStatusChanged(sourcePokemon);
                    }
                    break;

                case EnumCardEffectType.SwapSelf:
                    TrySwapSelf(sourcePlayer);
                    break;

                case EnumCardEffectType.SwapEnemy:
                    TrySwapEnemy(opponent);
                    break;

                case EnumCardEffectType.Psychic:
                    // Included in resolvedAttackDamage before the first hit.
                    break;

                case EnumCardEffectType.PowerUp:
                    // Included in resolvedAttackDamage before the first hit.
                    break;

                case EnumCardEffectType.LeechLife:
                    if (sourcePokemon != null && attackContext != null && attackContext.damageDealtToEnemyPokemon > 0)
                        HealPokemon(sourcePokemon, attackContext.damageDealtToEnemyPokemon);
                    break;

                case EnumCardEffectType.Poison:
                    // effectAmount = total poison damage per turn (e.g. 10 = normal, 30 = Severe Poison).
                    // 0 falls back to GameRulesConfig.poisonDamagePerTurn at apply time.
                    ApplyPoison(targetPokemon, Mathf.Max(0, effect.effectAmount));
                    break;

                case EnumCardEffectType.Root:
                    ApplyRoot(targetPokemon);
                    break;

                case EnumCardEffectType.Paralyze:
                    ApplyParalyze(targetPokemon);
                    break;

                case EnumCardEffectType.Expose:
                    if (targetPokemon != null)
                    {
                        int amount = Mathf.Abs(effect.effectAmount);
                        // Multiple Expose sources in the same turn stack additively.
                        targetPokemon.tempBuffsData.SetAttackDamageTakenDebuff(
                            targetPokemon.tempBuffsData.takeMoreDamageFromAttacksDebuff + amount);
                        BattleManager.Instance.NotifyPokemonStatusChanged(targetPokemon);
                    }
                    break;

                case EnumCardEffectType.Slow:
                    if (targetPokemon != null)
                    {
                        targetPokemon.tempBuffsData.attackEnergyCostChange = Mathf.Max(targetPokemon.tempBuffsData.attackEnergyCostChange, 1);
                        // Slowed lasts until the Pokemon retreats, so it must survive the start-of-turn reset.
                        targetPokemon.slowPersistsThroughNextOwnerTurn = true;
                        BattleManager.Instance.NotifyPokemonStatusChanged(targetPokemon);
                    }
                    break;

                case EnumCardEffectType.Asleep:
                    ApplyAsleep(targetPokemon);
                    break;

                case EnumCardEffectType.Confuse:
                    ApplyConfuse(targetPokemon);
                    break;

                case EnumCardEffectType.Burn:
                    ApplyBurn(targetPokemon, Mathf.Max(0, effect.effectAmount));
                    break;

                case EnumCardEffectType.DebuffSelf:
                {
                    var debuffTargets = new List<Pokemon>();
                    if (effect.effectAmount == 0 && opponent?.activePokemon != null)
                        debuffTargets.Add(opponent.activePokemon.pokemonLogic);
                    else if (effect.effectAmount == 1)
                    {
                        if (opponent?.activePokemon != null) debuffTargets.Add(opponent.activePokemon.pokemonLogic);
                        if (sourcePokemon != null) debuffTargets.Add(sourcePokemon);
                    }
                    else if (effect.effectAmount == 2 && sourcePokemon != null)
                        debuffTargets.Add(sourcePokemon);

                    foreach (var p in debuffTargets)
                    {
                        ApplyPoison(p);
                        ApplyBurn(p);
                        ApplyRoot(p);
                        p.tempBuffsData.attackEnergyCostChange = Mathf.Max(p.tempBuffsData.attackEnergyCostChange, 1);
                    }
                    break;
                }

                case EnumCardEffectType.Cleanse:
                    if (targetPokemon != null)
                    {
                        targetPokemon.ClearBuffsDebuffsAndStatuses();
                        BattleManager.Instance.NotifyPokemonStatusChanged(targetPokemon);
                    }
                    break;
            }

            if (useEffectDelays)
                yield return new WaitForSeconds(effectDelay);
        }

        if (currentAttack != null)
        {
            if (effects.Any(e => e.cardEffectType == EnumCardEffectType.LeechLife) &&
                sourcePokemon != null &&
                attackContext != null &&
                attackContext.damageDealtToEnemyPokemon > 0)
            {
                HealPokemon(sourcePokemon, attackContext.damageDealtToEnemyPokemon);
                if (useEffectDelays)
                    yield return new WaitForSeconds(effectDelay);
            }

            foreach (var effect in effects.Where(e => e.cardEffectType == EnumCardEffectType.SwapSelf))
            {
                TrySwapSelf(sourcePlayer);
                if (useEffectDelays)
                    yield return new WaitForSeconds(effectDelay);
            }
        }
    }

    private static int GetDiscardCost(PlayerController player, List<EffectData> effects)
    {
        if (effects == null) return 0;

        int total = 0;
        foreach (var effect in effects)
        {
            if (effect.cardEffectType == EnumCardEffectType.DiscardHand)
                total += GetDiscardAmount(player, effect.effectAmount);
        }
        return total;
    }

    private static int GetDiscardAmount(PlayerController player, int amount)
    {
        if (player == null) return 0;
        return amount >= 100 ? player.hand.Count : Mathf.Max(0, amount);
    }

    private static int GetModifiedAttackDamage(int baseDamage, Pokemon attacker, Pokemon defender, List<EffectData> effects)
    {
        int damage = baseDamage;
        if (effects == null) return damage;

        // If the attack discards energy from self before dealing bonus damage (e.g. Hydra Breath),
        // subtract the discarded count so PowerUp reflects the post-discard energy total.
        int selfDiscardCount = effects
            .Where(e => e.cardEffectType == EnumCardEffectType.EnergyDiscard
                     && e.cardEffectTarget == EnumCardEffectTarget.Self)
            .Sum(e => e.effectAmount);

        foreach (var effect in effects)
        {
            if (effect.cardEffectType == EnumCardEffectType.Psychic && defender != null)
                damage += effect.effectAmount * defender.energyEquipped.Values.Sum();
            else if (effect.cardEffectType == EnumCardEffectType.PowerUp && attacker != null)
            {
                int energyAfterDiscard = Mathf.Max(0, attacker.energyEquipped.Values.Sum() - selfDiscardCount);
                damage += effect.effectAmount * energyAfterDiscard;
            }
        }

        return damage;
    }

    private static void AddEnemyDamage(AttackEffectContext context, PlayerController sourcePlayer, PlayerController targetPlayer, int damage)
    {
        if (context == null || sourcePlayer == null || targetPlayer == null || targetPlayer == sourcePlayer) return;
        context.damageDealtToEnemyPokemon += Mathf.Max(0, damage);
    }

    private static bool CanContinueMultiattack(Pokemon targetPokemon, PlayerController targetPlayer)
    {
        if (targetPokemon == null || targetPlayer == null) return false;
        if (targetPokemon.currentHp <= 0) return false;
        return targetPlayer.activePokemon?.pokemonLogic == targetPokemon;
    }

    private IEnumerator WaitForMultiattackReplacement(PlayerController targetPlayer)
    {
        if (targetPlayer == null) yield break;

        bool waitingLogged = false;
        while (!BattleManager.Instance.isGameOver &&
               targetPlayer.activePokemon == null &&
               targetPlayer.benchPokemons.Count > 0)
        {
            if (!waitingLogged)
            {
                Debug.Log("[Multiattack] Target was knocked out; waiting for replacement Active before next hit.");
                waitingLogged = true;
            }

            yield return null;
        }
    }

    private static void TrySwapSelf(PlayerController sourcePlayer)
    {
        if (sourcePlayer == null || sourcePlayer.benchPokemons.Count == 0) return;

        if (sourcePlayer.playerType == EnumPlayerType.Human)
        {
            PlayerManager.Instance.PrepareToSwapSelf();
            return;
        }

        CardInstance target = ChooseBestSwapSelfTarget(sourcePlayer);
        if (target != null)
            PlayerManager.Instance.FreeSwapActive(sourcePlayer, target);
    }

    private static void TrySwapEnemy(PlayerController opponent)
    {
        if (opponent == null || opponent.benchPokemons.Count == 0) return;

        int idx = UnityEngine.Random.Range(0, opponent.benchPokemons.Count);
        PlayerManager.Instance.FreeSwapActive(opponent, opponent.benchPokemons[idx]);
    }

    private void ApplyPoison(Pokemon p, int damagePerTurn = 0)
    {
        if (p != null && p.tempBuffsData.canBePoisoned)
        {
            p.isPoisoned = true;
            if (damagePerTurn > 0)
            {
                // Stacks via Max so re-applying weaker poison never weakens an existing
                // stronger one (same convention as Expose).
                p.tempBuffsData.takeMoreDamageFromPoison =
                    Mathf.Max(p.tempBuffsData.takeMoreDamageFromPoison, damagePerTurn);
            }
            BattleManager.Instance.NotifyPokemonStatusChanged(p);
        }
    }

    private void ApplyRoot(Pokemon p)
    {
        if (p != null && p.tempBuffsData.canBeRooted)
        {
            p.tempBuffsData.rooted = true;
            p.tempBuffsData.canRetreat = false;
            p.rootPersistsThroughNextOwnerTurn = true;
            BattleManager.Instance.NotifyPokemonStatusChanged(p);
        }
    }

    private void ApplyParalyze(Pokemon p)
    {
        if (p != null && p.tempBuffsData.canBeParalyzed)
        {
            p.otherSpecialCondition = EnumSpecialConditionType.Paralyzed;
            p.tempBuffsData.canAttack = false;
            p.tempBuffsData.canRetreat = false;
            p.paralysisPersistsThroughNextOwnerTurn = true;
            BattleManager.Instance.NotifyPokemonStatusChanged(p);
        }
    }

    private void ApplyAsleep(Pokemon p)
    {
        if (p != null && p.tempBuffsData.canBeAsleep)
        {
            p.otherSpecialCondition = EnumSpecialConditionType.Asleep;
            p.tempBuffsData.canAttack = false;
            p.tempBuffsData.canRetreat = false;
            BattleManager.Instance.NotifyPokemonStatusChanged(p);
        }
    }

    private void ApplyConfuse(Pokemon p)
    {
        if (p != null && p.tempBuffsData.canBeConfused)
        {
            p.otherSpecialCondition = EnumSpecialConditionType.Confused;
            BattleManager.Instance.NotifyPokemonStatusChanged(p);
        }
    }

    private void ApplyBurn(Pokemon p, int damagePerTurn = 0)
    {
        if (p != null && p.tempBuffsData.canBeBurned)
        {
            p.isBurned = true;
            if (damagePerTurn > 0)
            {
                // Override semantics (same convention as Poison): the card-supplied amount REPLACES
                // the default burn damage ("do X rather than the usual amount"). Max so re-applying a
                // weaker burn never lowers a stronger one already in place.
                p.tempBuffsData.takeMoreDamageFromBurn =
                    Mathf.Max(p.tempBuffsData.takeMoreDamageFromBurn, damagePerTurn);
            }
            BattleManager.Instance.NotifyPokemonStatusChanged(p);
        }
    }
}
