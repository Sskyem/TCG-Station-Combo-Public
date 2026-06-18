using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

/// Persistent matchup statistics across runs.
/// Counts how many times deck A (controlled by brain X) played against deck B (controlled by brain Y),
/// and how many wins each side has. Matchups are stored unordered — A-vs-B with sides swapped is the
/// same matchup with reversed win counts.
///
/// Outputs to "Logs Export/Matchups":
///   - MatchupStats.jsonl — append-only raw battle log, one record per line.
///   - MatchupStats.txt   — human-readable summary regenerated periodically from aggregates.
public static class MatchupStatsLogger
{
    private const string FolderName = "Matchups";
    private const string LegacyJsonFileName = "MatchupStats.json"; // pre-2026-05-31 wrapped format, migrated on first load
    private const string JsonlFileName = "MatchupStats.jsonl";     // append-only raw log (one record per line)
    private const string TxtFileName = "MatchupStats.txt";

    // How often the human-readable summary is regenerated, in battles. The summary
    // rebuild is O(n); doing it every battle was O(n²) over a run. Raw records are
    // appended O(1) every battle, so no data is lost between flushes (CSV-of-record
    // equivalent is the .jsonl). A final flush runs on Application.quitting.
    private const int SummaryFlushEveryBattles = 25;

    // Aggregates are loaded from disk once per play session and then updated in memory.
    // Designed for runs well beyond ~25k battles: per-battle work is O(1), and summary
    // rendering is proportional to matchup count, not raw battle count.
    private static Dictionary<string, Section> _sections;
    private static int _battleCount;
    private static int _sinceSummaryFlush;
    private static bool _quitHookInstalled;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Bootstrap()
    {
        // Reset the cache so a fresh play session re-reads persisted aggregates once.
        _sections = null;
        _battleCount = 0;
        _sinceSummaryFlush = 0;
        BattleManager.OnGameOver -= HandleGameOver;
        BattleManager.OnGameOver += HandleGameOver;
    }

    private static void HandleGameOver(PlayerController winner)
    {
        if (GameRulesConfig.Instance != null && !GameRulesConfig.Instance.enableMatchupStatsLogs) return;

        try
        {
            RecordBattle(winner);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[MatchupStatsLogger] Failed to record battle: {ex}");
        }
    }

    private static void RecordBattle(PlayerController winner)
    {
        PlayerManager pm = PlayerManager.Instance;
        if (pm == null || pm.player1 == null || pm.player2 == null)
        {
            Debug.LogWarning("[MatchupStatsLogger] PlayerManager / players not available — skipping.");
            return;
        }

        BattleRecord record = new BattleRecord
        {
            Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            Turns     = TurnManager.Instance != null ? TurnManager.Instance.turnCounter : 0,
            SideA     = BuildSide(pm.player1, isPlayer1: true),
            SideB     = BuildSide(pm.player2, isPlayer1: false),
            Winner    = winner == null ? "Draw" : (winner == pm.player1 ? "A" : "B"),
        };

        string dir = GetExportDirectory();
        string jsonlPath = Path.Combine(dir, JsonlFileName);
        string txtPath   = Path.Combine(dir, TxtFileName);

        // Load the persisted lifetime aggregates once per session, then update them in memory.
        if (_sections == null)
        {
            LoadHistoryIntoAggregates(dir);
            InstallQuitFlush(dir);
        }

        AppendRecord(jsonlPath, record); // O(1) — no full-file rewrite
        ApplyRecord(record);

        // Rebuild the summary only periodically to keep per-battle cost flat.
        if (++_sinceSummaryFlush >= SummaryFlushEveryBattles)
        {
            WriteSummary(txtPath);
            _sinceSummaryFlush = 0;
        }

        Debug.Log($"[MatchupStatsLogger] Recorded battle: {record.SideA.Label} vs {record.SideB.Label} -> {record.Winner}");
    }

    private static SideInfo BuildSide(PlayerController player, bool isPlayer1)
    {
        string deck = isPlayer1
            ? GameRulesConfig.Instance?.player1DeckName ?? "?"
            : GameRulesConfig.Instance?.player2DeckName ?? "?";

        string brain = DescribeBrain(player, isPlayer1);
        return new SideInfo
        {
            Deck = deck,
            Brain = brain,
            Label = $"{deck} [{brain}]",
        };
    }

