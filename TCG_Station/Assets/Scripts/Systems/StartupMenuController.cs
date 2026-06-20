using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;
using UnityEngine.UI;

/// <summary>
/// Startup menu shown in the Initialization scene. Lets the user pick a play mode and
/// configure the most important settings, then writes them to the JSON config files
/// (StreamingAssets/GameRulesConfig.json + BenchmarkConfig.json) and launches the
/// gameplay scene via GameManager.BeginGameFromMenu().
///
/// Design notes:
///   - The menu edits GameRulesConfig.Instance fields directly, so a GameRulesConfig
///     component MUST be present in the Initialization scene (with loadFromJson = true so
///     the UI prefills from the current config). On Start it merges only menu-owned fields into
///     the JSON; the fresh GameRulesConfig in the gameplay scene reloads that file on Awake.
///   - Benchmark settings are persisted via the static BenchmarkRunner.SaveConfig(). For the
///     Simulation mode to actually run, the BenchmarkRunner component in the gameplay scene
///     must have its Inspector runEnabled = true (the Inspector switch is checked before JSON).
///   - All UI references are optional/null-safe so the scene can be wired incrementally.
/// </summary>
public class StartupMenuController : MonoBehaviour
{
    public enum MenuMode { Simulation, WatchAi, HumanVsAi }
    private static TMP_FontAsset runtimeMenuFont;

    // AI types selectable in the dropdowns, in display order.
    private static readonly EnumPlayerType[] AiTypes =
        { EnumPlayerType.Algorithm, EnumPlayerType.LLM, EnumPlayerType.ML };

    // AlgorithmBrain weight profiles selectable per seat, in display order. "Auto" detects the
    // archetype from the deck (DeckArchetypeDetector) and is resolved in GameManager.StartGame.
    private static readonly EnumAlgorithmProfile[] AlgorithmProfiles =
    {
        EnumAlgorithmProfile.Auto,
        EnumAlgorithmProfile.Standard,
        EnumAlgorithmProfile.Ramp,
        EnumAlgorithmProfile.TempoAggro,
        EnumAlgorithmProfile.ControlStatus,
        EnumAlgorithmProfile.HealStall,
    };

    [Header("Panels (optional)")]
    [SerializeField] GameObject modePanel;
    [SerializeField] GameObject configPanel;
    [SerializeField] GameObject advancedPanel;
    [SerializeField] TMP_Text modeLabel;

    [Header("Simple config")]
    [SerializeField] TMP_Dropdown player1DeckDropdown;
    [SerializeField] TMP_Dropdown player2DeckDropdown;
    [Tooltip("AI type for Player 1 (used in Watch AI vs AI mode).")]
    [SerializeField] TMP_Dropdown player1TypeDropdown;
    [Tooltip("AI type for the opponent (Player 2) in Watch / Human-vs-AI modes.")]
    [SerializeField] TMP_Dropdown opponentTypeDropdown;
    [Tooltip("AlgorithmBrain weight profile for Player 1 (Watch AI vs AI mode). 'Auto (detect)' picks " +
             "the profile from the deck's archetype. Ignored for non-Algorithm seats.")]
    [SerializeField] TMP_Dropdown player1ProfileDropdown;
    [Tooltip("AlgorithmBrain weight profile for the opponent (Player 2). 'Auto (detect)' picks the " +
             "profile from the deck's archetype. Ignored for non-Algorithm seats.")]
    [SerializeField] TMP_Dropdown player2ProfileDropdown;
    [Tooltip("Games per pairing (Simulation mode).")]
    [SerializeField] TMP_InputField matchesPerPairingInput;
    [SerializeField] Toggle uploadLogsToggle;
    [Tooltip("Run the benchmark without scene visuals (faster). Maps to GameRulesConfig.headlessMode. " +
             "Shown in Simulation mode. Optional — leave unwired to keep the value from the JSON config.")]
    [SerializeField] Toggle headlessModeToggle;

    [Header("Advanced config (optional)")]
    [SerializeField] TMP_InputField pointsToWinInput;
    [SerializeField] TMP_InputField maxTurnsInput;
    [SerializeField] TMP_InputField benchSizeInput;
    [Tooltip("0 = instant, 1 = normal speed. Forced to 0 in Simulation (fast) mode.")]
    [SerializeField] Slider aiDelayScaleSlider;
    [Tooltip("Stop / quit when a Simulation run finishes the full schedule.")]
    [SerializeField] Toggle stopOnFinishToggle;

    [Header("Start")]
    [SerializeField] Button startButton;

    private MenuMode mode = MenuMode.HumanVsAi;
    private GameRulesConfig cfg;
    private readonly List<string> deckNames = new List<string>();

