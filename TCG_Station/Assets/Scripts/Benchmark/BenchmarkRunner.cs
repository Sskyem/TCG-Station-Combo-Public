using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Runs a series of AI-vs-AI battles back to back, reloading the active scene
/// between matches so every game starts from a clean state.
///
/// v1 scope: Algorithm vs Algorithm only. Builds a round-robin schedule from a
/// list of deck names and plays a configurable number of games per pairing.
///
/// Inspector usage:
///   - loadFromJson = true  → all settings loaded from StreamingAssets/BenchmarkConfig.json
///                            (Inspector values are overwritten on Awake — use JSON for runs)
///   - loadFromJson = false → Inspector values used directly; JSON is ignored
///                            (quick toggle for debugging without touching any file)
///   - runEnabled = false   → component is present but does nothing; game plays normally
///
/// How it plugs into the existing flow:
///   - This is the only persistent (DontDestroyOnLoad) object. GameManager,
///     PlayerManager and GameRulesConfig are NOT persistent, so a scene reload
///     rebuilds them fresh with valid scene references.
///   - GameManager.StartGame() calls ConfigureNextMatch() before setting up players.
///   - We subscribe to BattleManager.OnGameOver (static event, survives scene reloads)
///     to detect the end of each match.
/// </summary>
public class BenchmarkRunner : MonoBehaviour
{
    public static BenchmarkRunner Instance { get; private set; }

    // ── Config source ──────────────────────────────────────────────────────────
    [Header("Config Source")]
    [Tooltip("ON  = schedule settings are loaded from BenchmarkConfig.json on Awake (read from next to the " +
             "build), so the schedule fields below are inactive and hidden in a collapsed 'Inactive' foldout. " +
             "The participants tool stays active as a bridge to the JSON; runEnabled stays a hard gate.\n" +
             "OFF = the JSON is ignored and the Inspector values below are used directly (quick debugging).")]
    public bool loadFromJson = true;

    // ── Master switch ──────────────────────────────────────────────────────────
    [Header("Benchmark — Master Switch")]
    [Tooltip("false = component is inactive, game plays as a normal single match.\n" +
             "true  = benchmark mode: round-robin schedule runs automatically.")]
    public bool runEnabled = false;

    // ── Participants ───────────────────────────────────────────────────────────
    [Header("Participants")]
    [Tooltip("Deck names to include in the round-robin. Must match 'deckName' in Decks/*.json (case-sensitive).")]
    public List<string> participants = new List<string>();

    [Tooltip("AlgorithmBrain numeric profiles to run. The full round-robin is repeated once per profile " +
             "(homogeneous: both seats use the same profile). Empty = Standard only. " +
             "Valid: Standard, Ramp, TempoAggro, ControlStatus, HealStall.")]
    public List<string> algorithmProfiles = new List<string>();

    [Tooltip("When true, each deck gets a profile from DeckArchetypeDetector and the round-robin is not repeated per algorithmProfiles entry.")]
    public bool autoDetectProfilesByDeck = false;

    // ── Match settings ─────────────────────────────────────────────────────────
    [Header("Match Settings")]
    [Tooltip("How many games to play per unordered pairing (e.g. DeckA vs DeckB).")]
    public int matchesPerPairing = 10;

    [Tooltip("Alternate which deck is Player 1 across a pairing's games to balance first-move advantage.")]
    public bool swapSeats = true;

    [Tooltip("Also schedule each deck against a copy of itself (mirror matches).")]
    public bool includeMirror = false;

    [Tooltip("Shuffle the final match list after building the round-robin. Keeps pairings/counts unchanged.")]
    public bool randomizeScheduleOrder = true;

    // ── Timing & flow ──────────────────────────────────────────────────────────
    [Header("Timing & Flow")]
    [Tooltip("Force aiDelayScale = 0 so AI turns resolve instantly (much faster runs).\n" +
             "When false, aiDelayScale from GameRulesConfig/Inspector is used unchanged.")]
    public bool fastMode = false;

    [Tooltip("Force-advance a match if it runs longer than this many real-time seconds.\n" +
             "Set to 0 to disable the watchdog.")]
    public float matchWatchdogSeconds = 180f;

    [Tooltip("Exit play mode (Editor) or quit the application (build) when the full schedule finishes.")]
    public bool stopOnFinish = true;

    // ── Runtime state (not serialized) ────────────────────────────────────────
    private readonly List<Matchup> schedule = new List<Matchup>();
    private bool scheduleBuilt = false;
    private int currentIndex = 0;
    private bool finished = false;
    private bool currentMatchResolved = false;
    private float matchStartRealtime = 0f;
    private string runId;
    private string resultsDir;
    private readonly List<MatchResult> results = new List<MatchResult>();
    // key = "<profile>\t<deck>" -> [wins, losses, draws, games].
    private readonly Dictionary<string, int[]> standings = new Dictionary<string, int[]>();
    private readonly List<EnumAlgorithmProfile> resolvedProfiles = new List<EnumAlgorithmProfile>();
    private readonly Dictionary<string, EnumAlgorithmProfile> deckProfiles = new Dictionary<string, EnumAlgorithmProfile>();
    // Explicit per-participant profile overrides parsed from object-form participants entries
    // ({ "deck": "...", "profile": "..." }). Keyed by deck name; value is the raw profile string.
    private readonly Dictionary<string, string> participantProfiles = new Dictionary<string, string>();
    private readonly List<string> scheduledDecks = new List<string>();
    private int playedMatches = 0;
    private int drawMatches = 0;
    private int timedOutMatches = 0;
    private float pendingReloadStartRealtime = -1f;
    private float lastMatchSeconds = 0f;
    private float lastReloadSeconds = 0f;
    private float totalMatchSeconds = 0f;
    private float totalReloadSeconds = 0f;
    private float recentMatchSeconds = 0f;
    private float recentReloadSeconds = 0f;
    private int matchTimingSamples = 0;
    private int reloadTimingSamples = 0;
    private int recentMatchTimingSamples = 0;
    private int recentReloadTimingSamples = 0;
    private const string AllSessionsMatchesFileName = "all_sessions_matches.jsonl";
    private const string AllSessionsSummaryFileName = "all_sessions_summary.txt";

