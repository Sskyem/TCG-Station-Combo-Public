using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

public class OllamaApiClient : MonoBehaviour, ILLMClient
{
    public static OllamaApiClient Instance { get; private set; }

    private EnumOllamaModel? _modelOverride;

    private void Awake() { }

    public static string ModelName(EnumOllamaModel model) => model switch
    {
        EnumOllamaModel.Qwen3_8b                => "qwen3:8b",
        EnumOllamaModel.Gemma4_12b_It_Q4_K_M    => "gemma4:12b-it-q4_K_M",
        EnumOllamaModel.Gemma4_E4b_It_Q4_K_M    => "gemma4:e4b-it-q4_K_M",
        _                                        => "gemma3:12b",
    };

    /// Returns the shared singleton, creating it if needed.
    public static OllamaApiClient EnsureInstance()
    {
        if (Instance != null) return Instance;
        GameObject host = new GameObject(nameof(OllamaApiClient));
        var client = host.AddComponent<OllamaApiClient>();
        Instance = client;
        DontDestroyOnLoad(host);
        return client;
    }

    /// Creates a fresh, independent instance locked to a specific model.
    public static OllamaApiClient CreateForPlayer(EnumOllamaModel model)
    {
        GameObject host = new GameObject($"{nameof(OllamaApiClient)}_{model}");
        var client = host.AddComponent<OllamaApiClient>();
        client._modelOverride = model;
        DontDestroyOnLoad(host);
        return client;
    }

    public IEnumerator SendPrompt(string prompt, Action<string> onResponse)
    {
        GameRulesConfig config = GameRulesConfig.Instance;
        string targetUrl = config != null ? config.ollamaBaseUrl : "http://127.0.0.1:11434/api/chat";
        EnumOllamaModel resolvedModel = _modelOverride ?? (config != null ? config.ollamaModel : EnumOllamaModel.Gemma3_12b);
        string targetModel = ModelName(resolvedModel);
        string systemPrompt = LlmRulesProvider.GetRulesText(EnumLlmProvider.Ollama);

        var payload = new OllamaChatRequest
        {
            model = targetModel,
            stream = false,
            messages = new[]
            {
                new OllamaChatMessage
                {
                    role = "system",
                    content = systemPrompt
                },
                new OllamaChatMessage
                {
                    role = "user",
                    content = prompt
                }
            }
        };

        string jsonPayload = JsonUtility.ToJson(payload);

        using (UnityWebRequest www = new UnityWebRequest(targetUrl, "POST"))
        {
            byte[] bodyRaw = Encoding.UTF8.GetBytes(jsonPayload);
            www.uploadHandler = new UploadHandlerRaw(bodyRaw);
            www.downloadHandler = new DownloadHandlerBuffer();
            www.SetRequestHeader("Content-Type", "application/json");
            www.timeout = 60;

            yield return www.SendWebRequest();

            if (www.result == UnityWebRequest.Result.Success)
            {
                string rawJson = www.downloadHandler.text;
                OllamaChatResponse responseData = JsonUtility.FromJson<OllamaChatResponse>(rawJson);
                string aiText = responseData != null && responseData.message != null
                    ? responseData.message.content
                    : null;

                Debug.Log("Czysta odpowiedź Ollama: " + aiText);
                onResponse?.Invoke(aiText);

                if (GameManager.Instance != null && GameManager.Instance.textField != null)
                {
                    GameManager.Instance.textField.text = aiText;
                }
            }
            else
            {
                Debug.LogError($"[OllamaApiClient] Błąd {www.responseCode}: {www.error}");
                onResponse?.Invoke(null);
            }
        }
    }
}

[Serializable]
public class OllamaChatRequest
{
    public string model;
    public bool stream;
    public OllamaChatMessage[] messages;
}

[Serializable]
public class OllamaChatMessage
{
    public string role;
    public string content;
}

[Serializable]
public class OllamaChatResponse
{
    public OllamaChatMessage message;
}
