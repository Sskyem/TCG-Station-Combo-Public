using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

public class LLMBrain : PlayerBrain
{
    private const int MaxOllamaActionsPerTurn = 12;

    public static event Action<PlayerController, EnumGeminiModel> OnGeminiApiFailed;

    private BoardVisualizer boardVisualizer;
    private ILLMClient llmClient;

    private readonly List<string> _gameHistory = new List<string>();
    private string _promptLogPath;

    private void EnsurePromptLogPath()
    {
        if (GameRulesConfig.Instance != null && !GameRulesConfig.Instance.enableLlmPromptLogs) return;
        if (_promptLogPath != null) return;
        string dir = Path.Combine(RuntimePaths.LogsRoot(), "LLM Prompts");
        Directory.CreateDirectory(dir);
        string playerSlug = myPlayer?.playerName?.Replace(" ", "_") ?? "unknown";
        _promptLogPath = Path.Combine(dir, $"llm_{playerSlug}_{DateTime.Now:yyyyMMdd_HHmmss_fff}.txt");

        EnumLlmProvider provider = GetMyProvider();
        string model = GetMyModelName();
        string mode = provider == EnumLlmProvider.Ollama
            ? "ACTION_INDEX (step-by-step, 1 request per action)"
            : "ACTION_SEQUENCE (full turn, 1 request per turn)";

        var header = new StringBuilder();
        header.AppendLine(new string('#', 60));
        header.AppendLine($"# SESSION: {provider} / {model}");
        header.AppendLine($"# Player:  {myPlayer?.playerName ?? "unknown"}");
        header.AppendLine($"# Mode:    {mode}");
        header.AppendLine($"# Started: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        header.AppendLine(new string('#', 60));
        header.AppendLine();
        File.WriteAllText(_promptLogPath, header.ToString(), Encoding.UTF8);
    }

    private void AppendPromptOnly(string playerName, int turn, string prompt, int? step = null)
    {
        if (GameRulesConfig.Instance != null && !GameRulesConfig.Instance.enableLlmPromptLogs) return;
        EnsurePromptLogPath();
        if (_promptLogPath == null) return;
        var sb = new StringBuilder();
        sb.AppendLine(new string('=', 60));
        string stepSuffix = step.HasValue ? $" | Step {step.Value}" : "";
        sb.AppendLine($"Turn {turn}{stepSuffix} | {playerName} | {DateTime.Now:HH:mm:ss}");
        sb.AppendLine(new string('=', 60));
        sb.AppendLine("--- PROMPT ---");
        sb.AppendLine(prompt);
        sb.AppendLine("--- RESPONSE (pending) ---");
        File.AppendAllText(_promptLogPath, sb.ToString(), Encoding.UTF8);
    }

    private void AppendResponseOnly(string response)
    {
        if (GameRulesConfig.Instance != null && !GameRulesConfig.Instance.enableLlmPromptLogs) return;
        if (_promptLogPath == null) return;
        var sb = new StringBuilder();
        sb.AppendLine(string.IsNullOrWhiteSpace(response) ? "(empty)" : response);
        sb.AppendLine();
        File.AppendAllText(_promptLogPath, sb.ToString(), Encoding.UTF8);
    }

    public override void Initialize(PlayerController controller)
    {
        base.Initialize(controller);
        boardVisualizer = FindFirstObjectByType<BoardVisualizer>();
        llmClient = ResolveLlmClient();
        _promptLogPath = null; // new file per game session
        _gameHistory.Clear();
        GeminiApiClient.OnFirstRateLimitHit -= HandleGeminiRateLimitHit;
        GeminiApiClient.OnFirstRateLimitHit += HandleGeminiRateLimitHit;
    }

    private void OnDestroy()
    {
        GeminiApiClient.OnFirstRateLimitHit -= HandleGeminiRateLimitHit;
    }

    private void HandleGeminiRateLimitHit(GeminiApiClient sourceClient, EnumGeminiModel model)
    {
        if (GetMyProvider() != EnumLlmProvider.Gemini) return;
        if (!ReferenceEquals(llmClient, sourceClient)) return;
        if (GetMyGeminiModel() != model) return;
        if (GameRulesConfig.Instance == null) return;

        EnumGeminiModel fallback = GetFallbackGeminiModel(model);
        if (IsPlayer1)
            GameRulesConfig.Instance.player1GeminiModel = fallback;
        else
            GameRulesConfig.Instance.player2GeminiModel = fallback;

        if (llmClient is GeminiApiClient geminiClient)
            geminiClient.SetModelOverride(fallback);

        Debug.LogWarning($"[LLMBrain] Auto-switched P{myPlayer.playerId} Gemini model after rate limit: {model} -> {fallback}.");
        OnGeminiApiFailed?.Invoke(myPlayer, model);
    }

    public override IEnumerator PerformSetupPhase(System.Action<List<CardInstance>> onSetupComplete)
    {
        Debug.Log($"[LLMBrain] Zaczynam myslec nad faza setupu dla gracza {myPlayer.playerName}...");

        List<CardInstance> availablePokemons = GetBasicPokemonsInHand();
        if (boardVisualizer != null && boardVisualizer.llmThinkingLog != null)
        {
            SetThinkingUI("Analizuje dostepne Basic Pokemony...");
        }

        if (availablePokemons.Count == 0)
        {
            Debug.LogWarning("[LLMBrain] Brak Basic Pokemonow na rece.");
            if (boardVisualizer != null && boardVisualizer.llmThinkingLog != null)
            {
                SetThinkingUI("Brak Basic Pokemonow na rece, wiec nie ma poprawnego wyboru.");
            }

            onSetupComplete?.Invoke(new List<CardInstance>());
            yield break;
        }

        string prompt = LlmPromptBuilder.BuildSetupPrompt(availablePokemons, GetMyProvider());

        llmClient ??= ResolveLlmClient();
        if (llmClient == null)
        {
            Debug.LogError("[LLMBrain] Nie znaleziono klienta LLM.");
            onSetupComplete?.Invoke(new List<CardInstance> { availablePokemons[0] });
            yield break;
        }

        AppendPromptOnly(myPlayer.playerName, 0, prompt);

        string aiChoice = null;
        yield return llmClient.SendPrompt(prompt, response => aiChoice = response);

        AppendResponseOnly(aiChoice);

        if (string.IsNullOrWhiteSpace(aiChoice))
        {
            Debug.LogWarning("[LLMBrain] Provider LLM nie zwrócił odpowiedzi. Wybieram awaryjnie pierwszą kartę.");
            if (GetMyProvider() == EnumLlmProvider.Gemini)
                OnGeminiApiFailed?.Invoke(myPlayer, GetMyGeminiModel());
            onSetupComplete?.Invoke(new List<CardInstance> { availablePokemons[0] });
            yield break;
        }

        aiChoice = aiChoice.Trim();
        if (boardVisualizer != null && boardVisualizer.llmThinkingLog != null)
        {
            string thinkingText = ExtractThinkingText(aiChoice);
            SetThinkingUI(thinkingText);
        }

        Debug.Log($"[LLMBrain] AI wybralo: {aiChoice}");

        string chosenCardId = ExtractChosenCardId(aiChoice);
        if (!string.IsNullOrEmpty(chosenCardId))
        {
            Debug.Log($"[LLMBrain] Sparsowane WYBOR_ID: {chosenCardId}");
        }
        else
        {
            Debug.LogWarning("[LLMBrain] Nie udalo sie sparsowac pola WYBOR_ID. Uzywam awaryjnego dopasowania po calej odpowiedzi.");
        }

        CardInstance chosenCard = FindCardByParsedId(availablePokemons, chosenCardId)
                                  ?? FindCardByParsedOptionNumber(availablePokemons, chosenCardId)
                                  ?? FindCardByMentionedId(availablePokemons, aiChoice)
                                  ?? FindCardByMentionedName(availablePokemons, aiChoice);

        List<CardInstance> cardsToPlay = new List<CardInstance>();
        if (chosenCard != null)
        {
            cardsToPlay.Add(chosenCard);
        }
        else
        {
            Debug.LogWarning("[LLMBrain] AI podjelo niezrozumiala decyzje. Wybieram awaryjnie pierwsza karte.");
            cardsToPlay.Add(availablePokemons[0]);
        }

        onSetupComplete?.Invoke(cardsToPlay);
    }

    public override IEnumerator PerformTurn()
    {
        Debug.Log($"[LLMBrain] Turn for player {myPlayer.playerName}, active: {myPlayer.activePokemon?.baseData?.cardName ?? "NULL"}");

        PlayerController opponent = playerManager.player1 == myPlayer ? playerManager.player2 : playerManager.player1;

        // Always re-resolve for Gemini so a mid-game model switch (fallback button) takes effect immediately.
        if (GetMyProvider() == EnumLlmProvider.Gemini)
            llmClient = ResolveLlmClient();
        else
            llmClient ??= ResolveLlmClient();
        if (llmClient == null)
        {
            Debug.LogError("[LLMBrain] No LLM client — ending turn.");
            TurnManager.Instance.RequestEndTurn();
            yield break;
        }

        float delay = GameRulesConfig.Instance != null
            ? GameRulesConfig.Instance.GetEffectiveLlmTurnDelay(GetMyProvider(), GetMyGeminiModel())
            : 0f;
        if (delay > 0f)
        {
            Debug.Log($"[LLMBrain] Rate limit delay: {delay}s");
            yield return new WaitForSeconds(delay);
        }

        if (GetMyProvider() == EnumLlmProvider.Ollama)
        {
            yield return PerformOllamaStepByStepTurn(opponent);
            yield break;
        }

        List<GameAction> legalActions = LegalActionGenerator.Generate(myPlayer, opponent, playerManager);

        if (legalActions.Count == 1 && legalActions[0].type == GameActionType.EndTurn)
        {
            Debug.Log("[LLMBrain] Only legal action is EndTurn.");
            TurnManager.Instance.RequestEndTurn();
            yield break;
        }

        GameStateSnapshot snapshot = GameStateSnapshot.Create(
            myPlayer, opponent,
            TurnManager.Instance.turnCounter,
            myPlayer.playerId);

        string history = BuildRecentHistory(GetMaxHistoryEntries(EnumLlmProvider.Gemini));
        string prompt = LlmPromptBuilder.BuildTurnPrompt(snapshot, legalActions, history, GetMyProvider());

        AppendPromptOnly(myPlayer.playerName, TurnManager.Instance.turnCounter, prompt);

        string aiResponse = null;
        yield return llmClient.SendPrompt(prompt, r => aiResponse = r);
        AppendResponseOnly(aiResponse);

        // Extract thinking from the first response before any retry overwrites aiResponse.
        string thinking = ExtractThinkingText(aiResponse);

        // Retry once if response arrived but contains no ACTION_SEQUENCE.
        // Resend the FULL original prompt (board + legal actions) plus a corrective note: the API call
        // is stateless, so a bare "you forgot ACTION_SEQUENCE" retry would have no board context and the
        // model would pick indices blindly. Re-supplying the prompt lets it decide properly.
        // The observed failure is the model writing a long THINKING and stopping before the action line,
        // so the retry forbids THINKING and asks for the single ACTION_SEQUENCE line only.
        if (!string.IsNullOrWhiteSpace(aiResponse) && !aiResponse.Contains("ACTION_SEQUENCE"))
        {
            Debug.LogWarning("[LLMBrain] Response missing ACTION_SEQUENCE — retrying with full-context prompt.");
            string retryPrompt = prompt +
                $"\n\nIMPORTANT: your previous reply was rejected because it had no ACTION_SEQUENCE line. " +
                $"Do NOT write a THINKING line this time. Reply with ONLY this single line and nothing else:\n" +
                $"ACTION_SEQUENCE: <comma-separated indices from LEGAL ACTIONS, with EndTurn ({legalActions.Count - 1}) last>";
            yield return llmClient.SendPrompt(retryPrompt, r => aiResponse = r);
            AppendResponseOnly($"[RETRY] {aiResponse}");
            // Keep the reasoning from the first attempt — the retry intentionally omits THINKING.
        }

        if (string.IsNullOrWhiteSpace(aiResponse))
        {
            Debug.LogWarning("[LLMBrain] Empty response — ending turn.");
            if (GetMyProvider() == EnumLlmProvider.Gemini)
                OnGeminiApiFailed?.Invoke(myPlayer, GetMyGeminiModel());
            TurnManager.Instance.RequestEndTurn();
            yield break;
        }

        _gameHistory.Add($"Turn {TurnManager.Instance.turnCounter}: {thinking}");
        UpdateThinkingUI(thinking.Length > 0 ? $"THINKING: {thinking}" : aiResponse);
        Debug.Log($"[LLMBrain] Response: {aiResponse}");

        List<int> sequence = ParseActionSequence(aiResponse, legalActions.Count);
        sequence = ReorderEnergyBeforeEvolve(sequence, legalActions);
        if (ShouldRetryAttackIntentEndTurnOnly(sequence, legalActions, thinking))
        {
            Debug.LogWarning("[LLMBrain] Response says it will attack but ACTION_SEQUENCE contains only EndTurn — retrying sequence line.");
            string retryPrompt = prompt +
                $"\n\nIMPORTANT: your THINKING said you will attack, but your ACTION_SEQUENCE contained only EndTurn. " +
                $"Reply with ONLY this single line and nothing else. Include the Attack index before EndTurn ({legalActions.Count - 1}):\n" +
                $"ACTION_SEQUENCE: <comma-separated indices from LEGAL ACTIONS, with Attack before EndTurn>";
            yield return llmClient.SendPrompt(retryPrompt, r => aiResponse = r);
            AppendResponseOnly($"[RETRY_INVALID_SEQUENCE] {aiResponse}");

            sequence = ParseActionSequence(aiResponse, legalActions.Count);
            sequence = ReorderEnergyBeforeEvolve(sequence, legalActions);
        }
        sequence = SanitizeSequenceAfterAttack(sequence, legalActions);
        Debug.Log($"[LLMBrain] Planned sequence: [{string.Join(", ", sequence)}]");

        LLMLogger.Instance?.LogTurn(
            TurnManager.Instance.turnCounter,
            myPlayer.playerId,
            GetMyProvider().ToString(),
            GetMyModelName(),
            "sequence",
            sequence.Select(i => legalActions[i].ToString()).ToList(),
            thinking,
            legalActions.Count);

        foreach (int index in sequence)
        {
            if (!IsStillActiveTurn()) yield break;

            GameAction action = legalActions[index];
            Debug.Log($"[LLMBrain] Executing [{index}]: {action}");
            GameActionExecutor.Execute(action, myPlayer, playerManager);

            if (action.type == GameActionType.EndTurn)
                yield break;

            GameRulesConfig cfg = GameRulesConfig.Instance;

            if (action.type == GameActionType.Attack)
            {
                float attackDelay = cfg != null ? cfg.aiAttackDelay : 1.5f;
                yield return new WaitForSeconds(attackDelay);
                break; // no actions allowed after Attack
            }

            float actionDelay = action.type switch
            {
                GameActionType.PlayBasicPokemon => cfg != null ? cfg.aiPlayCardDelay : 1.5f,
                GameActionType.PlayTrainer      => cfg != null ? cfg.aiPlayCardDelay : 1.5f,
                GameActionType.AttachEnergy     => cfg != null ? cfg.aiAttachEnergyDelay : 1.5f,
                GameActionType.Evolve           => cfg != null ? cfg.aiPlayCardDelay : 1.5f,
                GameActionType.Retreat          => cfg != null ? cfg.aiPlayCardDelay : 1.0f,
                _                               => 0f,
            };

            if (actionDelay > 0f)
                yield return new WaitForSeconds(actionDelay);
            else
                yield return null;

            if (!IsStillActiveTurn()) yield break;
        }

        TurnManager.Instance.RequestEndTurn();
    }

    private IEnumerator PerformOllamaStepByStepTurn(PlayerController opponent)
    {
        int turnNumber = TurnManager.Instance.turnCounter;
        var executedActions = new List<string>();
        var thinkingSteps = new List<string>();

        for (int step = 1; step <= MaxOllamaActionsPerTurn; step++)
        {
            if (!IsStillActiveTurn()) yield break;

            List<GameAction> legalActions = LegalActionGenerator.Generate(
                myPlayer,
                opponent,
                playerManager,
                includeFutureTurnActions: false);

            if (legalActions.Count == 1 && legalActions[0].type == GameActionType.EndTurn)
            {
                Debug.Log("[LLMBrain] Ollama step mode: only legal action is EndTurn.");
                RecordOllamaTurnHistory(turnNumber, executedActions, thinkingSteps);
                TurnManager.Instance.RequestEndTurn();
                yield break;
            }

            GameStateSnapshot snapshot = GameStateSnapshot.Create(
                myPlayer,
                opponent,
                TurnManager.Instance.turnCounter,
                myPlayer.playerId);

            string history = BuildRecentHistory(GetMaxHistoryEntries(EnumLlmProvider.Ollama));
            string prompt = LlmPromptBuilder.BuildOllamaActionPrompt(
                snapshot,
                legalActions,
                executedActions,
                step,
                history);

            AppendPromptOnly(myPlayer.playerName, TurnManager.Instance.turnCounter, prompt, step);

            string aiResponse = null;
            yield return llmClient.SendPrompt(prompt, r => aiResponse = r);

            AppendResponseOnly(aiResponse);

            if (string.IsNullOrWhiteSpace(aiResponse))
            {
                Debug.LogWarning("[LLMBrain] Ollama step mode: empty response — ending turn.");
                RecordOllamaTurnHistory(turnNumber, executedActions, thinkingSteps);
                TurnManager.Instance.RequestEndTurn();
                yield break;
            }

            string thinking = ExtractThinkingText(aiResponse);
            thinkingSteps.Add($"Step {step}: {thinking}");
            UpdateThinkingUI(aiResponse);
            Debug.Log($"[LLMBrain] Ollama step response: {aiResponse}");

            int index = ParseActionIndex(aiResponse, legalActions.Count);
            GameAction action = legalActions[index];

            Debug.Log($"[LLMBrain] Ollama executing step {step} [{index}]: {action}");
            executedActions.Add(action.ToString());
            GameActionExecutor.Execute(action, myPlayer, playerManager);

            if (action.type == GameActionType.EndTurn)
            {
                RecordOllamaTurnHistory(turnNumber, executedActions, thinkingSteps);
                yield break;
            }

            GameRulesConfig cfg = GameRulesConfig.Instance;

            if (action.type == GameActionType.Attack)
            {
                float attackDelay = cfg != null ? cfg.aiAttackDelay : 1.5f;
                yield return new WaitForSeconds(attackDelay);
                RecordOllamaTurnHistory(turnNumber, executedActions, thinkingSteps);
                TurnManager.Instance.RequestEndTurn();
                yield break;
            }

            float actionDelay = action.type switch
            {
                GameActionType.PlayBasicPokemon => cfg != null ? cfg.aiPlayCardDelay : 1.5f,
                GameActionType.PlayTrainer      => cfg != null ? cfg.aiPlayCardDelay : 1.5f,
                GameActionType.AttachEnergy     => cfg != null ? cfg.aiAttachEnergyDelay : 1.5f,
                GameActionType.Evolve           => cfg != null ? cfg.aiPlayCardDelay : 1.5f,
                GameActionType.Retreat          => cfg != null ? cfg.aiPlayCardDelay : 1.0f,
                _                               => 0f,
            };

            if (actionDelay > 0f)
                yield return new WaitForSeconds(actionDelay);
            else
                yield return null;
        }

        if (IsStillActiveTurn())
        {
            Debug.LogWarning($"[LLMBrain] Ollama step mode reached max {MaxOllamaActionsPerTurn} actions — ending turn.");
            RecordOllamaTurnHistory(turnNumber, executedActions, thinkingSteps);
            TurnManager.Instance.RequestEndTurn();
        }
    }

    /// Asks the LLM to pick which benched Pokemon to promote after the active was KO'd.
    /// Only used for Ollama (free) — Gemini sticks to the heuristic in BattleManager.
    /// Invokes the callback with the chosen CardInstance; on any failure picks index 0.
    public IEnumerator ChooseNewActiveAfterKO(List<CardInstance> benchOptions, Action<CardInstance> callback)
    {
        if (benchOptions == null || benchOptions.Count == 0)
        {
            callback?.Invoke(null);
            yield break;
        }
        if (benchOptions.Count == 1)
        {
            callback?.Invoke(benchOptions[0]);
            yield break;
        }

        EnsurePromptLogPath();
        llmClient ??= ResolveLlmClient();
        if (llmClient == null)
        {
            Debug.LogWarning("[LLMBrain] No LLM client for new-active choice — defaulting to first bench Pokemon.");
            callback?.Invoke(benchOptions[0]);
            yield break;
        }

        string prompt = BuildNewActivePrompt(benchOptions);
        AppendPromptOnly(myPlayer.playerName, TurnManager.Instance != null ? TurnManager.Instance.turnCounter : 0, prompt, null);

        string aiResponse = null;
        yield return llmClient.SendPrompt(prompt, r => aiResponse = r);
        AppendResponseOnly(aiResponse);

        int idx = ParseNewActiveIndex(aiResponse, benchOptions.Count);
        Debug.Log($"[LLMBrain] New-active choice [{idx}]: {benchOptions[idx].baseData.cardName}");
        callback?.Invoke(benchOptions[idx]);
    }

    private string BuildNewActivePrompt(List<CardInstance> benchOptions)
    {
        var sb = new StringBuilder();

        if (_gameHistory.Count > 0)
        {
            sb.AppendLine("GAME HISTORY (your previous turns):");
            sb.AppendLine(string.Join("\n", _gameHistory));
            sb.AppendLine();
        }

        sb.AppendLine("=== PROMOTE NEW ACTIVE AFTER KO ===");
        sb.AppendLine("Your Active Pokemon was knocked out. Choose ONE bench Pokemon to promote as the new Active.");
        sb.AppendLine("The new Active will face the opponent's current Active next turn — pick someone who can damage them, survive their counter-attack, or buy time.");
        sb.AppendLine();

        sb.AppendLine($"Score: you {myPlayer.score}/{GameRulesConfig.Instance.pointsToWin}, opponent {GetOpponentScore()}/{GameRulesConfig.Instance.pointsToWin}.");
        sb.AppendLine($"Turn: {(TurnManager.Instance != null ? TurnManager.Instance.turnCounter : 0)}.");
        sb.AppendLine();

        AppendOpponentSection(sb);
        sb.AppendLine();

        sb.AppendLine("BENCH OPTIONS (your candidates for the new Active):");
        for (int i = 0; i < benchOptions.Count; i++)
            AppendBenchOption(sb, i, benchOptions[i]);
        sb.AppendLine();

        sb.AppendLine("Selection rules:");
        sb.AppendLine("- Prefer a Pokemon that already has [READY] on at least one attack — they hit back immediately.");
        sb.AppendLine("- If no one is ready, pick the one with the most progress toward their cheapest attack cost.");
        sb.AppendLine("- Avoid promoting a Pokemon with very low HP that the opponent's Active can OHKO next turn, unless you have no better option.");
        sb.AppendLine("- Higher retreat cost on the promoted Active is a long-term liability; treat low retreat as a tiebreaker.");
        sb.AppendLine();

        sb.AppendLine("OUTPUT FORMAT — follow exactly, in this order (three lines, nothing else):");
        sb.AppendLine("STATE: Opponent=<name HP/maxHP energy>, Score=<my>-<opp>, BestReadyAttacker=<name or none>");
        sb.AppendLine("THINKING: <one short sentence justifying your pick>");
        sb.AppendLine("ACTION_INDEX: <one index from BENCH OPTIONS>");
        sb.AppendLine();
        sb.AppendLine("Example:");
        sb.AppendLine("STATE: Opponent=Chandelure 160/160 2xFire, Score=1-2, BestReadyAttacker=Lampent.");
        sb.AppendLine("THINKING: Lampent can hit Chandelure for 60 with Will-O-Wisp next turn.");
        sb.AppendLine("ACTION_INDEX: 0");
        return sb.ToString().TrimEnd();
    }

    private int GetOpponentScore()
    {
        if (playerManager == null) return 0;
        var opp = playerManager.player1 == myPlayer ? playerManager.player2 : playerManager.player1;
        return opp?.score ?? 0;
    }

    private void AppendOpponentSection(StringBuilder sb)
    {
        if (playerManager == null) { sb.AppendLine("OPPONENT: (unknown)"); return; }
        var opp = playerManager.player1 == myPlayer ? playerManager.player2 : playerManager.player1;
        if (opp == null) { sb.AppendLine("OPPONENT: (unknown)"); return; }

        var active = opp.activePokemon;
        if (active?.pokemonLogic == null || active.baseData is not PokemonData ad)
        {
            sb.AppendLine("OPPONENT ACTIVE: (none)");
        }
        else
        {
            var pl = active.pokemonLogic;
            int energy = pl.energyEquipped != null ? pl.energyEquipped.Values.Sum() : 0;
            var atkParts = new List<string>();
            if (ad.attacks != null)
            {
                foreach (var atk in ad.attacks)
                {
                    string cost = atk.attackCost != null && atk.attackCost.Count > 0
                        ? string.Join(",", atk.attackCost)
                        : "0";
                    bool ready = CardActions.CanAffordAttack(pl, atk);
                    string readyTag = ready ? " [READY]" : "";
                    atkParts.Add($"{atk.attackName}[{cost}]->{atk.damage}dmg{readyTag}");
                }
            }
            string status = pl.isPoisoned || pl.isBurned || pl.otherSpecialCondition != EnumSpecialConditionType.None
                ? $" status:{(pl.isPoisoned ? "Poisoned " : "")}{(pl.isBurned ? "Burned " : "")}{(pl.otherSpecialCondition != EnumSpecialConditionType.None ? pl.otherSpecialCondition.ToString() : "")}".TrimEnd()
                : "";
            sb.AppendLine($"OPPONENT ACTIVE: {active.baseData.cardName} HP:{pl.currentHp}/{ad.hp} Energy:{energy} Retreat:{ad.retreatCost}{status} | {string.Join(", ", atkParts)}");
        }

        if (opp.benchPokemons != null && opp.benchPokemons.Count > 0)
        {
            var names = opp.benchPokemons
                .Where(c => c?.baseData != null)
                .Select(c => {
                    var pd = c.baseData as PokemonData;
                    int hp = c.pokemonLogic?.currentHp ?? 0;
                    int maxHp = pd?.hp ?? 0;
                    return $"{c.baseData.cardName} ({hp}/{maxHp})";
                });
            sb.AppendLine($"OPPONENT BENCH: {string.Join(", ", names)}");
        }
        else
        {
            sb.AppendLine("OPPONENT BENCH: (empty)");
        }
        sb.AppendLine($"Opponent hand count: {opp.hand?.Count ?? 0}");
    }

    private void AppendBenchOption(StringBuilder sb, int i, CardInstance card)
    {
        var pd = card.baseData as PokemonData;
        var pl = card.pokemonLogic;
        int hp = pl != null ? pl.currentHp : 0;
        int maxHp = pd != null ? pd.hp : 0;
        int energy = pl?.energyEquipped != null ? pl.energyEquipped.Values.Sum() : 0;
        int retreat = pd != null ? pd.retreatCost : 0;

        var atkParts = new List<string>();
        if (pd?.attacks != null)
        {
            foreach (var atk in pd.attacks)
            {
                string cost = atk.attackCost != null && atk.attackCost.Count > 0
                    ? string.Join(",", atk.attackCost)
                    : "0";
                bool ready = pl != null && CardActions.CanAffordAttack(pl, atk);
                string readyTag = ready ? " [READY]" : "";
                atkParts.Add($"{atk.attackName}[{cost}]->{atk.damage}dmg{readyTag}");
            }
        }
        string status = pl != null && (pl.isPoisoned || pl.isBurned || pl.otherSpecialCondition != EnumSpecialConditionType.None)
            ? $" status:{(pl.isPoisoned ? "Poisoned " : "")}{(pl.isBurned ? "Burned " : "")}{(pl.otherSpecialCondition != EnumSpecialConditionType.None ? pl.otherSpecialCondition.ToString() : "")}".TrimEnd()
            : "";

        string energyBreakdown = "";
        if (pl?.energyEquipped != null && energy > 0)
        {
            var parts = pl.energyEquipped
                .Where(kv => kv.Value > 0)
                .Select(kv => $"{kv.Value}x{kv.Key}");
            energyBreakdown = $" ({string.Join(",", parts)})";
        }

        sb.AppendLine($"  {i}. {card.baseData.cardName} HP:{hp}/{maxHp} Energy:{energy}{energyBreakdown} Retreat:{retreat}{status} | {string.Join(", ", atkParts)}");
    }

    private static int ParseNewActiveIndex(string aiResponse, int optionCount)
    {
        if (string.IsNullOrWhiteSpace(aiResponse)) return 0;

        Match stateMatch = Regex.Match(aiResponse, @"STATE\s*:\s*(.+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (stateMatch.Success)
            Debug.Log($"[LLMBrain] STATE anchor (new active): {stateMatch.Groups[1].Value.Trim()}");

        Match match = Regex.Match(aiResponse, @"ACTION_INDEX\s*:\s*(\d+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (match.Success &&
            int.TryParse(match.Groups[1].Value.Trim(), out int idx) &&
            idx >= 0 && idx < optionCount)
        {
            return idx;
        }

        Debug.LogWarning("[LLMBrain] Could not parse ACTION_INDEX for new active — defaulting to 0.");
        return 0;
    }

    private void RecordOllamaTurnHistory(int turnNumber, List<string> executedActions, List<string> thinkingSteps)
    {
        string actionSummary = executedActions.Count > 0
            ? string.Join(" -> ", executedActions)
            : "no actions";
        string thinkingSummary = thinkingSteps.Count > 0
            ? string.Join(" | ", thinkingSteps)
            : "no thinking";

        _gameHistory.Add($"Turn {turnNumber}: actions {actionSummary}");

        LLMLogger.Instance?.LogTurn(
            turnNumber,
            myPlayer.playerId,
            GetMyProvider().ToString(),
            GetMyModelName(),
            "step",
            executedActions,
            thinkingSummary,
            -1);
    }

    private string BuildRecentHistory(int maxEntries)
    {
        if (_gameHistory.Count == 0 || maxEntries <= 0) return null;

        int start = Mathf.Max(0, _gameHistory.Count - maxEntries);
        int omitted = start;
        var lines = new List<string>();
        if (omitted > 0)
            lines.Add($"...{omitted} older own turn(s) omitted");

        for (int i = start; i < _gameHistory.Count; i++)
            lines.Add(_gameHistory[i]);

        return string.Join("\n", lines);
    }

    private static int GetMaxHistoryEntries(EnumLlmProvider provider)
    {
        GameRulesConfig cfg = GameRulesConfig.Instance;
        if (cfg == null)
            return provider == EnumLlmProvider.Ollama ? 3 : 4;

        return provider == EnumLlmProvider.Ollama
            ? cfg.ollamaHistoryTurnsVisible
            : cfg.geminiHistoryTurnsVisible;
    }

    private string GetMyModelName()
    {
        return GetMyProvider() switch
        {
            EnumLlmProvider.Ollama => GetMyOllamaModel().ToString(),
            EnumLlmProvider.OpenAI => GetMyOpenAiModel().ToString(),
            _                      => GetMyGeminiModel().ToString(),
        };
    }

    private bool IsStillActiveTurn()
    {
        if (playerManager.activePlayer == myPlayer) return true;
        Debug.Log($"[LLMBrain] Turn ended for {myPlayer.playerName}.");
        return false;
    }

    private void UpdateThinkingUI(string aiResponse)
    {
        if (boardVisualizer == null || boardVisualizer.llmThinkingLog == null) return;
        string thinking = ExtractThinkingText(aiResponse);
        SetThinkingUI(thinking);
    }

    private void SetThinkingUI(string message)
    {
        if (boardVisualizer == null || boardVisualizer.llmThinkingLog == null) return;
        boardVisualizer.llmThinkingLog.text = $"{GetThinkingHeader()}\n{message}";
    }

    private string GetThinkingHeader()
    {
        string playerName = myPlayer?.playerName ?? "Unknown player";
        return $"{playerName} — {GetMyProvider()} / {GetMyModelName()}";
    }

    private static List<int> ParseActionSequence(string aiResponse, int actionCount)
    {
        var result = new List<int>();

        // Primary: the explicit "ACTION_SEQUENCE: 1, 2, 3" label.
        Match match = Regex.Match(
            aiResponse,
            @"ACTION_SEQUENCE\s*:\s*([\d,\s]+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (match.Success)
            AddValidIndices(match.Groups[1].Value, actionCount, result);

        // Salvage: the model gave indices but mislabeled the line (e.g. "ACTIONS: 3,4,6",
        // "Sequence -> 3, 4, 6", or a bare "3, 4, 6" line). Accept only a line that is
        // essentially just a (possibly labeled) comma-separated number list — never prose
        // with stray numbers. Requiring a comma keeps a single number inside a sentence
        // (e.g. "Tropius has 160 HP") from being misread as an action.
        if (result.Count == 0)
        {
            foreach (string rawLine in aiResponse.Split('\n'))
            {
                string line = rawLine.Trim();
                Match listMatch = Regex.Match(
                    line,
                    @"^(?:[A-Za-z_ ]*[:\->]\s*)?(\d+(?:\s*,\s*\d+)+)\s*$",
                    RegexOptions.CultureInvariant);
                if (!listMatch.Success) continue;

                AddValidIndices(listMatch.Groups[1].Value, actionCount, result);
                if (result.Count > 0)
                {
                    Debug.LogWarning($"[LLMBrain] ACTION_SEQUENCE label missing — salvaged indices from line: '{line}'.");
                    break;
                }
            }
        }

        if (result.Count == 0)
        {
            Debug.LogWarning($"[LLMBrain] Could not parse ACTION_SEQUENCE — falling back to EndTurn only.\nRaw response:\n{aiResponse}");
            result.Add(actionCount - 1);
        }

        // Deduplicate while preserving order
        var deduped = new List<int>();
        foreach (int idx in result)
            if (!deduped.Contains(idx)) deduped.Add(idx);
        result = deduped;

        // EndTurn exactly once, at the end
        int endTurnIdx = actionCount - 1;
        result.RemoveAll(x => x == endTurnIdx);
        result.Add(endTurnIdx);

        return result;
    }

    private static bool ShouldRetryAttackIntentEndTurnOnly(List<int> sequence, List<GameAction> legalActions, string thinking)
    {
        if (sequence == null || legalActions == null || legalActions.Count == 0) return false;
        int endTurnIdx = legalActions.Count - 1;
        if (sequence.Count != 1 || sequence[0] != endTurnIdx) return false;
        if (!legalActions.Any(a => a.type == GameActionType.Attack)) return false;
        if (string.IsNullOrWhiteSpace(thinking)) return false;

        return Regex.IsMatch(
            thinking,
            @"\b(i\s+will|i'll|plan\s+to|going\s+to)\s+attack\b|\battack\s+with\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static List<int> SanitizeSequenceAfterAttack(List<int> sequence, List<GameAction> legalActions)
    {
        if (sequence == null || legalActions == null || legalActions.Count == 0)
            return sequence ?? new List<int>();

        int endTurnIdx = legalActions.Count - 1;
        var result = new List<int>();

        foreach (int idx in sequence)
        {
            if (idx < 0 || idx >= legalActions.Count) continue;

            GameAction action = legalActions[idx];
            if (action.type == GameActionType.EndTurn)
            {
                if (!result.Contains(endTurnIdx))
                    result.Add(endTurnIdx);
                continue;
            }

            result.Add(idx);

            if (action.type == GameActionType.Attack)
            {
                if (!result.Contains(endTurnIdx))
                    result.Add(endTurnIdx);
                break;
            }
        }

        result.RemoveAll(idx => idx != endTurnIdx && legalActions[idx].type == GameActionType.EndTurn);
        if (!result.Contains(endTurnIdx))
            result.Add(endTurnIdx);

        return result;
    }

    /// Parses a comma-separated list of indices, appending the in-range ones to <paramref name="result"/>.
    private static void AddValidIndices(string csv, int actionCount, List<int> result)
    {
        foreach (string part in csv.Split(','))
        {
            string token = part.Trim();
            if (token.Length == 0) continue;
            if (int.TryParse(token, out int idx) && idx >= 0 && idx < actionCount)
                result.Add(idx);
            else
                Debug.LogWarning($"[LLMBrain] Skipping invalid index '{token}' (max {actionCount - 1}).");
        }
    }

    private static int ParseActionIndex(string aiResponse, int actionCount)
    {
        Match stateMatch = Regex.Match(
            aiResponse,
            @"STATE\s*:\s*(.+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (stateMatch.Success)
            Debug.Log($"[LLMBrain] STATE anchor: {stateMatch.Groups[1].Value.Trim()}");
        else
            Debug.LogWarning("[LLMBrain] STATE anchor missing in response.");

        Match match = Regex.Match(
            aiResponse,
            @"ACTION_INDEX\s*:\s*(\d+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        if (match.Success &&
            int.TryParse(match.Groups[1].Value.Trim(), out int idx) &&
            idx >= 0 &&
            idx < actionCount)
        {
            return idx;
        }

        Match sequenceMatch = Regex.Match(
            aiResponse,
            @"ACTION_SEQUENCE\s*:\s*([\d,\s]+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        if (sequenceMatch.Success)
        {
            foreach (string part in sequenceMatch.Groups[1].Value.Split(','))
            {
                if (int.TryParse(part.Trim(), out idx) && idx >= 0 && idx < actionCount)
                {
                    Debug.LogWarning("[LLMBrain] Ollama step mode received ACTION_SEQUENCE; using first valid index.");
                    return idx;
                }
            }
        }

        Debug.LogWarning("[LLMBrain] Could not parse ACTION_INDEX — falling back to EndTurn.");
        return actionCount - 1;
    }

    /// If the sequence has Evolve(X) followed by AttachEnergy(X), move AttachEnergy before Evolve
    /// so the energy attaches to the pre-evolution card and carries over, avoiding stale references.
    private static List<int> ReorderEnergyBeforeEvolve(List<int> sequence, List<GameAction> legalActions)
    {
        var result = new List<int>(sequence);
        for (int i = 0; i < result.Count; i++)
        {
            GameAction action = legalActions[result[i]];
            if (action.type != GameActionType.Evolve || action.target == null) continue;

            CardInstance evolveTarget = action.target;
            for (int j = i + 1; j < result.Count; j++)
            {
                GameAction later = legalActions[result[j]];
                if (later.type == GameActionType.AttachEnergy && later.target == evolveTarget)
                {
                    // Move AttachEnergy (index j) to just before this Evolve (index i)
                    int attachIdx = result[j];
                    result.RemoveAt(j);
                    result.Insert(i, attachIdx);
                    Debug.Log($"[LLMBrain] Reordered: AttachEnergy moved before Evolve({evolveTarget.baseData?.cardName}).");
                    break;
                }
            }
        }
        return result;
    }

    private bool IsPlayer1 => playerManager != null && playerManager.player1 == myPlayer;

    private EnumLlmProvider GetMyProvider()
    {
        if (GameRulesConfig.Instance == null) return EnumLlmProvider.Gemini;
        return IsPlayer1
            ? GameRulesConfig.Instance.player1LlmProvider
            : GameRulesConfig.Instance.player2LlmProvider;
    }

    private EnumGeminiModel GetMyGeminiModel()
    {
        if (GameRulesConfig.Instance == null) return EnumGeminiModel.Flash20;
        return IsPlayer1
            ? GameRulesConfig.Instance.player1GeminiModel
            : GameRulesConfig.Instance.player2GeminiModel;
    }

    private EnumOllamaModel GetMyOllamaModel()
    {
        if (GameRulesConfig.Instance == null) return EnumOllamaModel.Gemma3_12b;
        return IsPlayer1
            ? GameRulesConfig.Instance.player1OllamaModel
            : GameRulesConfig.Instance.player2OllamaModel;
    }

    private EnumOpenAiModel GetMyOpenAiModel()
    {
        if (GameRulesConfig.Instance == null) return EnumOpenAiModel.Gpt4oMini;
        return IsPlayer1
            ? GameRulesConfig.Instance.player1OpenAiModel
            : GameRulesConfig.Instance.player2OpenAiModel;
    }

    private ILLMClient ResolveLlmClient()
    {
        return GetMyProvider() switch
        {
            EnumLlmProvider.Ollama => OllamaApiClient.CreateForPlayer(GetMyOllamaModel()),
            EnumLlmProvider.OpenAI => OpenAiApiClient.CreateForPlayer(GetMyOpenAiModel()),
            _                      => GeminiApiClient.CreateForPlayer(GetMyGeminiModel()),
        };
    }

    private static CardInstance FindCardByParsedId(List<CardInstance> availablePokemons, string chosenCardId)
    {
        if (string.IsNullOrWhiteSpace(chosenCardId))
        {
            return null;
        }

        foreach (CardInstance card in availablePokemons)
        {
            if (string.Equals(card.baseData.cardId, chosenCardId, System.StringComparison.OrdinalIgnoreCase))
            {
                return card;
            }
        }

        return null;
    }

    private static CardInstance FindCardByParsedOptionNumber(List<CardInstance> availablePokemons, string chosenCardId)
    {
        if (!int.TryParse(chosenCardId, out int optionNumber))
        {
            return null;
        }

        int index = optionNumber - 1;
        if (index < 0 || index >= availablePokemons.Count)
        {
            return null;
        }

        Debug.LogWarning($"[LLMBrain] WYBOR_ID '{chosenCardId}' wyglada jak numer opcji. Mapuje na karte: {availablePokemons[index].baseData.cardId}.");
        return availablePokemons[index];
    }

    private static CardInstance FindCardByMentionedId(List<CardInstance> availablePokemons, string aiChoice)
    {
        foreach (CardInstance card in availablePokemons)
        {
            if (aiChoice.Contains(card.baseData.cardId, StringComparison.OrdinalIgnoreCase))
            {
                return card;
            }
        }

        return null;
    }

    private static CardInstance FindCardByMentionedName(List<CardInstance> availablePokemons, string aiChoice)
    {
        CardInstance earliestMatch = null;
        int earliestIndex = int.MaxValue;

        foreach (CardInstance card in availablePokemons)
        {
            string name = card.baseData.cardName;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            Match match = Regex.Match(
                aiChoice,
                $@"\b{Regex.Escape(name)}\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

            if (match.Success && match.Index < earliestIndex)
            {
                earliestMatch = card;
                earliestIndex = match.Index;
            }
        }

        if (earliestMatch != null)
        {
            Debug.LogWarning($"[LLMBrain] Dopasowano wybor setupu po nazwie w odpowiedzi: {earliestMatch.baseData.cardName} ({earliestMatch.baseData.cardId}).");
        }

        return earliestMatch;
    }

    private static string ExtractChosenCardId(string aiChoice)
    {
        if (string.IsNullOrWhiteSpace(aiChoice))
        {
            return null;
        }

        Match match = Regex.Match(
            aiChoice,
            @"WYBOR_ID\s*:\s*([A-Za-z0-9_-]+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static string ExtractThinkingText(string aiChoice)
    {
        if (string.IsNullOrWhiteSpace(aiChoice))
        {
            return "Brak odpowiedzi modelu.";
        }

        Match thinkingMatch = Regex.Match(
            aiChoice,
            @"THINKING\s*:\s*(.+?)(?:\r?\n|$)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        if (thinkingMatch.Success)
        {
            return thinkingMatch.Groups[1].Value.Trim();
        }

        string withoutChoice = Regex.Replace(
            aiChoice,
            @"WYBOR_ID\s*:\s*([A-Za-z0-9_-]+)",
            string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Trim();

        return string.IsNullOrWhiteSpace(withoutChoice)
            ? "Model nie podał jawnego procesu myślenia."
            : withoutChoice;
    }

    public static EnumGeminiModel GetFallbackGeminiModel(EnumGeminiModel failedModel) => failedModel switch
    {
        EnumGeminiModel.Flash25Lite => EnumGeminiModel.Flash20Lite,
        EnumGeminiModel.Flash20Lite => EnumGeminiModel.Flash15,
        EnumGeminiModel.Pro25       => EnumGeminiModel.Flash25Lite,
        _                           => EnumGeminiModel.Flash25Lite,
    };

    public static string GeminiModelDisplayName(EnumGeminiModel model) => model switch
    {
        EnumGeminiModel.Flash25     => "Gemini 2.5 Flash",
        EnumGeminiModel.Flash25Lite => "Gemini 2.5 Flash Lite",
        EnumGeminiModel.Pro25       => "Gemini 2.5 Pro",
        EnumGeminiModel.Flash20     => "Gemini 2.0 Flash",
        EnumGeminiModel.Flash20Lite => "Gemini 2.0 Flash Lite",
        EnumGeminiModel.Flash31Lite => "Gemini 3.1 Flash Lite",
        EnumGeminiModel.Flash35     => "Gemini 3.5 Flash",
        EnumGeminiModel.Flash15     => "Gemini 1.5 Flash",
        EnumGeminiModel.Flash30     => "Gemini 3.0 Flash",
        EnumGeminiModel.Gemma4_26b  => "Gemma 4 26B",
        EnumGeminiModel.Gemma4_31b  => "Gemma 4 31B",
        _                           => model.ToString(),
    };
}