    // The summary (.txt) is rebuilt only every SummaryFlushEveryMatches + on finish
    // from incremental standings, so summary writes stay bounded during long runs.
    private const int SummaryFlushEveryMatches = 25;
    private int matchesSinceSummaryFlush = 0;

    private struct Matchup
    {
        public string DeckA; // player 1
        public string DeckB; // player 2
        public EnumAlgorithmProfile ProfileA; // player 1
        public EnumAlgorithmProfile ProfileB; // player 2
    }

    private class MatchResult
    {
        public int MatchNumber;
        public string DeckA;
        public string DeckB;
        public EnumAlgorithmProfile ProfileA;
        public EnumAlgorithmProfile ProfileB;
        public string WinnerDeck; // deck name, or "Draw"
        public int Turns;
        public bool TimedOut;
    }

    [Serializable]
    private class GlobalMatchRecord
    {
        public string RunId;
        public string Timestamp;
        public int MatchNumber;
        public string Profile;
        public string ProfileA;
        public string ProfileB;
        public string DeckA;
        public string DeckB;
        public string WinnerDeck;
        public int Turns;
        public bool TimedOut;
        public bool Headless;
    }

    // =========================================================================
    // Unity lifecycle
    // =========================================================================
    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);

        // Check the Inspector master switch BEFORE loading JSON so that
        // runEnabled=false in the Inspector always wins — JSON cannot re-enable it.
        if (!runEnabled)
        {
            Debug.Log("[BenchmarkRunner] runEnabled = false. Normal single-match play.");
            return;
        }

        if (loadFromJson)
            LoadFromJson();

        if (!runEnabled)
        {
            Debug.Log("[BenchmarkRunner] runEnabled disabled in JSON. Normal single-match play.");
            return;
        }

        runId = $"benchmark_{DateTime.Now:yyyyMMdd_HHmmss}";
        resultsDir = GetResultsDirectory();

        BattleManager.OnGameOver -= HandleGameOver;
        BattleManager.OnGameOver += HandleGameOver;

        int totalMatches = EstimateTotalMatches();
        Debug.Log($"[BenchmarkRunner] Active. runId={runId} | " +
                  $"{participants.Count} decks, {matchesPerPairing} game(s)/pairing → ~{totalMatches} matches total.");

        ApplyBenchmarkLogging();
    }

    // =========================================================================
    // Logging throttle for long runs.
    //
    // Over thousands of matches the dominant per-match cost is the Unity Editor
    // Console: it retains every Debug.Log entry (with a captured stack trace) for
    // the whole session, so its internal store grows unbounded and per-match
    // bookkeeping slows down linearly — exactly the "delay grows over time" symptom.
    // Each scene reload also re-loads the full card library, which logs one info
    // line per card every match.
    //
    // We drop info-level logs at the source (no Console entry, no logMessageReceived,
    // no stack-trace capture) and disable stack traces for the warnings/errors that
    // still pass. Result data is unaffected: the loggers (GameResultLogger,
    // DecisionLogger, MatchupStatsLogger, …) write to files via StreamWriter, not
    // through Debug.Log. Warnings and errors stay visible so real problems surface.
    // =========================================================================
    private LogType previousFilterLogType;
    private bool loggingThrottled;

    private void ApplyBenchmarkLogging()
    {
        previousFilterLogType = Debug.unityLogger.filterLogType;
        Debug.unityLogger.filterLogType = LogType.Warning; // drop Log, keep Warning/Error/Exception/Assert
        loggingThrottled = true;

        Application.SetStackTraceLogType(LogType.Log, StackTraceLogType.None);
        Application.SetStackTraceLogType(LogType.Warning, StackTraceLogType.None);
    }

    private void RestoreLogging()
    {
        if (!loggingThrottled) return;
        Debug.unityLogger.filterLogType = previousFilterLogType;
        loggingThrottled = false;
    }

    private void OnDestroy()
    {
        if (Instance == this)
        {
            BattleManager.OnGameOver -= HandleGameOver;
            RestoreLogging(); // filterLogType is global; don't leak the throttle into the next Play session
            Instance = null;
        }
    }

    // =========================================================================
    // Hook called by GameManager.StartGame() before player setup.
    // Applies the matchup for the current schedule index to GameRulesConfig.
    // =========================================================================
    public void ConfigureNextMatch()
    {
        if (!runEnabled || finished) return;

        RecordPendingReloadTiming();

        if (!scheduleBuilt)
            BuildSchedule();

        if (schedule.Count == 0)
        {
            Debug.LogError("[BenchmarkRunner] Schedule is empty — check participants list. Disabling benchmark.");
            runEnabled = false;
            return;
        }

        if (currentIndex >= schedule.Count)
            return; // FinishRun() handles termination

        Matchup m = schedule[currentIndex];
        GameRulesConfig cfg = GameRulesConfig.Instance;
        if (cfg == null)
        {
            Debug.LogError("[BenchmarkRunner] GameRulesConfig.Instance is null. Cannot configure match.");
            return;
        }

        // Both sides are Algorithm. Profiles can be homogeneous per scheduled run or per deck.
        cfg.player1Type    = EnumPlayerType.Algorithm;
        cfg.player2Type    = EnumPlayerType.Algorithm;
        cfg.player1AlgorithmProfile = m.ProfileA;
        cfg.player2AlgorithmProfile = m.ProfileB;
        cfg.player1DeckName = m.DeckA;
        cfg.player2DeckName = m.DeckB;

        if (fastMode)
            cfg.aiDelayScale = 0f;

        currentMatchResolved = false;
        matchStartRealtime = Time.realtimeSinceStartup;

        Debug.Log($"[BenchmarkRunner] Match {currentIndex + 1}/{schedule.Count}: " +
                  $"{m.DeckA} [{m.ProfileA}] (P1) vs {m.DeckB} [{m.ProfileB}] (P2)");
    }

    // =========================================================================
    // Schedule building (round-robin)
    // =========================================================================
    private void BuildSchedule()
    {
        scheduleBuilt = true;
        schedule.Clear();

        List<string> valid = ValidateParticipants();
        if (valid.Count < 2)
        {
            Debug.LogError($"[BenchmarkRunner] Need at least 2 valid participant decks, got {valid.Count}.");
            return;
        }

        scheduledDecks.Clear();
        scheduledDecks.AddRange(valid);

        resolvedProfiles.Clear();
        resolvedProfiles.AddRange(ResolveProfiles());
        deckProfiles.Clear();

        // Per-deck mode is active when auto-detect is on OR any participant has an explicit profile.
        // In that mode every deck is bound to one resolved profile (override > auto-detect > Standard).
        bool perDeck = UsePerDeckProfiles();
        if (perDeck)
            ResolveDeckProfiles(valid);

        int n = valid.Count;
        if (perDeck)
        {
            for (int i = 0; i < n; i++)
            {
                for (int j = i + 1; j < n; j++)
                    AddPairingGames(valid[i], valid[j], deckProfiles[valid[i]], deckProfiles[valid[j]]);

                if (includeMirror)
                    AddPairingGames(valid[i], valid[i], deckProfiles[valid[i]], deckProfiles[valid[i]]);
            }
        }
        else
        {
            // Repeat the entire round-robin once per profile (homogeneous profile on both seats per match).
            foreach (EnumAlgorithmProfile profile in resolvedProfiles)
            {
                for (int i = 0; i < n; i++)
                {
                    for (int j = i + 1; j < n; j++)
                        AddPairingGames(valid[i], valid[j], profile, profile);

                    if (includeMirror)
                        AddPairingGames(valid[i], valid[i], profile, profile);
                }
            }
        }

        if (randomizeScheduleOrder)
            ShuffleSchedule();

        Debug.Log($"[BenchmarkRunner] Schedule built: {valid.Count} decks × {resolvedProfiles.Count} profile(s) " +
                  $"[{string.Join(", ", resolvedProfiles)}], {matchesPerPairing} game(s)/pairing, " +
                  $"{schedule.Count} matches total, autoDetectProfilesByDeck={autoDetectProfilesByDeck}, " +
                  $"randomizeOrder={randomizeScheduleOrder}.");
    }

    private void ShuffleSchedule()
    {
        int seed = unchecked((int)DateTime.Now.Ticks);
        var rng = new System.Random(seed);
        for (int i = schedule.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (schedule[i], schedule[j]) = (schedule[j], schedule[i]);
        }
        Debug.Log($"[BenchmarkRunner] Schedule order randomized (seed={seed}).");
    }

    // Parses algorithmProfiles into enum values; unknown names are skipped with a warning.
    // Empty / all-invalid → defaults to a single Standard run (backward compatible).
    private List<EnumAlgorithmProfile> ResolveProfiles()
    {
        var result = new List<EnumAlgorithmProfile>();
        foreach (string name in algorithmProfiles)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (System.Enum.TryParse(name.Trim(), ignoreCase: true, out EnumAlgorithmProfile p))
            {
                if (!result.Contains(p)) result.Add(p);
            }
            else
            {
                Debug.LogWarning($"[BenchmarkRunner] Unknown algorithm profile '{name}', skipping.");
            }
        }
        if (result.Count == 0) result.Add(EnumAlgorithmProfile.Standard);
        return result;
    }

    // Per-deck profiles are used when auto-detect is on or any participant pinned an explicit profile.
    private bool UsePerDeckProfiles() => autoDetectProfilesByDeck || participantProfiles.Count > 0;

    // Resolves one deck's profile by the rule the user asked for:
    //   explicit override (incl. "Auto") > auto-detect (when autoDetectProfilesByDeck) > Standard.
    private EnumAlgorithmProfile ResolveDeckProfile(
        string deck,
        Dictionary<string, CardData> cards,
        Dictionary<string, DeckData> deckLibrary)
    {
        if (participantProfiles.TryGetValue(deck, out string ov) && !string.IsNullOrWhiteSpace(ov))
        {
            if (System.Enum.TryParse(ov.Trim(), ignoreCase: true, out EnumAlgorithmProfile p))
                return p == EnumAlgorithmProfile.Auto
                    ? DeckArchetypeDetector.Detect(deck, cards, deckLibrary)
                    : p;
            Debug.LogWarning($"[BenchmarkRunner] Unknown profile '{ov}' for deck '{deck}', applying fallback rule.");
        }

        if (autoDetectProfilesByDeck)
            return DeckArchetypeDetector.Detect(deck, cards, deckLibrary);

        return EnumAlgorithmProfile.Standard;
    }

    private void ResolveDeckProfiles(List<string> decks)
    {
        var cards = GameManager.Instance?.jsonLoader?.cardLibrary;
        var deckLibrary = GameManager.Instance?.jsonLoader?.deckLibrary;
        resolvedProfiles.Clear();

        foreach (string deck in decks)
        {
            EnumAlgorithmProfile profile = ResolveDeckProfile(deck, cards, deckLibrary);
            deckProfiles[deck] = profile;
            if (!resolvedProfiles.Contains(profile))
                resolvedProfiles.Add(profile);
        }

        if (resolvedProfiles.Count == 0)
            resolvedProfiles.Add(EnumAlgorithmProfile.Standard);
    }

    private void AddPairingGames(string deckX, string deckY, EnumAlgorithmProfile profileX, EnumAlgorithmProfile profileY)
    {
        for (int g = 0; g < matchesPerPairing; g++)
        {
            // swapSeats: alternate who is Player 1 to balance first-move advantage.
            bool flip = swapSeats && (g % 2 == 1);
            schedule.Add(new Matchup
            {
                DeckA = flip ? deckY : deckX,
                DeckB = flip ? deckX : deckY,
                ProfileA = flip ? profileY : profileX,
                ProfileB = flip ? profileX : profileY,
            });
        }
    }

    private List<string> ValidateParticipants()
    {
        var deckLibrary = GameManager.Instance?.jsonLoader?.deckLibrary;
        var result = new List<string>();

        foreach (string name in participants)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (deckLibrary != null && !deckLibrary.ContainsKey(name))
            {
                Debug.LogWarning($"[BenchmarkRunner] Deck not found in deckLibrary, skipping: '{name}'");
                continue;
            }
            if (result.Contains(name))
            {
                Debug.LogWarning($"[BenchmarkRunner] Duplicate participant ignored: '{name}'");
                continue;
            }
            result.Add(name);
        }
        return result;
    }

    private int EstimateTotalMatches()
    {
        int n = participants.Count;
        int pairs = n * (n - 1) / 2 + (includeMirror ? n : 0);
        int profileCount = UsePerDeckProfiles() ? 1 : Mathf.Max(1, ResolveProfiles().Count);
        return pairs * matchesPerPairing * profileCount;
    }

    // =========================================================================
    // End-of-match handling
    // =========================================================================
    private void HandleGameOver(PlayerController winner)
    {
        if (!runEnabled || finished || currentMatchResolved) return;
        currentMatchResolved = true;

        RecordResult(winner, timedOut: false);

        currentIndex++;
        if (currentIndex >= schedule.Count)
        {
            StartCoroutine(FinishRunRoutine());
            return;
        }

        StartCoroutine(ReloadForNextMatch());
    }

    private void RecordResult(PlayerController winner, bool timedOut)
    {
        Matchup m = schedule[currentIndex];
        PlayerManager pm = PlayerManager.Instance;
        RecordMatchTiming();

        string winnerDeck = "Draw";
        if (winner != null && pm != null)
            winnerDeck = (winner == pm.player1) ? m.DeckA : m.DeckB;

        int turns = TurnManager.Instance != null ? TurnManager.Instance.turnCounter : 0;

        var r = new MatchResult
        {
            MatchNumber = currentIndex + 1,
            DeckA       = m.DeckA,
            DeckB       = m.DeckB,
            ProfileA    = m.ProfileA,
            ProfileB    = m.ProfileB,
            WinnerDeck  = winnerDeck,
            Turns       = turns,
            TimedOut    = timedOut,
        };
        results.Add(r);
        UpdateStandings(r);
        AppendGlobalMatchRecord(r);

        string outcome = timedOut ? "TIMEOUT" : (winnerDeck == "Draw" ? "Draw" : $"{winnerDeck} won");
        Debug.Log($"[BenchmarkRunner] Match {r.MatchNumber} result: " +
                  $"{m.DeckA} vs {m.DeckB}  →  {outcome} ({turns} turns)");

        // Summary is rebuilt only periodically to keep per-match cost flat.
        if (++matchesSinceSummaryFlush >= SummaryFlushEveryMatches)
        {
            WriteSummaryFile();
            matchesSinceSummaryFlush = 0;
        }
    }

    private IEnumerator ReloadForNextMatch()
    {
        // Let other OnGameOver subscribers (loggers) run this frame first.
        yield return null;

        // Optionally upload this match's logs before reloading. We drive the upload here (instead
        // of letting LogUploader self-start) because this object is persistent: awaiting it keeps
        // the upload alive across the otherwise-immediate scene reload, which would kill a coroutine
        // started on the non-persistent LogUploader object.
        var cfg = GameRulesConfig.Instance;
        if (cfg != null && cfg.logUploadEnabled && cfg.uploadLogsDuringBenchmark && LogUploader.Instance != null)
            yield return LogUploader.Instance.UploadAfterLogging();

        // Reset static ID counters so card/pokemon IDs don't leak across matches.
        CardInstance.ResetIdCounter();
        Pokemon.ResetIdCounter();

        Scene active = SceneManager.GetActiveScene();
        Debug.Log($"[BenchmarkRunner] Reloading '{active.name}' for match {currentIndex + 1}/{schedule.Count}.");
        pendingReloadStartRealtime = Time.realtimeSinceStartup;
        SceneManager.LoadScene(active.name);
    }

    // =========================================================================
    // Watchdog — force-advance if a match hangs (AI exception, stuck coroutine…)
    // =========================================================================
    private void Update()
    {
        if (!runEnabled || finished || currentMatchResolved) return;
        if (matchWatchdogSeconds <= 0f) return;

        BattleManager bm = BattleManager.Instance;
        if (bm == null || bm.isGameOver) return;

        if (Time.realtimeSinceStartup - matchStartRealtime > matchWatchdogSeconds)
        {
            Debug.LogWarning($"[BenchmarkRunner] Match {currentIndex + 1} exceeded watchdog " +
                             $"({matchWatchdogSeconds}s). Forcing advance.");
            currentMatchResolved = true;
            // The watchdog reload never fires BattleManager.OnGameOver, so without this the
            // ML GameResultLogger would write no games.jsonl row for this match. Adjudicate by
            // score so timed-out games still get a winner (tagged end_reason="timeout").
            GameResultLogger.Instance?.WriteByScore("timeout");
            RecordResult(null, timedOut: true);

            currentIndex++;
            if (currentIndex >= schedule.Count)
                FinishRun();
            else
                StartCoroutine(ReloadForNextMatch());
        }
    }

    // =========================================================================
    // Finish
    // =========================================================================
    // Upload the final match's logs (if enabled) before FinishRun quits the app/editor.
    private IEnumerator FinishRunRoutine()
    {
        yield return null; // let loggers finish writing this frame first

        var cfg = GameRulesConfig.Instance;
        if (cfg != null && cfg.logUploadEnabled && cfg.uploadLogsDuringBenchmark && LogUploader.Instance != null)
            yield return LogUploader.Instance.UploadAfterLogging();

        FinishRun();
    }

    private void FinishRun()
    {
        finished = true;
        RestoreLogging(); // re-enable info logs so the completion banner below is visible
        WriteSummaryFile();
        WriteAllSessionsSummaryFile();
        Debug.Log($"<color=green><b>[BenchmarkRunner] Finished {results.Count} matches. " +
                  $"Results → {resultsDir}</b></color>");

        if (!stopOnFinish) return;

#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }

    // =========================================================================
    // Output files
    // =========================================================================
    private void WriteSummaryFile()
    {
        try
        {
            Directory.CreateDirectory(resultsDir);
            WriteSummary(Path.Combine(resultsDir, $"{runId}.txt"));
            ResetRecentTimingWindow();
        }
        catch (Exception ex)
        {
            Debug.LogError($"[BenchmarkRunner] Failed to write summary: {ex}");
        }
    }

    private bool IsHeadlessRun()
    {
        return Application.isBatchMode || (GameRulesConfig.Instance != null && GameRulesConfig.Instance.IsHeadlessMode);
    }

    private void AppendGlobalMatchRecord(MatchResult r)
    {
        try
        {
            Directory.CreateDirectory(resultsDir);
            var record = new GlobalMatchRecord
            {
                RunId = runId,
                Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                MatchNumber = r.MatchNumber,
                Profile = ProfilesLabel(r.ProfileA, r.ProfileB),
                ProfileA = r.ProfileA.ToString(),
                ProfileB = r.ProfileB.ToString(),
                DeckA = r.DeckA,
                DeckB = r.DeckB,
                WinnerDeck = r.WinnerDeck,
                Turns = r.Turns,
                TimedOut = r.TimedOut,
                Headless = IsHeadlessRun(),
            };
            string path = Path.Combine(resultsDir, AllSessionsMatchesFileName);
            File.AppendAllText(path, JsonUtility.ToJson(record) + "\n", Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[BenchmarkRunner] Failed to append all-sessions match record: {ex}");
        }
    }

    private void WriteAllSessionsSummaryFile()
    {
        try
        {
            Directory.CreateDirectory(resultsDir);
            string matchesPath = Path.Combine(resultsDir, AllSessionsMatchesFileName);
            string summaryPath = Path.Combine(resultsDir, AllSessionsSummaryFileName);
            WriteAllSessionsSummary(matchesPath, summaryPath);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[BenchmarkRunner] Failed to write all-sessions summary: {ex}");
        }
    }

    private void RecordMatchTiming()
    {
        if (matchStartRealtime <= 0f) return;

        lastMatchSeconds = Mathf.Max(0f, Time.realtimeSinceStartup - matchStartRealtime);
        totalMatchSeconds += lastMatchSeconds;
        recentMatchSeconds += lastMatchSeconds;
        matchTimingSamples++;
        recentMatchTimingSamples++;
    }

    private void RecordPendingReloadTiming()
    {
        if (pendingReloadStartRealtime <= 0f) return;

        lastReloadSeconds = Mathf.Max(0f, Time.realtimeSinceStartup - pendingReloadStartRealtime);
        totalReloadSeconds += lastReloadSeconds;
        recentReloadSeconds += lastReloadSeconds;
        reloadTimingSamples++;
        recentReloadTimingSamples++;
        pendingReloadStartRealtime = -1f;
    }

    private void ResetRecentTimingWindow()
    {
        recentMatchSeconds = 0f;
        recentReloadSeconds = 0f;
        recentMatchTimingSamples = 0;
        recentReloadTimingSamples = 0;
    }

    private static string StandingKey(EnumAlgorithmProfile profile, string deck) => $"{profile}\t{deck}";
    private static string ProfilesLabel(EnumAlgorithmProfile profileA, EnumAlgorithmProfile profileB) =>
        profileA == profileB ? profileA.ToString() : $"{profileA} vs {profileB}";

    private int[] GetStanding(EnumAlgorithmProfile profile, string deck)
    {
        string key = StandingKey(profile, deck);
        if (!standings.TryGetValue(key, out int[] a))
        {
            a = new int[4];
            standings[key] = a;
        }
        return a;
    }

    private string ProfileLabelForDeck(IEnumerable<EnumAlgorithmProfile> profiles, string deck)
    {
        var playedProfiles = profiles
            .Where(p => standings.TryGetValue(StandingKey(p, deck), out int[] s) && s[3] > 0)
            .Distinct()
            .ToList();

        if (playedProfiles.Count == 1)
            return playedProfiles[0].ToString();
        if (playedProfiles.Count > 1)
            return "Mixed";
        if (deckProfiles.TryGetValue(deck, out EnumAlgorithmProfile detected))
            return detected.ToString();
        return "-";
    }

    private void UpdateStandings(MatchResult r)
    {
        int[] a = GetStanding(r.ProfileA, r.DeckA);
        int[] b = GetStanding(r.ProfileB, r.DeckB);
        a[3]++;
        b[3]++;
        playedMatches++;
        if (r.TimedOut) timedOutMatches++;

        if (r.WinnerDeck == "Draw")
        {
            a[2]++;
            b[2]++;
            drawMatches++;
        }
        else if (r.WinnerDeck == r.DeckA)
        {
            a[0]++;
            b[1]++;
        }
        else
        {
            b[0]++;
            a[1]++;
        }
    }

    private void WriteSummary(string path)
    {
        // Reconstruct the deck/profile axes if BuildSchedule hasn't populated them in this lifetime
        // (e.g. summary written very early): derive them from the standings keys.
        List<EnumAlgorithmProfile> profiles = resolvedProfiles.Count > 0
            ? new List<EnumAlgorithmProfile>(resolvedProfiles)
            : standings.Keys.Select(k => k.Split('\t')[0])
                  .Distinct()
                  .Select(s => System.Enum.TryParse(s, out EnumAlgorithmProfile p) ? p : EnumAlgorithmProfile.Standard)
                  .Distinct().ToList();
        List<string> decks = scheduledDecks.Count > 0
            ? new List<string>(scheduledDecks)
            : standings.Keys.Select(k => k.Split('\t')[1]).Distinct().ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"# Benchmark run: {runId}");
        sb.AppendLine($"# Algorithm vs Algorithm — profileMode: {(UsePerDeckProfiles() ? "per-deck (override > auto-detect > Standard)" : "homogeneous")}");
        sb.AppendLine($"# Headless: {IsHeadlessRun()}");
        int participantCount = decks.Count;
        int pairingCount = participantCount * (participantCount - 1) / 2 + (includeMirror ? participantCount : 0);
        sb.AppendLine($"# Schedule: {participantCount} deck(s), {pairingCount} pairing(s), {matchesPerPairing} match(es)/pairing, {profiles.Count} profile(s), autoDetectProfilesByDeck={autoDetectProfilesByDeck}, randomizeOrder={randomizeScheduleOrder}");
        sb.AppendLine($"# Progress: {playedMatches}/{schedule.Count} matches" + (finished ? " (COMPLETE)" : " (in progress)"));
        sb.AppendLine($"# Draws: {drawMatches}  |  Timeouts: {timedOutMatches}");
        if (matchTimingSamples > 0)
        {
            float avgMatch = totalMatchSeconds / matchTimingSamples;
            float recentMatch = recentMatchTimingSamples > 0 ? recentMatchSeconds / recentMatchTimingSamples : 0f;
            sb.AppendLine($"# Timing: match avg {avgMatch:F3}s, recent {recentMatch:F3}s, last {lastMatchSeconds:F3}s");
        }
        if (reloadTimingSamples > 0)
        {
            float avgReload = totalReloadSeconds / reloadTimingSamples;
            float recentReload = recentReloadTimingSamples > 0 ? recentReloadSeconds / recentReloadTimingSamples : 0f;
            sb.AppendLine($"# Reload: avg {avgReload:F3}s, recent {recentReload:F3}s, last {lastReloadSeconds:F3}s");
        }
        sb.AppendLine("#");

        // Per-deck win rate per profile + aggregate standings.
        float WinRate(EnumAlgorithmProfile p, string d)
        {
            int[] a = standings.TryGetValue(StandingKey(p, d), out int[] v) ? v : null;
            return (a != null && a[3] > 0) ? 100f * a[0] / a[3] : -1f; // -1 = no games
        }

        var rows = decks.Select(d =>
        {
            var cells = profiles.Select(p => WinRate(p, d)).ToList();
            var played = cells.Where(c => c >= 0f).ToList();
            float mean = played.Count > 0 ? played.Average() : 0f;
            float spread = played.Count > 0 ? played.Max() - played.Min() : 0f;
            int wins = 0;
            int losses = 0;
            int draws = 0;
            int games = 0;
            foreach (EnumAlgorithmProfile p in profiles)
            {
                if (!standings.TryGetValue(StandingKey(p, d), out int[] s)) continue;
                wins += s[0];
                losses += s[1];
                draws += s[2];
                games += s[3];
            }
            float totalWinRate = games > 0 ? 100f * wins / games : 0f;
            return new
            {
                Deck = d,
                Profile = ProfileLabelForDeck(profiles, d),
                Cells = cells,
                Mean = mean,
                Spread = spread,
                Wins = wins,
                Losses = losses,
                Draws = draws,
                Games = games,
                TotalWinRate = totalWinRate,
            };
        })
        .OrderByDescending(r => r.TotalWinRate)
        .ThenByDescending(r => r.Wins)
        .ThenBy(r => r.Deck)
        .ToList();

        sb.AppendLine("Standings (by win rate):");
        sb.AppendLine("# Games = how many matches this deck played in this run, not total benchmark matches.");
        sb.AppendLine($"  {"Deck",-26} {"Profile",-13} {"W",4} {"L",4} {"D",4} {"Games",7} {"WinRate",9}");
        foreach (var r in rows)
        {
            sb.AppendLine($"  {r.Deck,-26} {r.Profile,-13} {r.Wins,4} {r.Losses,4} {r.Draws,4} {r.Games,7} {r.TotalWinRate,8:F1}%");
        }
        sb.AppendLine();

        sb.AppendLine("Win rate by deck x profile:");
        sb.AppendLine("# Mean = average win rate across profiles; Spread = max-min.");
        sb.AppendLine("# High spread means the deck's measured strength depends on which agent profile played it");
        sb.AppendLine("# (agent-sensitive); low spread means the ranking is robust to the agent.");

        sb.Append($"  {"Deck",-26}");
        foreach (var p in profiles) sb.Append($" {p.ToString(),12}");
        sb.Append($" {"Mean",8} {"Spread",8}");
        sb.AppendLine();

        foreach (var r in rows)
        {
            sb.Append($"  {r.Deck,-26}");
            foreach (float c in r.Cells)
                sb.Append(c >= 0f ? $" {c,11:F1}%" : $" {"-",12}");
            sb.Append($" {r.Mean,7:F1}% {r.Spread,7:F1}%");
            sb.AppendLine();
        }

        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
    }

    private void WriteAllSessionsSummary(string matchesPath, string summaryPath)
    {
        var globalStandings = new Dictionary<string, int[]>();
        var runs = new HashSet<string>();
        var profiles = new HashSet<string>();
        var decks = new HashSet<string>();
        int matchCount = 0;
        int drawCount = 0;
        int timeoutCount = 0;

        if (File.Exists(matchesPath))
        {
            foreach (string line in File.ReadLines(matchesPath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                GlobalMatchRecord r;
                try
                {
                    r = JsonUtility.FromJson<GlobalMatchRecord>(line);
                }
                catch
                {
                    continue;
                }
                if (r == null || string.IsNullOrWhiteSpace(r.DeckA) || string.IsNullOrWhiteSpace(r.DeckB))
                    continue;

                string profileA = !string.IsNullOrWhiteSpace(r.ProfileA)
                    ? r.ProfileA
                    : (string.IsNullOrWhiteSpace(r.Profile) ? EnumAlgorithmProfile.Standard.ToString() : r.Profile);
                string profileB = !string.IsNullOrWhiteSpace(r.ProfileB)
                    ? r.ProfileB
                    : (string.IsNullOrWhiteSpace(r.Profile) ? EnumAlgorithmProfile.Standard.ToString() : r.Profile);
                runs.Add(r.RunId);
                profiles.Add(profileA);
                profiles.Add(profileB);
                decks.Add(r.DeckA);
                decks.Add(r.DeckB);
                matchCount++;
                if (r.TimedOut) timeoutCount++;

                int[] a = GetGlobalStanding(globalStandings, profileA, r.DeckA);
                int[] b = GetGlobalStanding(globalStandings, profileB, r.DeckB);
                a[3]++;
                b[3]++;

                if (r.WinnerDeck == "Draw")
                {
                    a[2]++;
                    b[2]++;
                    drawCount++;
                }
                else if (r.WinnerDeck == r.DeckA)
                {
                    a[0]++;
                    b[1]++;
                }
                else if (r.WinnerDeck == r.DeckB)
                {
                    b[0]++;
                    a[1]++;
                }
                else
                {
                    a[2]++;
                    b[2]++;
                    drawCount++;
                }
            }
        }

        var orderedProfiles = profiles.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        var orderedDecks = decks.OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList();

        var rows = orderedDecks.Select(d =>
        {
            int wins = 0;
            int losses = 0;
            int draws = 0;
            int games = 0;
            var playedProfiles = new List<string>();
            foreach (string p in orderedProfiles)
            {
                if (!globalStandings.TryGetValue(GlobalStandingKey(p, d), out int[] s)) continue;
                if (s[3] > 0) playedProfiles.Add(p);
                wins += s[0];
                losses += s[1];
                draws += s[2];
                games += s[3];
            }
            float winRate = games > 0 ? 100f * wins / games : 0f;
            var distinctProfiles = playedProfiles.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            string profile = distinctProfiles.Count == 1 ? distinctProfiles[0] : (distinctProfiles.Count > 1 ? "Mixed" : "-");
            return new { Deck = d, Profile = profile, Wins = wins, Losses = losses, Draws = draws, Games = games, WinRate = winRate };
        })
        .OrderByDescending(r => r.WinRate)
        .ThenByDescending(r => r.Wins)
        .ThenBy(r => r.Deck)
        .ToList();

        var sb = new StringBuilder();
        sb.AppendLine("# Benchmark all sessions");
        sb.AppendLine($"# Last update: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"# Source: {AllSessionsMatchesFileName}");
        sb.AppendLine($"# Runs: {runs.Count}  |  Matches: {matchCount}  |  Draws: {drawCount}  |  Timeouts: {timeoutCount}");
        sb.AppendLine();

        sb.AppendLine("Standings (all sessions, by win rate):");
        sb.AppendLine("# Games = how many matches this deck played across recorded sessions, not total benchmark matches.");
        sb.AppendLine($"  {"Deck",-26} {"Profile",-13} {"W",4} {"L",4} {"D",4} {"Games",7} {"WinRate",9}");
        foreach (var r in rows)
            sb.AppendLine($"  {r.Deck,-26} {r.Profile,-13} {r.Wins,4} {r.Losses,4} {r.Draws,4} {r.Games,7} {r.WinRate,8:F1}%");

        File.WriteAllText(summaryPath, sb.ToString(), Encoding.UTF8);
    }

    private static string GlobalStandingKey(string profile, string deck) => $"{profile}\t{deck}";

    private static int[] GetGlobalStanding(Dictionary<string, int[]> target, string profile, string deck)
    {
        string key = GlobalStandingKey(profile, deck);
        if (!target.TryGetValue(key, out int[] a))
        {
            a = new int[4];
            target[key] = a;
        }
        return a;
    }

    private static string GetResultsDirectory()
    {
        return RuntimePaths.BenchmarkLogsRoot();
    }

    // =========================================================================
    // JSON loading (overrides Inspector values when loadFromJson = true)
    // =========================================================================
    private void LoadFromJson()
    {
        string path = RuntimePaths.ConfigPath("BenchmarkConfig.json");
        if (!File.Exists(path))
        {
            Debug.Log($"[BenchmarkRunner] No BenchmarkConfig.json at {path}. Using Inspector values.");
            return;
        }

        try
        {
            JObject obj = JObject.Parse(File.ReadAllText(path), new JsonLoadSettings
            {
                CommentHandling = CommentHandling.Ignore
            });

            runEnabled           = GetValue(obj, "runEnabled",           runEnabled);
            matchesPerPairing    = Mathf.Max(1, GetValue(obj, "matchesPerPairing", matchesPerPairing));
            swapSeats            = GetValue(obj, "swapSeats",            swapSeats);
            includeMirror        = GetValue(obj, "includeMirror",        includeMirror);
            randomizeScheduleOrder = GetValue(obj, "randomizeScheduleOrder", randomizeScheduleOrder);
            autoDetectProfilesByDeck = GetValue(obj, "autoDetectProfilesByDeck", autoDetectProfilesByDeck);
            fastMode             = GetValue(obj, "fastMode",             fastMode);
            matchWatchdogSeconds = GetValue(obj, "matchWatchdogSeconds", matchWatchdogSeconds);
            stopOnFinish         = GetValue(obj, "stopOnFinish",         stopOnFinish);

            participants.Clear();
            participantProfiles.Clear();
            if (obj["participants"] is JArray arr)
            {
                foreach (JToken t in arr)
                {
                    if (t == null) continue;

                    // Object form: { "deck": "Name", "profile": "Ramp" } — profile is optional.
                    if (t.Type == JTokenType.Object)
                    {
                        var entry = (JObject)t;
                        string name = (entry["deck"] ?? entry["name"])?.ToString();
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        name = name.Trim();
                        participants.Add(name);

                        string prof = entry["profile"]?.ToString();
                        if (!string.IsNullOrWhiteSpace(prof))
                            participantProfiles[name] = prof.Trim();
                    }
                    else
                    {
                        string name = t.ToString();
                        if (!string.IsNullOrWhiteSpace(name))
                            participants.Add(name.Trim());
                    }
                }
            }

            if (obj["algorithmProfiles"] is JArray profArr)
            {
                algorithmProfiles.Clear();
                foreach (JToken t in profArr)
                {
                    string name = t?.ToString();
                    if (!string.IsNullOrWhiteSpace(name))
                        algorithmProfiles.Add(name.Trim());
                }
            }
            if (algorithmProfiles.Any(p => System.Enum.TryParse(p, ignoreCase: true, out EnumAlgorithmProfile parsed) &&
                                           parsed == EnumAlgorithmProfile.Auto))
                autoDetectProfilesByDeck = true;

            string profilesStr = algorithmProfiles.Count > 0 ? string.Join("/", algorithmProfiles) : "Standard";
            Debug.Log($"[BenchmarkRunner] Loaded from JSON: runEnabled={runEnabled}, " +
                      $"participants={participants.Count}, profiles={profilesStr}, " +
                      $"autoDetectProfilesByDeck={autoDetectProfilesByDeck}, matchesPerPairing={matchesPerPairing}");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[BenchmarkRunner] Failed to parse BenchmarkConfig.json: {ex.Message}. Using Inspector values.");
        }
    }

    private static T GetValue<T>(JObject obj, string key, T fallback)
    {
        if (!obj.TryGetValue(key, out JToken token) || token.Type == JTokenType.Null)
            return fallback;
        try
        {
            return token.Value<T>();
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[BenchmarkRunner] Invalid value for '{key}' ({token}): {e.Message}. Using '{fallback}'.");
            return fallback;
        }
    }

    // =========================================================================
    // Persistence — write a BenchmarkConfig.json the startup menu can produce.
    // Mirrors the keys read by LoadFromJson(). Static so the menu can call it
    // without a BenchmarkRunner instance (the runner only exists in the gameplay scene).
    // NOTE: for runEnabled=true to actually start a benchmark, the BenchmarkRunner
    // component in the gameplay scene must have its Inspector runEnabled = true
    // (the Inspector switch is checked before JSON is loaded; see Awake()).
    // =========================================================================
    public static void SaveConfig(
        bool runEnabled,
        IEnumerable<string> participants,
        int matchesPerPairing,
        bool swapSeats,
        bool includeMirror,
        bool randomizeScheduleOrder,
        bool fastMode,
        float matchWatchdogSeconds,
        bool stopOnFinish,
        IEnumerable<string> algorithmProfiles = null)
    {
        string path = RuntimePaths.ConfigPath("BenchmarkConfig.json");

        // The menu no longer drives autoDetectProfilesByDeck (the global checkbox was removed in favour
        // of the per-seat EnumAlgorithmProfile.Auto sentinel and per-participant overrides). Preserve a
        // hand-authored value from the file so a menu save does not clobber a benchmark setup.
        bool autoDetectProfilesByDeck = false;
        if (File.Exists(path))
        {
            try
            {
                JObject ex = JObject.Parse(File.ReadAllText(path), new JsonLoadSettings { CommentHandling = CommentHandling.Ignore });
                autoDetectProfilesByDeck = GetValue(ex, "autoDetectProfilesByDeck", false);
            }
            catch { /* fall through: default false */ }
        }

        // Preserve an existing algorithmProfiles list when the caller does not supply one, so saving
        // from the menu does not silently drop a hand-authored multi-profile benchmark setup.
        if (algorithmProfiles == null && File.Exists(path))
        {
            try
            {
                JObject existing = JObject.Parse(File.ReadAllText(path), new JsonLoadSettings { CommentHandling = CommentHandling.Ignore });
                if (existing["algorithmProfiles"] is JArray existingProfilesArray)
                    algorithmProfiles = existingProfilesArray.Select(t => t?.ToString()).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            }
            catch { /* fall through: no profiles preserved */ }
        }

        // Preserve hand-authored per-participant profile overrides ({ "deck", "profile" }) so a menu
        // save (which only passes deck names) does not silently drop them.
        var existingProfiles = new Dictionary<string, string>();
        if (File.Exists(path))
        {
            try
            {
                JObject ex = JObject.Parse(File.ReadAllText(path), new JsonLoadSettings { CommentHandling = CommentHandling.Ignore });
                if (ex["participants"] is JArray pa)
                {
                    foreach (JToken t in pa)
                    {
                        if (t?.Type != JTokenType.Object) continue;
                        var e = (JObject)t;
                        string nm = (e["deck"] ?? e["name"])?.ToString();
                        string pr = e["profile"]?.ToString();
                        if (!string.IsNullOrWhiteSpace(nm) && !string.IsNullOrWhiteSpace(pr))
                            existingProfiles[nm.Trim()] = pr.Trim();
                    }
                }
            }
            catch { /* fall through: no overrides preserved */ }
        }

        JObject obj = new JObject
        {
            ["_section_schedule"]      = "BENCHMARK SCHEDULE",
            ["_profilesHelp"]        = "Each participant is { \"deck\", \"profile\" }. Valid profile values: " +
                                       "Standard, Ramp, TempoAggro, ControlStatus, HealStall, Auto " +
                                       "(Auto = detect the deck archetype). Edit a participant's \"profile\" to test a different weight set.",
            ["runEnabled"]           = runEnabled,
            ["matchesPerPairing"]    = Mathf.Max(1, matchesPerPairing),
            ["swapSeats"]            = swapSeats,
            ["includeMirror"]        = includeMirror,
            ["randomizeScheduleOrder"] = randomizeScheduleOrder,
            ["autoDetectProfilesByDeck"] = autoDetectProfilesByDeck,
            ["fastMode"]             = fastMode,
            ["matchWatchdogSeconds"] = matchWatchdogSeconds,
            ["stopOnFinish"]         = stopOnFinish,
            ["_section_participants"] = "PARTICIPANTS",
            // Always write the explicit object form { "deck", "profile" } so every participant shows its
            // weight profile next to its deck name — editable in a build without knowing the JSON schema.
            // Profile = preserved override, else a faithful default: "Auto" when autoDetectProfilesByDeck
            // is on (so per-deck detection is preserved, not silently overridden by a literal "Standard"),
            // otherwise "Standard".
            ["participants"]         = new JArray((participants ?? Enumerable.Empty<string>())
                                                  .Where(p => !string.IsNullOrWhiteSpace(p))
                                                  .Select(p => p.Trim())
                                                  .Select(p => (JToken)new JObject
                                                  {
                                                      ["deck"] = p,
                                                      ["profile"] = existingProfiles.TryGetValue(p, out string pr)
                                                          ? pr
                                                          : (autoDetectProfilesByDeck
                                                              ? EnumAlgorithmProfile.Auto.ToString()
                                                              : EnumAlgorithmProfile.Standard.ToString()),
                                                  })
                                                  .Cast<object>()
                                                  .ToArray()),
        };

        if (algorithmProfiles != null)
        {
            obj["_section_profileSweep"] = "OPTIONAL PROFILE SWEEP";
            obj["algorithmProfiles"] = new JArray(algorithmProfiles
                                                  .Where(p => !string.IsNullOrWhiteSpace(p))
                                                  .Select(p => p.Trim())
                                                  .Cast<object>()
                                                  .ToArray());
        }

        try
        {
            File.WriteAllText(path, obj.ToString(Newtonsoft.Json.Formatting.Indented));
            Debug.Log($"[BenchmarkRunner] Zapisano config do {path} (runEnabled={runEnabled}).");
        }
        catch (Exception e)
        {
            Debug.LogError($"[BenchmarkRunner] Nie udało się zapisać BenchmarkConfig.json: {e.Message}");
        }
    }
}
