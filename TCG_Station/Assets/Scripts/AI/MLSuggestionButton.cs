using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// Scene-button helper for asking the Python ML server what it would do next.
/// This is an advisor only: it never executes actions and never mutates game state.
/// </summary>
public class MLSuggestionButton : MonoBehaviour
{
    [SerializeField] private PlayerManager playerManager;
    [SerializeField] private BoardVisualizer boardVisualizer;

    private static readonly GameActionType[] SupportedOrder =
    {
        GameActionType.PlayBasicPokemon,
        GameActionType.AttachEnergy,
        GameActionType.Retreat,
        GameActionType.Attack,
    };

    private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
    {
        NullValueHandling = NullValueHandling.Ignore,
        Formatting = Formatting.None,
        Converters = { new Newtonsoft.Json.Converters.StringEnumConverter() },
    };

    public void RequestMlSuggestion()
    {
        StopAllCoroutines();
        StartCoroutine(RequestMlSuggestionRoutine());
    }

    private IEnumerator RequestMlSuggestionRoutine()
    {
        if (playerManager == null)
            playerManager = PlayerManager.Instance;
        if (boardVisualizer == null)
            boardVisualizer = FindFirstObjectByType<BoardVisualizer>();

        if (playerManager == null || playerManager.activePlayer == null)
        {
            SetOutput("ML Suggestion\nNo active player found.");
            yield break;
        }

        PlayerController player = playerManager.activePlayer;
        PlayerController opponent = playerManager.player1 == player ? playerManager.player2 : playerManager.player1;
        if (opponent == null)
        {
            SetOutput("ML Suggestion\nOpponent not found.");
            yield break;
        }

        List<GameAction> legal = LegalActionGenerator.Generate(
            player, opponent, playerManager, includeFutureTurnActions: false);

        SetOutput($"ML Suggestion\nP{player.playerId} {player.playerName}: querying model...");
        yield return AdvisorEventReporter.Post(
            "ML",
            "start",
            $"Querying Python model for {legal.Count} legal action(s).",
            player);

        var lines = new List<string>
        {
            $"ML Suggestion — P{player.playerId} {player.playerName}",
            $"Turn {TurnManager.Instance?.turnCounter ?? 0}",
        };

        string firstExecutable = null;
        foreach (GameActionType type in SupportedOrder)
        {
            List<GameAction> candidates = legal.Where(a => a.type == type).ToList();
            if (candidates.Count == 0) continue;

            string category = CategoryName(type);
            PredictResponse response = null;
            yield return QueryPredict(player, opponent, category, candidates, r => response = r);

            if (response == null || !string.IsNullOrEmpty(response.error))
            {
                string error = response?.error ?? "empty response";
                lines.Add($"{category}: error {error}");
                yield return AdvisorEventReporter.Post("ML", "error", error, player, category);
                continue;
            }

            bool skipped = response.action_index < 0 ||
                           response.action_index >= candidates.Count ||
                           response.action_label == "(skip)";
            string suffix = skipped
                ? "skip"
                : $"{BuildDisplayLabel(category, candidates[response.action_index], player)} ({response.confidence:P1})";
            lines.Add($"{category}: {suffix}");
            yield return AdvisorEventReporter.Post(
                "ML",
                "category",
                skipped ? "Model chose skip." : "Model suggested an action.",
                player,
                category,
                response.action_label,
                response.confidence);

            if (!skipped && firstExecutable == null)
                firstExecutable = $"{category} -> {BuildDisplayLabel(category, candidates[response.action_index], player)} ({response.confidence:P1})";
        }

        lines.Add("");
        lines.Add(firstExecutable != null
            ? $"Suggested next action: {firstExecutable}"
            : "Suggested next action: no ML-supported action");

        SetOutput(string.Join("\n", lines));
        yield return AdvisorEventReporter.Post(
            "ML",
            "final",
            firstExecutable != null ? $"Suggested next action: {firstExecutable}" : "No ML-supported action suggested.",
            player);
    }

