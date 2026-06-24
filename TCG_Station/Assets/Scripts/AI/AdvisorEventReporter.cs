using System.Collections;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Networking;

public static class AdvisorEventReporter
{
    private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
    {
        NullValueHandling = NullValueHandling.Ignore,
        Formatting = Formatting.None,
        Converters = { new Newtonsoft.Json.Converters.StringEnumConverter() },
    };

    public static IEnumerator Post(
        string advisor,
        string stage,
        string message,
        PlayerController player = null,
        string category = null,
        string actionLabel = null,
        float? confidence = null,
        string provider = null,
        string model = null)
    {
        var payload = new Dictionary<string, object>
        {
            ["advisor"] = advisor,
            ["stage"] = stage,
            ["message"] = message,
            ["turn"] = TurnManager.Instance != null ? TurnManager.Instance.turnCounter : 0,
        };

        if (player != null)
        {
            payload["player_id"] = player.playerId;
            payload["player_name"] = player.playerName;
        }
        if (!string.IsNullOrWhiteSpace(category)) payload["category"] = category;
        if (!string.IsNullOrWhiteSpace(actionLabel)) payload["action_label"] = actionLabel;
        if (confidence.HasValue) payload["confidence"] = confidence.Value;
        if (!string.IsNullOrWhiteSpace(provider)) payload["provider"] = provider;
        if (!string.IsNullOrWhiteSpace(model)) payload["model"] = model;

        string json = JsonConvert.SerializeObject(payload, JsonSettings);
        string url = BuildEventUrl();
        using var www = new UnityWebRequest(url, "POST");
        www.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
        www.downloadHandler = new DownloadHandlerBuffer();
        www.SetRequestHeader("Content-Type", "application/json");
        www.timeout = 4;
        yield return www.SendWebRequest();

        if (www.result != UnityWebRequest.Result.Success)
            Debug.LogWarning($"[AdvisorEventReporter] Could not report advisor event to {url}: {www.error}");
    }

    public static string BuildEventUrl()
    {
        string predictUrl = GameRulesConfig.Instance != null
            ? GameRulesConfig.Instance.mlServerUrl
            : "http://127.0.0.1:8000/predict";

        if (string.IsNullOrWhiteSpace(predictUrl))
            return "http://127.0.0.1:8000/api/advisor/event";

        try
        {
            var uri = new System.Uri(predictUrl);
            return $"{uri.Scheme}://{uri.Authority}/api/advisor/event";
        }
        catch
        {
            return predictUrl.Replace("/predict", "/api/advisor/event");
        }
    }
}
