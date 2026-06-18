using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using System;

public class GameRulesConfig : MonoBehaviour
{
    public static GameRulesConfig Instance { get; private set; }

    [Header("Config Source")]
    [Tooltip("ON  = at runtime every other field is loaded from GameRulesConfig.json (read from next to the " +
             "build, NOT Assets/StreamingAssets), so the Inspector fields below are inactive and hidden in a " +
             "collapsed 'Inactive' foldout.\n" +
             "OFF = the JSON is ignored and the Inspector values below are used directly.")]
    public bool loadFromJson = true;
    [Tooltip("Disable scene visuals for benchmark builds. Application.isBatchMode also forces this at runtime.")]
    public bool headlessMode = false;

    public bool IsHeadlessMode => headlessMode || Application.isBatchMode;

    [Header("Game Rules")]
    public int deckSize = 30;
    public int benchSize = 3;
    public int starterHandSize = 5;
    public int pointsToWin = 4;
    public int maxTurns = 30;
    public int poisonDamagePerTurn = 10;
    public int burnDamagePerTurn = 20;

    [Header("Match Setup")]
    public EnumPlayerType player1Type = EnumPlayerType.Human;
    public EnumPlayerType player2Type = EnumPlayerType.Algorithm;
    public string player1DeckName = "Venusaur Butterfree";
    public string player2DeckName = "Venusaur Butterfree";

    // Numeric tuning profile for each AlgorithmBrain seat (ignored for non-Algorithm players).
    // Set a seat to EnumAlgorithmProfile.Auto to have its deck archetype auto-detected
    // (DeckArchetypeDetector) and resolved to a concrete profile in GameManager.StartGame.
    public EnumAlgorithmProfile player1AlgorithmProfile = EnumAlgorithmProfile.Standard;
    public EnumAlgorithmProfile player2AlgorithmProfile = EnumAlgorithmProfile.Standard;

    [Header("LLM — Fallbacks (used when per-player keys absent in JSON)")]
    public EnumLlmProvider llmProvider = EnumLlmProvider.Gemini;
    public EnumGeminiModel geminiModel = EnumGeminiModel.Flash20;
    public EnumOllamaModel ollamaModel = EnumOllamaModel.Qwen3_8b;
    public EnumOpenAiModel openAiModel = EnumOpenAiModel.Gpt4oMini;

    [Header("Player 1 — LLM")]
    public EnumLlmProvider player1LlmProvider = EnumLlmProvider.Gemini;
    public EnumGeminiModel player1GeminiModel = EnumGeminiModel.Flash20;
    public EnumOllamaModel player1OllamaModel = EnumOllamaModel.Qwen3_8b;
    public EnumOpenAiModel player1OpenAiModel = EnumOpenAiModel.Gpt4oMini;

    [Header("Player 2 — LLM")]
    public EnumLlmProvider player2LlmProvider = EnumLlmProvider.Gemini;
    public EnumGeminiModel player2GeminiModel = EnumGeminiModel.Flash20;
    public EnumOllamaModel player2OllamaModel = EnumOllamaModel.Qwen3_8b;
    public EnumOpenAiModel player2OpenAiModel = EnumOpenAiModel.Gpt4oMini;

    [Header("ML — Python Inference Server")]
    [Tooltip("Preset for the ML inference server host. Custom keeps the manual mlServerUrl value.")]
    public EnumMlServerPreset mlServerPreset = EnumMlServerPreset.Localhost;
    [Tooltip("HTTP endpoint of the Python ML inference server (FastAPI serve.py). Auto-filled unless the preset is Custom.")]
    public string mlServerUrl = "http://127.0.0.1:8000/predict";

    [Header("Advisor — LLM")]
    [Tooltip("Provider used by the scene LLM Advisor button. Independent from player1/player2 LLM providers.")]
    public EnumLlmProvider llmAdvisorProvider = EnumLlmProvider.Ollama;
    public EnumGeminiModel llmAdvisorGeminiModel = EnumGeminiModel.Flash25Lite;
    public EnumOllamaModel llmAdvisorOllamaModel = EnumOllamaModel.Qwen3_8b;
    public EnumOpenAiModel llmAdvisorOpenAiModel = EnumOpenAiModel.Gpt4oMini;

