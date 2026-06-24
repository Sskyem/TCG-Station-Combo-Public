using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using UnityEngine;

/// <summary>
/// Per-decision JSONL logger for ML behavioral-cloning datasets.
/// One JSONL line per trainable player decision. AlgorithmBrain supplies heuristic scores directly;
/// brains executing shared GameAction objects (LLM/ML) are logged against the legal candidate set.
/// Each line contains: game_id, turn, player_id, decision category, full GameStateSnapshot, all candidate scores, chosen label.
///
/// Tier 1 of the two-layer pipeline described in docs/ML_PIPELINE.md.
/// Python feature_extractor.py consumes these JSONL files later.
/// </summary>
public class DecisionLogger : MonoBehaviour
{
    public static DecisionLogger Instance { get; private set; }

    /// <summary>Absolute path of the decision file for the current game (null before BeginGame).</summary>
    public string CurrentFilePath => filePath;

    [Header("Configuration")]
    [Tooltip("Set false to disable decision logging entirely (e.g. during interactive play).")]
    public bool enabled_logging = true;

    private string gameId;
    private string filePath;
    private StreamWriter writer;
    private bool active;
    private int sequenceWithinGame;

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
        CloseWriter();
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    public void BeginGame(string id)
    {
        enabled_logging = GameRulesConfig.Instance == null || GameRulesConfig.Instance.enableMlDecisionLogs;
        if (!enabled_logging) return;

        gameId = id ?? GameManager.CreateBattleId("game");
        sequenceWithinGame = 0;

        string dir = GetExportDirectory();
        filePath = Path.Combine(dir, $"{gameId}_decisions.jsonl");

        try
        {
            writer = new StreamWriter(filePath, append: false, Encoding.UTF8);
            active = true;
            Debug.Log($"[DecisionLogger] BeginGame: {filePath}");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[DecisionLogger] Failed to open {filePath}: {ex.Message}");
            active = false;
        }
    }

