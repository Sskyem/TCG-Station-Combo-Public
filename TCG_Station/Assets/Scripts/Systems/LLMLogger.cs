using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using UnityEngine;

/// <summary>
/// Per-turn JSONL logger for LLM players (Gemini sequence mode and Ollama step mode).
/// Writes one line per completed LLM turn with the actions the model actually chose, its parsed
/// THINKING text, provider/model, and turn metadata. Complements DecisionLogger (which only logs
/// the heuristic AlgorithmBrain scorer): this lets post-hoc analysis line up LLM decisions against
/// Algorithm decisions in the same game_id, joined via GameResultLogger output.
///
/// Output: Logs Export/ML/llm_decisions.jsonl (append-only, shared across the run).
/// </summary>
public class LLMLogger : MonoBehaviour
{
    public static LLMLogger Instance { get; private set; }

    private string gameId;
    private int sequenceWithinGame;
    private bool active;

    private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
    {
        NullValueHandling = NullValueHandling.Ignore,
        Formatting = Formatting.None,
        Converters = { new Newtonsoft.Json.Converters.StringEnumConverter() },
    };

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    public void BeginGame(string id)
    {
        if (GameRulesConfig.Instance != null && !GameRulesConfig.Instance.enableLlmDecisionLogs)
        {
            active = false;
            return;
        }

        gameId = id ?? GameManager.CreateBattleId("game");
        sequenceWithinGame = 0;
        active = true;
        Debug.Log($"[LLMLogger] BeginGame: {gameId}");
    }

    /// <summary>
    /// Record one completed LLM turn. Call after the action sequence has been parsed/executed.
    /// </summary>
    public void LogTurn(
        int turn,
        int playerId,
        string provider,
        string model,
        string mode,
        IReadOnlyList<string> actionsChosen,
        string thinking,
        int legalActionCount)
    {
        if (GameRulesConfig.Instance != null && !GameRulesConfig.Instance.enableLlmDecisionLogs) return;
        if (!active) return;

        var record = new LLMTurnRecord
        {
            game_id = gameId,
            seq = sequenceWithinGame++,
            turn = turn,
            player_id = playerId,
            provider = provider,
            model = model,
            mode = mode,
            actions_chosen = actionsChosen != null ? new List<string>(actionsChosen) : new List<string>(),
            thinking = thinking,
            legal_action_count = legalActionCount,
            timestamp_unix_ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

        try
        {
            string dir = GetExportDirectory();
            string path = Path.Combine(dir, "llm_decisions.jsonl");
            using var writer = new StreamWriter(path, append: true, Encoding.UTF8);
            writer.WriteLine(JsonConvert.SerializeObject(record, JsonSettings));
        }
        catch (Exception ex)
        {
            Debug.LogError($"[LLMLogger] Failed to write LLM turn: {ex.Message}");
        }
    }

    private string GetExportDirectory()
    {
        string dir = RuntimePaths.MlLogsRoot();
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Serializable]
    public class LLMTurnRecord
    {
        public string game_id;
        public int seq;
        public int turn;
        public int player_id;
        public string provider;
        public string model;
        public string mode;
        public List<string> actions_chosen;
        public string thinking;
        public int legal_action_count;
        public long timestamp_unix_ms;
    }
}