    [Header("LLM — Shared Settings")]
    [Tooltip("Preset for Ollama API host. Custom keeps the manual ollamaBaseUrl value.")]
    public EnumOllamaEndpointPreset ollamaEndpointPreset = EnumOllamaEndpointPreset.Localhost;
    public string ollamaBaseUrl = "http://127.0.0.1:11434/api/chat";
    [Tooltip("Use the per-provider rules file (LLM_RULES_Gemini.txt / LLM_RULES_Ollama.txt). " +
             "When off, a short built-in fallback prompt is used instead.")]
    public bool llmUseRulesFile = true;
    [Tooltip("How many previous own turns Gemini sees in GAME HISTORY. 0 disables history.")]
    public int geminiHistoryTurnsVisible = 4;
    [Tooltip("How many previous own turns Ollama sees in GAME HISTORY. 0 disables history.")]
    public int ollamaHistoryTurnsVisible = 3;

    [Header("Gemini Generation Config")]
    [Tooltip("0.0 = deterministic, 1.0 = creative")]
    public float geminiTemperature = 0.2f;
    [Tooltip("Max tokens in Gemini response. Thinking models (2.5/3.x) spend output tokens on internal " +
             "reasoning, so a low cap can truncate the answer to empty — keep this generous (2048+).")]
    public int geminiMaxOutputTokens = 2048;

    [Header("OpenAI (ChatGPT) Generation Config")]
    [Tooltip("0.0 = deterministic, 1.0 = creative. Ignored by reasoning models (GPT-5 / o-series), " +
             "which only accept the default temperature.")]
    public float openAiTemperature = 0.2f;
    [Tooltip("Max tokens in the OpenAI response (max_completion_tokens). Reasoning models (GPT-5 / o-series) " +
             "spend output tokens on hidden reasoning, so a low cap can truncate the answer to empty - keep this generous (2048+).")]
    public int openAiMaxOutputTokens = 2048;

    [Header("Telemetry — Remote Log Upload")]
    [Tooltip("Send game results and decision logs to the remote server after each battle. " +
             "Server URL and API key are hardcoded in LogUploader.cs (not in this JSON config).")]
    public bool logUploadEnabled = false;

    [Tooltip("Also upload logs while the benchmark is running. Off by default because benchmark reloads " +
             "the scene right after each match, which can interrupt the upload. Requires logUploadEnabled.")]
    public bool uploadLogsDuringBenchmark = false;

    [Header("Logs Export")]
    public bool enableReadableBattleLogs = true;
    public bool enableDeckbuilderBattleLogs = true;
    public bool enableMatchupStatsLogs = true;
    public bool enableMlDecisionLogs = true;
    public bool enableMlGameResultLogs = true;
    public bool enableLlmPromptLogs = true;
    public bool enableLlmDecisionLogs = true;

    [Header("LLM Rate Limiting")]
    [Tooltip("Auto-apply provider delay: 30RPM=2s, 15RPM=4s, 5RPM=12s, Ollama=0s")]
    public bool llmAutoDelay = true;
    [Tooltip("Manual delay between LLM turns (used only when llmAutoDelay is false)")]
    public float llmTurnDelay = 0f;

    public float EffectiveLlmTurnDelay => GetEffectiveLlmTurnDelay(llmProvider);

    public float GetEffectiveLlmTurnDelay(EnumLlmProvider provider, EnumGeminiModel? modelOverride = null)
    {
        if (!llmAutoDelay) return llmTurnDelay;
        if (provider != EnumLlmProvider.Gemini) return 0f;
        return (modelOverride ?? geminiModel) switch
        {
            EnumGeminiModel.Flash25Lite => 2f,
            EnumGeminiModel.Flash20Lite => 2f,
            EnumGeminiModel.Flash31Lite => 4f,
            EnumGeminiModel.Flash15     => 4f,
            EnumGeminiModel.Gemma4_26b  => 4f,
            EnumGeminiModel.Gemma4_31b  => 4f,
            _                           => 12f,
        };
    }

