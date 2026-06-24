using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using UnityEngine;

/// <summary>
/// Per-game JSONL logger for the ML pipeline.
/// Writes one line per finished game with high-level result metadata: game_id, winner, turn count,
/// deck ids, brain types, start/end timestamps. Used to join with DecisionLogger output and
/// later weight decisions by win/loss when graduating from pure BC to BC+reward.
///
/// Output: Logs Export/ML/games.jsonl (append-only).
/// </summary>
public class GameResultLogger : MonoBehaviour
{
    public static GameResultLogger Instance { get; private set; }

    public GameResultRecord LastRecord { get; private set; }

    [Header("Configuration")]
    public bool enabled_logging = true;

    private string gameId;
    private long startedAtUnixMs;
    private string deckIdA;
    private string deckIdB;
    private string brainA;
    private string brainB;
    private bool active;
    private bool wrote;

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

    private void OnEnable()
    {
        BattleManager.OnGameOver -= HandleGameOver;
        BattleManager.OnGameOver += HandleGameOver;
    }

    private void OnDisable()
    {
        BattleManager.OnGameOver -= HandleGameOver;
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    public void BeginGame(string id, string deckA = null, string deckB = null, string brainAName = null, string brainBName = null)
    {
        enabled_logging = GameRulesConfig.Instance == null || GameRulesConfig.Instance.enableMlGameResultLogs;
        if (!enabled_logging) return;

        gameId = id ?? GameManager.CreateBattleId("game");
        startedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        deckIdA = deckA;
        deckIdB = deckB;
        brainA = brainAName;
        brainB = brainBName;
        active = true;
        wrote = false;
        Debug.Log($"[GameResultLogger] BeginGame: {gameId} ({brainA} vs {brainB})");
    }

    private void HandleGameOver(PlayerController winner)
    {
        if (GameRulesConfig.Instance != null && !GameRulesConfig.Instance.enableMlGameResultLogs) return;
        if (!active || wrote) return;

        var pm = PlayerManager.Instance;
        string winnerLabel = winner == null ? "Draw"
            : winner == pm?.player1 ? "A"
            : winner == pm?.player2 ? "B"
            : "Unknown";

        WriteRecord(winnerLabel, "game_over");
    }

    /// <summary>
    /// Adjudicate and write the result for a game that ended WITHOUT a natural OnGameOver —
    /// e.g. the benchmark watchdog timed the match out. The winner is decided by current score
    /// (higher score wins; a tie is a Draw). Without this, timed-out games leave a decisions
    /// file with no games.jsonl row, so winner-based filtering silently drops them and only the
    /// minority of games that reach a natural win condition ever get a winner.
    /// </summary>
    public void WriteByScore(string endReason)
    {
        if (GameRulesConfig.Instance != null && !GameRulesConfig.Instance.enableMlGameResultLogs) return;
        if (!active || wrote) return;

        var pm = PlayerManager.Instance;
        int a = pm?.player1?.score ?? 0;
        int b = pm?.player2?.score ?? 0;
        string winnerLabel = a > b ? "A" : b > a ? "B" : "Draw";

        WriteRecord(winnerLabel, string.IsNullOrEmpty(endReason) ? "timeout" : endReason);
    }

    private void WriteRecord(string winnerLabel, string endReason)
    {
        wrote = true;

        var pm = PlayerManager.Instance;
        int turns = TurnManager.Instance != null ? TurnManager.Instance.turnCounter : 0;
        int scoreA = pm?.player1?.score ?? 0;
        int scoreB = pm?.player2?.score ?? 0;
        int cardsDrawnA = pm?.player1?.cardsDrawnThisGame ?? 0;
        int cardsDrawnB = pm?.player2?.cardsDrawnThisGame ?? 0;
        bool isBenchmark = BenchmarkRunner.Instance != null && BenchmarkRunner.Instance.runEnabled;

        long endedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var record = new GameResultRecord
        {
            game_id = gameId,
            deck_a = deckIdA,
            deck_b = deckIdB,
            brain_a = brainA,
            brain_b = brainB,
            winner = winnerLabel,
            end_reason = endReason,
            turns = turns,
            score_a = scoreA,
            score_b = scoreB,
            cards_drawn_a = cardsDrawnA,
            cards_drawn_b = cardsDrawnB,
            is_benchmark = isBenchmark,
            started_at_unix_ms = startedAtUnixMs,
            ended_at_unix_ms = endedAtUnixMs,
            duration_ms = endedAtUnixMs - startedAtUnixMs,
        };

        LastRecord = record;

        try
        {
            string dir = GetExportDirectory();
            string path = Path.Combine(dir, "games.jsonl");
            using var writer = new StreamWriter(path, append: true, Encoding.UTF8);
            writer.WriteLine(JsonConvert.SerializeObject(record, JsonSettings));
            Debug.Log($"[GameResultLogger] Wrote game result for {gameId} (winner: {winnerLabel}, reason: {endReason}, turns: {turns})");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[GameResultLogger] Failed to write game result: {ex.Message}");
        }

        active = false;
    }

    private string GetExportDirectory()
    {
        string dir = RuntimePaths.MlLogsRoot();
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Serializable]
    public class GameResultRecord
    {
        public string game_id;
        public string deck_a;
        public string deck_b;
        public string brain_a;
        public string brain_b;
        public string winner;
        public string end_reason;
        public int turns;
        public int score_a;
        public int score_b;
        public int cards_drawn_a;
        public int cards_drawn_b;
        public bool is_benchmark;
        public long started_at_unix_ms;
        public long ended_at_unix_ms;
        public long duration_ms;
    }
}
