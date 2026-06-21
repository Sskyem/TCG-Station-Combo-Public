using System;
using System.Collections;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// ChatGPT (OpenAI) provider for <see cref="ILLMClient"/>.
/// Mirrors <see cref="GeminiApiClient"/>: full-turn sequential mode (one request per turn,
/// the model answers with ACTION_SEQUENCE). The API key is read from OPENAI_API_KEY.txt next
/// to the build (never embedded), exactly like GEMINI_API_KEY.txt.
/// </summary>
public class OpenAiApiClient : MonoBehaviour, ILLMClient
{
    private const string Endpoint = "https://api.openai.com/v1/chat/completions";

    public static OpenAiApiClient Instance { get; private set; }

    private string apiToken;
    private EnumOpenAiModel? _modelOverride;
    public string LastErrorMessage { get; private set; }

    private void Awake()
    {
        // Token is always needed regardless of creation path.
        LoadToken();
    }

    #region Singleton / Factory
    /// Returns the shared singleton, creating it if needed.
    public static OpenAiApiClient EnsureInstance()
    {
        if (Instance != null) return Instance;
        GameObject host = new GameObject(nameof(OpenAiApiClient));
        var client = host.AddComponent<OpenAiApiClient>();
        Instance = client;
        DontDestroyOnLoad(host);
        return client;
    }

    /// Creates a fresh, independent instance locked to a specific model.
    /// Use this when two LLMBrain instances need different OpenAI models.
    public static OpenAiApiClient CreateForPlayer(EnumOpenAiModel model)
    {
        GameObject host = new GameObject($"{nameof(OpenAiApiClient)}_{model}");
        var client = host.AddComponent<OpenAiApiClient>();
        client._modelOverride = model;
        DontDestroyOnLoad(host);
        return client;
    }
    #endregion

    public void SetModelOverride(EnumOpenAiModel model) => _modelOverride = model;

    public static string ModelToApiName(EnumOpenAiModel model) => model switch
    {
        EnumOpenAiModel.Gpt4oMini => "gpt-4o-mini",
        EnumOpenAiModel.Gpt4o     => "gpt-4o",
        EnumOpenAiModel.Gpt41Mini => "gpt-4.1-mini",
        EnumOpenAiModel.Gpt41     => "gpt-4.1",
        EnumOpenAiModel.Gpt5Mini  => "gpt-5-mini",
        EnumOpenAiModel.Gpt5      => "gpt-5",
        EnumOpenAiModel.O4Mini    => "o4-mini",
        _                         => "gpt-4o-mini",
    };

    public static string OpenAiModelDisplayName(EnumOpenAiModel model) => model switch
    {
        EnumOpenAiModel.Gpt4oMini => "GPT-4o mini",
        EnumOpenAiModel.Gpt4o     => "GPT-4o",
        EnumOpenAiModel.Gpt41Mini => "GPT-4.1 mini",
        EnumOpenAiModel.Gpt41     => "GPT-4.1",
        EnumOpenAiModel.Gpt5Mini  => "GPT-5 mini",
        EnumOpenAiModel.Gpt5      => "GPT-5",
        EnumOpenAiModel.O4Mini    => "o4-mini",
        _                         => model.ToString(),
    };

    // Reasoning models reject custom temperature and spend output tokens on hidden reasoning.
    private static bool IsReasoningModel(EnumOpenAiModel model) => model switch
    {
        EnumOpenAiModel.Gpt5 or EnumOpenAiModel.Gpt5Mini or EnumOpenAiModel.O4Mini => true,
        _ => false,
    };

    private EnumOpenAiModel GetCurrentModel()
    {
        return _modelOverride
            ?? (GameRulesConfig.Instance != null ? GameRulesConfig.Instance.openAiModel : EnumOpenAiModel.Gpt4oMini);
    }

    private void LoadToken()
    {
        string path = RuntimePaths.ApiKeyPath("OPENAI_API_KEY.txt");

        if (File.Exists(path))
        {
            try
            {
                string rawToken = File.ReadAllText(path);
                apiToken = rawToken.Trim();
                apiToken = System.Text.RegularExpressions.Regex.Replace(apiToken, @"[^\x20-\x7E]", "");
                Debug.Log($"[OpenAiApiClient] Token wczytany. Dlugosc: {apiToken.Length} znakow.");
            }
            catch (Exception e)
            {
                apiToken = null;
                LastErrorMessage = $"Nie mozna odczytac OPENAI_API_KEY.txt: {e.Message}";
                Debug.LogError($"[OpenAiApiClient] {LastErrorMessage}");
            }
        }
        else
        {
            LastErrorMessage = "Brakuje pliku OPENAI_API_KEY.txt.";
            Debug.LogError("Nie znaleziono pliku OPENAI_API_KEY.txt!");
        }
    }