    [Header("Delays (0 = instant, 1 = default speed)")]
    [Tooltip("Scales all AI and flow delays. 0 = instant, 1 = default speed.")]
    public float aiDelayScale = 1f;

    // Base delay values (scale = 1.0). Multiply by aiDelayScale at runtime.
    private const float BaseKnockoutPromotionDelay   = 2.2f;
    private const float BaseDamageTextDisplayDuration = 1.2f;
    private const float BaseAiSetupDelay             = 1.0f;
    private const float BaseAiPlayCardDelay          = 1.5f;
    private const float BaseAiAttachEnergyDelay      = 1.5f;
    private const float BaseAiAttackDelay            = 1.5f;
    private const float BaseAiEndTurnDelay           = 2.0f;

    public float knockoutPromotionDelay    => BaseKnockoutPromotionDelay    * aiDelayScale;
    public float damageTextDisplayDuration => BaseDamageTextDisplayDuration * aiDelayScale;
    public float aiSetupDelay             => BaseAiSetupDelay              * aiDelayScale;
    public float aiPlayCardDelay          => BaseAiPlayCardDelay           * aiDelayScale;
    public float aiAttachEnergyDelay      => BaseAiAttachEnergyDelay       * aiDelayScale;
    public float aiAttackDelay            => BaseAiAttackDelay             * aiDelayScale;
    public float aiEndTurnDelay           => BaseAiEndTurnDelay            * aiDelayScale;

    // Shortest of the ai* delays — used as the upper bound for short visual tweens
    // (e.g. HP countdown on biggerHpText) so they always finish before the next action.
    public float minAiDelay => Mathf.Min(
        aiSetupDelay,
        aiPlayCardDelay,
        Mathf.Min(aiAttachEnergyDelay, Mathf.Min(aiAttackDelay, aiEndTurnDelay))
    );

    [Header("Attack Animation")]
    public float attackPunchScale = 1.25f;

    // Matches the biggerHpText HP-countdown animation so the punch and the HP drop end together.
    public float attackPunchDuration => Mathf.Max(0.05f, minAiDelay * 0.75f);

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        // NOTE: intentionally NOT DontDestroyOnLoad. Rebuilding fresh on each scene reload
        // is required so BenchmarkRunner can override the matchup per match; values are
        // reloaded from JSON / Inspector here and then overridden in GameManager.StartGame.
        if (loadFromJson) LoadFromJson();
        ValidateValues();
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    private static T Get<T>(JObject obj, string key, T fallback)
    {
        if (!obj.TryGetValue(key, out JToken token) || token.Type == JTokenType.Null)
            return fallback;

        try
        {
            return token.Value<T>();
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[GameRulesConfig] Invalid value for '{key}' ({token}): {e.Message}. Using fallback '{fallback}'.");
            return fallback;
        }
    }

    private static TEnum GetEnum<TEnum>(JObject obj, string key, TEnum fallback) where TEnum : struct
    {
        if (!obj.TryGetValue(key, out JToken token) || token.Type == JTokenType.Null)
            return fallback;

        if (token.Type == JTokenType.String &&
            Enum.TryParse(token.Value<string>(), true, out TEnum parsedFromString))
        {
            return parsedFromString;
        }

        if (token.Type == JTokenType.Integer &&
            Enum.IsDefined(typeof(TEnum), token.Value<int>()))
        {
            return (TEnum)Enum.ToObject(typeof(TEnum), token.Value<int>());
        }

        Debug.LogWarning($"[GameRulesConfig] Invalid enum value for '{key}' ({token}). Using fallback '{fallback}'.");
        return fallback;
    }

    // Force-load values from GameRulesConfig.json regardless of the loadFromJson flag, then validate.
    // Used by the startup menu so it authors from the actual file — round-tripping fields it does not
    // expose (e.g. headlessMode, LLM settings) instead of clobbering them with stale Inspector defaults.
    public void ReloadFromJson()
    {
        LoadFromJson();
        ValidateValues();
    }