    /// <summary>
    /// Record one decision group (e.g. all PlayBasic candidates scored at this point in the turn).
    /// Capture the snapshot BEFORE the chosen action is executed.
    /// </summary>
    public void LogDecision(
        string category,
        int turn,
        int playerId,
        GameStateSnapshot snapshot,
        IReadOnlyList<ScoreEntry> scores,
        string chosenLabel,
        int chosenTargetInstanceId = -1)
    {
        if (GameRulesConfig.Instance != null && !GameRulesConfig.Instance.enableMlDecisionLogs) return;
        if (!active || writer == null) return;

        var record = new DecisionRecord
        {
            game_id = gameId,
            seq = sequenceWithinGame++,
            turn = turn,
            player_id = playerId,
            brain_type = BrainTypeForPlayer(playerId),
            category = category,
            chosen_label = chosenLabel,
            chosen_target_instance_id = chosenTargetInstanceId,
            scores = scores != null ? new List<ScoreEntry>(scores) : new List<ScoreEntry>(),
            snapshot = snapshot,
            timestamp_unix_ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

        try
        {
            string line = JsonConvert.SerializeObject(record, JsonSettings);
            writer.WriteLine(line);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[DecisionLogger] Failed to serialize decision: {ex.Message}");
        }
    }

    private static string BrainTypeForPlayer(int playerId)
    {
        PlayerManager pm = PlayerManager.Instance;
        PlayerController player = playerId == 1 ? pm?.player1 : playerId == 2 ? pm?.player2 : null;
        return player?.playerType.ToString();
    }

    /// <summary>
    /// Log a concrete action selected by a non-Algorithm brain in the same BC schema used by
    /// AlgorithmBrain: pre-action snapshot, same-category legal candidates, and chosen target id.
    /// Returns false when the planned action is no longer legal and therefore must not become a
    /// training label.
    /// </summary>
    public bool LogGameActionChoice(
        PlayerController player,
        PlayerManager playerManager,
        GameAction chosenAction)
    {
        if (GameRulesConfig.Instance != null && !GameRulesConfig.Instance.enableMlDecisionLogs) return false;
        if (!active || writer == null || player == null || playerManager == null || chosenAction == null) return false;
        if (TurnManager.Instance == null || TurnManager.Instance.activePlayerId == 0) return false;
        if (player.brain is AlgorithmBrain) return false; // AlgorithmBrain already logs scored decisions.

        PlayerController opponent = playerManager.player1 == player
            ? playerManager.player2
            : playerManager.player1;
        if (opponent == null) return false;

        List<GameAction> legal = LegalActionGenerator.Generate(
                player,
                opponent,
                playerManager,
                includeFutureTurnActions: false)
            .FindAll(action => action.type == chosenAction.type);

        GameAction matched = legal.Find(action => ActionsMatch(action, chosenAction));
        if (matched == null)
        {
            Debug.LogWarning(
                $"[DecisionLogger] Skipping non-legal planned action from {player.playerType}: {chosenAction}");
            return false;
        }

        int turn = TurnManager.Instance != null ? TurnManager.Instance.turnCounter : 0;
        GameStateSnapshot snapshot = GameStateSnapshot.Create(player, opponent, turn, player.playerId);
        var entries = new List<ScoreEntry>(legal.Count);
        foreach (GameAction candidate in legal)
        {
            entries.Add(new ScoreEntry(
                TrainingLabel(candidate, player),
                0,
                false,
                new List<string> { $"source:{player.playerType}" },
                TrainingTargetInstanceId(candidate, player)));
        }

        string chosenLabel = TrainingLabel(matched, player);
        int chosenTargetId = TrainingTargetInstanceId(matched, player);
        LogDecision(
            TrainingCategory(matched),
            turn,
            player.playerId,
            snapshot,
            entries,
            chosenLabel,
            chosenTargetId);
        return true;
    }

    private static bool ActionsMatch(GameAction left, GameAction right)
    {
        if (left == null || right == null || left.type != right.type) return false;
        return left.type switch
        {
            GameActionType.PlayBasicPokemon => left.card == right.card,
            GameActionType.Evolve => left.card == right.card && left.target == right.target,
            GameActionType.AttachEnergy => left.target == right.target,
            GameActionType.Attack => left.attackIndex == right.attackIndex,
            GameActionType.Retreat => left.target == right.target,
            GameActionType.PlayTrainer => left.card == right.card,
            GameActionType.EndTurn => true,
            _ => false,
        };
    }

    private static string TrainingCategory(GameAction action)
    {
        return action.type switch
        {
            GameActionType.PlayBasicPokemon => "PlayBasic",
            GameActionType.Evolve => "Evolve",
            GameActionType.AttachEnergy => "AttachEnergy",
            GameActionType.Attack => "Attack",
            GameActionType.Retreat => "Retreat",
            GameActionType.PlayTrainer => "PlayTrainer",
            GameActionType.EndTurn => "EndTurn",
            _ => "Other",
        };
    }

    private static string TrainingLabel(GameAction action, PlayerController player)
    {
        string name = action.card?.baseData?.cardName ?? "?";
        return action.type switch
        {
            GameActionType.PlayBasicPokemon => $"PlayBasic({name})",
            GameActionType.Evolve => $"Evolve(into {name})",
            GameActionType.AttachEnergy => $"AttachEnergy(to {action.target?.baseData?.cardName ?? "?"})",
            GameActionType.Attack => AttackTrainingLabel(action.attackIndex, player),
            GameActionType.Retreat => $"Retreat(to {action.target?.baseData?.cardName ?? "?"})",
            GameActionType.PlayTrainer => $"PlayTrainer({name})",
            GameActionType.EndTurn => "EndTurn",
            _ => action.ToString(),
        };
    }

    private static string AttackTrainingLabel(int attackIndex, PlayerController player)
    {
        if (player?.activePokemon?.baseData is PokemonData data &&
            data.attacks != null &&
            attackIndex >= 0 &&
            attackIndex < data.attacks.Count)
        {
            return $"Attack[{attackIndex}] {data.attacks[attackIndex]?.attackName ?? "?"}";
        }
        return $"Attack[{attackIndex}]";
    }

    private static int TrainingTargetInstanceId(GameAction action, PlayerController player)
    {
        return action.type switch
        {
            GameActionType.PlayBasicPokemon => action.card?.instanceId ?? -1,
            GameActionType.Evolve => action.target?.instanceId ?? -1,
            GameActionType.AttachEnergy => action.target?.instanceId ?? -1,
            GameActionType.Attack => player?.activePokemon?.instanceId ?? -1,
            GameActionType.Retreat => action.target?.instanceId ?? -1,
            GameActionType.PlayTrainer => action.card?.instanceId ?? -1,
            _ => -1,
        };
    }

    private void HandleGameOver(PlayerController winner)
    {
        LogGameEnd(winner);
        CloseWriter();
    }

    /// <summary>
    /// Write a terminal "GameEnd" record capturing the FINAL state and winner. Per-turn
    /// decision snapshots are taken BEFORE the chosen action, so the deciding KO (the 4th
    /// point that ends the game) never appears in any decision snapshot — the game stops
    /// before the next one. This record fills that gap so the winning state is recorded.
    /// It is tagged category "GameEnd" and excluded from BC training on the Python side
    /// (logs.NON_TRAINABLE_CATEGORIES); it exists for analysis / Replay / winner recovery.
    /// </summary>
    private void LogGameEnd(PlayerController winner)
    {
        if (GameRulesConfig.Instance != null && !GameRulesConfig.Instance.enableMlDecisionLogs) return;
        if (!active || writer == null) return;

        try
        {
            var pm = PlayerManager.Instance;
            if (pm == null || pm.player1 == null || pm.player2 == null) return;

            string winnerLabel = winner == null ? "Draw"
                : winner == pm.player1 ? "A"
                : winner == pm.player2 ? "B"
                : "Unknown";
            int winnerId = winner == null ? 0 : winner.playerId;
            int turn = TurnManager.Instance != null ? TurnManager.Instance.turnCounter : 0;

            // Final board state AFTER the deciding KO (scores reflect the winning point).
            GameStateSnapshot finalSnapshot =
                GameStateSnapshot.Create(pm.player1, pm.player2, turn, pm.player1.playerId);

            var record = new DecisionRecord
            {
                game_id = gameId,
                seq = sequenceWithinGame++,
                turn = turn,
                player_id = winnerId,
                category = "GameEnd",
                chosen_label = winnerLabel,
                scores = new List<ScoreEntry>
                {
                    new ScoreEntry(
                        $"winner:{winnerLabel}",
                        winner != null ? winner.score : 0,
                        false,
                        new List<string> { $"score_a:{pm.player1.score}", $"score_b:{pm.player2.score}" }),
                },
                snapshot = finalSnapshot,
                timestamp_unix_ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };

            writer.WriteLine(JsonConvert.SerializeObject(record, JsonSettings));
            Debug.Log($"[DecisionLogger] GameEnd: winner={winnerLabel} score {pm.player1.score}-{pm.player2.score} turn {turn}");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[DecisionLogger] Failed to write GameEnd: {ex.Message}");
        }
    }

    private void CloseWriter()
    {
        if (writer == null) return;
        try
        {
            writer.Flush();
            writer.Dispose();
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[DecisionLogger] Close error: {ex.Message}");
        }
        writer = null;
        active = false;
    }

    /// <summary>
    /// Relative folder (under Logs Export/ML/Decisions/) for the current game's logs, so training can
    /// include/exclude contexts without mixing them:
    ///   benchmark/                       — Algorithm-vs-Algorithm benchmark runs (fast, mass scale)
    ///   interactive/&lt;type_vs_type&gt;/      — everything else, bucketed by the player-type matchup
    ///                                       (order-independent: algorithm_vs_ml == ml_vs_algorithm)
    /// Decisions can come from Algorithm, LLM, ML, or another brain integrated with the shared
    /// GameAction pipeline. Matchup folders keep those data distributions independently selectable.
    /// Legacy files written before this split stay in the Decisions/ root; SMB logs go to received/.
    /// </summary>
    public static string DecisionSourceFolder()
    {
        if (BenchmarkRunner.Instance != null && BenchmarkRunner.Instance.runEnabled)
            return "benchmark";

        GameRulesConfig cfg = GameRulesConfig.Instance;
        string a = cfg != null ? cfg.player1Type.ToString() : "Unknown";
        string b = cfg != null ? cfg.player2Type.ToString() : "Unknown";
        bool aFirst = string.CompareOrdinal(a, b) <= 0;
        string matchup = ((aFirst ? a : b) + "_vs_" + (aFirst ? b : a)).ToLowerInvariant();
        return Path.Combine("interactive", matchup);
    }

    private string GetExportDirectory()
    {
        string dir = Path.Combine(RuntimePaths.MlLogsRoot(), "Decisions", DecisionSourceFolder());
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ── Serialized record types ──────────────────────────────────────────────

    [Serializable]
    public class DecisionRecord
    {
        public string game_id;
        public int seq;
        public int turn;
        public int player_id;
        public string brain_type;
        public string category;
        public string chosen_label;
        public int chosen_target_instance_id = -1;
        public long timestamp_unix_ms;
        public List<ScoreEntry> scores;
        public GameStateSnapshot snapshot;
    }

    [Serializable]
    public class ScoreEntry
    {
        public string label;
        public int score;
        public bool blocked;
        public List<string> reasons;

        // Stable board identity of the candidate's target Pokemon (CardInstance.instanceId),
        // matching GameStateSnapshot pokemon InstanceId. -1 when the candidate has no board target.
        // Lets the Python action encoder disambiguate same-name candidates (e.g. active vs bench)
        // and attach the target's live-state features. Required for AttachEnergy/Retreat/Evolve quality.
        public int target_instance_id = -1;

        public ScoreEntry() { }

        public ScoreEntry(string label, int score, bool blocked, List<string> reasons, int targetInstanceId = -1)
        {
            this.label = label;
            this.score = score;
            this.blocked = blocked;
            this.reasons = reasons ?? new List<string>();
            this.target_instance_id = targetInstanceId;
        }
    }
}
