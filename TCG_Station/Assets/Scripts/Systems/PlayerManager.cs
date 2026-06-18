using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Random = UnityEngine.Random;

public class PlayerManager : MonoBehaviour
{
    public static event System.Action<CardInstance, string, int> OnPokemonPlayedToBoard;
    public static event System.Action<CardInstance, int> OnTrainerPlayed;
    public static event System.Action<CardInstance, CardInstance, PlayerController> OnPokemonEvolved;
    public static event Action<PlayerController, CardInstance, CardInstance> OnPokemonRetreated;
    public static event Action<CardInstance> OnPokemonEnergyChanged;
    public static event Action<CardInstance, EnumPokemonType> OnEnergyAttached;
    public static event System.Action<CardInstance, int> OnCardDiscardedFromHand;

    public static void NotifyEnergyChanged(CardInstance card) => OnPokemonEnergyChanged?.Invoke(card);

    public static void NotifyCardDiscardedFromHand(CardInstance card, int playerId) =>
        OnCardDiscardedFromHand?.Invoke(card, playerId);

    private static string P(PlayerController owner) =>
        owner != null ? $"[Player {owner.playerId} — {owner.playerName}] " : "";

    private static bool IsHeadlessRuntime()
    {
        return Application.isBatchMode || (GameRulesConfig.Instance != null && GameRulesConfig.Instance.IsHeadlessMode);
    }