    private void LoadFromJson()
    {
        string path = RuntimePaths.ConfigPath("GameRulesConfig.json");
        if (!File.Exists(path))
        {
            Debug.LogWarning($"[GameRulesConfig] Nie znaleziono {path} — używam wartości z Inspektora.");
            return;
        }

        try
        {
            JObject obj = JObject.Parse(File.ReadAllText(path), new JsonLoadSettings
            {
                CommentHandling = CommentHandling.Ignore
            });

            headlessMode        = Get(obj, "headlessMode",        headlessMode);
            deckSize            = Get(obj, "deckSize",            deckSize);
            benchSize           = Get(obj, "benchSize",           benchSize);
            starterHandSize     = Get(obj, "starterHandSize",     starterHandSize);
            pointsToWin         = Get(obj, "pointsToWin",         pointsToWin);
            maxTurns            = Get(obj, "maxTurns",            maxTurns);
            poisonDamagePerTurn = Get(obj, "poisonDamagePerTurn", poisonDamagePerTurn);
            burnDamagePerTurn   = Get(obj, "burnDamagePerTurn",   burnDamagePerTurn);

            player1Type         = GetEnum(obj, "player1Type", player1Type);
            player2Type         = GetEnum(obj, "player2Type", player2Type);
            player1AlgorithmProfile = GetEnum(obj, "player1AlgorithmProfile", player1AlgorithmProfile);
            player2AlgorithmProfile = GetEnum(obj, "player2AlgorithmProfile", player2AlgorithmProfile);
            player1DeckName     = Get(obj, "player1DeckName",     player1DeckName);
            player2DeckName     = Get(obj, "player2DeckName",     player2DeckName);
            llmProvider         = GetEnum(obj, "llmProvider", llmProvider);
            player1LlmProvider  = GetEnum(obj, "player1LlmProvider", llmProvider);
            player2LlmProvider  = GetEnum(obj, "player2LlmProvider", llmProvider);
            geminiModel         = GetEnum(obj, "geminiModel",         geminiModel);
            player1GeminiModel  = GetEnum(obj, "player1GeminiModel",  geminiModel);
            player2GeminiModel  = GetEnum(obj, "player2GeminiModel",  geminiModel);
            openAiModel         = GetEnum(obj, "openAiModel",         openAiModel);
            player1OpenAiModel  = GetEnum(obj, "player1OpenAiModel",  openAiModel);
            player2OpenAiModel  = GetEnum(obj, "player2OpenAiModel",  openAiModel);
            mlServerUrl         = Get(obj, "mlServerUrl", mlServerUrl);
            mlServerPreset      = GetEnum(obj, "mlServerPreset", InferMlServerPreset(mlServerUrl));
            ApplyMlServerPreset();
            llmAdvisorProvider  = GetEnum(obj, "llmAdvisorProvider", llmAdvisorProvider);
            llmAdvisorGeminiModel = GetEnum(obj, "llmAdvisorGeminiModel", llmAdvisorGeminiModel);
            llmAdvisorOllamaModel = GetEnum(obj, "llmAdvisorOllamaModel", llmAdvisorOllamaModel);
            llmAdvisorOpenAiModel = GetEnum(obj, "llmAdvisorOpenAiModel", llmAdvisorOpenAiModel);
            ollamaBaseUrl       = Get(obj, "ollamaBaseUrl", ollamaBaseUrl);
            ollamaEndpointPreset = GetEnum(obj, "ollamaEndpointPreset", InferOllamaEndpointPreset(ollamaBaseUrl));
            ApplyOllamaEndpointPreset();
            ollamaModel         = GetEnum(obj, "ollamaModel",         ollamaModel);
            player1OllamaModel  = GetEnum(obj, "player1OllamaModel",  ollamaModel);
            player2OllamaModel  = GetEnum(obj, "player2OllamaModel",  ollamaModel);
            llmUseRulesFile     = Get(obj, "llmUseRulesFile", llmUseRulesFile);
            geminiHistoryTurnsVisible = Get(obj, "geminiHistoryTurnsVisible", geminiHistoryTurnsVisible);
            ollamaHistoryTurnsVisible = Get(obj, "ollamaHistoryTurnsVisible", ollamaHistoryTurnsVisible);
            llmAutoDelay        = Get(obj, "llmAutoDelay", llmAutoDelay);
            llmTurnDelay        = Get(obj, "llmTurnDelay", llmTurnDelay);

            geminiTemperature      = Get(obj, "geminiTemperature",      geminiTemperature);
            geminiMaxOutputTokens  = Get(obj, "geminiMaxOutputTokens",  geminiMaxOutputTokens);
            openAiTemperature      = Get(obj, "openAiTemperature",      openAiTemperature);
            openAiMaxOutputTokens  = Get(obj, "openAiMaxOutputTokens",  openAiMaxOutputTokens);

            logUploadEnabled  = Get(obj, "logUploadEnabled",  logUploadEnabled);
            uploadLogsDuringBenchmark = Get(obj, "uploadLogsDuringBenchmark", uploadLogsDuringBenchmark);

            enableReadableBattleLogs    = Get(obj, "enableReadableBattleLogs",    enableReadableBattleLogs);
            enableDeckbuilderBattleLogs = Get(obj, "enableDeckbuilderBattleLogs", enableDeckbuilderBattleLogs);
            enableMatchupStatsLogs      = Get(obj, "enableMatchupStatsLogs",      enableMatchupStatsLogs);
            enableMlDecisionLogs        = Get(obj, "enableMlDecisionLogs",        enableMlDecisionLogs);
            enableMlGameResultLogs      = Get(obj, "enableMlGameResultLogs",      enableMlGameResultLogs);
            enableLlmPromptLogs         = Get(obj, "enableLlmPromptLogs",         enableLlmPromptLogs);
            enableLlmDecisionLogs       = Get(obj, "enableLlmDecisionLogs",       enableLlmDecisionLogs);

            aiDelayScale        = Get(obj, "aiDelayScale",         aiDelayScale);
            attackPunchScale    = Get(obj, "attackPunchScale",     attackPunchScale);

            Debug.Log($"[GameRulesConfig] Załadowano z JSON: benchSize={benchSize}, deckSize={deckSize}, pointsToWin={pointsToWin}, llmProvider={llmProvider}, aiDelayScale={aiDelayScale}");
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[GameRulesConfig] Błąd parsowania JSON: {e.Message} — używam wartości z Inspektora.");
        }
    }

