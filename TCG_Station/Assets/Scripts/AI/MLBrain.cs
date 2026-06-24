using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// Behavioral-cloning agent. Queries the Python inference server (FastAPI serve.py, /predict)
/// for the four decision categories present in the BC dataset — PlayBasic, AttachEnergy, Retreat,
/// Attack — choosing one candidate target (or an explicit skip) per category.
///
/// The dataset only contains those four per-category decisions, so the model never learned setup,
/// evolution, trainer, or cross-category action selection. Those phases reuse the AlgorithmBrain
/// heuristic via a helper instance (PerformSetupPhase / PerformEvolutionPhase / PerformTrainerPhase),
/// keeping the turn structure identical to the teacher and the comparison clean: ML replaces only
/// the within-category target selection the dataset captured.
///
/// Tier-2 client side of docs/ML_PIPELINE.md. Request/response contract matches serve.py /predict.
/// </summary>
public class MLBrain : PlayerBrain
{
    private const int MaxBasicPlaysPerTurn = 8;

    private AlgorithmBrain heuristicHelper; // setup, evolutions, trainers (not in the BC dataset)
    private BoardVisualizer boardVisualizer;
    private string serverUrl;

    private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
    {
        NullValueHandling = NullValueHandling.Ignore,
        Formatting = Formatting.None,
        Converters = { new Newtonsoft.Json.Converters.StringEnumConverter() },
    };

    public override void Initialize(PlayerController controller)
    {
        base.Initialize(controller);
        serverUrl = GameRulesConfig.Instance != null
            ? GameRulesConfig.Instance.mlServerUrl
            : "http://127.0.0.1:8000/predict";
        boardVisualizer = FindFirstObjectByType<BoardVisualizer>();

        // Helper used only for phases the model cannot decide. It is never registered as the player's
        // brain, so its PerformTurn is never invoked — we call its phase methods directly.
        heuristicHelper = gameObject.AddComponent<AlgorithmBrain>();
        heuristicHelper.Initialize(controller);
    }

    public override IEnumerator PerformSetupPhase(Action<List<CardInstance>> onSetupComplete)
    {
        // No setup policy in the BC dataset — reuse the Algorithm heuristic.
        yield return heuristicHelper.PerformSetupPhase(onSetupComplete);
    }

    public override IEnumerator PerformTurn()
    {
        Debug.Log($"[MLBrain] Turn for {myPlayer.playerName}, active: {myPlayer.activePokemon?.baseData?.cardName ?? "NULL"}");
        PlayerController opponent = playerManager.player1 == myPlayer ? playerManager.player2 : playerManager.player1;
        GameRulesConfig cfg = GameRulesConfig.Instance;

        // Phase 1 — PlayBasic (ML, looped: one query per basic until the model skips or the bench fills).
        yield return MlPlayBasicPhase(opponent, cfg);
        if (!IsStillActiveTurn()) yield break;

        // Phases 2 & 3 — evolutions and trainers reuse the heuristic (not in the BC dataset).
        yield return heuristicHelper.PerformEvolutionPhase(opponent);
        if (!IsStillActiveTurn()) yield break;
        yield return heuristicHelper.PerformTrainerPhase(opponent);
        if (!IsStillActiveTurn()) yield break;

        // Phase 4 — AttachEnergy (ML, single: one energy per turn).
        yield return MlAttachEnergyPhase(opponent, cfg);
        if (!IsStillActiveTurn()) yield break;

        // Phase 5 — Retreat (ML), suppressed when a SwapSelf trainer already repositioned the active.
        if (!heuristicHelper.SwappedActiveViaTrainerThisTurn)
        {
            yield return MlRetreatPhase(opponent, cfg);
            if (!IsStillActiveTurn()) yield break;
        }

        // Phase 6 — Attack (ML, single: ends the turn).
        yield return MlAttackPhase(opponent, cfg);
        if (!IsStillActiveTurn()) yield break;

        yield return new WaitForSeconds(cfg != null ? cfg.aiEndTurnDelay : 0.5f);
        TurnManager.Instance.RequestEndTurn();
    }

    // ── Per-category ML phases ────────────────────────────────────────────────

