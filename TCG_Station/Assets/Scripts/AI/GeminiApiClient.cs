using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

public class GeminiApiClient : MonoBehaviour, ILLMClient
{
    /// Fires on the first 429/503 hit — before any retry wait. Passes the source client and the rate-limited model.
    public static event Action<GeminiApiClient, EnumGeminiModel> OnFirstRateLimitHit;

    #region Singleton / Factory
    public static GeminiApiClient Instance { get; private set; }

    private void Awake()
    {
        // Token is always needed regardless of creation path.
        LoadToken();
    }

    /// Returns the shared singleton, creating it if needed.
    public static GeminiApiClient EnsureInstance()
    {
        if (Instance != null) return Instance;
        GameObject host = new GameObject(nameof(GeminiApiClient));
        var client = host.AddComponent<GeminiApiClient>(); // Awake runs here
        Instance = client;
        DontDestroyOnLoad(host);
        return client;
    }

    /// Creates a fresh, independent instance locked to a specific model.
    /// Use this when two LLMBrain instances need different Gemini models.
    public static GeminiApiClient CreateForPlayer(EnumGeminiModel model)
    {
        GameObject host = new GameObject($"{nameof(GeminiApiClient)}_{model}");
        var client = host.AddComponent<GeminiApiClient>(); // Awake runs here
        client._modelOverride = model;
        DontDestroyOnLoad(host);
        return client;
    }
    #endregion

    private string apiToken;
    private string finalUrl;
    private EnumGeminiModel? _modelOverride;
    public string LastErrorMessage { get; private set; }

    public void SetModelOverride(EnumGeminiModel model)
    {
        _modelOverride = model;
        BuildFinalUrl();
    }

    private static string ModelToApiName(EnumGeminiModel model) => model switch
    {
        EnumGeminiModel.Flash25     => "gemini-2.5-flash",
        EnumGeminiModel.Flash25Lite => "gemini-2.5-flash-lite",
        EnumGeminiModel.Pro25       => "gemini-2.5-pro",
        EnumGeminiModel.Flash20     => "gemini-2.0-flash",
        EnumGeminiModel.Flash20Lite => "gemini-2.0-flash-lite",
        EnumGeminiModel.Flash31Lite => "gemini-3.1-flash-lite",
        EnumGeminiModel.Flash35     => "gemini-3.5-flash",
        EnumGeminiModel.Flash15     => "gemini-1.5-flash",
        EnumGeminiModel.Flash30     => "gemini-3.0-flash",
        EnumGeminiModel.Gemma4_26b  => "gemma-4-26b-a4b-it",
        EnumGeminiModel.Gemma4_31b  => "gemma-4-31b-it",
        _                           => "gemini-2.5-flash-lite",
    };

    private void LoadToken()
    {
        string path = RuntimePaths.ApiKeyPath("GEMINI_API_KEY.txt");

        if (File.Exists(path))
        {
            try
            {
                string rawToken = File.ReadAllText(path);
                apiToken = rawToken.Trim();
                apiToken = System.Text.RegularExpressions.Regex.Replace(apiToken, @"[^\x20-\x7E]", "");
                BuildFinalUrl();
                Debug.Log($"Token wczytany. Długość: {apiToken.Length} znaków.");
            }
            catch (Exception e)
            {
                apiToken = null;
                LastErrorMessage = $"Nie mozna odczytac GEMINI_API_KEY.txt: {e.Message}";
                Debug.LogError($"[GeminiApiClient] {LastErrorMessage}");
            }
        }
        else
        {
            LastErrorMessage = "Brakuje pliku GEMINI_API_KEY.txt.";
            Debug.LogError("Nie znaleziono pliku GEMINI_API_KEY.txt!");
        }
    }

    private void BuildFinalUrl()
    {
        EnumGeminiModel model = GetCurrentModel();
        string modelName = ModelToApiName(model);
        finalUrl = $"https://generativelanguage.googleapis.com/v1beta/models/{modelName}:generateContent?key={apiToken}";
        Debug.Log($"[GeminiApiClient] Using model: {modelName}");
    }

    private EnumGeminiModel GetCurrentModel()
    {
        return _modelOverride
            ?? (GameRulesConfig.Instance != null ? GameRulesConfig.Instance.geminiModel : EnumGeminiModel.Flash25Lite);
    }