    private void ValidateValues()
    {
        deckSize = ClampInt(nameof(deckSize), deckSize, 1, 200);
        benchSize = ClampInt(nameof(benchSize), benchSize, 0, 10);
        starterHandSize = ClampInt(nameof(starterHandSize), starterHandSize, 1, deckSize);
        pointsToWin = ClampInt(nameof(pointsToWin), pointsToWin, 1, 20);
        maxTurns = ClampInt(nameof(maxTurns), maxTurns, 1, 500);
        poisonDamagePerTurn = ClampInt(nameof(poisonDamagePerTurn), poisonDamagePerTurn, 0, 500);
        burnDamagePerTurn = ClampInt(nameof(burnDamagePerTurn), burnDamagePerTurn, 0, 500);

        geminiTemperature = ClampFloat(nameof(geminiTemperature), geminiTemperature, 0f, 2f);
        geminiMaxOutputTokens = ClampInt(nameof(geminiMaxOutputTokens), geminiMaxOutputTokens, 1, 8192);
        openAiTemperature = ClampFloat(nameof(openAiTemperature), openAiTemperature, 0f, 2f);
        openAiMaxOutputTokens = ClampInt(nameof(openAiMaxOutputTokens), openAiMaxOutputTokens, 1, 16384);
        geminiHistoryTurnsVisible = ClampInt(nameof(geminiHistoryTurnsVisible), geminiHistoryTurnsVisible, 0, 30);
        ollamaHistoryTurnsVisible = ClampInt(nameof(ollamaHistoryTurnsVisible), ollamaHistoryTurnsVisible, 0, 30);
        llmTurnDelay = ClampFloat(nameof(llmTurnDelay), llmTurnDelay, 0f, 3600f);
        aiDelayScale = ClampFloat(nameof(aiDelayScale), aiDelayScale, 0f, 20f);
        attackPunchScale = ClampFloat(nameof(attackPunchScale), attackPunchScale, 0.1f, 10f);

        player1DeckName = ValidateRequiredString(nameof(player1DeckName), player1DeckName, "Venusaur Butterfree");
        player2DeckName = ValidateRequiredString(nameof(player2DeckName), player2DeckName, "Venusaur Butterfree");
        ApplyMlServerPreset();
        mlServerUrl = ValidateRequiredString(nameof(mlServerUrl), mlServerUrl, "http://127.0.0.1:8000/predict");
        ApplyOllamaEndpointPreset();
        ollamaBaseUrl = ValidateRequiredString(nameof(ollamaBaseUrl), ollamaBaseUrl, "http://127.0.0.1:11434/api/chat");
    }