    private IEnumerator QueryPredict(
        PlayerController player,
        PlayerController opponent,
        string category,
        List<GameAction> candidates,
        Action<PredictResponse> onResponse)
    {
        var snapshot = GameStateSnapshot.Create(
            player,
            opponent,
            TurnManager.Instance != null ? TurnManager.Instance.turnCounter : 0,
            player.playerId);

        var actions = new List<ActionDto>(candidates.Count + 1);
        foreach (GameAction action in candidates)
        {
            actions.Add(new ActionDto
            {
                label = BuildLabel(category, action, player),
                category = category,
                target_instance_id = TargetInstanceId(action, player),
            });
        }
        actions.Add(new ActionDto { label = "(skip)", category = category, target_instance_id = -1 });

        string url = GameRulesConfig.Instance != null
            ? GameRulesConfig.Instance.mlServerUrl
            : "http://127.0.0.1:8000/predict";
        string json = JsonConvert.SerializeObject(
            new PredictRequest { snapshot = snapshot, legal_actions = actions },
            JsonSettings);

        using var www = new UnityWebRequest(url, "POST");
        www.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
        www.downloadHandler = new DownloadHandlerBuffer();
        www.SetRequestHeader("Content-Type", "application/json");
        yield return www.SendWebRequest();

        if (www.result != UnityWebRequest.Result.Success)
        {
            onResponse(new PredictResponse { error = $"{www.error} ({url})" });
            yield break;
        }

        try
        {
            onResponse(JsonConvert.DeserializeObject<PredictResponse>(www.downloadHandler.text));
        }
        catch (Exception ex)
        {
            onResponse(new PredictResponse { error = ex.Message });
        }
    }

    private void SetOutput(string message)
    {
        if (boardVisualizer == null)
            boardVisualizer = FindFirstObjectByType<BoardVisualizer>();

        if (boardVisualizer != null && boardVisualizer.llmThinkingLog != null)
            boardVisualizer.llmThinkingLog.text = message;

        Debug.Log($"[MLSuggestionButton] {message}");
    }

    private static string CategoryName(GameActionType type)
    {
        return type switch
        {
            GameActionType.PlayBasicPokemon => "PlayBasic",
            GameActionType.AttachEnergy => "AttachEnergy",
            GameActionType.Retreat => "Retreat",
            GameActionType.Attack => "Attack",
            _ => type.ToString(),
        };
    }

    private static string BuildLabel(string category, GameAction action, PlayerController player)
    {
        return category switch
        {
            "PlayBasic" => $"PlayBasic({action.card?.baseData?.cardName ?? "?"})",
            "AttachEnergy" => $"AttachEnergy(to {action.target?.baseData?.cardName ?? "?"})",
            "Retreat" => $"Retreat(to {action.target?.baseData?.cardName ?? "?"})",
            "Attack" => AttackLabel(action.attackIndex, player),
            _ => action.ToString(),
        };
    }

    private static string BuildDisplayLabel(string category, GameAction action, PlayerController player)
    {
        string target = action.target?.baseData?.cardName ?? action.card?.baseData?.cardName ?? "?";
        string position = TargetPosition(action, player);

        return category switch
        {
            "PlayBasic" => $"Play {target}",
            "AttachEnergy" => $"Attach Energy to {target}{position}",
            "Retreat" => $"Retreat to {target}{position}",
            "Attack" => AttackLabel(action.attackIndex, player),
            _ => BuildLabel(category, action, player),
        };
    }

    private static string TargetPosition(GameAction action, PlayerController player)
    {
        CardInstance target = action.type == GameActionType.PlayBasicPokemon ? action.card : action.target;
        if (target == null || player == null)
            return "";

        if (target == player.activePokemon)
            return " [ACTIVE]";

        int benchIndex = player.benchPokemons != null ? player.benchPokemons.IndexOf(target) : -1;
        if (benchIndex >= 0)
            return $" [BENCH {benchIndex + 1}]";

        return "";
    }

    private static string AttackLabel(int index, PlayerController player)
    {
        if (player?.activePokemon?.baseData is PokemonData data &&
            data.attacks != null &&
            index >= 0 &&
            index < data.attacks.Count)
        {
            return $"Attack[{index}] {data.attacks[index]?.attackName ?? "?"}";
        }
        return $"Attack[{index}]";
    }

    private static int TargetInstanceId(GameAction action, PlayerController player)
    {
        return action.type switch
        {
            GameActionType.PlayBasicPokemon => action.card?.instanceId ?? -1,
            GameActionType.Attack => player?.activePokemon?.instanceId ?? -1,
            _ => action.target?.instanceId ?? -1,
        };
    }

    [Serializable]
    private class PredictRequest
    {
        public GameStateSnapshot snapshot;
        public List<ActionDto> legal_actions;
    }

    [Serializable]
    private class ActionDto
    {
        public string label;
        public string category;
        public int target_instance_id;
    }

    [Serializable]
    private class PredictResponse
    {
        public int action_index = -1;
        public string action_label;
        public float confidence;
        public string error;
    }
}