    public void SendPrompt(string userText)
    {
        StartCoroutine(SendPrompt(userText, aiText => GameManager.Instance.llmResponse = aiText));
    }

    public IEnumerator SendPrompt(string userText, System.Action<string> onResponse)
    {
        if (!string.IsNullOrEmpty(apiToken))
            LastErrorMessage = null;

        if (string.IsNullOrEmpty(apiToken))
        {
            string keyPath = RuntimePaths.ApiKeyPath("GEMINI_API_KEY.txt");
            LastErrorMessage ??= File.Exists(keyPath)
                ? "Plik GEMINI_API_KEY.txt jest pusty albo nie zawiera poprawnego klucza."
                : "Brakuje pliku GEMINI_API_KEY.txt obok pliku gry.";
            Debug.LogError("Brak tokena! Nie można wysłać zapytania.");
            onResponse?.Invoke(null);
            yield break;
        }

        BuildFinalUrl(); // refresh model selection in case config loaded after Awake
        yield return PostRequest(userText, onResponse);
    }

    private IEnumerator PostRequest(string userText, System.Action<string> onResponse)
    {
        var request = new GeminiRequest();
        request.systemInstruction = new SystemInstruction
        {
            parts = new List<Part> { new Part { text = LlmRulesProvider.GetRulesText(EnumLlmProvider.Gemini) } }
        };
        request.contents = new List<Content>
        {
            new Content
            {
                role = "user",
                parts = new List<Part> { new Part { text = userText } }
            }
        };
        var cfg = GameRulesConfig.Instance;
        request.generationConfig = new GenerationConfig
        {
            temperature      = cfg != null ? cfg.geminiTemperature     : 0.2f,
            maxOutputTokens  = cfg != null ? cfg.geminiMaxOutputTokens : 2048
        };

        string jsonPayload = JsonUtility.ToJson(request);

        const int maxRetries = 4;
        int retryDelay = 30;
        bool rateLimitEventFired = false;

        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            BuildFinalUrl();
            Debug.Log($"Wysyłam pod adres: {finalUrl.Substring(0, 45)}... (attempt {attempt + 1}/{maxRetries})");

            using (UnityWebRequest www = new UnityWebRequest(finalUrl, "POST"))
            {
                byte[] bodyRaw = Encoding.UTF8.GetBytes(jsonPayload);
                www.uploadHandler = new UploadHandlerRaw(bodyRaw);
                www.downloadHandler = new DownloadHandlerBuffer();
                www.SetRequestHeader("Content-Type", "application/json");

                yield return www.SendWebRequest();

                if (www.result == UnityWebRequest.Result.Success)
                {
                    string rawJson = www.downloadHandler.text;
                    GeminiResponse responseData = JsonUtility.FromJson<GeminiResponse>(rawJson);

                    if (responseData?.candidates == null || responseData.candidates.Length == 0)
                    {
                        LastErrorMessage = "Gemini nie zwrocilo zadnego kandydata odpowiedzi.";
                        Debug.LogError($"[GeminiApiClient] No candidates in response. Raw JSON:\n{rawJson}");
                        onResponse?.Invoke(null);
                        yield break;
                    }

                    Candidate cand = responseData.candidates[0];
                    string finishReason = cand.finishReason ?? "";

                    if (cand.content?.parts == null || cand.content.parts.Count == 0)
                    {
                        LastErrorMessage = $"Gemini zwrocilo pusta odpowiedz (finishReason: {finishReason}).";
                        Debug.LogError($"[GeminiApiClient] Empty content (finishReason={finishReason}). Raw JSON:\n{rawJson}");
                        onResponse?.Invoke(null);
                        yield break;
                    }

                    string aiText = cand.content.parts[0].text;

                    if (finishReason == "MAX_TOKENS")
                        Debug.LogWarning($"[GeminiApiClient] Response truncated by maxOutputTokens — raise geminiMaxOutputTokens in GameRulesConfig. Partial text ({aiText?.Length ?? 0} chars):\n{aiText}");
                    else if (!string.IsNullOrEmpty(finishReason) && finishReason != "STOP")
                        Debug.LogWarning($"[GeminiApiClient] Unusual finishReason: {finishReason}");

                    Debug.Log("Czysta odpowiedź AI: " + aiText);
                    onResponse?.Invoke(aiText);

                    if (GameManager.Instance.textField != null)
                        GameManager.Instance.textField.text = aiText;

                    yield break;
                }

                if (www.responseCode == 429 || www.responseCode == 503)
                {
                    EnumGeminiModel modelBeforeFallback = GetCurrentModel();
                    if (!rateLimitEventFired)
                    {
                        rateLimitEventFired = true;
                        Debug.LogWarning($"[GeminiApiClient] First rate-limit hit ({www.responseCode}) — firing OnFirstRateLimitHit for {modelBeforeFallback}.");
                        OnFirstRateLimitHit?.Invoke(this, modelBeforeFallback);
                    }
                    string responseBody = www.downloadHandler != null ? www.downloadHandler.text : "";
                    LastErrorMessage = BuildApiErrorMessage(www.responseCode, responseBody);
                    if (!string.IsNullOrWhiteSpace(responseBody))
                        Debug.LogWarning($"[GeminiApiClient] {www.responseCode} response body:\n{responseBody}");
                    int waitSeconds = GetCurrentModel() != modelBeforeFallback ? 2 : retryDelay;
                    Debug.LogWarning($"[GeminiApiClient] {www.responseCode} — waiting {waitSeconds}s before retry {attempt + 1}/{maxRetries - 1}. Next attempt will use current fallback model if one was applied.");
                    yield return new WaitForSeconds(waitSeconds);
                    retryDelay *= 2; // exponential backoff: 30s → 60s → 120s
                }
                else
                {
                    string responseBody = www.downloadHandler != null ? www.downloadHandler.text : "";
                    LastErrorMessage = BuildApiErrorMessage(www.responseCode, responseBody);
                    Debug.LogError($"Błąd {www.responseCode}: {www.error}\n{responseBody}");
                    onResponse?.Invoke(null);
                    yield break;
                }
            }
        }