    private void Awake()
    {
        ApplyRuntimeMenuFont();
    }

    // =========================================================================
    // Shown by GameManager.Start() once cards/decks are loaded.
    // =========================================================================
    public void Show()
    {
        cfg = GameRulesConfig.Instance;
        if (cfg == null)
        {
            Debug.LogError("[StartupMenuController] GameRulesConfig.Instance is null. " +
                           "Add a GameRulesConfig component to the Initialization scene " +
                           "so the menu can read/write settings.");
        }
        else
        {
            // Author from the actual JSON file, not stale Inspector defaults. This matters when the
            // Initialization scene's GameRulesConfig may have loadFromJson = false. Reloading here
            // still ensures the UI is prefilled from the actual runtime file.
            cfg.ReloadFromJson();
        }

        PopulateDecks();
        PopulateAiTypeDropdowns();
        PopulateProfileDropdowns();
        PrefillFromConfig();
        ApplyRuntimeMenuFont();

        if (startButton != null)
        {
            startButton.onClick.RemoveListener(OnStartClicked);
            startButton.onClick.AddListener(OnStartClicked);
        }

        SetMode(mode);
        if (modePanel != null) modePanel.SetActive(true);
        if (configPanel != null) configPanel.SetActive(true);
    }

    private void ApplyRuntimeMenuFont()
    {
        if (runtimeMenuFont == null)
        {
            Font sourceFont = Font.CreateDynamicFontFromOSFont(
                new[] { "Segoe UI", "Arial", "Liberation Sans" },
                90);

            if (sourceFont == null)
            {
                Debug.LogWarning("[StartupMenuController] Could not create a runtime font with Polish glyph support.");
                return;
            }

            runtimeMenuFont = TMP_FontAsset.CreateFontAsset(
                sourceFont,
                90,
                9,
                GlyphRenderMode.SDFAA,
                1024,
                1024,
                AtlasPopulationMode.Dynamic,
                true);

            if (runtimeMenuFont == null)
            {
                Debug.LogWarning("[StartupMenuController] Could not create a runtime TMP_FontAsset with Polish glyph support.");
                return;
            }

            runtimeMenuFont.TryAddCharacters("ąćęłńóśźżĄĆĘŁŃÓŚŹŻ", out _);
        }

        foreach (TMP_Text text in FindObjectsByType<TMP_Text>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (text == null) continue;
            text.font = runtimeMenuFont;
            text.SetAllDirty();
        }
    }

    // ── Mode selection (wire these to the three mode buttons) ────────────────
    public void SelectSimulation() => SetMode(MenuMode.Simulation);
    public void SelectWatchAi()    => SetMode(MenuMode.WatchAi);
    public void SelectHumanVsAi()  => SetMode(MenuMode.HumanVsAi);

    private void SetMode(MenuMode m)
    {
        mode = m;
        if (modeLabel != null)
        {
            modeLabel.text = m switch
            {
                MenuMode.Simulation => "Wybrane: Symulacja (benchmark)",
                MenuMode.WatchAi    => "Wybrane: Oglądaj AI vs AI",
                _                   => "Wybrane: Gra: Człowiek vs AI",
            };
        }

        bool sim = m == MenuMode.Simulation;
        // Player 1 type only matters when watching two AIs; in Human mode P1 is the human,
        // in Simulation the benchmark forces both sides to Algorithm.
        if (player1TypeDropdown != null) player1TypeDropdown.gameObject.SetActive(m == MenuMode.WatchAi);
        if (opponentTypeDropdown != null) opponentTypeDropdown.gameObject.SetActive(!sim);
        // Profile dropdowns mirror the AI-type dropdowns: P1 only when watching two AIs, P2 in any
        // non-simulation mode. Simulation profiles come from BenchmarkConfig.json, not the menu.
        if (player1ProfileDropdown != null) player1ProfileDropdown.gameObject.SetActive(m == MenuMode.WatchAi);
        if (player2ProfileDropdown != null) player2ProfileDropdown.gameObject.SetActive(!sim);
        if (matchesPerPairingInput != null) matchesPerPairingInput.gameObject.SetActive(sim);
        // Headless only makes sense for the unattended benchmark run.
        if (headlessModeToggle != null) headlessModeToggle.gameObject.SetActive(sim);
    }

    // ── Quick debug bypass (wire to an optional "Debug start" button) ────────
    public void QuickDebugStart()
    {
        if (GameManager.Instance != null) GameManager.Instance.BeginGameFromMenu();
    }

    public void ToggleAdvanced()
    {
        if (advancedPanel != null) advancedPanel.SetActive(!advancedPanel.activeSelf);
    }

