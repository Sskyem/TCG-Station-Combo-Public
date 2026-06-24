using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public class BattleManager : MonoBehaviour
{
    #region Singleton
    public static BattleManager Instance { get; private set; }
    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }
    #endregion

    [Header("Modules")]
    public TurnManager turnManager;
    public BoardVisualizer boardVisualizer;
    public JsonLoader jsonLoader;
    public PlayerManager playerManager;
    public CardActions cardActions;

    [Header("UI Elements")]
    public TMPro.TMP_Text winnerText;
    public GameObject gameOverPanel;

    [Header("Win Condition")]
    public bool isGameOver = false;

    // Eventy — BoardVisualizer i przyszłe loggery mogą się tutaj podpiąć
    public static event System.Action<Pokemon, PlayerController> OnPokemonKnockedOut;
    public static event System.Action<CardInstance, PlayerController> OnPokemonPromoted;
    public static event System.Action<PlayerController> OnGameOver;
    public static event System.Action<Pokemon> OnPokemonHpChanged;
    public static event System.Action<Pokemon> OnPokemonStatusChanged;
    public static event System.Action<Pokemon, PlayerController, Pokemon, string, int> OnAttackExecuted;

    private GameManager gameManager;
    private int combatResolutionDepth;
    private PlayerController pendingForcedWinner;
    private bool pendingForcedDraw;

    private void Start()
    {
        gameManager = GameManager.Instance;
        playerManager = PlayerManager.Instance;
        playerManager.battleManager = this;
    }

    // =====================================================================
    #region Między turami (Between Turns)
    // =====================================================================

    /// <summary>
    /// Wywołaj na końcu każdej tury, PRZED zmianą aktywnego gracza.
    /// Aplikuje obrażenia od Poison i Burn oraz pasywne leczenie.
    /// </summary>
    public bool ProcessBetweenTurns()
    {
        Debug.Log("[BattleManager] --- Between-turns phase ---");

        BeginCombatResolution();
        try
        {
            bool anyVisualChange = false;
            anyVisualChange |= ProcessStatusDamageForPlayer(playerManager.player1);
            anyVisualChange |= ProcessStatusDamageForPlayer(playerManager.player2);
            return anyVisualChange;
        }
        finally
        {
            EndCombatResolution();
        }
    }

    /// <summary>
    /// Aplikuje obrażenia od statusów dla aktywnego Pokemona danego gracza.
    /// </summary>
    private bool ProcessStatusDamageForPlayer(PlayerController player)
    {
        if (player.activePokemon == null) return false;

        Pokemon active = player.activePokemon.pokemonLogic;
        PlayerController opponent = GetOpponent(player);
        bool anyVisualChange = false;

        // POISON: damage between turns. effectAmount on the card = total damage per turn.
        // If no override is set (== 0), fall back to GameRulesConfig.poisonDamagePerTurn.
        if (active.isPoisoned)
        {
            int poisonDmg = active.tempBuffsData.takeMoreDamageFromPoison > 0
                ? active.tempBuffsData.takeMoreDamageFromPoison
                : GameRulesConfig.Instance.poisonDamagePerTurn;
            Debug.Log($"[BattleManager] {active.pokemonData.cardName} (P{player.playerId}) takes {poisonDmg} poison damage.");
            ApplyStatusDamage(active, player, opponent, poisonDmg);
            anyVisualChange = true;
        }

        // BURN: 20 obrażeń między turami, potem rzut monetą. Orzeł = wyleczony.
        if (active.isBurned)
        {
            // Override semantics (same as Poison): a card-supplied burn amount (e.g. Magmortar's 40)
            // REPLACES the default rather than stacking on top of it.
            int burnDmg = active.tempBuffsData.takeMoreDamageFromBurn > 0
                ? active.tempBuffsData.takeMoreDamageFromBurn
                : GameRulesConfig.Instance.burnDamagePerTurn;
            Debug.Log($"[BattleManager] {active.pokemonData.cardName} (P{player.playerId}) takes {burnDmg} burn damage.");
            ApplyStatusDamage(active, player, opponent, burnDmg);
            anyVisualChange = true;

            // Rzut monetą: orzeł (true) = Burn znika, reszka (false) = Burn zostaje
            bool heads = UnityEngine.Random.Range(0, 2) == 1;
            if (heads)
            {
                active.isBurned = false;
                OnPokemonStatusChanged?.Invoke(active);
                Debug.Log($"[BattleManager] {active.pokemonData.cardName} recovered from Burn (coin: heads).");
                anyVisualChange = true;
            }
            else
            {
                Debug.Log($"[BattleManager] {active.pokemonData.cardName} is still Burned (coin: tails).");
            }
        }

        // HEAL PER TURN: pasywna regeneracja (np. z efektu Root lub karty trenera)
        if (active.tempBuffsData.healPerTurn > 0)
        {
            anyVisualChange |= CardActions.HealPokemon(active, active.tempBuffsData.healPerTurn);
        }

        return anyVisualChange;
    }

    #endregion

    // =====================================================================
    #region Początek tury (Start of Turn)
    // =====================================================================

    /// <summary>
    /// Wywołaj na początku tury nowego aktywnego gracza, PO zmianie aktywnego gracza.
    /// Obsługuje blokady Sleep/Paralyze i resetuje tymczasowe buffy.
    /// </summary>
    public void ProcessStartOfTurn(PlayerController activePlayer)
    {
        Debug.Log($"[BattleManager] --- Start of turn (Player {activePlayer.playerId}: {activePlayer.playerName}) ---");

        if (activePlayer.activePokemon != null)
        {
            Pokemon active = activePlayer.activePokemon.pokemonLogic;

            // SLEEP: rzut monetą na początku tury właściciela. Orzeł = budzenie.
            if (active.otherSpecialCondition == EnumSpecialConditionType.Asleep)
            {
                bool heads = UnityEngine.Random.Range(0, 2) == 1;
                if (heads)
                {
                    active.otherSpecialCondition = EnumSpecialConditionType.None;
                    OnPokemonStatusChanged?.Invoke(active);
                    Debug.Log($"[BattleManager] {active.pokemonData.cardName} woke up (coin: heads).");
                }
                else
                {
                    Debug.Log($"[BattleManager] {active.pokemonData.cardName} is still Asleep (coin: tails). Cannot attack or retreat.");
                }
            }
        }

        // Resetujemy tymczasowe buffy i flagi akcji PO obsłudze statusów.
        // Persistentne warunki (isPoisoned, otherSpecialCondition) NIE są dotykane.
        ResetAllBuffsForPlayer(activePlayer);

        ApplyActiveSpecialConditionRestrictions(activePlayer);
    }

    public void ProcessEndOfTurn(PlayerController endingPlayer)
    {
        Pokemon active = endingPlayer?.activePokemon?.pokemonLogic;
        if (active == null) return;

        if (active.otherSpecialCondition == EnumSpecialConditionType.Paralyzed)
        {
            if (active.paralysisPersistsThroughNextOwnerTurn)
            {
                active.paralysisPersistsThroughNextOwnerTurn = false;
                return;
            }

            active.otherSpecialCondition = EnumSpecialConditionType.None;
            active.paralysisPersistsThroughNextOwnerTurn = false;
            OnPokemonStatusChanged?.Invoke(active);
            Debug.Log($"[BattleManager] {active.pokemonData.cardName} recovered from Paralysis.");
        }
    }

    #endregion

    // =====================================================================
    #region Obrażenia (Damage)
    // =====================================================================

    /// <summary>
    /// Groups all damage and effects belonging to one combat action. Win conditions
    /// are evaluated only after the outermost resolution ends, so simultaneous KOs
    /// can correctly produce a draw.
    /// </summary>
    public void BeginCombatResolution()
    {
        combatResolutionDepth++;
    }

    public void EndCombatResolution()
    {
        if (combatResolutionDepth <= 0)
        {
            Debug.LogWarning("[BattleManager] EndCombatResolution called without a matching begin.");
            combatResolutionDepth = 0;
            return;
        }

        combatResolutionDepth--;
        if (combatResolutionDepth == 0)
            ResolvePendingGameOver();
    }

    /// <summary>
    /// Zadaje obrażenia od ataku z uwzględnieniem modyfikatorów obu stron.
    /// Wywołaj przy wykonaniu ataku przez Pokemona.
    /// </summary>
    public int ApplyAttackDamage(Pokemon attacker, PlayerController attackerOwner,
                                  Pokemon defender, PlayerController defenderOwner,
                                  int baseDamage, string moveName = "", bool notifyHpChanged = true)
    {
        if (baseDamage <= 0)
        {
            // Zero-damage attacks (utility moves) still fire the event so visuals/animations play.
            OnAttackExecuted?.Invoke(attacker, attackerOwner, defender, moveName, 0);
            return 0;
        }

        // Sumujemy wszystkie modyfikatory obrażeń
        int totalDamage = baseDamage
            + attackerOwner.doMoreDamageToActive               // buff gracza (np. ze stadionu)
            + attacker.tempBuffsData.doMoreDamageToActive      // buff Pokemona (np. PowerUp)
            + defender.tempBuffsData.takeMoreDamageFromAttacks;// debuff obrońcy (np. Expose)

        // Obrażenia nie mogą być ujemne
        totalDamage = Mathf.Max(0, totalDamage);

        Debug.Log($"[BattleManager] {attacker.pokemonData.cardName} attacks {defender.pokemonData.cardName}" +
                  $" for {totalDamage} dmg (base: {baseDamage}).");

        int hpBefore = defender.currentHp;
        defender.currentHp = Mathf.Max(0, defender.currentHp - totalDamage);
        int damageDealt = Mathf.Max(0, hpBefore - defender.currentHp);
        if (notifyHpChanged) OnPokemonHpChanged?.Invoke(defender);
        OnAttackExecuted?.Invoke(attacker, attackerOwner, defender, moveName, damageDealt);
        CheckForKnockout(defender, defenderOwner, attackerOwner);

        // COUNTERATTACK: defender odpowiada obrażeniami gdy jest zaatakowany
        if (damageDealt > 0 && defender.tempBuffsData.counterAttackDamage > 0)
        {
            int counterDmg = defender.tempBuffsData.counterAttackDamage;
            Debug.Log($"[BattleManager] Counterattack: {defender.pokemonData.cardName} deals {counterDmg} dmg back to {attacker.pokemonData.cardName}.");
            attacker.currentHp = Mathf.Max(0, attacker.currentHp - counterDmg);
            OnPokemonHpChanged?.Invoke(attacker);
            CheckForKnockout(attacker, attackerOwner, defenderOwner);
        }

        // RECOIL: atakujący też może dostać obrażenia (np. efekt DebuffSelf / Counterattack)
        if (attacker.tempBuffsData.recoilDamage > 0)
        {
            Debug.Log($"[BattleManager] {attacker.pokemonData.cardName} takes {attacker.tempBuffsData.recoilDamage} recoil damage.");
            attacker.currentHp = Mathf.Max(0, attacker.currentHp - attacker.tempBuffsData.recoilDamage);
            OnPokemonHpChanged?.Invoke(attacker);
            CheckForKnockout(attacker, attackerOwner, defenderOwner);
        }

        return damageDealt;
    }

    /// <summary>
    /// Zadaje obrażenia od efektów kart (BenchDmg, Psychic itp.).
    /// Nie uwzględnia modyfikatorów ataku.
    /// </summary>
    public int ApplyEffectDamage(Pokemon target, PlayerController targetOwner, int damage)
    {
        if (damage <= 0) return 0;
        Debug.Log($"[BattleManager] Effect dmg: {damage} → {target.pokemonData.cardName} (P{targetOwner.playerId}). HP: {target.currentHp}→{Mathf.Max(0, target.currentHp - damage)}.");
        int hpBefore = target.currentHp;
        target.currentHp = Mathf.Max(0, target.currentHp - damage);
        int damageDealt = Mathf.Max(0, hpBefore - target.currentHp);
        OnPokemonHpChanged?.Invoke(target);
        CheckForKnockout(target, targetOwner, GetOpponent(targetOwner));
        return damageDealt;
    }

    /// <summary>
    /// Zadaje obrażenia od statusów (Poison, Burn).
    /// Nie uwzględnia modyfikatorów ataku — tylko modyfikatory statusowe.
    /// </summary>
    private void ApplyStatusDamage(Pokemon target, PlayerController targetOwner,
                                    PlayerController opponent, int damage)
    {
        if (damage <= 0) return;

        target.currentHp = Mathf.Max(0, target.currentHp - damage);
        OnPokemonHpChanged?.Invoke(target);
        CheckForKnockout(target, targetOwner, opponent);
    }

    public static void NotifyHpChanged(Pokemon pokemon) => OnPokemonHpChanged?.Invoke(pokemon);

    #endregion

    // =====================================================================
    #region Nokaut (Knockout)
    // =====================================================================

    /// <summary>
    /// Sprawdza czy HP Pokemona spadło do zera lub poniżej i obsługuje nokaut.
    /// </summary>
    private void CheckForKnockout(Pokemon pokemon, PlayerController owner, PlayerController opponent)
    {
        if (pokemon.currentHp > 0) return;

        // Specjalny efekt (np. karta z "survive at 10 HP") — Pokemon nie pada
        if (!pokemon.tempBuffsData.canBeKnockedOut)
        {
            pokemon.currentHp = 10;
            Debug.Log($"[BattleManager] {pokemon.pokemonData.cardName} would be KO'd, but a special effect kept it at 10 HP.");
            return;
        }

        HandleKnockout(pokemon, owner, opponent);
    }

    /// <summary>
    /// Obsługuje cały przebieg nokautu: punkty dla przeciwnika, usunięcie z planszy,
    /// prośba o nowego Active tylko po KO aktywnego Pokemona i sprawdzenie warunku zwycięstwa.
    /// </summary>
    private void HandleKnockout(Pokemon faintedPokemon, PlayerController owner, PlayerController opponent)
    {
        faintedPokemon.currentHp = 0;
        bool wasActive = owner.activePokemon != null && owner.activePokemon.pokemonLogic == faintedPokemon;

        Debug.Log($"[BattleManager] {faintedPokemon.pokemonData.cardName} (P{owner.playerId}) knocked out!");

        // Czyścimy statusy — pokonany Pokemon nie "zaraża" stosu odrzutków
        faintedPokemon.isPoisoned = false;
        faintedPokemon.isBurned = false;
        faintedPokemon.otherSpecialCondition = EnumSpecialConditionType.None;
        faintedPokemon.rootPersistsThroughNextOwnerTurn = false;
        faintedPokemon.paralysisPersistsThroughNextOwnerTurn = false;
        OnPokemonStatusChanged?.Invoke(faintedPokemon);

        // Przeciwnik zdobywa punkt (każde KO = 1 punkt, brak EX w tej grze)
        opponent.score++;
        Debug.Log($"[BattleManager] Player {opponent.playerId} scores! ({opponent.score}/{GameRulesConfig.Instance.pointsToWin})");

        OnPokemonKnockedOut?.Invoke(faintedPokemon, owner);

        RemoveKnockedOutPokemon(faintedPokemon, owner);

        // Czy to był ostatni punkt potrzebny do wygranej? Podczas rozliczania
        // całego ataku werdykt jest odroczony, aby uwzględnić self-damage/counterattack.
        if (CheckWinCondition(opponent))
            return;

        if (wasActive)
        {
            // Właściciel musi wystawić nowego Pokemona z ławki (jeśli ma)
            StartCoroutine(RequestNewActiveAfterKnockoutDelay(owner));
        }
    }

    private IEnumerator RequestNewActiveAfterKnockoutDelay(PlayerController owner)
    {
        float delay = GameRulesConfig.Instance != null ? GameRulesConfig.Instance.knockoutPromotionDelay : 0.8f;
        if (delay > 0f)
            yield return new WaitForSeconds(delay);

        if (isGameOver || owner == null)
            yield break;

        RequestNewActive(owner);
    }

    /// <summary>
    /// Przenosi pokonanego Pokemona z pola Active albo ławki na stos odrzutków.
    /// </summary>
    private void RemoveKnockedOutPokemon(Pokemon faintedPokemon, PlayerController owner)
    {
        if (owner.activePokemon != null && owner.activePokemon.pokemonLogic == faintedPokemon)
        {
            CardInstance faintedCard = owner.activePokemon;
            owner.discardPile.Add(faintedCard);
            owner.activePokemon = null;
            faintedCard.benchSlotIndex = CardInstance.NotOnBench;
            Debug.Log($"[BattleManager] {faintedPokemon.pokemonData.cardName} sent to discard pile.");
            return;
        }

        int benchIndex = owner.benchPokemons.FindIndex(card => card?.pokemonLogic == faintedPokemon);
        if (benchIndex >= 0)
        {
            CardInstance faintedCard = owner.benchPokemons[benchIndex];
            owner.discardPile.Add(faintedCard);
            owner.benchPokemons.RemoveAt(benchIndex);
            faintedCard.benchSlotIndex = CardInstance.NotOnBench;
            ReindexBenchSlotIndices(owner);
            Debug.Log($"[BattleManager] {faintedPokemon.pokemonData.cardName} sent from bench to discard pile.");
            return;
        }

        Debug.LogWarning($"[BattleManager] Knocked out Pokemon {faintedPokemon.pokemonData.cardName} was not found on P{owner.playerId}'s board.");
    }

    /// <summary>
    /// Prosi gracza o wybranie nowego Active Pokemona z ławki po nokaucie.
    /// Człowiek klika w UI (chooseMode = true), AI wybiera automatycznie.
    /// </summary>
    private void RequestNewActive(PlayerController owner)
    {
        if (owner.benchPokemons.Count == 0)
        {
            Debug.Log($"[BattleManager] Player {owner.playerId} has no Pokemon left — game over.");
            PlayerController winner = GetOpponent(owner);
            if (combatResolutionDepth > 0)
            {
                if (pendingForcedWinner != null && pendingForcedWinner != winner)
                    pendingForcedDraw = true;
                else
                    pendingForcedWinner = winner;
            }
            else
                TriggerGameOver(winner);
            return;
        }

        if (owner.playerType == EnumPlayerType.Human)
        {
            // Człowiek sam wybiera klikając — czekamy na wywołanie PromoteFromBench()
            owner.chooseMode = true;
            Debug.Log($"[BattleManager] Player {owner.playerId}: choose a new Active Pokemon from the bench.");
            // TODO: Podświetl ławkę w BoardVisualizer żeby gracz wiedział, że musi wybrać
            return;
        }

        // Ollama LLM: zapytaj model, którego Pokemona promować (gemini zostaje na heurystyce, bo płatne).
        if (owner.brain is LLMBrain llmBrain &&
            GameRulesConfig.Instance != null &&
            ((owner.playerId == 1 && GameRulesConfig.Instance.player1LlmProvider == EnumLlmProvider.Ollama) ||
             (owner.playerId == 2 && GameRulesConfig.Instance.player2LlmProvider == EnumLlmProvider.Ollama)))
        {
            owner.chooseMode = true;
            var benchSnapshot = new List<CardInstance>(owner.benchPokemons);
            StartCoroutine(AwaitLlmNewActiveChoice(owner, llmBrain, benchSnapshot));
            return;
        }

        // AI heuristic fallback (Algorithm, Gemini, ML): pick best from bench.
        CardInstance fallback = ChooseBestPromotion(owner);
        PromoteFromBench(owner, fallback);
        Debug.Log($"[BattleManager] AI (P{owner.playerId}) promoted {fallback.pokemonLogic.pokemonData.cardName} to Active.");
    }

    private IEnumerator AwaitLlmNewActiveChoice(PlayerController owner, LLMBrain llmBrain, List<CardInstance> benchOptions)
    {
        CardInstance chosen = null;
        yield return llmBrain.ChooseNewActiveAfterKO(benchOptions, c => chosen = c);

        if (isGameOver) yield break;

        // Validate the LLM's pick is still on the bench (could have been removed by a race condition).
        if (chosen == null || !owner.benchPokemons.Contains(chosen))
        {
            Debug.LogWarning($"[BattleManager] LLM new-active choice invalid — falling back to heuristic.");
            chosen = ChooseBestPromotion(owner);
        }

        owner.chooseMode = false;
        PromoteFromBench(owner, chosen);
        Debug.Log($"[BattleManager] LLM (P{owner.playerId}) promoted {chosen.pokemonLogic.pokemonData.cardName} to Active.");
    }

    /// <summary>
    /// Wybiera najlepszego kandydata do promocji z ławki AI.
    /// Ocenia zarówno ataki gotowe teraz, jak i ataki uruchamiane przez energię,
    /// którą właściciel dostanie do podpięcia w swojej najbliższej turze.
    /// </summary>
    private CardInstance ChooseBestPromotion(PlayerController owner)
    {
        var bench = owner.benchPokemons;
        var candidates = bench
            .Where(card => !HasFinalEvolutionWaitingInHand(owner, card))
            .ToList();
        if (candidates.Count == 0)
            candidates = bench;

        PlayerController opponent = GetOpponent(owner);
        EnumPokemonType projectedEnergy = GetPromotionTurnEnergy(owner);

        CardInstance chosen = candidates
            .OrderByDescending(card => GetBestPromotionAttackValue(card, opponent, projectedEnergy))
            .ThenByDescending(card => CanAttackAfterProjectedAttach(card, projectedEnergy))
            .ThenByDescending(card => CanAttackNow(card))
            .ThenByDescending(card => card.pokemonLogic?.energyEquipped?.Values.Sum() ?? 0)
            .ThenByDescending(card => card.pokemonLogic?.currentHp ?? 0)
            .First();

        Debug.Log(
            $"[BattleManager] Promotion choice P{owner.playerId}: {chosen.baseData.cardName}; " +
            $"projected energy={projectedEnergy}, " +
            $"attack value={GetBestPromotionAttackValue(chosen, opponent, projectedEnergy)}, " +
            $"ready now={CanAttackNow(chosen)}, " +
            $"ready after attach={CanAttackAfterProjectedAttach(chosen, projectedEnergy)}.");

        return chosen;
    }

    private EnumPokemonType GetPromotionTurnEnergy(PlayerController owner)
    {
        EnergyZone zone = playerManager?.GetEnergyZoneFor(owner);
        if (zone == null) return EnumPokemonType.None;

        // A KO normally happens during the opponent's turn. AdvanceEnergy runs at the
        // beginning of the promoted Pokemon owner's turn, making nextEnergy available.
        // If promotion happens during the owner's own turn, currentEnergy is still usable.
        return playerManager.activePlayer == owner
            ? zone.currentEnergy
            : zone.nextEnergy;
    }

    private int GetBestPromotionAttackValue(
        CardInstance card,
        PlayerController opponent,
        EnumPokemonType projectedEnergy)
    {
        if (card?.pokemonLogic == null || card.baseData is not PokemonData pokemonData ||
            pokemonData.attacks == null)
            return 0;

        Dictionary<EnumPokemonType, int> projected =
            new Dictionary<EnumPokemonType, int>(card.pokemonLogic.energyEquipped);
        AddProjectedEnergy(projected, projectedEnergy);

        int benchCount = opponent?.benchPokemons?.Count(bench => bench?.pokemonLogic != null) ?? 0;
        return pokemonData.attacks
            .Where(attack => CanPayAttackCost(
                projected,
                attack,
                card.pokemonLogic.tempBuffsData.attackEnergyCostChange))
            .Select(attack => attack.damage + GetBenchDamageValue(attack, benchCount))
            .DefaultIfEmpty(0)
            .Max();
    }

    private bool CanAttackNow(CardInstance card)
    {
        if (card?.pokemonLogic == null || card.baseData is not PokemonData pokemonData ||
            pokemonData.attacks == null)
            return false;

        return pokemonData.attacks.Any(attack =>
            CardActions.CanAffordAttack(card.pokemonLogic, attack));
    }

    private bool CanAttackAfterProjectedAttach(CardInstance card, EnumPokemonType projectedEnergy)
    {
        if (projectedEnergy == EnumPokemonType.None ||
            card?.pokemonLogic == null ||
            card.baseData is not PokemonData pokemonData ||
            pokemonData.attacks == null)
            return false;

        Dictionary<EnumPokemonType, int> projected =
            new Dictionary<EnumPokemonType, int>(card.pokemonLogic.energyEquipped);
        AddProjectedEnergy(projected, projectedEnergy);

        return pokemonData.attacks.Any(attack => CanPayAttackCost(
            projected,
            attack,
            card.pokemonLogic.tempBuffsData.attackEnergyCostChange));
    }

    private static void AddProjectedEnergy(
        Dictionary<EnumPokemonType, int> energy,
        EnumPokemonType energyType)
    {
        if (energyType == EnumPokemonType.None) return;
        energy.TryGetValue(energyType, out int current);
        energy[energyType] = current + 1;
    }

    private static bool CanPayAttackCost(
        Dictionary<EnumPokemonType, int> available,
        AttackData attack,
        int attackEnergyCostChange)
    {
        if (attack?.attackCost == null) return false;

        Dictionary<EnumPokemonType, int> remaining =
            new Dictionary<EnumPokemonType, int>(available);
        int wildcard = remaining
            .Where(pair => CardActions.IsWildcardEnergy(pair.Key))
            .Sum(pair => pair.Value);

        foreach (EnumPokemonType required in attack.attackCost.Where(type =>
                     type != EnumPokemonType.Colorless))
        {
            remaining.TryGetValue(required, out int count);
            if (count > 0)
            {
                remaining[required] = count - 1;
            }
            else if (wildcard > 0)
            {
                wildcard--;
            }
            else
            {
                return false;
            }
        }

        int colorlessNeeded = attack.attackCost.Count(type =>
            type == EnumPokemonType.Colorless);
        colorlessNeeded = Mathf.Max(0, colorlessNeeded + attackEnergyCostChange);

        int remainingEnergy = remaining
            .Where(pair => !CardActions.IsWildcardEnergy(pair.Key))
            .Sum(pair => pair.Value) + wildcard;
        return remainingEnergy >= colorlessNeeded;
    }

    private static int GetBenchDamageValue(AttackData attack, int enemyBenchCount)
    {
        if (attack?.effects == null || enemyBenchCount <= 0) return 0;

        return attack.effects
            .Where(effect =>
                effect.cardEffectType == EnumCardEffectType.BenchDmg &&
                (effect.cardEffectTarget == EnumCardEffectTarget.EnemyBenchPokemon ||
                 effect.cardEffectTarget == EnumCardEffectTarget.AllOpponents ||
                 effect.cardEffectTarget == EnumCardEffectTarget.All))
            .Sum(effect => Mathf.Max(0, effect.effectAmount) * enemyBenchCount);
    }

    private bool HasFinalEvolutionWaitingInHand(PlayerController owner, CardInstance benchPokemon)
    {
        if (owner == null || benchPokemon?.baseData is not PokemonData benchData) return false;

        return owner.hand.Any(card =>
            card?.baseData is PokemonData handPokemon &&
            handPokemon.stage > benchData.stage &&
            handPokemon.evolvesFrom == benchData.cardName &&
            !HasFurtherEvolutionInOwnerCards(owner, handPokemon));
    }

    private bool HasFurtherEvolutionInOwnerCards(PlayerController owner, PokemonData pokemon)
    {
        return GetOwnerPokemonData(owner).Any(pd => pd.evolvesFrom == pokemon.cardName);
    }

    private IEnumerable<PokemonData> GetOwnerPokemonData(PlayerController owner)
    {
        if (owner.activePokemon?.baseData is PokemonData active)
            yield return active;

        foreach (var card in owner.benchPokemons)
            if (card?.baseData is PokemonData bench)
                yield return bench;

        foreach (var card in owner.hand)
            if (card?.baseData is PokemonData hand)
                yield return hand;

        foreach (var card in owner.deck)
            if (card?.baseData is PokemonData deck)
                yield return deck;

        foreach (var card in owner.discardPile)
            if (card?.baseData is PokemonData discard)
                yield return discard;
    }

    /// <summary>
    /// Przesuwa wybranego Pokemona z ławki na pole Active po nokaucie.
    /// Wywołaj gdy człowiek kliknie Pokemona z ławki w trybie chooseMode.
    /// </summary>
    public void PromoteFromBench(PlayerController owner, CardInstance benchPokemon)
    {
        if (!owner.benchPokemons.Contains(benchPokemon))
        {
            Debug.LogWarning($"[BattleManager] PromoteFromBench: {benchPokemon.baseData.cardName} is not on the bench!");
            return;
        }

        owner.activePokemon = benchPokemon;
        owner.benchPokemons.Remove(benchPokemon);
        benchPokemon.benchSlotIndex = CardInstance.NotOnBench;

        ReindexBenchSlotIndices(owner);

        Debug.Log($"[BattleManager] {benchPokemon.pokemonLogic.pokemonData.cardName} promoted to Active (P{owner.playerId}).");
        owner.chooseMode = false;
        OnPokemonPromoted?.Invoke(benchPokemon, owner);
    }

    #endregion

    // =====================================================================
    #region Warunek zwycięstwa (Win Condition)
    // =====================================================================

    /// <summary>
    /// Sprawdza, czy gracz zdobył wystarczającą liczbę punktów.
    /// Zwraca true jeśli gra się skończyła.
    /// </summary>
    private bool CheckWinCondition(PlayerController potentialWinner)
    {
        if (potentialWinner.score >= GameRulesConfig.Instance.pointsToWin)
        {
            if (combatResolutionDepth > 0)
                return false;

            TriggerGameOver(potentialWinner);
            return true;
        }
        return false;
    }

    private void ResolvePendingGameOver()
    {
        if (isGameOver || playerManager == null) return;

        PlayerController p1 = playerManager.player1;
        PlayerController p2 = playerManager.player2;
        int pointsToWin = GameRulesConfig.Instance.pointsToWin;
        bool p1ReachedTarget = p1 != null && p1.score >= pointsToWin;
        bool p2ReachedTarget = p2 != null && p2.score >= pointsToWin;

        if (p1ReachedTarget && p2ReachedTarget)
        {
            TriggerGameOver(null);
            return;
        }

        if (p1ReachedTarget)
        {
            TriggerGameOver(p1);
            return;
        }

        if (p2ReachedTarget)
        {
            TriggerGameOver(p2);
            return;
        }

        if (pendingForcedDraw)
            TriggerGameOver(null);
        else if (pendingForcedWinner != null)
            TriggerGameOver(pendingForcedWinner);

        pendingForcedWinner = null;
        pendingForcedDraw = false;
    }

    private void TriggerGameOver(PlayerController winner)
    {
        // Guard re-entry: a single resolution can KO multiple Pokemon and call
        // CheckWinCondition more than once, which would fire OnGameOver several
        // times for one match (duplicate records in subscribers like MatchupStatsLogger).
        if (isGameOver) return;
        isGameOver = true;
        pendingForcedWinner = null;
        pendingForcedDraw = false;

        if (winner == null)
            Debug.Log($"[BattleManager] GAME OVER! Draw ({playerManager.player1.score}-{playerManager.player2.score}).");
        else
            Debug.Log($"[BattleManager] GAME OVER! Winner: {winner.playerName} ({winner.score}/{GameRulesConfig.Instance.pointsToWin} points).");

        if (winnerText != null)
            winnerText.text = winner == null ? "Draw!" : $"{winner.playerName} wins!";

        if (gameOverPanel != null)
            gameOverPanel.SetActive(true);

        OnGameOver?.Invoke(winner);
    }

    public void TriggerTurnLimitGameOver()
    {
        if (isGameOver) return;
        isGameOver = true;

        PlayerController p1 = playerManager.player1;
        PlayerController p2 = playerManager.player2;

        string resultText;
        PlayerController winner = null;

        if (p1.score > p2.score)
        {
            winner = p1;
            resultText = $"{p1.playerName} wins on points! ({p1.score} vs {p2.score} KO)";
        }
        else if (p2.score > p1.score)
        {
            winner = p2;
            resultText = $"{p2.playerName} wins on points! ({p2.score} vs {p1.score} KO)";
        }
        else
        {
            resultText = $"Draw! ({p1.score} - {p2.score} KO after {GameRulesConfig.Instance.maxTurns} turns)";
        }

        Debug.Log($"[BattleManager] Turn limit reached! {resultText}");

        if (winnerText != null) winnerText.text = resultText;
        if (gameOverPanel != null) gameOverPanel.SetActive(true);

        OnGameOver?.Invoke(winner);
    }

    #endregion

    // =====================================================================
    #region Reset buffów (Buff Reset)
    // =====================================================================

    /// <summary>
    /// Resetuje tymczasowe buffy i flagi akcji gracza na początku jego tury.
    /// Persistentne statusy (isPoisoned, otherSpecialCondition) NIE są dotykane.
    /// </summary>
    private void ResetAllBuffsForPlayer(PlayerController player)
    {
        // Modyfikatory gracza (np. doMoreDamageToActive, canAddEnergy)
        player.ResetPlayerModifiers();

        // Flagi per-tura
        player.usedSupporterThisTurn = false;
        player.doneTurn = false;
        player.hasAttackedThisTurn = false;

        // Tymczasowe buffy aktywnego Pokemona
        if (player.activePokemon != null)
            ResetPokemonBuffsForTurnStart(player.activePokemon.pokemonLogic);

        // Tymczasowe buffy Pokemonów na ławce
        foreach (CardInstance benchCard in player.benchPokemons)
        {
            if (benchCard?.pokemonLogic != null)
                ResetPokemonBuffsForTurnStart(benchCard.pokemonLogic);
        }

        // Pierwsza tura gracza: ewolucja zablokowana dla wszystkich Pokemonów na planszy (zasada setupu)
        if (!player.hasHadFirstTurn)
        {
            player.canEvolve = false;
            if (player.activePokemon != null)
                player.activePokemon.pokemonLogic.tempBuffsData.canEvolve = false;
            foreach (CardInstance benchCard in player.benchPokemons)
                if (benchCard?.pokemonLogic != null)
                    benchCard.pokemonLogic.tempBuffsData.canEvolve = false;
            player.hasHadFirstTurn = true;
            Debug.Log($"[BattleManager] Player {player.playerId}: pierwsza tura — ewolucje zablokowane.");
        }

        Debug.Log($"[BattleManager] All buffs and turn flags reset for Player {player.playerId}.");
    }

    #endregion

    // =====================================================================
    #region Helpery (Helpers)
    // =====================================================================

    private void ResetPokemonBuffsForTurnStart(Pokemon pokemon)
    {
        bool preserveRoot = pokemon.tempBuffsData.rooted && pokemon.rootPersistsThroughNextOwnerTurn;
        bool preserveSlow = pokemon.slowPersistsThroughNextOwnerTurn;
        int slowCost = preserveSlow ? pokemon.tempBuffsData.attackEnergyCostChange : 0;
        int exposeDamageIncrease = pokemon.tempBuffsData.takeMoreDamageFromAttacksDebuff;
        int poisonDamageIncrease = pokemon.isPoisoned
            ? pokemon.tempBuffsData.takeMoreDamageFromPoison
            : 0;
        int burnDamageIncrease = pokemon.isBurned
            ? pokemon.tempBuffsData.takeMoreDamageFromBurn
            : 0;

        pokemon.ResetBuffs();

        pokemon.tempBuffsData.SetAttackDamageTakenDebuff(exposeDamageIncrease);
        pokemon.tempBuffsData.takeMoreDamageFromPoison = poisonDamageIncrease;
        pokemon.tempBuffsData.takeMoreDamageFromBurn = burnDamageIncrease;

        if (preserveRoot)
        {
            pokemon.tempBuffsData.rooted = true;
            pokemon.tempBuffsData.canRetreat = false;
            pokemon.rootPersistsThroughNextOwnerTurn = false;
        }

        if (preserveSlow)
        {
            // Slowed stays until the Pokemon retreats; keep the extra attack cost across this reset.
            pokemon.tempBuffsData.attackEnergyCostChange = slowCost;
        }

        OnPokemonStatusChanged?.Invoke(pokemon);
    }

    private void ApplyActiveSpecialConditionRestrictions(PlayerController player)
    {
        Pokemon active = player?.activePokemon?.pokemonLogic;
        if (active == null) return;

        if (active.otherSpecialCondition == EnumSpecialConditionType.Paralyzed)
        {
            active.tempBuffsData.canAttack = false;
            active.tempBuffsData.canRetreat = false;
            active.paralysisPersistsThroughNextOwnerTurn = false;
            Debug.Log($"[BattleManager] {active.pokemonData.cardName} is Paralyzed. Cannot attack or retreat this turn.");
        }
        else if (active.otherSpecialCondition == EnumSpecialConditionType.Asleep)
        {
            active.tempBuffsData.canAttack = false;
            active.tempBuffsData.canRetreat = false;
        }
    }

    /// <summary> Zwraca przeciwnika danego gracza. </summary>
    public PlayerController GetOpponent(PlayerController player)
    {
        if (player == playerManager.player1) return playerManager.player2;
        if (player == playerManager.player2) return playerManager.player1;

        Debug.LogError("[BattleManager] GetOpponent: unknown player!");
        return null;
    }

    public void NotifyPokemonStatusChanged(Pokemon pokemon)
    {
        if (pokemon == null) return;
        OnPokemonStatusChanged?.Invoke(pokemon);
    }

    /// <summary> Zwraca właściciela danej karty (szuka w Active i Bench obu graczy). </summary>
    public PlayerController GetCardOwner(CardInstance card)
    {
        if (playerManager.player1.IsCardOwnerInLogic(card)) return playerManager.player1;
        if (playerManager.player2.IsCardOwnerInLogic(card)) return playerManager.player2;
        return null;
    }

    /// <summary> Utrzymuje benchSlotIndex == indeks na liście benchPokemons. </summary>
    private void ReindexBenchSlotIndices(PlayerController owner)
    {
        for (int i = 0; i < owner.benchPokemons.Count; i++)
            owner.benchPokemons[i].benchSlotIndex = i;
    }

    #endregion
}