        LastErrorMessage ??= "Gemini nie odpowiedzialo po wszystkich probach.";
        Debug.LogError($"[GeminiApiClient] All {maxRetries} attempts failed (429). Giving up.");
        onResponse?.Invoke(null);
    }

    private static string BuildApiErrorMessage(long responseCode, string responseBody)
    {
        string apiMessage = null;
        try
        {
            GeminiErrorResponse error = JsonUtility.FromJson<GeminiErrorResponse>(responseBody);
            apiMessage = error?.error?.message;
        }
        catch
        {
            // The HTTP status still gives a useful and safe message.
        }

        if (responseCode == 400)
            return "Gemini odrzucilo zapytanie albo nazwe modelu (HTTP 400).";
        if (responseCode == 401 || responseCode == 403)
            return $"Gemini odrzucilo klucz API albo dostep do modelu (HTTP {responseCode}).";
        if (responseCode == 429)
            return "Gemini ma pusty limit lub przekroczona kwote dla wybranego modelu (HTTP 429).";
        if (responseCode == 503)
            return "Gemini jest chwilowo niedostepne (HTTP 503).";

        return string.IsNullOrWhiteSpace(apiMessage)
            ? $"Gemini nie dziala dla wybranego modelu (HTTP {responseCode})."
            : $"Gemini: {apiMessage}";
    }
}

// --- KLASY DANYCH JSON ---

[Serializable]
public class GeminiRequest
{
    public SystemInstruction systemInstruction;
    public List<Content> contents;
    public GenerationConfig generationConfig;
}

[Serializable]
public class SystemInstruction
{
    public List<Part> parts;
}

[Serializable]
public class GenerationConfig
{
    public float temperature;
    public int maxOutputTokens;
}

[Serializable]
public class Content
{
    public string role;
    public List<Part> parts;
}

[Serializable]
public class Part
{
    public string text;
}

[Serializable]
public class GeminiResponse
{
    public Candidate[] candidates;
}

[Serializable]
public class GeminiErrorResponse
{
    public GeminiError error;
}

[Serializable]
public class GeminiError
{
    public int code;
    public string message;
    public string status;
}

[Serializable]
public class Candidate
{
    public Content content;
    public string finishReason;
}