    #region Singleton
    public static PlayerManager Instance { get; private set; }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        // NOTE: intentionally NOT DontDestroyOnLoad. Persisting this singleton across a
        // scene reload would keep stale references to destroyed scene objects (p1EnergyZone,
        // retreatButton, playersParent, ...), which are used every turn. Rebuilding it with
        // the fresh scene keeps those references valid for BenchmarkRunner's match reloads.
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }
    #endregion

    public GameManager gameManager;
    public BattleManager battleManager;
    public CardActions cardActions;
    public BoardVisualizer boardVisualizer;
    public TurnManager turnManager;

    [Header("Player Controllers")]
    public PlayerController player1; // hostPlayer/player
    public PlayerController player2; // clientPlayer/AI
    public PlayerController activePlayer;

    [Header("Player Prefab")]
    public Transform playersParent;
    public GameObject playerPrefab;

    [Header("Energy Setup")]
    public EnergyZone p1EnergyZone;
    public EnergyZone p2EnergyZone;

    [Header("Retreat Setup")]
    public Retreat retreatButton;

    [Header("Selection Mode")]
    public CardInstance selectedCard;
    public bool selectionModeActive = false;
    public EnumSelectionAction SelectionAction = EnumSelectionAction.None;

    CardInstance pendingEvolutionFromHandCard;
    PlayerController pendingEvolutionOwner;
    PlayerController pendingSelectionOwner;
    Action<CardInstance> pendingSelectionCallback;
    bool p1EnergyClickSubscribed;
    bool retreatClickSubscribed;


    private void OnEnable()
    {
        ResolveSceneReferences();
        if (IsHeadlessRuntime())
            return;

        CardInputEvents.CardClicked -= HandleCardClicked;
        CardInputEvents.CardClicked += HandleCardClicked;
        SubscribeSceneEvents();
    }

    private void OnDisable()
    {
        // Odpinanie eventów — bez tego przy restarcie sceny zrobiłyby się podwójne subskrypcje
        CardInputEvents.CardClicked -= HandleCardClicked;
        if (p1EnergyZone != null && p1EnergyClickSubscribed)
            p1EnergyZone.OnEnergyClicked -= PrepareToGiveEnergy;
        if (retreatButton != null && retreatClickSubscribed)
            retreatButton.OnRetreatClicked -= PrepareToRetreat;
        p1EnergyClickSubscribed = false;
        retreatClickSubscribed = false;
    }

    private void ResolveSceneReferences()
    {
        bool headless = IsHeadlessRuntime();

        if (!headless && boardVisualizer == null)
            boardVisualizer = FindFirstObjectByType<BoardVisualizer>();

        if (!headless && boardVisualizer != null)
        {
            if (p1EnergyZone == null)
                p1EnergyZone = boardVisualizer.p1EnergyZoneScript;
            if (p2EnergyZone == null)
                p2EnergyZone = boardVisualizer.p2EnergyZoneScript;
        }

        if (headless)
            EnsureHeadlessEnergyZones();
        else if (retreatButton == null)
            retreatButton = FindFirstObjectByType<Retreat>();
    }

    private void SubscribeSceneEvents()
    {
        if (IsHeadlessRuntime())
            return;

        if (p1EnergyZone != null && !p1EnergyClickSubscribed)
        {
            p1EnergyZone.OnEnergyClicked += PrepareToGiveEnergy;
            p1EnergyClickSubscribed = true;
        }
        else if (p1EnergyZone == null)
        {
            Debug.LogWarning("[PlayerManager] p1EnergyZone is not assigned; manual energy clicks are disabled until the reference is restored.");
        }

        if (retreatButton != null && !retreatClickSubscribed)
        {
            retreatButton.OnRetreatClicked += PrepareToRetreat;
            retreatClickSubscribed = true;
        }
        else if (retreatButton == null)
        {
            Debug.LogWarning("[PlayerManager] retreatButton is not assigned; manual retreat button is disabled until the reference is restored.");
        }
    }

    private void DisableSceneInputForHeadless()
    {
        CardInputEvents.CardClicked -= HandleCardClicked;
        if (p1EnergyZone != null && p1EnergyClickSubscribed)
            p1EnergyZone.OnEnergyClicked -= PrepareToGiveEnergy;
        if (retreatButton != null && retreatClickSubscribed)
            retreatButton.OnRetreatClicked -= PrepareToRetreat;
        p1EnergyClickSubscribed = false;
        retreatClickSubscribed = false;
    }


    public void StartPlayerManager(string player1Name, string player2Name) // Wywoływane przez GameManager po załadowaniu sceny
    {
        if (gameManager == null)
            gameManager = GameManager.Instance;
        cardActions = CardActions.Instance;

        if (cardActions == null)
        {
            Debug.LogError("[PlayerManager] Nie znaleziono obiektu CardActions na scenie! Dodaj go do hierarchii.");
            return;
        }

        if (IsHeadlessRuntime())
            DisableSceneInputForHeadless();

        FindPlayersParent();
        InstantiatePlayers(player1Name, player2Name, GameRulesConfig.Instance.player1Type, GameRulesConfig.Instance.player2Type);
        SetupPlayer(player1); // Creates deck, draws starting hand, and visualizes it on the board
        SetupPlayer(player2);
        if (!IsHeadlessRuntime())
            boardVisualizer = FindFirstObjectByType<BoardVisualizer>();
        turnManager = FindAnyObjectByType<TurnManager>();
        ResolveSceneReferences();
        SubscribeSceneEvents();
    }



    public void TurnOnSelectionMode()
    {
        selectionModeActive = true;
        Debug.Log($"[PlayerManager] Selection mode turned ON.");
    }



    private void HandleCardClicked(CardInstance clickedCard)
    {
        if (clickedCard == null) return;

        // CHOOSE MODE — gracz musi wybrać nowego Active po nokaucie (klikanie ławki)
        if (player1 != null && player1.chooseMode)
        {
            if (player1.benchPokemons.Contains(clickedCard))
                battleManager.PromoteFromBench(player1, clickedCard);
            else
                Debug.Log("[PlayerManager] W trybie choose kliknij Pokemona ze swojej ławki.");
            return;
        }

        // SELECTION MODE — gracz wskazuje cel dla konkretnej akcji (energia, ewolucja, retreat…)
        if (selectionModeActive)
        {
            selectedCard = clickedCard;
            Debug.Log($"[PlayerManager] Wybrano kartę: {clickedCard.baseData.cardName}");

            if (SelectionAction == EnumSelectionAction.AttachEnergy)
                HandleAttachEnergySelection(clickedCard);
            else if (SelectionAction == EnumSelectionAction.EnergyRamp)
                HandleEnergyRampSelection(clickedCard);
            else if (SelectionAction == EnumSelectionAction.Retreat)
                HandleRetreatSelection(clickedCard);
            else if (SelectionAction == EnumSelectionAction.EvolveOntoTarget)
                HandleEvolveSelection(clickedCard);
            else if (SelectionAction == EnumSelectionAction.SwapSelf)
                HandleSwapSelfSelection(clickedCard);
            else
                Debug.LogWarning($"[PlayerManager] Nieznana akcja wyboru: {SelectionAction}");

            return;
        }
        // PLAY MODE — normalne kliknięcie karty z ręki, powinna od razu zadziałać

        PlayerController handOwner = GetHandOwner(clickedCard);

        if (turnManager != null && turnManager.activePlayerId == 0)
        {
            HandleSetupCardClicked(clickedCard, handOwner);
            return;
        }

        if (activePlayer == null || activePlayer.playerType != EnumPlayerType.Human)
        {
            Debug.Log("Kliknięto kartę, ale aktywny gracz nie jest człowiekiem. Ignoruję kliknięcie.");
            return;
        }

        if (handOwner != activePlayer)
        {
            LogIgnoredNonHandClick(clickedCard, handOwner);
            return;
        }

        PlayCardFromHand(clickedCard);
    }

    private void HandleSetupCardClicked(CardInstance clickedCard, PlayerController handOwner)
    {
        if (handOwner == null)
        {
            LogIgnoredNonHandClick(clickedCard, null);
            return;
        }

        if (handOwner.playerType != EnumPlayerType.Human)
        {
            Debug.Log("[PlayerManager] Setup: kliknięta karta nie należy do ludzkiego gracza.");
            return;
        }

        if (clickedCard.baseData.cardType != EnumCardType.Pokemon)
        {
            Debug.Log("[PlayerManager] W fazie setupu można wystawić tylko Basic Pokemona.");
            return;
        }

        TryPlayPokemon(clickedCard);
    }

    private void PlayCardFromHand(CardInstance clickedCard)
    {
        if (clickedCard.baseData.cardType == EnumCardType.Pokemon)
        {
            TryPlayPokemon(clickedCard);
        }
        else
        {
            TryPlayTrainer(clickedCard);
        }
    }

    private void LogIgnoredNonHandClick(CardInstance clickedCard, PlayerController handOwner)
    {
        PlayerController boardOwner = GetBoardOwner(clickedCard);
        if (boardOwner != null)
        {
            Debug.Log("[PlayerManager] Kliknięto Pokemona na planszy poza trybem wyboru — brak akcji.");
            return;
        }

        if (handOwner != null)
        {
            Debug.Log("[PlayerManager] Kliknięta karta nie jest kartą z ręki aktywnego gracza.");
            return;
        }

        Debug.LogWarning($"[PlayerManager] Kliknięta karta '{clickedCard.baseData.cardName}' nie znajduje się w ręce ani na planszy żadnego gracza.");
    }

    private PlayerController GetHandOwner(CardInstance card)
    {
        if (card == null) return null;
        if (player1?.hand != null && player1.hand.Contains(card)) return player1;
        if (player2?.hand != null && player2.hand.Contains(card)) return player2;
        return null;
    }

    private PlayerController GetBoardOwner(CardInstance card)
    {
        if (card == null) return null;
        if (IsOnPlayersBoard(player1, card)) return player1;
        if (IsOnPlayersBoard(player2, card)) return player2;
        return null;
    }

    private bool IsOnPlayersBoard(PlayerController owner, CardInstance card)
    {
        if (owner == null || card == null) return false;
        return owner.activePokemon == card || owner.benchPokemons.Contains(card);
    }

    private IEnumerable<CardInstance> GetOwnedBoardPokemon(PlayerController owner)
    {
        if (owner == null) yield break;

        if (owner.activePokemon != null)
            yield return owner.activePokemon;

        foreach (CardInstance benchCard in owner.benchPokemons)
            if (benchCard != null)
                yield return benchCard;
    }

    private void HandleAttachEnergySelection(CardInstance card)
    {
        EnergyZone zone = GetEnergyZoneFor(activePlayer);
        if (zone != null && GiveEnergyToPokemon(zone, card))
            TurnOffSelectionMode();
    }

    private void HandleEnergyRampSelection(CardInstance card)
    {
        if (pendingSelectionOwner == null || !pendingSelectionOwner.benchPokemons.Contains(card))
        {
            Debug.LogWarning("[EnergyRamp] Wybierz Pokemona z własnej ławki.");
            return;
        }

        pendingSelectionCallback?.Invoke(card);
        TurnOffSelectionMode();
    }

    private void HandleRetreatSelection(CardInstance card)
    {
        if (Retreat(activePlayer, card))
            TurnOffSelectionMode();
    }

    // Nie woła TurnOffSelectionMode tutaj — TryCompleteEvolutionFromSelection robi to sam po zakończeniu
    private void HandleEvolveSelection(CardInstance card)
    {
        TryCompleteEvolutionFromSelection(card);
    }

    private void HandleSwapSelfSelection(CardInstance card)
    {
        if (FreeSwapActive(activePlayer, card))
            TurnOffSelectionMode();
    }

    

    public void SetSelectionAction(EnumSelectionAction action)
    {
        SelectionAction = action;
    }


    public void FindPlayersParent()
    {
        GameObject existing = GameObject.Find("PlayersParent");
        if (existing != null)
        {
            playersParent = existing.transform;
            return;
        }

        if (IsHeadlessRuntime())
        {
            playersParent = new GameObject("PlayersParent").transform;
            return;
        }

        Debug.LogError("[PlayerManager] PlayersParent not found in scene.");
    }

    public void InstantiatePlayers(string player1Name, string player2Name, EnumPlayerType player1Type, EnumPlayerType player2Type)
    {
        // --- GRACZ 1 ---
        GameObject p1Object = Instantiate(playerPrefab, playersParent);
        player1 = p1Object.GetComponent<PlayerController>();
        player1.name = "Player1_Controller";
        player1.playerId = 1;
        player1.playerName = player1Name;
        player1.playerType = player1Type;

        // Mózg doczepiony dynamicznie jako komponent — typ zależy od ustawień w Inspectorze
        EnumAlgorithmProfile p1Profile = GameRulesConfig.Instance?.player1AlgorithmProfile ?? EnumAlgorithmProfile.Standard;
        EnumAlgorithmProfile p2Profile = GameRulesConfig.Instance?.player2AlgorithmProfile ?? EnumAlgorithmProfile.Standard;
        player1.brain = AttachBrainToPlayer(p1Object, player1Type, p1Profile);
        player1.brain.Initialize(player1);

        // --- GRACZ 2 ---
        GameObject p2Object = Instantiate(playerPrefab, playersParent);
        player2 = p2Object.GetComponent<PlayerController>();
        player2.name = "Player2_Controller";
        player2.playerId = 2;
        player2.playerName = player2Name;
        player2.playerType = player2Type;

        player2.brain = AttachBrainToPlayer(p2Object, player2Type, p2Profile);
        player2.brain.Initialize(player2);
    }

    // Doczepia odpowiedni skrypt mózgu jako komponent — rodzaj zależy od EnumPlayerType.
    // algorithmProfile dotyczy wyłącznie AlgorithmBrain (numeryczny profil wag), ignorowany dla innych mózgów.
    private PlayerBrain AttachBrainToPlayer(GameObject playerObject, EnumPlayerType brainType, EnumAlgorithmProfile algorithmProfile)
    {
        switch (brainType)
        {
            case EnumPlayerType.Human:
                return playerObject.AddComponent<HumanBrain>();

            case EnumPlayerType.LLM:
                return playerObject.AddComponent<LLMBrain>();

            case EnumPlayerType.Algorithm:
            {
                var algoBrain = playerObject.AddComponent<AlgorithmBrain>();
                algoBrain.SetProfile(algorithmProfile);
                return algoBrain;
            }

            case EnumPlayerType.ML:
                return playerObject.AddComponent<MLBrain>();

            default:
                Debug.LogWarning($"[PlayerManager] Nieznany typ mózgu ({brainType}), awaryjnie przypisuję AlgorithmBrain.");
                var fallback = playerObject.AddComponent<AlgorithmBrain>();
                fallback.SetProfile(algorithmProfile);
                return fallback;
        }
    }


    public void SetupPlayer(PlayerController player)
    {
        if (player == null) return;

        string deckName = (player == player1) ? GameRulesConfig.Instance.player1DeckName : GameRulesConfig.Instance.player2DeckName;

        player.ResetPlayerModifiers();

        Dictionary<string, DeckData> deckLibrary = gameManager?.jsonLoader?.deckLibrary;
        if (deckLibrary == null || deckLibrary.Count == 0)
        {
            Debug.LogError("[PlayerManager] No decks loaded. Cannot setup player.");
            return;
        }

        if (!deckLibrary.TryGetValue(deckName, out DeckData deckData))
        {
            string fallbackDeckName = deckLibrary.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).First();
            Debug.LogError($"[PlayerManager] Deck not found: '{deckName}'. Falling back to '{fallbackDeckName}'.");
            deckName = fallbackDeckName;
            deckData = deckLibrary[deckName];
        }

        player.deck = PlayerManager.GenerateSpecificDeck(gameManager.jsonLoader.cardLibrary, gameManager.jsonLoader.deckLibrary, deckName);
        if (GameRulesConfig.Instance != null && player.deck.Count != GameRulesConfig.Instance.deckSize)
        {
            Debug.LogWarning($"[PlayerManager] Deck '{deckName}' has {player.deck.Count} cards, but GameRulesConfig.deckSize is {GameRulesConfig.Instance.deckSize}.");
        }

        player.deckEnergies = new List<EnumPokemonType>(deckData.energyTypes);
        BattleResultExporter.Instance?.RegisterPlayerDeck(
            player,
            player == player1 ? "A" : "B",
            deckName,
            deckData);

        Debug.Log($"{player.playerName} energie: {string.Join(", ", player.deckEnergies)}");
        Debug.Log($"[PlayerManager] Generated deck for {player.playerName}");

        List<CardInstance> startingHand = cardActions.DrawCard(player, GameRulesConfig.Instance.starterHandSize, triggerEvent: false);
        BattleResultExporter.Instance?.RegisterDrawnCards(player, startingHand);
        if (!IsHeadlessRuntime())
            gameManager?.boardVisualizer?.VisualizeDrawnCards(player, startingHand);
    }


    public void SwitchActivePlayer()
    {
        if (turnManager.activePlayerId == 1)
        {
            turnManager.activePlayerId = 2;
            activePlayer = player2;
        }
        else
        {
            turnManager.activePlayerId = 1;
            activePlayer = player1;
        }
        Debug.Log($"[PlayerManager] Active player switched to: {activePlayer.playerName}");
    }

    public bool AreActiveSpotsOccupied()
    {
        if (player1.activePokemon != null && player2.activePokemon != null)
        {
            return true;
        }
        return false;
    }

    public bool TryPlayBasicPokemon(CardInstance card, PlayerController owner)
    {
        if (card == null || owner == null)
            return false;

        if (card.pokemonLogic.pokemonData.stage != 0)
        {
            Debug.Log($"{P(owner)}Karta nie jest podstawowym Pokemonem (Basic).");
            return false;
        }

        if (owner.activePokemon == null)
        {
            Debug.Log($"{P(owner)}Pole Active jest puste, zagrywanie Pokemona jako Active.");
            owner.activePokemon = card;
            card.benchSlotIndex = CardInstance.NotOnBench;
            DestroyCardInHand_LOGIC(card, owner);
            card.pokemonLogic.tempBuffsData.canEvolve = false;
            Debug.Log($"{P(owner)}Zagrywasz {card.pokemonLogic.pokemonData.cardName} jako Active Pokemona.");
            return true;
        }
        else
        {
            if (owner.benchPokemons.Count >= GameRulesConfig.Instance.benchSize)
            {
                Debug.Log($"{P(owner)}Ławka jest pełna.");
                return false;
            }

            Debug.Log($"{P(owner)}Pole Active jest zajęte przez {owner.activePokemon.baseData.cardName}, zagrywanie Pokemona na ławkę.");
            owner.benchPokemons.Add(card);
            ReindexBenchSlotIndices(owner);
            DestroyCardInHand_LOGIC(card, owner);
            card.pokemonLogic.tempBuffsData.canEvolve = false;
            Debug.Log($"{P(owner)}Zagrywasz {card.pokemonLogic.pokemonData.cardName} na ławkę.");
            return true;
        }
    }


    public void TryPlayPokemon(CardInstance card)
    {
        if (card?.baseData == null)
            return;

        PlayerController owner = GetHandOwner(card);
        if (owner == null)
        {
            PlayerController boardOwner = GetBoardOwner(card);
            if (boardOwner != null)
                Debug.Log("[PlayerManager] TryPlayPokemon pominięte — ta karta jest już na planszy.");
            else
                Debug.LogWarning($"[PlayerManager] TryPlayPokemon: '{card.baseData.cardName}' nie znajduje się w ręce żadnego gracza.");
            return;
        }

        if (!CanPlayFromHandNow(owner, card))
            return;

        PokemonData pokemonData = card.pokemonLogic?.pokemonData;
        if (pokemonData == null)
        {
            Debug.LogWarning($"[PlayerManager] TryPlayPokemon: '{card.baseData.cardName}' nie ma danych Pokemona.");
            return;
        }

        //BASIC
        if (pokemonData.stage == 0)
        {
            bool success = TryPlayBasicPokemon(card, owner);

            if (!success)
                return; // nic się nie dzieje, UI nie reaguje

            Debug.Log($"{P(owner)}Jest to Basic Pokemon.");
            OnPokemonPlayedToBoard?.Invoke(
                card,
                owner.activePokemon == card ? "Active" : "Bench",
                owner.playerId);
        }
        //EVOLVE
        else
        {
            if (!CanAttemptEvolutionFromHand(owner, pokemonData))
                return;

            List<CardInstance> validTargets = GetEvolvableTargets(card, owner);

            if (validTargets.Count == 0)
            {
                LogNoEvolutionTargets(owner, pokemonData);
                return;
            }

            // Człowiek musi kliknąć cel na planszy — włączamy tryb wyboru i czekamy
            if (owner.playerType == EnumPlayerType.Human)
            {
                pendingEvolutionFromHandCard = card;
                pendingEvolutionOwner = owner;
                TurnOnSelectionMode();
                SetSelectionAction(EnumSelectionAction.EvolveOntoTarget);
                Debug.Log("[SelectionMode] Wybierz na planszy Pokemona (Active lub Bench), na którego ewoluujesz tą kartą.");
                return;
            }

            // AI / LLM: bierze pierwszy legalny cel bez czekania na kliknięcie
            CardInstance targetToEvolve = validTargets[0];
            Debug.Log($"{P(owner)}Evolving {targetToEvolve.baseData.cardName} into {card.baseData.cardName}!");
            ExecuteEvolutionPlay(card, targetToEvolve, owner);
        }
    }

    private bool CanPlayFromHandNow(PlayerController owner, CardInstance card)
    {
        if (turnManager == null)
            return true;

        if (turnManager.activePlayerId == 0)
        {
            if (card?.baseData is PokemonData pd && pd.stage > 0)
            {
                Debug.LogWarning($"{P(owner)}W fazie setupu można wystawić tylko Basic Pokemona.");
                return false;
            }

            return true;
        }

        if (activePlayer != owner || turnManager.activePlayerId != owner.playerId)
        {
            Debug.LogWarning($"{P(owner)}To nie jest tura właściciela tej karty.");
            return false;
        }

        return true;
    }

    private bool CanAttemptEvolutionFromHand(PlayerController owner, PokemonData evolutionData)
    {
        if (turnManager != null && turnManager.activePlayerId == 0)
        {
            Debug.LogWarning($"{P(owner)}Nie można ewoluować w fazie setupu.");
            return false;
        }

        if (!owner.canEvolve)
        {
            Debug.LogWarning($"{P(owner)}Ewolucje są zablokowane w tej turze.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(evolutionData.evolvesFrom))
        {
            Debug.LogWarning($"{P(owner)}{evolutionData.cardName} nie ma ustawionego poprzedniego stadium w danych karty.");
            return false;
        }

        return true;
    }

    private void LogNoEvolutionTargets(PlayerController owner, PokemonData evolutionData)
    {
        bool previousStageOnBoard = GetOwnedBoardPokemon(owner).Any(boardCard =>
            boardCard.pokemonLogic?.pokemonData?.cardName == evolutionData.evolvesFrom);

        if (!previousStageOnBoard)
        {
            Debug.LogWarning($"{P(owner)}Brak {evolutionData.evolvesFrom} na planszy — {evolutionData.cardName} nie ma legalnego celu ewolucji.");
            return;
        }

        Debug.LogWarning($"{P(owner)}{evolutionData.evolvesFrom} jest na planszy, ale nie może teraz ewoluować (zagrany lub ewoluowany w tej turze albo efekt blokuje ewolucję).");
    }

    void TryCompleteEvolutionFromSelection(CardInstance clickedBoardCard)
    {
        if (pendingEvolutionFromHandCard == null || pendingEvolutionOwner == null)
        {
            Debug.LogWarning("[PlayerManager] Brak oczekującej karty ewolucji.");
            TurnOffSelectionMode();
            return;
        }

        if (!pendingEvolutionOwner.hand.Contains(pendingEvolutionFromHandCard))
        {
            Debug.LogWarning("[PlayerManager] Karta ewolucji nie jest już w ręce — anulowano.");
            TurnOffSelectionMode();
            return;
        }

        if (pendingEvolutionOwner.hand.Contains(clickedBoardCard))
        {
            Debug.Log("[SelectionMode] To jest karta ewolucji z ręki — wybierz jej poprzednie stadium na planszy.");
            return;
        }

        CardInstance targetInstance = clickedBoardCard;
        bool onBoard = pendingEvolutionOwner.activePokemon == targetInstance
                         || pendingEvolutionOwner.benchPokemons.Contains(targetInstance);

        if (!onBoard || clickedBoardCard.pokemonLogic == null)
        {
            Debug.Log("[SelectionMode] Wybierz swojego Pokemona z planszy (Active lub ławka), który może przyjąć tę ewolucję.");
            return;
        }

        List<CardInstance> validTargets = GetEvolvableTargets(pendingEvolutionFromHandCard, pendingEvolutionOwner);
        if (!validTargets.Contains(targetInstance))
        {
            Debug.Log("[SelectionMode] Ten Pokemon nie może przyjąć tej ewolucji.");
            return;
        }

        Debug.Log($"{P(pendingEvolutionOwner)}Evolving {targetInstance.baseData.cardName} into {pendingEvolutionFromHandCard.baseData.cardName}!");
        ExecuteEvolutionPlay(pendingEvolutionFromHandCard, targetInstance, pendingEvolutionOwner);
        TurnOffSelectionMode();
    }

    public void ExecuteEvolutionPlay(CardInstance evolutionCard, CardInstance targetToEvolve, PlayerController owner)
    {
        bool success = TryPlayEvolution(evolutionCard, targetToEvolve, owner);
        if (success)
            OnPokemonEvolved?.Invoke(evolutionCard, targetToEvolve, owner);
    }


    public bool TryPlayTrainer(CardInstance card)
    {
        PlayerController owner = GetHandOwner(card);
        if (owner == null) return false;

        // Trenerów nie można grać w fazie Setup
        if (turnManager.activePlayerId == 0)
        {
            Debug.Log("Nie można zagrywać kart Trenerów w fazie Setup!");
            return false;
        }

        // I tylko w swojej turze
        if (turnManager.activePlayerId != owner.playerId || activePlayer != owner)
        {
            Debug.Log("To nie jest twoja tura!");
            return false;
        }

        TrainerData trainerData = card.baseData as TrainerData;
        if (trainerData == null) return false;

        if (!CanPlayTrainerType(owner, trainerData))
        {
            Debug.LogWarning($"[PlayerManager] Cannot play Trainer now: {trainerData.cardName} ({trainerData.trainerSubType}).");
            return false;
        }

        Debug.Log($"[PlayerManager] Zagrywam Trenera: {trainerData.cardName} (P{owner.playerId}, subtype: {trainerData.trainerSubType}).");

        if (trainerData.trainerSubType == EnumTrainerSubType.Supporter)
        {
            if (owner.usedSupporterThisTurn)
            {
                Debug.LogWarning("[PlayerManager] Gracz już zagrał Supportera w tej turze.");
                return false;
            }
            owner.usedSupporterThisTurn = true;
            owner.canUseSupporters = false;
        }

        cardActions.ExecuteCardEffects(owner, trainerData.effects);

        // Karta leci z ręki na stos odrzutków
        DestroyCardInHand_LOGIC(card, owner);
        owner.discardPile.Add(card);

        // Powiadamiamy BoardVisualizer, żeby przeniósł grafikę karty
        OnTrainerPlayed?.Invoke(card, owner.playerId);
        return true;
    }

    private bool CanPlayTrainerType(PlayerController owner, TrainerData trainerData)
    {
        switch (trainerData.trainerSubType)
        {
            case EnumTrainerSubType.Item:
                return owner.canUseItems;

            case EnumTrainerSubType.Supporter:
                return owner.canUseSupporters && !owner.usedSupporterThisTurn;

            case EnumTrainerSubType.Tool:
                return owner.canUseTools;

            case EnumTrainerSubType.Stadium:
                return owner.canUseStadiums;

            default:
                return false;
        }
    }


    public List<CardInstance> GetEvolvableTargets(CardInstance card, PlayerController player)
    {
        List<CardInstance> evolvablePokemonList = new List<CardInstance>();
        if (card?.pokemonLogic?.pokemonData == null || player == null)
            return evolvablePokemonList;

        foreach (CardInstance cardInstance in player.benchPokemons)
        {
            if (cardInstance?.pokemonLogic?.pokemonData != null
                && card.pokemonLogic.pokemonData.evolvesFrom == cardInstance.pokemonLogic.pokemonData.cardName
                && cardInstance.pokemonLogic.tempBuffsData.canEvolve)
            {
                evolvablePokemonList.Add(cardInstance);
            }
        }
        if (player.activePokemon?.pokemonLogic?.pokemonData != null
            && card.pokemonLogic.pokemonData.evolvesFrom == player.activePokemon.pokemonLogic.pokemonData.cardName
            && player.activePokemon.pokemonLogic.tempBuffsData.canEvolve)
        {
            evolvablePokemonList.Add(player.activePokemon);
        }

        return evolvablePokemonList;
    }

    public bool TryPlayEvolution(CardInstance evolutionCard, CardInstance targetToEvolve, PlayerController owner)
    {
        if (evolutionCard == null || targetToEvolve == null || owner == null)
            return false;

        if (!owner.canEvolve)
        {
            Debug.LogWarning($"{P(owner)}Ewolucje są zablokowane w tej turze.");
            return false;
        }

        if (!owner.hand.Contains(evolutionCard))
        {
            Debug.LogWarning($"{P(owner)}Karta ewolucji nie znajduje się już w ręce.");
            return false;
        }

        List<CardInstance> validTargets = GetEvolvableTargets(evolutionCard, owner);
        if (!validTargets.Contains(targetToEvolve))
        {
            Debug.LogWarning($"{P(owner)}Nielegalny cel ewolucji dla {evolutionCard.baseData.cardName}.");
            return false;
        }

        // Karta ewolucji wychodzi z ręki
        owner.hand.Remove(evolutionCard);

        // Szukamy celu na planszy i podmieniamy referencję (Active lub Bench)
        if (owner.activePokemon == targetToEvolve)
        {
            owner.activePokemon = evolutionCard;
            evolutionCard.benchSlotIndex = CardInstance.NotOnBench;
        }
        else if (owner.benchPokemons.Contains(targetToEvolve))
        {
            int index = owner.benchPokemons.IndexOf(targetToEvolve);
            owner.benchPokemons[index] = evolutionCard;
            evolutionCard.benchSlotIndex = index;
        }
        else
        {
            return false; // Nie znaleziono celu, przerywamy
        }

        // Transfer danych — ewolucja "dziedziczy" obrażenia i energie poprzedniej formy
        int damageTaken = targetToEvolve.pokemonLogic.pokemonData.hp - targetToEvolve.pokemonLogic.currentHp;
        evolutionCard.pokemonLogic.currentHp -= damageTaken;

        evolutionCard.pokemonLogic.energyEquipped = targetToEvolve.pokemonLogic.energyEquipped;

        // Evolution keeps damage and energy only. Statuses and temporary modifiers do not carry over.
        evolutionCard.pokemonLogic.ClearBuffsDebuffsAndStatuses();

        // Nie można ewoluować tej samej karty dwa razy w tej samej turze
        evolutionCard.pokemonLogic.tempBuffsData.canEvolve = false;

        string energyStr = evolutionCard.pokemonLogic.energyEquipped.Count > 0
            ? string.Join(", ", evolutionCard.pokemonLogic.energyEquipped.Select(kv => $"{kv.Value}x{kv.Key}"))
            : "brak";
        Debug.Log($"[PlayerManager] Ewolucja: {targetToEvolve.baseData.cardName} → {evolutionCard.baseData.cardName}. " +
                  $"HP: {evolutionCard.pokemonLogic.currentHp}/{evolutionCard.pokemonLogic.pokemonData.hp} " +
                  $"(przeniesiono {damageTaken} dmg). Energie: {energyStr}.");

        return true;
    }


    public void DestroyCardInHand_LOGIC(CardInstance cardInstance, PlayerController player)
    {
        player.hand.Remove(cardInstance);
    }

    /// <summary> Utrzymuje benchSlotIndex == indeks na liście benchPokemons (gęsta numeracja od 0). </summary>
    private void ReindexBenchSlotIndices(PlayerController owner)
    {
        for (int i = 0; i < owner.benchPokemons.Count; i++)
            owner.benchPokemons[i].benchSlotIndex = i;
    }



    public static List<CardInstance> GenerateTestDeck(Dictionary<string, CardData> cardLibrary, int deckSize)
    {
        List<CardInstance> newDeck = new List<CardInstance>();
        List<string> allKeys = new List<string>(cardLibrary.Keys);

        for (int i = 0; i < deckSize; i++)
        {
                int index = Random.Range(0, allKeys.Count);
            string cardId = allKeys[index];

            CardData originalData = cardLibrary[cardId];

            // Każda karta to nowa instancja — unikalne ID, własny licznik HP
            CardInstance newCard = new CardInstance(originalData);
            newDeck.Add(newCard);
        }

        return newDeck;
    }

    public static List<CardInstance> GenerateSpecificDeck(
    Dictionary<string, CardData> cardLibrary,
    Dictionary<string, DeckData> deckLibrary,
    string deckName)
    {
        List<CardInstance> newDeck = new List<CardInstance>();

        if (!deckLibrary.ContainsKey(deckName))
        {
            Debug.LogError($"Deck not found: {deckName}");
            return newDeck;
        }

        DeckData deckData = deckLibrary[deckName];

        foreach (DeckCardData deckCard in deckData.cards)
        {
            if (!cardLibrary.ContainsKey(deckCard.cardId))
            {
                Debug.LogError($"Card not found: {deckCard.cardId}");
                continue;
            }

            CardData originalData = cardLibrary[deckCard.cardId];

            for (int i = 0; i < deckCard.count; i++)
            {
                CardInstance instance = new CardInstance(originalData);
                newDeck.Add(instance);
            }
        }

        ShuffleDeck(newDeck);
        EnsureStartingPokemon(newDeck);
        return newDeck;
    }


    public static void ShuffleDeck(List<CardInstance> deck)
    {
        for (int i = deck.Count - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);

            // swap
            CardInstance temp = deck[i];
            deck[i] = deck[j];
            deck[j] = temp;
        }
    }


    public static void EnsureStartingPokemon(List<CardInstance> deck)
    {
        for (int i = 0; i < deck.Count; i++)
        {
            CardData data = deck[i].baseData;

            if (data is PokemonData pokemon && pokemon.stage == 0)
            {
                if (i == 0) return; // jest już na pierwszym miejscu — nic do roboty

                // Przenosimy pierwszy napotkany Basic na czoło talii
                CardInstance basic = deck[i];
                deck.RemoveAt(i);
                deck.Insert(0, basic);
                return;
            }
        }

        Debug.LogWarning("No Stage 0 Pokemon found in deck!");
    }


    public void InitializeEnergy()
    {
        ResolveSceneReferences();
        if (p1EnergyZone == null || p2EnergyZone == null)
        {
            Debug.LogError("[PlayerManager] Cannot initialize energy zones: missing p1EnergyZone or p2EnergyZone reference.");
            return;
        }

        p1EnergyZone.InitializeEnergy(player1.deckEnergies);
        p2EnergyZone.InitializeEnergy(player2.deckEnergies);
    }

    private void EnsureHeadlessEnergyZones()
    {
        if (p1EnergyZone == null)
            p1EnergyZone = CreateHeadlessEnergyZone("HeadlessEnergyZone_P1");
        if (p2EnergyZone == null)
            p2EnergyZone = CreateHeadlessEnergyZone("HeadlessEnergyZone_P2");
    }

    private static EnergyZone CreateHeadlessEnergyZone(string objectName)
    {
        GameObject go = new GameObject(objectName);
        go.hideFlags = HideFlags.HideInHierarchy;
        return go.AddComponent<EnergyZone>();
    }

    public EnergyZone GetEnergyZoneFor(PlayerController player)
    {
        if (player == player1) return p1EnergyZone;
        if (player == player2) return p2EnergyZone;
        Debug.LogError($"Invalid player: {player}");
        return null;
    }

    public void UpdateEnergyZones(PlayerController player)
    {
        EnergyZone zone = GetEnergyZoneFor(player);
        if (zone != null) zone.AdvanceEnergy(player.deckEnergies);
    }


    public void PrepareToGiveEnergy(EnergyZone energyZone)
    {
        if (turnManager.turnCounter == 0) return;

        if (selectionModeActive)
        {
            Debug.Log("[Energy] Najpierw dokończ aktualny wybór.");
            return;
        }

        if (activePlayer == null || activePlayer.playerType != EnumPlayerType.Human)
        {
            Debug.Log("[Energy] Aktywny gracz nie jest człowiekiem — kliknięcie energii z UI zignorowane.");
            return;
        }

        if (!activePlayer.canAddEnergy)
        {
            Debug.LogWarning("[Energy] Energia została już podpięta w tej turze.");
            return;
        }

        if (energyZone != GetEnergyZoneFor(activePlayer))
        {
            Debug.LogWarning("[Energy] Kliknięta strefa energii nie należy do aktywnego gracza.");
            return;
        }

        EnumPokemonType energyToGive = energyZone.currentEnergy;

        if (energyToGive == EnumPokemonType.None)
        {
            Debug.LogWarning("No energy to give!");
            return;
        }

        TurnOnSelectionMode();
        SetSelectionAction(EnumSelectionAction.AttachEnergy);
    }

    public bool GiveEnergyToPokemon(EnergyZone energyZone, CardInstance clickedCard)
    {
        if (activePlayer == null)
            return false;

        if (!activePlayer.canAddEnergy)
        {
            Debug.LogWarning("[Energy] Energia została już podpięta w tej turze.");
            return false;
        }

        if (energyZone == null || energyZone != GetEnergyZoneFor(activePlayer))
        {
            Debug.LogWarning("[Energy] Nieprawidłowa strefa energii dla aktywnego gracza.");
            return false;
        }

        if (!IsOnPlayersBoard(activePlayer, clickedCard) || clickedCard.pokemonLogic == null)
        {
            Debug.Log("[SelectionMode] Wybierz swojego Pokemona z planszy (Active lub ławka), któremu chcesz podpiąć energię.");
            return false;
        }

        EnumPokemonType energyToGive = energyZone.currentEnergy;
        if (energyToGive == EnumPokemonType.None)
        {
            Debug.LogWarning("[Energy] Brak energii do podpięcia.");
            return false;
        }

        AddEnergyToPokemon(clickedCard, energyToGive);

        energyZone.ConsumeCurrentEnergy();
        activePlayer.canAddEnergy = false;

        Debug.Log($"{P(activePlayer)}Added {energyToGive} energy to {clickedCard.baseData.cardName}");
        OnEnergyAttached?.Invoke(clickedCard, energyToGive);
        return true;
    }

    public void AddEnergyToPokemon(CardInstance targetCard, EnumPokemonType energyType, int amount = 1)
    {
        if (targetCard?.pokemonLogic == null || amount <= 0 || energyType == EnumPokemonType.None)
        {
            return;
        }

        var targetDictionary = targetCard.pokemonLogic.energyEquipped;

        if (targetDictionary.ContainsKey(energyType))
        {
            targetDictionary[energyType] += amount;
        }
        else
        {
            targetDictionary.Add(energyType, amount);
        }

        OnPokemonEnergyChanged?.Invoke(targetCard);
    }

    public bool PrepareToSelectBenchPokemon(PlayerController owner, EnumSelectionAction action, Action<CardInstance> onSelected)
    {
        if (owner == null || owner.benchPokemons.Count == 0 || onSelected == null)
        {
            return false;
        }

        pendingSelectionOwner = owner;
        pendingSelectionCallback = onSelected;
        TurnOnSelectionMode();
        SetSelectionAction(action);
        return true;
    }


    public void PrepareToRetreat(Retreat retreat)
    {
        if (activePlayer == null || activePlayer.activePokemon == null) return;

        CardInstance active = activePlayer.activePokemon;
        if (activePlayer.usedManualRetreatThisTurn)
        {
            Debug.LogWarning("[Retreat] Manual retreat was already used this turn.");
            return;
        }

        if (!active.pokemonLogic.tempBuffsData.canRetreat)
        {
            Debug.LogWarning($"[Retreat] {active.baseData.cardName} cannot retreat due to an effect.");
            return;
        }

        int cost = Mathf.Max(0, active.pokemonLogic.pokemonData.retreatCost + activePlayer.retreatEnergyCostChange);
        int totalEnergy = active.pokemonLogic.energyEquipped.Values.Sum();

        if (totalEnergy < cost)
        {
            Debug.LogWarning($"[Retreat] Za mało energii! Potrzebujesz {cost}, a masz tylko {totalEnergy}.");
            // Tutaj możesz dodać np. sygnał dźwiękowy błędu lub komunikat dla gracza w UI
            return;
        }

        if (activePlayer.benchPokemons.Count == 0)
        {
            Debug.LogWarning("[Retreat] Brak Pokemonów na ławce!");
            return;
        }

        TurnOnSelectionMode();
        SetSelectionAction(EnumSelectionAction.Retreat);
        Debug.Log("[SelectionMode] Wybierz Pokemona z ławki, który zastąpi obecnego.");
    }


    public bool Retreat(PlayerController owner, CardInstance benchedPokemon)
    {
        if (owner == null)
        {
            Debug.LogWarning("[Retreat] Missing owner.");
            return false;
        }

        if (owner.usedManualRetreatThisTurn)
        {
            Debug.LogWarning("[Retreat] Manual retreat was already used this turn.");
            return false;
        }

        if (benchedPokemon == null || !owner.benchPokemons.Contains(benchedPokemon))
        {
            Debug.LogWarning($"[Retreat] Błąd: Wybrana karta nie znajduje się na ławce!");
            return false;
        }

        CardInstance oldActivePokemon = owner.activePokemon;
        if (oldActivePokemon == null || !oldActivePokemon.pokemonLogic.tempBuffsData.canRetreat)
        {
            Debug.LogWarning("[Retreat] Active Pokemon cannot retreat due to an effect.");
            return false;
        }

        // Płacenie kosztu wycofania — zdejmujemy energie z karty po kolei aż zapłacimy całość
        int costToPay = Mathf.Max(0, oldActivePokemon.pokemonLogic.pokemonData.retreatCost + owner.retreatEnergyCostChange);
        var equippedEnergies = oldActivePokemon.pokemonLogic.energyEquipped;
        int totalEnergy = equippedEnergies.Values.Sum();
        if (totalEnergy < costToPay)
        {
            Debug.LogWarning($"[Retreat] Za mało energii! Potrzebujesz {costToPay}, a masz tylko {totalEnergy}.");
            return false;
        }

        int energyPaid = 0;

        // Kopiujemy klucze żeby bezpiecznie iterować (modyfikujemy słownik w loopie)
        List<EnumPokemonType> energyTypesAvailable = new List<EnumPokemonType>(equippedEnergies.Keys);

        foreach (var energyType in energyTypesAvailable)
        {
            while (equippedEnergies[energyType] > 0 && energyPaid < costToPay)
            {
                equippedEnergies[energyType]--;
                energyPaid++;
            }

            if (energyPaid >= costToPay) break;
        }

        // Czyścimy klucze z zerową wartością — słownik nie powinien mieć "pustych" typów
        foreach (var energyType in energyTypesAvailable)
        {
            if (equippedEnergies[energyType] <= 0)
                equippedEnergies.Remove(energyType);
        }

        // Zamieniamy Active ze wskazanym slotem ławki (numeracja slotów się nie zmienia)
        int slot = owner.benchPokemons.IndexOf(benchedPokemon);
        if (slot < 0)
        {
            Debug.LogWarning("[Retreat] Wewnętrzny błąd: nie znaleziono slotu ławki.");
            return false;
        }

        owner.benchPokemons[slot] = oldActivePokemon;
        oldActivePokemon.benchSlotIndex = slot;
        oldActivePokemon.pokemonLogic.ClearBuffsDebuffsAndStatuses();

        owner.activePokemon = benchedPokemon;
        benchedPokemon.benchSlotIndex = CardInstance.NotOnBench;
        owner.usedManualRetreatThisTurn = true;

        Debug.Log($"[Retreat] Udane wycofanie! Zapłacono {energyPaid} energii. " +
                  $"{FormatPokemonCombatState(oldActivePokemon)} ucieka na ławkę (slot {slot}). " +
                  $"Nowym aktywnym Pokemonem zostaje {FormatPokemonCombatState(benchedPokemon)}.");
        BattleManager.Instance.NotifyPokemonStatusChanged(oldActivePokemon.pokemonLogic);
        OnPokemonRetreated?.Invoke(owner, oldActivePokemon, benchedPokemon);
        return true;
    }

    private string FormatPokemonCombatState(CardInstance card)
    {
        if (card?.pokemonLogic == null)
            return card?.baseData?.cardName ?? "?";

        Pokemon pokemon = card.pokemonLogic;
        string energies = FormatEnergySummary(pokemon.energyEquipped);
        return $"{pokemon.pokemonData.cardName} [HP {pokemon.currentHp}/{pokemon.pokemonData.hp}, Energy: {energies}]";
    }

    private string FormatEnergySummary(Dictionary<EnumPokemonType, int> energies)
    {
        if (energies == null) return "none";

        var parts = energies
            .Where(kv => kv.Value > 0)
            .Select(kv => $"{kv.Value}x{kv.Key}")
            .ToList();

        return parts.Count > 0 ? string.Join(", ", parts) : "none";
    }


    public bool FreeSwapActive(PlayerController owner, CardInstance benchPokemon)
    {
        if (owner == null || !owner.benchPokemons.Contains(benchPokemon)) return false;

        CardInstance oldActive = owner.activePokemon;
        if (oldActive == null || oldActive.pokemonLogic.tempBuffsData.rooted)
        {
            Debug.LogWarning("[Swap] Active Pokemon cannot be swapped due to Root.");
            return false;
        }

        int slot = owner.benchPokemons.IndexOf(benchPokemon);

        owner.benchPokemons[slot] = oldActive;
        owner.activePokemon = benchPokemon;
        benchPokemon.benchSlotIndex = CardInstance.NotOnBench;
        oldActive.benchSlotIndex = slot;
        oldActive.pokemonLogic.ClearBuffsDebuffsAndStatuses();

        BattleManager.Instance.NotifyPokemonStatusChanged(oldActive.pokemonLogic);
        OnPokemonRetreated?.Invoke(owner, oldActive, benchPokemon);
        return true;
    }

    public void PrepareToSwapSelf()
    {
        if (activePlayer?.activePokemon == null) return;
        if (activePlayer.activePokemon.pokemonLogic.tempBuffsData.rooted)
        {
            Debug.LogWarning("[SwapSelf] Active Pokemon cannot be swapped due to Root.");
            return;
        }

        if (activePlayer.benchPokemons.Count == 0) return;
        TurnOnSelectionMode();
        SelectionAction = EnumSelectionAction.SwapSelf;
    }

    public void TurnOffSelectionMode()
    {
        selectionModeActive = false;
        selectedCard = null;
        SelectionAction = EnumSelectionAction.None;
        pendingEvolutionFromHandCard = null;
        pendingEvolutionOwner = null;
        pendingSelectionOwner = null;
        pendingSelectionCallback = null;
    }

    /// <summary>
    /// Wywoływana przez przycisk "Atak" na HUD. Wykonuje jedyny atak aktywnego Pokemona.
    /// </summary>
    public void TryAttack(int attackIndex = 0)
    {
        Debug.Log($"[TryAttack] Turn {turnManager.turnCounter}, Player {activePlayer.playerId}");

        if (selectionModeActive) return;

        if (activePlayer.hasAttackedThisTurn)
        {
            Debug.LogWarning("[TryAttack] Ignored — player already attacked this turn.");
            return;
        }

        // First turn (turnCounter == 1): Player 2 cannot attack
        if (turnManager.turnCounter == 1 && activePlayer.playerId == 2)
        {
            Debug.Log("[TryAttack] Player 2 cannot attack on turn 1");
            return;
        }

        // Check: Czy jest aktywny Pokemon, który może atakować albo czy jest Pokemon którego można atakować?
        // W setup phase może nie być aktywnych Pokemonów, więc atak jest niemożliwy. Wtedy po prostu nic się nie dzieje po kliknięciu "Atak".
        CardInstance attackerCard = activePlayer.activePokemon;
        if (attackerCard == null) return;
        if (!attackerCard.pokemonLogic.tempBuffsData.canAttack)
        {
            Debug.LogWarning($"[TryAttack] {attackerCard.baseData.cardName} nie może atakować w tej turze (canAttack=false — Sleep/Paralyze?).");
            return;
        }

        if (attackerCard.pokemonLogic.otherSpecialCondition == EnumSpecialConditionType.Confused)
        {
            bool attackWorks = UnityEngine.Random.Range(0, 2) == 1;
            if (!attackWorks)
            {
                Debug.LogWarning($"[TryAttack] {attackerCard.baseData.cardName} is Confused — coin flip tails, attack failed.");
                return;
            }
        }

        var attacks = attackerCard.pokemonLogic.pokemonData.attacks;
        if (attacks == null || attacks.Count == 0)
        {
            Debug.LogWarning($"[TryAttack] {attackerCard.baseData.cardName} nie ma zdefiniowanych ataków.");
            return;
        }

        AttackData attack = attacks[Mathf.Clamp(attackIndex, 0, attacks.Count - 1)];

        PlayerController defenderOwner = BattleManager.Instance.GetOpponent(activePlayer);
        if (defenderOwner.activePokemon == null)
        {
            Debug.LogWarning($"[TryAttack] Przeciwnik nie ma aktywnego Pokemona — atak niemożliwy.");
            return;
        }

        Debug.Log($"[Attack] Player {activePlayer.playerId} attacks with {attackerCard.baseData.cardName} using {attack.attackName}");
        bool attackExecuted = CardActions.ExecuteAttack(attackerCard, activePlayer, defenderOwner.activePokemon, defenderOwner, attack);
        if (!attackExecuted)
        {
            Debug.LogWarning($"[TryAttack] Attack '{attack.attackName}' was not executed; turn remains active.");
            return;
        }

        activePlayer.hasAttackedThisTurn = true;

        // For UI-driven players (Human), the attack itself ends the turn — AI brains
        // already call RequestEndTurn explicitly after their own visual delay.
        if (activePlayer.brain is HumanBrain)
            StartCoroutine(EndTurnAfterHumanAttack());
    }

    private IEnumerator EndTurnAfterHumanAttack()
    {
        float delay = GameRulesConfig.Instance != null
            ? GameRulesConfig.Instance.aiEndTurnDelay
            : 1f;
        yield return new WaitForSeconds(delay);
        TurnManager.Instance.RequestEndTurn();
    }


}


public static class CardInputEvents
{
    public static event Action<CardInstance> CardClicked;

    public static void RaiseCardClicked(CardInstance card)
    {
        CardClicked?.Invoke(card);
    }
}