    private static string DescribeBrain(PlayerController player, bool isPlayer1)
    {
        if (player.brain == null) return "Unknown";

        switch (player.brain)
        {
            case HumanBrain _: return "Human";
            case AlgorithmBrain _: return "Algorithm";
            case LLMBrain _:
                if (GameRulesConfig.Instance == null) return "LLM";
                EnumLlmProvider provider = isPlayer1
                    ? GameRulesConfig.Instance.player1LlmProvider
                    : GameRulesConfig.Instance.player2LlmProvider;
                string model = provider switch
                {
                    EnumLlmProvider.Ollama => (isPlayer1
                        ? GameRulesConfig.Instance.player1OllamaModel.ToString()
                        : GameRulesConfig.Instance.player2OllamaModel.ToString()),
                    EnumLlmProvider.OpenAI => (isPlayer1
                        ? GameRulesConfig.Instance.player1OpenAiModel.ToString()
                        : GameRulesConfig.Instance.player2OpenAiModel.ToString()),
                    _ => (isPlayer1
                        ? GameRulesConfig.Instance.player1GeminiModel.ToString()
                        : GameRulesConfig.Instance.player2GeminiModel.ToString()),
                };
                return $"LLM-{provider}/{model}";
            default:
                return player.brain.GetType().Name;
        }
    }

    private static string GetExportDirectory()
    {
        string dir = Path.Combine(RuntimePaths.LogsRoot(), FolderName);
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ── JSON persistence ────────────────────────────────────────────────────

    [Serializable]
    private class SideInfo
    {
        public string Deck;
        public string Brain;
        public string Label;
    }

    [Serializable]
    private class BattleRecord
    {
        public string Timestamp;
        public int Turns;
        public SideInfo SideA;
        public SideInfo SideB;
        public string Winner; // "A", "B", or "Draw"
    }

    [Serializable]
    private class BattleRecordList
    {
        public List<BattleRecord> Battles = new List<BattleRecord>();
    }

    /// Loads lifetime aggregates once per session. Reads the append-only .jsonl if
    /// present; otherwise migrates the legacy wrapped .json (one-time) into .jsonl.
    private static void LoadHistoryIntoAggregates(string dir)
    {
        _sections = new Dictionary<string, Section>();
        _battleCount = 0;

        string jsonlPath = Path.Combine(dir, JsonlFileName);
        if (File.Exists(jsonlPath))
        {
            LoadFromJsonlIntoAggregates(jsonlPath);
            return;
        }

        // Migration: legacy single-object array → append-only jsonl.
        string legacyPath = Path.Combine(dir, LegacyJsonFileName);
        if (File.Exists(legacyPath))
        {
            List<BattleRecord> migrated = LoadFromLegacyJson(legacyPath);
            try
            {
                var sb = new StringBuilder();
                foreach (BattleRecord r in migrated)
                    sb.Append(JsonUtility.ToJson(r)).Append('\n');
                File.WriteAllText(jsonlPath, sb.ToString(), Encoding.UTF8);
                Debug.Log($"[MatchupStatsLogger] Migrated {migrated.Count} legacy record(s) to {JsonlFileName}.");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MatchupStatsLogger] Legacy migration write failed ({ex.Message}); continuing in memory.");
            }
            foreach (BattleRecord r in migrated)
                ApplyRecord(r);
        }
    }