    private IEnumerator MlPlayBasicPhase(PlayerController opponent, GameRulesConfig cfg)
    {
        for (int i = 0; i < MaxBasicPlaysPerTurn; i++)
        {
            if (!IsStillActiveTurn()) yield break;
            List<GameAction> candidates = LegalCandidates(opponent, GameActionType.PlayBasicPokemon);
            if (candidates.Count == 0) yield break;

            int choice = -1;
            yield return QueryPredict("PlayBasic", candidates, opponent, idx => choice = idx);
            if (choice < 0) yield break; // model skipped

            GameAction action = candidates[choice];
            Debug.Log($"[MLBrain] PlayBasic -> {BuildLabel("PlayBasic", action)}");
            GameActionExecutor.Execute(action, myPlayer, playerManager);
            yield return new WaitForSeconds(cfg != null ? cfg.aiPlayCardDelay : 1.5f);
        }
    }

    private IEnumerator MlAttachEnergyPhase(PlayerController opponent, GameRulesConfig cfg)
    {
        List<GameAction> candidates = LegalCandidates(opponent, GameActionType.AttachEnergy);
        if (candidates.Count == 0) yield break;

        int choice = -1;
        yield return QueryPredict("AttachEnergy", candidates, opponent, idx => choice = idx);
        if (choice < 0) yield break;

        GameAction action = candidates[choice];
        Debug.Log($"[MLBrain] AttachEnergy -> {BuildLabel("AttachEnergy", action)}");
        GameActionExecutor.Execute(action, myPlayer, playerManager);
        yield return new WaitForSeconds(cfg != null ? cfg.aiAttachEnergyDelay : 1.5f);
    }

    private IEnumerator MlRetreatPhase(PlayerController opponent, GameRulesConfig cfg)
    {
        List<GameAction> candidates = LegalCandidates(opponent, GameActionType.Retreat);
        if (candidates.Count == 0) yield break;

        int choice = -1;
        yield return QueryPredict("Retreat", candidates, opponent, idx => choice = idx);
        if (choice < 0) yield break;

        GameAction action = candidates[choice];
        Debug.Log($"[MLBrain] Retreat -> {BuildLabel("Retreat", action)}");
        GameActionExecutor.Execute(action, myPlayer, playerManager);
        yield return new WaitForSeconds(cfg != null ? cfg.aiPlayCardDelay : 1.0f);
    }

    private IEnumerator MlAttackPhase(PlayerController opponent, GameRulesConfig cfg)
    {
        List<GameAction> candidates = LegalCandidates(opponent, GameActionType.Attack);
        if (candidates.Count == 0) yield break;

        int choice = -1;
        yield return QueryPredict("Attack", candidates, opponent, idx => choice = idx);
        if (choice < 0) yield break;

        GameAction action = candidates[choice];
        Debug.Log($"[MLBrain] Attack -> {BuildLabel("Attack", action)}");
        GameActionExecutor.Execute(action, myPlayer, playerManager);
        yield return new WaitForSeconds(cfg != null ? cfg.aiAttackDelay : 1.5f);
    }

    // ── Inference ─────────────────────────────────────────────────────────────

    private List<GameAction> LegalCandidates(PlayerController opponent, GameActionType type)
    {
        return LegalActionGenerator
            .Generate(myPlayer, opponent, playerManager, includeFutureTurnActions: false)
            .Where(a => a.type == type)
            .ToList();
    }

