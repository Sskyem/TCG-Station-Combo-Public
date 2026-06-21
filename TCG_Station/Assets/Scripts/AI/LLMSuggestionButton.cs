using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;

/// <summary>
/// Scene-button helper for asking the configured LLM advisor for the next action.
/// This is an advisor only: it never executes actions and never mutates game state.
/// </summary>
public class LLMSuggestionButton : MonoBehaviour
{
    [SerializeField] private PlayerManager playerManager;
    [SerializeField] private BoardVisualizer boardVisualizer;

    private ILLMClient llmClient;
    private EnumLlmProvider? clientProvider;
    private EnumGeminiModel? clientGeminiModel;
    private EnumOllamaModel? clientOllamaModel;
    private EnumOpenAiModel? clientOpenAiModel;

    public void RequestLlmSuggestion()
    {
        StopAllCoroutines();
        StartCoroutine(RequestLlmSuggestionRoutine());
    }

    private IEnumerator RequestLlmSuggestionRoutine()
    {
        playerManager ??= PlayerManager.Instance;
        boardVisualizer ??= FindFirstObjectByType<BoardVisualizer>();

        if (playerManager == null || playerManager.activePlayer == null)
        {
            SetOutput("LLM Advisor\nNo active player found.");
            yield break;
        }

        PlayerController player = playerManager.activePlayer;
        PlayerController opponent = playerManager.player1 == player ? playerManager.player2 : playerManager.player1;
        if (opponent == null)
        {
            SetOutput("LLM Advisor\nOpponent not found.");
            yield break;
        }

        GameRulesConfig cfg = GameRulesConfig.Instance;
        EnumLlmProvider provider = cfg != null ? cfg.llmAdvisorProvider : EnumLlmProvider.Ollama;
        string model = AdvisorModelName(provider, cfg);

        List<GameAction> legalActions = LegalActionGenerator.Generate(
            player, opponent, playerManager, includeFutureTurnActions: false);

        if (legalActions.Count == 0)
        {
            SetOutput($"LLM Advisor — {provider} / {model}\nNo legal actions found.");
            yield return AdvisorEventReporter.Post("LLM", "error", "No legal actions found.", player, provider: provider.ToString(), model: model);
            yield break;
        }

        string prompt = BuildAdvisorPrompt(provider, player, opponent, legalActions);
        SetOutput($"LLM Advisor — {provider} / {model}\nP{player.playerId} {player.playerName}: querying provider...");
        yield return AdvisorEventReporter.Post(
            "LLM",
            "start",
            $"Querying {provider}/{model} for {legalActions.Count} legal action(s).",
            player,
            provider: provider.ToString(),
            model: model);

        llmClient = ResolveAdvisorClient(provider, cfg);
        if (llmClient == null)
        {
            SetOutput($"LLM Advisor — {provider} / {model}\nCould not create LLM client.");
            yield return AdvisorEventReporter.Post("LLM", "error", "Could not create LLM client.", player, provider: provider.ToString(), model: model);
            yield break;
        }

        string response = null;
        yield return llmClient.SendPrompt(prompt, r => response = r);

        if (string.IsNullOrWhiteSpace(response))
        {
            string error = GetClientErrorMessage(provider);
            SetOutput($"LLM Advisor — {provider} / {model}\n{error}");
            yield return AdvisorEventReporter.Post("LLM", "error", error, player, provider: provider.ToString(), model: model);
            yield break;
        }

        int index = provider == EnumLlmProvider.Ollama
            ? ParseActionIndex(response, legalActions.Count)
            : ParseFirstSequenceIndex(response, legalActions.Count);
        GameAction chosen = legalActions[Mathf.Clamp(index, 0, legalActions.Count - 1)];
        string thinking = ExtractThinkingText(response);

        var lines = new List<string>
        {
            $"LLM Advisor — {provider} / {model}",
            $"P{player.playerId} {player.playerName} · Turn {TurnManager.Instance?.turnCounter ?? 0}",
            $"Suggested next action: [{index}] {chosen}",
        };
        if (!string.IsNullOrWhiteSpace(thinking))
        {
            lines.Add("");
            lines.Add($"Thinking: {thinking}");
        }

        SetOutput(string.Join("\n", lines));
        yield return AdvisorEventReporter.Post(
            "LLM",
            "final",
            $"Suggested next action: [{index}] {chosen}",
            player,
            actionLabel: chosen.ToString(),
            provider: provider.ToString(),
            model: model);
    }