    // =========================================================================
    // Start: apply UI → config, persist, launch gameplay scene.
    // =========================================================================
    private void OnStartClicked()
    {
        if (cfg == null) cfg = GameRulesConfig.Instance;
        if (cfg == null)
        {
            Debug.LogError("[StartupMenuController] Cannot start: GameRulesConfig.Instance is null.");
            return;
        }

        ApplyAdvanced();

        string deck1 = SelectedDeck(player1DeckDropdown, cfg.player1DeckName);
        string deck2 = SelectedDeck(player2DeckDropdown, cfg.player2DeckName);
        cfg.player1DeckName = deck1;
        cfg.player2DeckName = deck2;

        bool runBenchmark = false;

        switch (mode)
        {
            case MenuMode.Simulation:
                // Benchmark forces both sides to Algorithm in ConfigureNextMatch(); set here too
                // so a non-benchmark fallback still behaves sensibly.
                cfg.player1Type = EnumPlayerType.Algorithm;
                cfg.player2Type = EnumPlayerType.Algorithm;
                cfg.aiDelayScale = 0f;
                // Headless applies to the unattended benchmark only. Unwired toggle → keep the JSON value.
                if (headlessModeToggle != null) cfg.headlessMode = headlessModeToggle.isOn;
                runBenchmark = true;
                break;

            case MenuMode.WatchAi:
                cfg.player1Type = SelectedAiType(player1TypeDropdown, EnumPlayerType.Algorithm);
                cfg.player2Type = SelectedAiType(opponentTypeDropdown, EnumPlayerType.Algorithm);
                cfg.headlessMode = false;
                break;

            case MenuMode.HumanVsAi:
                cfg.player1Type = EnumPlayerType.Human;
                cfg.player2Type = SelectedAiType(opponentTypeDropdown, EnumPlayerType.Algorithm);
                cfg.headlessMode = false;
                break;
        }

        if (uploadLogsToggle != null) cfg.logUploadEnabled = uploadLogsToggle.isOn;

        // Per-seat AlgorithmBrain profile from the dropdowns (incl. "Auto" = detect from the deck,
        // resolved in GameManager.StartGame). Simulation profiles come from BenchmarkConfig.json, so
        // the menu dropdowns are only read for the interactive modes. Null-safe: an unwired dropdown
        // keeps the value already loaded from GameRulesConfig.json (so a hand-set Auto is preserved).
        if (mode == MenuMode.WatchAi)
            cfg.player1AlgorithmProfile = SelectedAlgorithmProfile(player1ProfileDropdown, cfg.player1AlgorithmProfile);
        if (mode != MenuMode.Simulation)
            cfg.player2AlgorithmProfile = SelectedAlgorithmProfile(player2ProfileDropdown, cfg.player2AlgorithmProfile);

        // The startup UI owns only a subset of GameRulesConfig. Merge those values into the
        // existing file so hand-edited LLM models/providers and other advanced settings are not
        // replaced by values held by the scene object.
        if (!cfg.SaveStartupMenuSettingsToJson())
        {
            Debug.LogError("[StartupMenuController] Start cancelled because GameRulesConfig.json could not be saved safely.");
            return;
        }

        // Participants: full round-robin over every loaded deck (sensible default for a demo).
        // SaveConfig preserves the hand-authored autoDetectProfilesByDeck flag and per-participant
        // profile overrides already present in BenchmarkConfig.json.
        bool stopOnFinish = stopOnFinishToggle != null ? stopOnFinishToggle.isOn : false;
        BenchmarkRunner.SaveConfig(
            runEnabled: runBenchmark,
            participants: deckNames,
            matchesPerPairing: ParseInt(matchesPerPairingInput, 5),
            swapSeats: true,
            includeMirror: false,
            randomizeScheduleOrder: true,
            fastMode: true,
            matchWatchdogSeconds: 180f,
            stopOnFinish: stopOnFinish);

        if (GameManager.Instance != null)
            GameManager.Instance.BeginGameFromMenu();
        else
            Debug.LogError("[StartupMenuController] GameManager.Instance is null — cannot launch scene.");
    }

    // =========================================================================
    // Helpers
    // =========================================================================
    private void ApplyAdvanced()
    {
        cfg.pointsToWin = ParseInt(pointsToWinInput, cfg.pointsToWin);
        cfg.maxTurns    = ParseInt(maxTurnsInput, cfg.maxTurns);
        cfg.benchSize   = ParseInt(benchSizeInput, cfg.benchSize);
        if (aiDelayScaleSlider != null) cfg.aiDelayScale = aiDelayScaleSlider.value;
    }