    private void OnValidate()
    {
        ApplyMlServerPreset();
        ApplyOllamaEndpointPreset();
    }

    private void ApplyMlServerPreset()
    {
        if (mlServerPreset == EnumMlServerPreset.Custom) return;
        mlServerUrl = GetMlServerUrl(mlServerPreset);
    }

    private static string GetMlServerUrl(EnumMlServerPreset preset)
    {
        return preset switch
        {
            EnumMlServerPreset.Localhost => "http://127.0.0.1:8000/predict",
            _ => "http://127.0.0.1:8000/predict",
        };
    }

    private static EnumMlServerPreset InferMlServerPreset(string url)
    {
        string normalized = (url ?? "").Trim();
        return normalized switch
        {
            "http://127.0.0.1:8000/predict" => EnumMlServerPreset.Localhost,
            "http://localhost:8000/predict" => EnumMlServerPreset.Localhost,
            _ => EnumMlServerPreset.Custom,
        };
    }

    private void ApplyOllamaEndpointPreset()
    {
        if (ollamaEndpointPreset == EnumOllamaEndpointPreset.Custom) return;
        ollamaBaseUrl = GetOllamaBaseUrl(ollamaEndpointPreset);
    }

    private static string GetOllamaBaseUrl(EnumOllamaEndpointPreset preset)
    {
        return preset switch
        {
            EnumOllamaEndpointPreset.Localhost => "http://127.0.0.1:11434/api/chat",
            _ => "http://127.0.0.1:11434/api/chat",
        };
    }

    private static EnumOllamaEndpointPreset InferOllamaEndpointPreset(string url)
    {
        string normalized = (url ?? "").Trim();
        return normalized switch
        {
            "http://127.0.0.1:11434/api/chat" => EnumOllamaEndpointPreset.Localhost,
            "http://localhost:11434/api/chat" => EnumOllamaEndpointPreset.Localhost,
            _ => EnumOllamaEndpointPreset.Custom,
        };
    }

    private static int ClampInt(string key, int value, int min, int max)
    {
        int clamped = Mathf.Clamp(value, min, max);
        if (clamped != value)
            Debug.LogWarning($"[GameRulesConfig] '{key}'={value} is outside {min}-{max}. Clamped to {clamped}.");
        return clamped;
    }

    private static float ClampFloat(string key, float value, float min, float max)
    {
        if (float.IsNaN(value) || float.IsInfinity(value))
        {
            Debug.LogWarning($"[GameRulesConfig] '{key}' is not a finite number. Using {min}.");
            return min;
        }

        float clamped = Mathf.Clamp(value, min, max);
        if (!Mathf.Approximately(clamped, value))
            Debug.LogWarning($"[GameRulesConfig] '{key}'={value} is outside {min}-{max}. Clamped to {clamped}.");
        return clamped;
    }

    private static string ValidateRequiredString(string key, string value, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(value))
            return value.Trim();