    private string BuildAdvisorPrompt(
        EnumLlmProvider provider,
        PlayerController player,
        PlayerController opponent,
        List<GameAction> legalActions)
    {
        GameStateSnapshot snapshot = GameStateSnapshot.Create(
            player,
            opponent,
            TurnManager.Instance != null ? TurnManager.Instance.turnCounter : 0,
            player.playerId);

        if (provider == EnumLlmProvider.Ollama)
            return LlmPromptBuilder.BuildOllamaActionPrompt(snapshot, legalActions, Array.Empty<string>(), 1);

        return LlmPromptBuilder.BuildTurnPrompt(snapshot, legalActions, null, provider);
    }

    private ILLMClient ResolveAdvisorClient(EnumLlmProvider provider, GameRulesConfig cfg)
    {
        EnumGeminiModel geminiModel = cfg != null ? cfg.llmAdvisorGeminiModel : EnumGeminiModel.Flash25Lite;
        EnumOllamaModel ollamaModel = cfg != null ? cfg.llmAdvisorOllamaModel : EnumOllamaModel.Gemma3_12b;
        EnumOpenAiModel openAiModel = cfg != null ? cfg.llmAdvisorOpenAiModel : EnumOpenAiModel.Gpt4oMini;

        bool sameClient = llmClient != null &&
                          clientProvider == provider &&
                          clientGeminiModel == geminiModel &&
                          clientOllamaModel == ollamaModel &&
                          clientOpenAiModel == openAiModel;
        if (sameClient) return llmClient;

        clientProvider = provider;
        clientGeminiModel = geminiModel;
        clientOllamaModel = ollamaModel;
        clientOpenAiModel = openAiModel;

        return provider switch
        {
            EnumLlmProvider.Ollama => OllamaApiClient.CreateForPlayer(ollamaModel),
            EnumLlmProvider.OpenAI => OpenAiApiClient.CreateForPlayer(openAiModel),
            _ => GeminiApiClient.CreateForPlayer(geminiModel),
        };
    }

    private static string AdvisorModelName(EnumLlmProvider provider, GameRulesConfig cfg)
    {
        return provider switch
        {
            EnumLlmProvider.Ollama => (cfg != null ? cfg.llmAdvisorOllamaModel : EnumOllamaModel.Gemma3_12b).ToString(),
            EnumLlmProvider.OpenAI => (cfg != null ? cfg.llmAdvisorOpenAiModel : EnumOpenAiModel.Gpt4oMini).ToString(),
            _ => (cfg != null ? cfg.llmAdvisorGeminiModel : EnumGeminiModel.Flash25Lite).ToString(),
        };
    }

    private void SetOutput(string message)
    {
        if (boardVisualizer == null)
            boardVisualizer = FindFirstObjectByType<BoardVisualizer>();

        if (boardVisualizer != null && boardVisualizer.llmThinkingLog != null)
            boardVisualizer.llmThinkingLog.text = message;

        Debug.Log($"[LLMSuggestionButton] {message}");
    }

    private string GetClientErrorMessage(EnumLlmProvider provider)
    {
        if (llmClient is OpenAiApiClient openAiClient &&
            !string.IsNullOrWhiteSpace(openAiClient.LastErrorMessage))
            return openAiClient.LastErrorMessage;

        if (llmClient is GeminiApiClient geminiClient &&
            !string.IsNullOrWhiteSpace(geminiClient.LastErrorMessage))
            return geminiClient.LastErrorMessage;

        if (llmClient is OllamaApiClient ollamaClient &&
            !string.IsNullOrWhiteSpace(ollamaClient.LastErrorMessage))
            return ollamaClient.LastErrorMessage;

        return $"{provider} nie zwrocil odpowiedzi.";
    }

    private static int ParseActionIndex(string response, int actionCount)
    {
        Match match = Regex.Match(
            response ?? "",
            @"ACTION_INDEX\s*:\s*(\d+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        if (match.Success &&
            int.TryParse(match.Groups[1].Value.Trim(), out int idx) &&
            idx >= 0 &&
            idx < actionCount)
        {
            return idx;
        }

        return actionCount - 1;
    }

    private static int ParseFirstSequenceIndex(string response, int actionCount)
    {
        Match match = Regex.Match(
            response ?? "",
            @"ACTION_SEQUENCE\s*:\s*([\d,\s]+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        if (match.Success)
        {
            foreach (string part in match.Groups[1].Value.Split(','))
            {
                if (int.TryParse(part.Trim(), out int idx) && idx >= 0 && idx < actionCount)
                    return idx;
            }
        }

        return ParseActionIndex(response, actionCount);
    }

    private static string ExtractThinkingText(string response)
    {
        if (string.IsNullOrWhiteSpace(response)) return "";

        Match match = Regex.Match(
            response,
            @"THINKING\s*:\s*(.+?)(?:\n[A-Z_]+\s*:|$)",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);

        return match.Success ? match.Groups[1].Value.Trim() : "";
    }
}
