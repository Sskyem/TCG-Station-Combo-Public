using TMPro;
using UnityEngine;
using UnityEngine.UI;
using System;

public class GameManager : MonoBehaviour
{
    public static string CreateBattleId(string prefix = "battle")
    {
        return $"{prefix}_{DateTime.Now:yyyyMMdd_HHmmss_fff}";
    }

    #region Singleton
    public static GameManager Instance { get; private set; }
    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        // NOTE: intentionally NOT DontDestroyOnLoad. This is a single-scene game, so
        // persisting across loads is a no-op for normal play — but it would leave stale
        // scene references after a BenchmarkRunner scene reload. Letting this singleton
        // rebuild with the fresh scene keeps all references valid between matches.
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }
    #endregion

    [Header("CORE MODULES")]
    [Header(">> Scene: Initialization")]
    [SerializeField] FolderCreator folderCreator;
    public JsonLoader jsonLoader;
    public CardValidator cardValidator;
    public PlayerManager playerManager;
    public GeminiApiClient geminiApiClient;

    [Header(">> Scene: Board")]
    public TurnManager turnManager;
    public BoardVisualizer boardVisualizer;

    [Header("UI Elements")]
    public Button playButton;
    [SerializeField] EnumScenes boardScene = EnumScenes.Board;

    [Tooltip("Skip the startup menu and start the game immediately (fast Editor iteration). " +
             "Also enabled by the -skipMenu command-line argument.")]
    [SerializeField] bool skipMenu = false;

    public TMP_InputField promptInput;
    public TMP_Text textField;
    public string llmResponse;



    private void Start()
    {
        folderCreator.CreateFolders();
        jsonLoader.LoadCards();
        jsonLoader.LoadDecks();

        // Validate loaded cards
        //playButton.enabled = cardValidator.AreAllCardsCorrect(); //SKIPPING FOR NOW

        Debug.Log("<color=green><b>[GameManager] Inicialization done</b></color>");

        // If a startup menu is present in this scene (Initialization), hand control to it
        // instead of starting a match immediately. The menu writes the JSON config and then
        // calls BeginGameFromMenu() to load the gameplay scene, where a fresh GameManager
        // runs StartGame() normally. skipMenu (Inspector or -skipMenu CLI arg) bypasses it.
        StartupMenuController menu = FindFirstObjectByType<StartupMenuController>();
        if (menu != null && Application.isBatchMode)
        {
            BeginGameFromMenu();
            return;
        }
        if (menu != null && !ShouldSkipMenu())
        {
            menu.Show();
            return;
        }

        FindMissingReferences();
        StartGame();
    }

    private bool ShouldSkipMenu()
    {
        if (skipMenu) return true;
        foreach (string arg in System.Environment.GetCommandLineArgs())
            if (string.Equals(arg, "-skipMenu", System.StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static bool IsHeadlessRuntime()
    {
        return Application.isBatchMode || (GameRulesConfig.Instance != null && GameRulesConfig.Instance.IsHeadlessMode);
    }

    // Called by the startup menu's Start button after it has written the JSON config.
    // Loads the gameplay scene; that scene's own GameManager.Start() runs StartGame(),
    // and its GameRulesConfig / BenchmarkRunner read the freshly written JSON on Awake.
    public void BeginGameFromMenu()
    {
        UnityEngine.SceneManagement.SceneManager.LoadScene(boardScene.ToString());
    }


    public void SwitchSceneToBoard()
    {
        UnityEngine.SceneManagement.SceneManager.sceneLoaded += OnSceneLoaded;
        UnityEngine.SceneManagement.SceneManager.LoadScene(boardScene.ToString());
    }

    private void OnSceneLoaded(UnityEngine.SceneManagement.Scene scene, UnityEngine.SceneManagement.LoadSceneMode mode)
    {
        UnityEngine.SceneManagement.SceneManager.sceneLoaded -= OnSceneLoaded;

        FindMissingReferences();
        StartGame();
    }



    public void StartGame()
    {
        Debug.Log("<color=green><b>[GameManager] Starting local game...</b></color>");

        // Benchmark hook: when a BenchmarkRunner is present, let it apply the matchup
        // (player types + deck names) for the current schedule index before player setup.
        // No-op for normal single-match play (Instance is null).
        BenchmarkRunner.Instance?.ConfigureNextMatch();

        GameRulesConfig config = GameRulesConfig.Instance;

        // Resolve auto-detected AlgorithmBrain profiles to a concrete profile up front, so every
        // downstream consumer (result logger label, PlayerManager → SetProfile) sees the final value.
        // A seat set to EnumAlgorithmProfile.Auto is detected from its deck archetype here. During a
        // benchmark the runner already bound each seat's concrete profile (per-deck override >
        // auto-detect > Standard), so the seats never arrive as Auto and this block is a no-op.
        if (config != null)
        {
            var cards = jsonLoader?.cardLibrary;
            var decks = jsonLoader?.deckLibrary;
            if (config.player1AlgorithmProfile == EnumAlgorithmProfile.Auto)
                config.player1AlgorithmProfile = DeckArchetypeDetector.Detect(config.player1DeckName, cards, decks);
            if (config.player2AlgorithmProfile == EnumAlgorithmProfile.Auto)
                config.player2AlgorithmProfile = DeckArchetypeDetector.Detect(config.player2DeckName, cards, decks);
        }

        BattleResultExporter battleResultExporter = null;
        if (config == null || config.enableDeckbuilderBattleLogs)
        {
            battleResultExporter = GetComponent<BattleResultExporter>();
            if (battleResultExporter == null)
                battleResultExporter = gameObject.AddComponent<BattleResultExporter>();
        }

        HumanReadableBattleLogger readableLogger = null;
        if (config == null || config.enableReadableBattleLogs)
        {
            readableLogger = GetComponent<HumanReadableBattleLogger>();
            if (readableLogger == null)
                readableLogger = gameObject.AddComponent<HumanReadableBattleLogger>();
        }

        DecisionLogger decisionLogger = null;
        if (config == null || config.enableMlDecisionLogs)
        {
            decisionLogger = GetComponent<DecisionLogger>();
            if (decisionLogger == null)
                decisionLogger = gameObject.AddComponent<DecisionLogger>();
        }

        GameResultLogger gameResultLogger = null;
        if (config == null || config.enableMlGameResultLogs)
        {
            gameResultLogger = GetComponent<GameResultLogger>();
            if (gameResultLogger == null)
                gameResultLogger = gameObject.AddComponent<GameResultLogger>();
        }

        LLMLogger llmLogger = null;
        if (config == null || config.enableLlmDecisionLogs)
        {
            llmLogger = GetComponent<LLMLogger>();
            if (llmLogger == null)
                llmLogger = gameObject.AddComponent<LLMLogger>();
        }

        string battleId = CreateBattleId();
        battleResultExporter?.BeginBattle(battleId);
        readableLogger?.BeginBattle(battleId);
        decisionLogger?.BeginGame(battleId);
        // Brain label = type, with the AlgorithmBrain numeric profile appended (e.g. "Algorithm:Ramp"),
        // so games.jsonl can be analysed per (deck × profile).
        string BrainLabel(EnumPlayerType type, EnumAlgorithmProfile prof) =>
            type == EnumPlayerType.Algorithm ? $"Algorithm:{prof}" : type.ToString();
        string brainA = config != null ? BrainLabel(config.player1Type, config.player1AlgorithmProfile) : null;
        string brainB = config != null ? BrainLabel(config.player2Type, config.player2AlgorithmProfile) : null;
        gameResultLogger?.BeginGame(battleId, config?.player1DeckName, config?.player2DeckName, brainA, brainB);
        llmLogger?.BeginGame(battleId);

        bool headless = IsHeadlessRuntime();
        if (headless)
        {
            if (boardVisualizer == null)
                boardVisualizer = FindFirstObjectByType<BoardVisualizer>();
            boardVisualizer?.DisableForHeadless();
        }
        else
        {
            if (boardVisualizer == null)
                boardVisualizer = FindFirstObjectByType<BoardVisualizer>();
            boardVisualizer?.StartBoardVisualizer();
        }
        playerManager.StartPlayerManager("Playero1", "Playero2");
        turnManager.StartTurnManager();
    }


    //public void TestLLM()
    //{
    //    llm.StartLLM();
    //    if (llmResponse != null)
    //    {
    //        llm.SendPrompt(promptInput.text);
    //    }
    //}



    private void FindMissingReferences()
    {
        turnManager = FindFirstObjectByType<TurnManager>();
        boardVisualizer = FindFirstObjectByType<BoardVisualizer>();
    }


    public void ExitGame()
    {
        Debug.Log("Zamykanie gry...");
        Application.Quit();
    }
}