    public IEnumerator SendPrompt(string userText, Action<string> onResponse)
    {
        if (!string.IsNullOrEmpty(apiToken))
            LastErrorMessage = null;

        if (string.IsNullOrEmpty(apiToken))
        {
            string keyPath = RuntimePaths.ApiKeyPath("OPENAI_API_KEY.txt");
            LastErrorMessage ??= File.Exists(keyPath)
                ? "Plik OPENAI_API_KEY.txt jest pusty albo nie zawiera poprawnego klucza."
                : "Brakuje pliku OPENAI_API_KEY.txt obok pliku gry.";
            Debug.LogError("[OpenAiApiClient] Brak tokena! Nie mozna wyslac zapytania.");
            onResponse?.Invoke(null);
            yield break;
        }

        EnumOpenAiModel model = GetCurrentModel();
        string modelName = ModelToApiName(model);
        string systemPrompt = LlmRulesProvider.GetRulesText(EnumLlmProvider.OpenAI);

        var cfg = GameRulesConfig.Instance;
        int maxTokens = cfg != null ? cfg.openAiMaxOutputTokens : 2048;
        float temperature = cfg != null ? cfg.openAiTemperature : 0.2f;

        var body = new JObject
        {
            ["model"] = modelName,
            ["messages"] = new JArray
            {
                new JObject { ["role"] = "system", ["content"] = systemPrompt },
                new JObject { ["role"] = "user", ["content"] = userText },
            },
            ["max_completion_tokens"] = maxTokens,
        };
        if (!IsReasoningModel(model))
            body["temperature"] = temperature;

        string jsonPayload = body.ToString(Formatting.None);

        const int maxRetries = 4;
        int retryDelay = 5;

        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            Debug.Log($"[OpenAiApiClient] Using model: {modelName} (attempt {attempt + 1}/{maxRetries})");

            using (UnityWebRequest www = new UnityWebRequest(Endpoint, "POST"))
            {
                byte[] bodyRaw = Encoding.UTF8.GetBytes(jsonPayload);
                www.uploadHandler = new UploadHandlerRaw(bodyRaw);
                www.downloadHandler = new DownloadHandlerBuffer();
                www.SetRequestHeader("Content-Type", "application/json");
                www.SetRequestHeader("Authorization", $"Bearer {apiToken}");
                www.timeout = 120;

                float startTime = Time.realtimeSinceStartup;
                yield return www.SendWebRequest();
                float latencyMs = (Time.realtimeSinceStartup - startTime) * 1000f;

                if (www.result == UnityWebRequest.Result.Success)
                {
                    string rawJson = www.downloadHandler.text;
                    string aiText = null;
                    string finishReason = "";
                    int promptTokens = 0;
                    int completionTokens = 0;
                    int totalTokens = 0;

                    try
                    {
                        JObject root = JObject.Parse(rawJson);
                        JToken choice = root["choices"]?[0];
                        aiText = (string)choice?["message"]?["content"];
                        finishReason = (string)choice?["finish_reason"] ?? "";
                        JToken usage = root["usage"];
                        promptTokens = (int?)usage?["prompt_tokens"] ?? 0;
                        completionTokens = (int?)usage?["completion_tokens"] ?? 0;
                        totalTokens = (int?)usage?["total_tokens"] ?? 0;
                    }
                    catch (Exception e)
                    {
                        LastErrorMessage = $"Nie udalo sie odczytac odpowiedzi OpenAI: {e.Message}";
                        Debug.LogError($"[OpenAiApiClient] Failed to parse response: {e.Message}\nRaw JSON:\n{rawJson}");
                        onResponse?.Invoke(null);
                        yield break;
                    }

                    Debug.Log($"[OpenAiApiClient] tokens prompt={promptTokens} completion={completionTokens} total={totalTokens} | latency={latencyMs:F0}ms | finish={finishReason}");

                    if (string.IsNullOrEmpty(aiText))
                    {
                        LastErrorMessage = $"OpenAI zwrocilo pusta odpowiedz (finish_reason: {finishReason}).";
                        Debug.LogError($"[OpenAiApiClient] Empty content (finishReason={finishReason}). Raw JSON:\n{rawJson}");
                        onResponse?.Invoke(null);
                        yield break;
                    }

                    if (finishReason == "length")
                        Debug.LogWarning("[OpenAiApiClient] Response truncated by max_completion_tokens - raise openAiMaxOutputTokens in GameRulesConfig.");

                    Debug.Log("[OpenAiApiClient] Czysta odpowiedz AI: " + aiText);
                    onResponse?.Invoke(aiText);

                    if (GameManager.Instance != null && GameManager.Instance.textField != null)
                        GameManager.Instance.textField.text = aiText;

                    yield break;
                }

                if (www.responseCode == 429 || www.responseCode == 503)
                {
                    string responseBody = www.downloadHandler != null ? www.downloadHandler.text : "";
                    LastErrorMessage = BuildApiErrorMessage(www.responseCode, responseBody);
                    Debug.LogWarning($"[OpenAiApiClient] {www.responseCode} - waiting {retryDelay}s before retry {attempt + 1}/{maxRetries - 1}.\n{responseBody}");
                    yield return new WaitForSeconds(retryDelay);
                    retryDelay *= 2;
                }
                else
                {
                    string responseBody = www.downloadHandler != null ? www.downloadHandler.text : "";
                    LastErrorMessage = BuildApiErrorMessage(www.responseCode, responseBody);
                    Debug.LogError($"[OpenAiApiClient] Blad {www.responseCode}: {www.error}\n{responseBody}");
                    onResponse?.Invoke(null);
                    yield break;
                }
            }
        }

        LastErrorMessage ??= "OpenAI nie odpowiedzial po wszystkich probach.";
        Debug.LogError($"[OpenAiApiClient] All {maxRetries} attempts failed (rate-limited). Giving up.");
        onResponse?.Invoke(null);
    }

    private static string BuildApiErrorMessage(long responseCode, string responseBody)
    {
        string apiMessage = null;
        try
        {
            apiMessage = (string)JObject.Parse(responseBody)?["error"]?["message"];
        }
        catch
        {
            // The HTTP status still gives a useful and safe message.
        }

        if (responseCode == 401)
            return "OpenAI odrzucilo klucz API (HTTP 401).";
        if (responseCode == 429)
            return "OpenAI przekroczylo limit zapytan albo dostepna kwote (HTTP 429).";
        if (responseCode == 503)
            return "OpenAI jest chwilowo niedostepne (HTTP 503).";

        return string.IsNullOrWhiteSpace(apiMessage)
            ? $"OpenAI nie dziala dla wybranego modelu (HTTP {responseCode})."
            : $"OpenAI: {apiMessage}";
    }
}
