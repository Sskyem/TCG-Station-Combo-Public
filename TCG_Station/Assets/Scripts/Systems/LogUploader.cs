using System;
using System.Collections;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// Uploads ML logs to a remote server after each battle.
/// Requires logUploadEnabled=true and logServerUrl set in GameRulesConfig.
/// Upload is fire-and-forget: failures are only logged as warnings, never thrown.
/// </summary>
public class LogUploader : MonoBehaviour
{
    public static LogUploader Instance { get; private set; }

    [Serializable]
    private sealed class LocalUploadConfig
    {
        public string serverUrl;
        public string apiKey;
    }

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
    private void OnDisable() => BattleManager.OnGameOver -= HandleGameOver;

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    private void HandleGameOver(PlayerController _)
    {
        var config = GameRulesConfig.Instance;
        if (config == null || !config.logUploadEnabled)
            return;

        // Benchmark runs reload the scene one frame after game-over. This LogUploader lives on a
        // non-persistent scene object, so a self-started coroutine would be killed mid-upload by
        // the reload. When the benchmark is active, BenchmarkRunner (which is persistent) drives
        // UploadAfterLogging itself and waits for it before reloading — see ReloadForNextMatch.
        bool benchmarkRunning = BenchmarkRunner.Instance != null && BenchmarkRunner.Instance.runEnabled;
        if (benchmarkRunning)
            return;

        StartCoroutine(UploadAfterLogging());
    }

    /// <summary>
    /// Uploads the last game's result + decisions JSONL. Public so BenchmarkRunner can drive it
    /// on its own (persistent) object and await completion before reloading the scene.
    /// </summary>
    public IEnumerator UploadAfterLogging()
    {
        // Wait one frame so GameResultLogger and DecisionLogger finish writing files
        yield return new WaitForEndOfFrame();

        var record = GameResultLogger.Instance?.LastRecord;
        if (record == null)
        {
            Debug.LogWarning("[LogUploader] No game result record found, skipping upload.");
            yield break;
        }

        LocalUploadConfig uploadConfig = LoadLocalUploadConfig();
        if (uploadConfig == null)
            yield break;

        string gameId  = record.game_id;
        string baseUrl = uploadConfig.serverUrl.TrimEnd('/');
        string apiKey  = uploadConfig.apiKey;
        string clientId = GetOrCreateClientId();

        // 1. Upload game result (adds client_id to the existing record)
        JObject gameJson = JObject.FromObject(record, JsonSerializer.CreateDefault(JsonSettings));
        gameJson["client_id"] = clientId;
        yield return Post(baseUrl + "/game-result", Encoding.UTF8.GetBytes(gameJson.ToString(Formatting.None)),
                          "application/json", apiKey, timeout: 10);

        // 2. Upload decisions JSONL file. Decision logs are split into per-context subfolders
        // (Decisions/benchmark/, Decisions/interactive/<matchup>/, …), so the exact subfolder isn't
        // known here. Use the logger's current path when it matches, else search the tree by name.
        string decisionsRoot = Path.Combine(RuntimePaths.MlLogsRoot(), "Decisions");
        string fileName = $"{gameId}_decisions.jsonl";
        string decisionPath = null;

        string current = DecisionLogger.Instance != null ? DecisionLogger.Instance.CurrentFilePath : null;
        if (!string.IsNullOrEmpty(current) &&
            Path.GetFileName(current) == fileName && File.Exists(current))
        {
            decisionPath = current;
        }
        else if (Directory.Exists(decisionsRoot))
        {
            try
            {
                string[] matches = Directory.GetFiles(decisionsRoot, fileName, SearchOption.AllDirectories);
                if (matches.Length > 0) decisionPath = matches[0];
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[LogUploader] Error searching for decisions of {gameId}: {ex.Message}");
            }
        }

        if (decisionPath == null)
        {
            Debug.LogWarning($"[LogUploader] Decisions file not found for {gameId} under {decisionsRoot}");
            yield break;
        }

        byte[] fileBytes = File.ReadAllBytes(decisionPath);
        yield return Post(baseUrl + "/decisions/" + gameId, fileBytes,
                          "application/x-ndjson", apiKey, timeout: 30);

        Debug.Log($"[LogUploader] Uploaded game {gameId} ({fileBytes.Length / 1024} KB decisions)");
    }

    private static LocalUploadConfig LoadLocalUploadConfig()
    {
        string path = RuntimePaths.ConfigPath("LogUploader.local.json");
        if (!File.Exists(path))
        {
            Debug.LogWarning(
                $"[LogUploader] Upload enabled, but {path} does not exist. Copy LogUploader.local.example.json and fill it locally.");
            return null;
        }

        try
        {
            LocalUploadConfig config = JsonConvert.DeserializeObject<LocalUploadConfig>(File.ReadAllText(path));
            if (config == null ||
                string.IsNullOrWhiteSpace(config.serverUrl) ||
                string.IsNullOrWhiteSpace(config.apiKey))
            {
                Debug.LogWarning($"[LogUploader] {path} is missing serverUrl or apiKey.");
                return null;
            }

            return config;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[LogUploader] Could not read {path}: {e.Message}");
            return null;
        }
    }

    private IEnumerator Post(string url, byte[] body, string contentType, string apiKey, int timeout)
    {
        using var www = new UnityWebRequest(url, "POST");
        www.uploadHandler   = new UploadHandlerRaw(body);
        www.downloadHandler = new DownloadHandlerBuffer();
        www.SetRequestHeader("Content-Type", contentType);
        www.SetRequestHeader("X-Api-Key", apiKey);
        www.timeout = timeout;
        yield return www.SendWebRequest();

        if (www.result != UnityWebRequest.Result.Success)
            Debug.LogWarning($"[LogUploader] POST {url} failed ({www.responseCode}): {www.error}");
    }

    private static string GetOrCreateClientId()
    {
        string path = RuntimePaths.ClientIdPath();

        if (File.Exists(path))
            return File.ReadAllText(path).Trim();

        string id = Guid.NewGuid().ToString();
        File.WriteAllText(path, id);
        return id;
    }
}