    /// Builds the /predict payload (snapshot + candidates + synthetic skip), POSTs it, and reports the
    /// chosen candidate index via <paramref name="onResult"/> — or -1 when the model picks skip or the
    /// request fails (a safe no-op for that category).
    private IEnumerator QueryPredict(string category, List<GameAction> candidates, PlayerController opponent, Action<int> onResult)
    {
        var snapshot = GameStateSnapshot.Create(
            myPlayer, opponent, TurnManager.Instance.turnCounter, myPlayer.playerId);

        var actions = new List<ActionDto>(candidates.Count + 1);
        foreach (GameAction a in candidates)
            actions.Add(new ActionDto
            {
                label = BuildLabel(category, a),
                category = category,
                target_instance_id = TargetInstanceId(a),
            });
        // Synthetic skip — matches the training group, which always appends a "(skip)" candidate.
        int skipIndex = candidates.Count;
        actions.Add(new ActionDto { label = "(skip)", category = category, target_instance_id = -1 });

        string json = JsonConvert.SerializeObject(new PredictRequest { snapshot = snapshot, legal_actions = actions }, JsonSettings);

        // Read the endpoint fresh so an Inspector/JSON preset change takes effect without a restart.
        string url = GameRulesConfig.Instance != null ? GameRulesConfig.Instance.mlServerUrl : serverUrl;

        string responseText = null;
        using (var www = new UnityWebRequest(url, "POST"))
        {
            www.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
            www.downloadHandler = new DownloadHandlerBuffer();
            www.SetRequestHeader("Content-Type", "application/json");
            yield return www.SendWebRequest();

            if (www.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"[MLBrain] {category} request failed: {www.error} ({url}). Skipping category.");
                onResult(-1);
                yield break;
            }
            responseText = www.downloadHandler.text;
        }

        PredictResponse resp = null;
        try { resp = JsonConvert.DeserializeObject<PredictResponse>(responseText); }
        catch (Exception ex) { Debug.LogError($"[MLBrain] {category} response parse error: {ex.Message} | raw: {responseText}"); }

        if (resp == null || !string.IsNullOrEmpty(resp.error))
        {
            Debug.LogError($"[MLBrain] {category} server error: {resp?.error ?? "null response"}. Skipping category.");
            onResult(-1);
            yield break;
        }

        if (resp.action_index < 0 || resp.action_index > skipIndex)
        {
            Debug.LogWarning($"[MLBrain] {category} action_index {resp.action_index} out of range (0..{skipIndex}). Skipping category.");
            onResult(-1);
            yield break;
        }

        SetThinkingUI($"{category}: {resp.action_label} (conf {resp.confidence:0.00})");
        onResult(resp.action_index == skipIndex ? -1 : resp.action_index);
    }

    // Reproduces the AlgorithmBrain label format the dataset was logged with, so the Python feature
    // encoder reads the same target card name for card_static (see features.py action_vector).
    private string BuildLabel(string category, GameAction a)
    {
        switch (category)
        {
            case "PlayBasic":    return $"PlayBasic({a.card?.baseData?.cardName ?? "?"})";
            case "AttachEnergy": return $"AttachEnergy(to {a.target?.baseData?.cardName ?? "?"})";
            case "Retreat":      return $"Retreat(to {a.target?.baseData?.cardName ?? "?"})";
            case "Attack":       return AttackLabel(a.attackIndex);
            default:             return a.ToString();
        }
    }

    private string AttackLabel(int index)
    {
        if (myPlayer.activePokemon?.baseData is PokemonData pd && pd.attacks != null &&
            index >= 0 && index < pd.attacks.Count)
            return $"Attack[{index}] {pd.attacks[index]?.attackName ?? "?"}";
        return $"Attack[{index}]";
    }

    // Matches the target identity the dataset logged: AttachEnergy/Retreat target the board Pokemon,
    // Attack targets the active (Target=active in AlgorithmBrain), PlayBasic targets the hand card.
    private int TargetInstanceId(GameAction a)
    {
        switch (a.type)
        {
            case GameActionType.PlayBasicPokemon: return a.card?.instanceId ?? -1;
            case GameActionType.Attack:           return myPlayer.activePokemon?.instanceId ?? -1;
            default:                              return a.target?.instanceId ?? -1; // AttachEnergy, Retreat
        }
    }

    private bool IsStillActiveTurn()
    {
        if (playerManager != null && playerManager.activePlayer == myPlayer) return true;
        Debug.Log($"[MLBrain] Turn ended for {myPlayer.playerName}.");
        return false;
    }

    private void SetThinkingUI(string message)
    {
        if (boardVisualizer == null || boardVisualizer.llmThinkingLog == null) return;
        boardVisualizer.llmThinkingLog.text = $"{myPlayer?.playerName} — ML / BC\n{message}";
    }

    // ── DTOs (match serve.py /predict contract) ───────────────────────────────

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
        public float inference_ms;
        public string error;
    }
}