        Debug.LogWarning($"[GameRulesConfig] '{key}' is empty. Using '{fallback}'.");
        return fallback;
    }

    // =========================================================================
    // Persistence — write current values back to StreamingAssets/GameRulesConfig.json.
    // Symmetric with LoadFromJson(): same keys, enums serialized as strings (the loader
    // parses both strings and ints via GetEnum). Used by the startup menu so UI choices
    // survive into the gameplay scene, where a fresh GameRulesConfig reloads this file.
    // =========================================================================
    public void SaveToJson()
    {
        ValidateValues();

        JObject obj = new JObject
        {
            ["headlessMode"]        = headlessMode,
            ["deckSize"]            = deckSize,
            ["benchSize"]           = benchSize,
            ["starterHandSize"]     = starterHandSize,
            ["pointsToWin"]         = pointsToWin,
            ["maxTurns"]            = maxTurns,
            ["poisonDamagePerTurn"] = poisonDamagePerTurn,
            ["burnDamagePerTurn"]   = burnDamagePerTurn,

            ["player1Type"]     = player1Type.ToString(),
            ["player2Type"]     = player2Type.ToString(),
            ["player1AlgorithmProfile"] = player1AlgorithmProfile.ToString(),
            ["player2AlgorithmProfile"] = player2AlgorithmProfile.ToString(),
            ["player1DeckName"] = player1DeckName,
            ["player2DeckName"] = player2DeckName,

            ["llmProvider"]        = llmProvider.ToString(),
            ["player1LlmProvider"] = player1LlmProvider.ToString(),
            ["player2LlmProvider"] = player2LlmProvider.ToString(),
            ["geminiModel"]        = geminiModel.ToString(),
            ["player1GeminiModel"] = player1GeminiModel.ToString(),
            ["player2GeminiModel"] = player2GeminiModel.ToString(),
            ["ollamaModel"]        = ollamaModel.ToString(),
            ["player1OllamaModel"] = player1OllamaModel.ToString(),
            ["player2OllamaModel"] = player2OllamaModel.ToString(),
            ["openAiModel"]        = openAiModel.ToString(),
            ["player1OpenAiModel"] = player1OpenAiModel.ToString(),
            ["player2OpenAiModel"] = player2OpenAiModel.ToString(),

            ["mlServerPreset"] = mlServerPreset.ToString(),
            ["mlServerUrl"]    = mlServerUrl,

            ["llmAdvisorProvider"]    = llmAdvisorProvider.ToString(),
            ["llmAdvisorGeminiModel"] = llmAdvisorGeminiModel.ToString(),
            ["llmAdvisorOllamaModel"] = llmAdvisorOllamaModel.ToString(),
            ["llmAdvisorOpenAiModel"] = llmAdvisorOpenAiModel.ToString(),

            ["ollamaEndpointPreset"]      = ollamaEndpointPreset.ToString(),
            ["ollamaBaseUrl"]             = ollamaBaseUrl,
            ["llmUseRulesFile"]           = llmUseRulesFile,
            ["geminiHistoryTurnsVisible"] = geminiHistoryTurnsVisible,
            ["ollamaHistoryTurnsVisible"] = ollamaHistoryTurnsVisible,
            ["llmAutoDelay"]              = llmAutoDelay,
            ["llmTurnDelay"]              = llmTurnDelay,

            ["geminiTemperature"]     = geminiTemperature,
            ["geminiMaxOutputTokens"] = geminiMaxOutputTokens,
            ["openAiTemperature"]     = openAiTemperature,
            ["openAiMaxOutputTokens"] = openAiMaxOutputTokens,

            ["logUploadEnabled"] = logUploadEnabled,
            ["uploadLogsDuringBenchmark"] = uploadLogsDuringBenchmark,

            ["enableReadableBattleLogs"]    = enableReadableBattleLogs,
            ["enableDeckbuilderBattleLogs"] = enableDeckbuilderBattleLogs,
            ["enableMatchupStatsLogs"]      = enableMatchupStatsLogs,
            ["enableMlDecisionLogs"]        = enableMlDecisionLogs,
            ["enableMlGameResultLogs"]      = enableMlGameResultLogs,
            ["enableLlmPromptLogs"]         = enableLlmPromptLogs,
            ["enableLlmDecisionLogs"]       = enableLlmDecisionLogs,

            ["aiDelayScale"]     = aiDelayScale,
            ["attackPunchScale"] = attackPunchScale,
        };

        try
        {
            string path = RuntimePaths.ConfigPath("GameRulesConfig.json");
            File.WriteAllText(path, obj.ToString(Formatting.Indented));
            Debug.Log($"[GameRulesConfig] Zapisano do {path}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[GameRulesConfig] Nie udało się zapisać JSON: {e.Message}");
        }
    }
}