    private void PopulateDecks()
    {
        deckNames.Clear();
        var library = GameManager.Instance?.jsonLoader?.deckLibrary;
        if (library != null)
            deckNames.AddRange(library.Keys.OrderBy(k => k));

        if (deckNames.Count == 0)
            Debug.LogWarning("[StartupMenuController] No decks loaded — deck dropdowns will be empty.");

        FillDropdown(player1DeckDropdown, deckNames);
        FillDropdown(player2DeckDropdown, deckNames);
    }

    private void PopulateAiTypeDropdowns()
    {
        var labels = AiTypes.Select(t => t.ToString()).ToList();
        FillDropdown(player1TypeDropdown, labels);
        FillDropdown(opponentTypeDropdown, labels);
    }

    private void PopulateProfileDropdowns()
    {
        var labels = AlgorithmProfiles
            .Select(p => p == EnumAlgorithmProfile.Auto ? "Auto (detect)" : p.ToString())
            .ToList();
        FillDropdown(player1ProfileDropdown, labels);
        FillDropdown(player2ProfileDropdown, labels);
    }

    private void PrefillFromConfig()
    {
        if (cfg == null) return;
        SelectInDropdown(player1DeckDropdown, deckNames, cfg.player1DeckName);
        SelectInDropdown(player2DeckDropdown, deckNames, cfg.player2DeckName);

        SelectAiTypeInDropdown(player1TypeDropdown, cfg.player1Type);
        SelectAiTypeInDropdown(opponentTypeDropdown, cfg.player2Type);

        SelectAlgorithmProfileInDropdown(player1ProfileDropdown, cfg.player1AlgorithmProfile);
        SelectAlgorithmProfileInDropdown(player2ProfileDropdown, cfg.player2AlgorithmProfile);

        SetText(pointsToWinInput, cfg.pointsToWin);
        SetText(maxTurnsInput, cfg.maxTurns);
        SetText(benchSizeInput, cfg.benchSize);
        if (aiDelayScaleSlider != null) aiDelayScaleSlider.value = cfg.aiDelayScale;
        if (uploadLogsToggle != null) uploadLogsToggle.isOn = cfg.logUploadEnabled;
        if (headlessModeToggle != null) headlessModeToggle.isOn = cfg.headlessMode;
    }

    private static void FillDropdown(TMP_Dropdown dropdown, List<string> options)
    {
        if (dropdown == null) return;
        dropdown.ClearOptions();
        dropdown.AddOptions(options);
    }

    private static void SelectInDropdown(TMP_Dropdown dropdown, List<string> options, string value)
    {
        if (dropdown == null || string.IsNullOrEmpty(value)) return;
        int idx = options.IndexOf(value);
        if (idx >= 0) dropdown.SetValueWithoutNotify(idx);
    }

    private static void SelectAiTypeInDropdown(TMP_Dropdown dropdown, EnumPlayerType type)
    {
        if (dropdown == null) return;
        int idx = System.Array.IndexOf(AiTypes, type);
        if (idx >= 0) dropdown.SetValueWithoutNotify(idx);
    }

    private static void SelectAlgorithmProfileInDropdown(TMP_Dropdown dropdown, EnumAlgorithmProfile profile)
    {
        if (dropdown == null) return;
        int idx = System.Array.IndexOf(AlgorithmProfiles, profile);
        if (idx >= 0) dropdown.SetValueWithoutNotify(idx);
    }

    private static EnumAlgorithmProfile SelectedAlgorithmProfile(TMP_Dropdown dropdown, EnumAlgorithmProfile fallback)
    {
        if (dropdown == null) return fallback;
        int idx = Mathf.Clamp(dropdown.value, 0, AlgorithmProfiles.Length - 1);
        return AlgorithmProfiles[idx];
    }

    private string SelectedDeck(TMP_Dropdown dropdown, string fallback)
    {
        if (dropdown == null || deckNames.Count == 0) return fallback;
        int idx = Mathf.Clamp(dropdown.value, 0, deckNames.Count - 1);
        return deckNames[idx];
    }

    private static EnumPlayerType SelectedAiType(TMP_Dropdown dropdown, EnumPlayerType fallback)
    {
        if (dropdown == null) return fallback;
        int idx = Mathf.Clamp(dropdown.value, 0, AiTypes.Length - 1);
        return AiTypes[idx];
    }

    private static int ParseInt(TMP_InputField field, int fallback)
    {
        if (field == null || string.IsNullOrWhiteSpace(field.text)) return fallback;
        return int.TryParse(field.text, out int v) ? v : fallback;
    }

    private static void SetText(TMP_InputField field, int value)
    {
        if (field != null) field.text = value.ToString();
    }
}