    private static void LoadFromJsonlIntoAggregates(string path)
    {
        try
        {
            foreach (string line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                BattleRecord r = JsonUtility.FromJson<BattleRecord>(line);
                if (r != null) ApplyRecord(r);
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[MatchupStatsLogger] Could not read {JsonlFileName} ({ex.Message}); starting fresh.");
        }
    }

    private static List<BattleRecord> LoadFromLegacyJson(string path)
    {
        try
        {
            string json = File.ReadAllText(path, Encoding.UTF8);
            BattleRecordList parsed = JsonUtility.FromJson<BattleRecordList>(json);
            return parsed?.Battles ?? new List<BattleRecord>();
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[MatchupStatsLogger] Could not parse legacy history ({ex.Message}); starting fresh.");
            return new List<BattleRecord>();
        }
    }

    private static void AppendRecord(string jsonlPath, BattleRecord record)
    {
        File.AppendAllText(jsonlPath, JsonUtility.ToJson(record) + "\n", Encoding.UTF8);
    }

    /// Ensure a final summary flush when the run ends (build quit or exiting play mode),
    /// so the last < SummaryFlushEveryBattles battles always land in the .txt.
    private static void InstallQuitFlush(string dir)
    {
        if (_quitHookInstalled) return;
        _quitHookInstalled = true;

        string txtPath = Path.Combine(dir, TxtFileName);
        Application.quitting += () =>
        {
            if (_sections != null && _battleCount > 0)
            {
                try { WriteSummary(txtPath); }
                catch (Exception ex) { Debug.LogError($"[MatchupStatsLogger] Final summary flush failed: {ex}"); }
            }
        };
    }

    // ── Human-readable summary ──────────────────────────────────────────────

    private static void ApplyRecord(BattleRecord r)
    {
        if (r?.SideA == null || r.SideB == null) return;

        // Sort the two sides so the section heading is canonical (e.g. always
        // "Algorithm vs Human", never "Human vs Algorithm").
        bool flip = string.CompareOrdinal(r.SideA.Brain, r.SideB.Brain) > 0;
        string brainLeft  = flip ? r.SideB.Brain : r.SideA.Brain;
        string brainRight = flip ? r.SideA.Brain : r.SideB.Brain;
        string deckLeft   = flip ? r.SideB.Deck  : r.SideA.Deck;
        string deckRight  = flip ? r.SideA.Deck  : r.SideB.Deck;
        // After the side flip, A/B winners must also be re-mapped to Left/Right.
        string winnerLR =
            r.Winner == "Draw" ? "Draw" :
            (r.Winner == "A" && !flip) || (r.Winner == "B" && flip) ? "Left" : "Right";

        string sectionKey = $"{brainLeft} vs {brainRight}";
        if (!_sections.TryGetValue(sectionKey, out Section section))
        {
            section = new Section { BrainLeft = brainLeft, BrainRight = brainRight };
            _sections[sectionKey] = section;
        }

        // Mirror-deck heuristic: if brains are identical (e.g. Algorithm vs Algorithm), we'd
        // otherwise list "Dragapult vs Hydrapple" and "Hydrapple vs Dragapult" as two rows.
        // Normalize by sorting deck names alphabetically when brains are equal.
        if (brainLeft == brainRight && string.CompareOrdinal(deckLeft, deckRight) > 0)
        {
            (deckLeft, deckRight) = (deckRight, deckLeft);
            winnerLR = winnerLR == "Left" ? "Right" : winnerLR == "Right" ? "Left" : "Draw";
        }

        string deckKey = $"{deckLeft}||{deckRight}";
        if (!section.Matchups.TryGetValue(deckKey, out DeckMatchup mu))
        {
            mu = new DeckMatchup { DeckLeft = deckLeft, DeckRight = deckRight };
            section.Matchups[deckKey] = mu;
        }

        mu.Games++;
        mu.TotalTurns += r.Turns;
        if (winnerLR == "Left")  mu.LeftWins++;
        else if (winnerLR == "Right") mu.RightWins++;
        else mu.Draws++;

        _battleCount++;
    }

    private static void WriteSummary(string path)
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine($"# Matchup stats — {_battleCount} battle(s) recorded");
        sb.AppendLine($"# Last update: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();

        // Step 3: render sections.
        foreach (Section section in _sections.Values.OrderBy(s => s.BrainLeft).ThenBy(s => s.BrainRight))
        {
            sb.AppendLine($"[{section.BrainLeft} vs {section.BrainRight}]");
            foreach (DeckMatchup mu in section.Matchups.Values
                         .OrderByDescending(m => m.Games)
                         .ThenBy(m => m.DeckLeft)
                         .ThenBy(m => m.DeckRight))
            {
                float avgTurns = mu.Games > 0 ? (float)mu.TotalTurns / mu.Games : 0f;
                string drawSuffix = mu.Draws > 0 ? $", {mu.Draws} draw(s)" : "";
                sb.AppendLine(
                    $"  {mu.DeckLeft} {mu.LeftWins}-{mu.RightWins} {mu.DeckRight}  " +
                    $"({mu.Games} game{(mu.Games == 1 ? "" : "s")}, avg {avgTurns:F1} turns{drawSuffix})");
            }
            sb.AppendLine();
        }

        // The per-battle raw log lives in MatchupStats.jsonl; the .txt is summary-only.
        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
    }

    private class Section
    {
        public string BrainLeft;
        public string BrainRight;
        public Dictionary<string, DeckMatchup> Matchups = new Dictionary<string, DeckMatchup>();
    }

    private class DeckMatchup
    {
        public string DeckLeft;
        public string DeckRight;
        public int Games;
        public int LeftWins;
        public int RightWins;
        public int Draws;
        public int TotalTurns;
    }
}
