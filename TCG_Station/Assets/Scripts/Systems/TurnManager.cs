using System;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.UI;

public class TurnManager : MonoBehaviour
{
    public static TurnManager Instance { get; private set; }
    public static event Action<int, PlayerController> OnTurnStarted;
    public static event Action OnSetupComplete;

    private void Awake()
    {
        if (Instance == null) Instance = this;
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    private void Start()
    {
        if (GameManager.Instance != null)
        {
            GameManager.Instance.turnManager = this;
        }
    }

    public CardActions cardActions;
    public PlayerManager playerManager;
    public BoardVisualizer boardVisualizer;

    private static bool IsHeadlessRuntime()
    {
        return Application.isBatchMode || (GameRulesConfig.Instance != null && GameRulesConfig.Instance.IsHeadlessMode);
    }


    [Header("UI")]
    public TMP_Text turnInfoText;

    [Header("Buttons")]
    [FormerlySerializedAs("doneSetup")]
    public Button doneSetupButton;

    [Header("Game state (Local)")]
    // 0 = faza setupu, 1 = tura Gracza 1, 2 = tura Gracza 2
    public int activePlayerId = 0;
    public int firstPlayerId = 0; // which player went first (set once in RandomizeFirstPlayer)
    public int turnCounter = 0;
    public int setupSum = 0; // ile setupów zostało ukończonych — gdy obaj gracze skończą, startujemy grę

    // Tu można podpiąć eventy dla innych skryptów (np. dobieranie karty na starcie tury)
    //public event Action<int> OnTurnChanged;



    public void StartTurnManager()
    {
        cardActions = CardActions.Instance;
        playerManager = PlayerManager.Instance;
        if (!IsHeadlessRuntime())
            boardVisualizer = FindFirstObjectByType<BoardVisualizer>();
        ConfigureDoneSetupButton();

        StartGame();
    }

    private void ConfigureDoneSetupButton()
    {
        if (IsHeadlessRuntime()) return;

        if (doneSetupButton == null)
        {
            foreach (Button button in FindObjectsByType<Button>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (button != null && button.name == "Button Done Setup")
                {
                    doneSetupButton = button;
                    break;
                }
            }
        }

        if (doneSetupButton == null)
        {
            Debug.LogWarning("[TurnManager] Button Done Setup is not assigned; human setup cannot be confirmed from UI.");
            return;
        }

        doneSetupButton.onClick.RemoveListener(OnDoneSetupClicked);
        doneSetupButton.onClick.AddListener(OnDoneSetupClicked);
        doneSetupButton.gameObject.SetActive(playerManager != null && playerManager.player1?.brain is HumanBrain);
    }

    
    public void RandomizeFirstPlayer()
    {
        int startplayer = UnityEngine.Random.Range(1, 3); // Zwraca 1 lub 2
        Debug.Log("Wylosowano: " + startplayer);
        if (startplayer == 1)
        {
            activePlayerId = 1;
            firstPlayerId = 1;
            Debug.Log($"[TurnManager] Player 1: {playerManager.player1.playerName} starts first.");
            playerManager.activePlayer = playerManager.player1;
        }
        else
        {
            activePlayerId = 2;
            firstPlayerId = 2;
            Debug.Log($"[TurnManager] Player 2: {playerManager.player2.playerName} starts first.");
            playerManager.activePlayer = playerManager.player2;
        }
    }


    public void StartGame()
    {
        activePlayerId = 0;
        Debug.Log("[TurnManager] Gra wystartowała. Faza setupu.");


        // Zamiast czekać na jeden przycisk, odpalamy osobny coroutine, który synchronizuje obu graczy
        playerManager.InitializeEnergy();
        StartCoroutine(RunSetupPhaseAsynchronously());
    }

    private IEnumerator RunSetupPhaseAsynchronously()
    {
        bool isPlayer1Ready = false;
        bool isPlayer2Ready = false;

        // 1. Odpalamy mózg Gracza 1 — typ nieważny, każdy mózg obsługuje setup po swojemu
        StartCoroutine(playerManager.player1.brain.PerformSetupPhase((chosenCards) =>
        {
            // AI zwróci listę kart do zagrania; człowiek zwróci null, bo sam klika w UI
            if (chosenCards != null && chosenCards.Count > 0)
            {
                PlayCardFromBrain(chosenCards[0], playerManager.player1);
            }
            isPlayer1Ready = true;
        }));

        // 2. To samo dla Gracza 2
        StartCoroutine(playerManager.player2.brain.PerformSetupPhase((chosenCards) =>
        {
            if (chosenCards != null && chosenCards.Count > 0)
            {
                PlayCardFromBrain(chosenCards[0], playerManager.player2);
            }
            isPlayer2Ready = true;
        }));

        // 3. Czekamy, aż obaj skończą (AI może być szybsze od człowieka)
        while (isPlayer1Ready == false || isPlayer2Ready == false)
        {
            yield return null;
        }

        Debug.Log("[TurnManager] Obaj gracze zakończyli fazę Setup. Zaczynamy grę!");
        OnSetupComplete?.Invoke();

        if (doneSetupButton != null)
        {
            doneSetupButton.gameObject.SetActive(false);
        }

        if (!IsHeadlessRuntime())
            boardVisualizer?.FlipAllHiddenCards();
        yield return StartCoroutine(ChangeTurn());
    }

    // Pomocnicza: zagrywa kartę wybraną przez mózg AI (człowiek robi to sam kliknięciem)
    private void PlayCardFromBrain(CardInstance cardToPlay, PlayerController player) // PlayerController only to make sure player1 doesn't play hidden card; only player2
    {
        playerManager.TryPlayPokemon(cardToPlay);
    }

    public void OnDoneSetupClicked()
    {
        // Człowiek kliknął "Done Setup" — przekazujemy to do jego mózgu
        if (playerManager != null && playerManager.player1?.brain is HumanBrain humanBrain)
        {
            humanBrain.ConfirmSetupButtonClicked();
        }
    }

    

    /// <summary>
    /// Wywołaj gdy gracz (człowiek lub AI) chce zakończyć swoją turę.
    /// Sprawdza czy gracz może zakończyć turę (np. czy wybrał nowego Active po KO).
    /// </summary>
    public void RequestEndTurn()
    {
        if (BattleManager.Instance.isGameOver)
        {
            Debug.Log("[TurnManager] RequestEndTurn zignorowany — gra już zakończona.");
            return;
        }

        // Gracz nie może zakończyć tury, jeśli musi najpierw wybrać nowego Active po nokaucie
        if (playerManager.activePlayer != null && playerManager.activePlayer.chooseMode)
        {
            Debug.LogWarning("[TurnManager] Cannot end turn — player must choose a new Active Pokemon first.");
            return;
        }

        if (playerManager.selectionModeActive)
        {
            Debug.LogWarning("[TurnManager] Cannot end turn — player must finish the current selection first.");
            return;
        }

        Debug.Log($"[TurnManager] RequestEndTurn — {playerManager.activePlayer?.playerName} kończy turę {turnCounter}.");
        StartCoroutine(ChangeTurn());
    }

    private IEnumerator ChangeTurn()
    {
        // 1. FAZA MIĘDZY TURAMI: obrażenia od Poison/Burn, pasywne leczenie
        // Wywoływana przed zmianą gracza — dotyczy obu stron planszy
        // Przy pierwszej zmianie tury (ze Setupu) pomijamy, bo nie ma jeszcze walki
        if (activePlayerId != 0)
        {
            BattleManager.Instance.ProcessEndOfTurn(playerManager.activePlayer);
            bool hadBetweenTurnsVisuals = BattleManager.Instance.ProcessBetweenTurns();
            if (BattleManager.Instance.isGameOver) yield break;

            if (hadBetweenTurnsVisuals && !IsHeadlessRuntime())
            {
                float pause = GameRulesConfig.Instance != null
                    ? Mathf.Max(0.5f, GameRulesConfig.Instance.damageTextDisplayDuration)
                    : 1.2f;
                yield return new WaitForSeconds(pause);
            }

            // Czekamy aż wszystkie promocje po KO zostaną rozwiązane.
            // RequestNewActiveAfterKnockoutDelay działa z opóźnieniem — bez tego
            // tura nowego gracza startuje zanim slot Active jest obsadzony.
            while (!BattleManager.Instance.isGameOver)
            {
                bool p1Ready = playerManager.player1.activePokemon != null;
                bool p2Ready = playerManager.player2.activePokemon != null;
                if (p1Ready && p2Ready) break;
                yield return null;
            }
            if (BattleManager.Instance.isGameOver) yield break;
        }

        // 2. Przejście do pierwszej tury lub zmiana aktywnego gracza
        if (activePlayerId == 0)
        {
            RandomizeFirstPlayer();
        }
        else
        {
            playerManager.SwitchActivePlayer();
        }

        turnCounter++;

        if (turnCounter > GameRulesConfig.Instance.maxTurns)
        {
            BattleManager.Instance.TriggerTurnLimitGameOver();
            yield break;
        }

        // 3. POCZĄTEK TURY: blokady Paralyze/Sleep, rzut monetą przy Sleep, reset buffów
        // Wywoływana po zmianie gracza — dotyczy nowego aktywnego gracza
        BattleManager.Instance.ProcessStartOfTurn(playerManager.activePlayer);

        // 4. Dobieranie karty — na starcie każdej tury, również w pierwszej.
        cardActions.DrawCard(playerManager.activePlayer, 1); // triggerEvent=true odpala OnCardDrewFromEffect → VisualizeDrawnCards

        // 5. Odnawianie strefy energii dla nowego aktywnego gracza.
        // Pierwszy gracz w turze 1 widzi tylko Next z setupu; Current zostaje puste.
        // Od pierwszej tury drugiego gracza kazda tura aktywnego gracza przesuwa Next do Current.
        if (turnCounter > 1)
        {
            playerManager.UpdateEnergyZones(playerManager.activePlayer);
        }

        Debug.Log($"[TurnManager] Turn {turnCounter} — {playerManager.activePlayer.playerName}'s turn.");
        OnTurnStarted?.Invoke(turnCounter, playerManager.activePlayer);

        // 6. Uruchomienie mózgu aktywnego gracza (AI wykonuje decyzje i auto-kończy turę;
        //    HumanBrain ma no-op — czeka na kliknięcie przycisku "Koniec tury" w HUD)
        StartCoroutine(playerManager.activePlayer.brain.PerformTurn());
    }

}
