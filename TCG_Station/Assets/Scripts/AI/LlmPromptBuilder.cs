using System.Collections.Generic;
using System.Linq;
using System.Text;

public static class LlmPromptBuilder
{
    public static string BuildTurnPrompt(
        GameStateSnapshot snapshot,
        List<GameAction> legalActions,
        string gameHistory = null,
        EnumLlmProvider provider = EnumLlmProvider.Gemini)
    {
        var sb = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(gameHistory))
        {
            sb.AppendLine("GAME HISTORY (your previous turns):");
            sb.AppendLine(gameHistory);
            sb.AppendLine();
        }

        sb.AppendLine($"=== TURN {snapshot.TurnNumber} - YOUR MOVE ===");
        sb.AppendLine();

        AppendPokemonSection(sb, "YOUR ACTIVE", snapshot.MyState.ActivePokemon, full: true);
        AppendBenchSection(sb, "YOUR BENCH", snapshot.MyState.Bench);

        sb.AppendLine($"Your KO score: {snapshot.MyState.Score}");
        sb.AppendLine($"Deck: {snapshot.MyState.DeckCount} cards");
        if (snapshot.MyState.AvailableEnergy != EnumPokemonType.None)
            sb.AppendLine($"Available energy to attach: {snapshot.MyState.AvailableEnergy}");
        if (snapshot.MyState.NextEnergy != EnumPokemonType.None)
            sb.AppendLine($"Next turn energy: {snapshot.MyState.NextEnergy}");
        sb.AppendLine($"Supporter used this turn: {(snapshot.MyState.UsedSupporterThisTurn ? "yes" : "no")}");
        AppendDeckEnergyPool(sb, snapshot.MyState.DeckEnergyPool);
        AppendRampReachableEnergy(sb, snapshot.MyState.RampReachableEnergyTypes, snapshot.MyState.DeckEnergyPool);

        AppendHandSection(sb, snapshot.MyState.Hand);
        sb.AppendLine();

        AppendPokemonSection(sb, "OPPONENT ACTIVE", snapshot.OpponentState.ActivePokemon, full: false);
        AppendBenchSection(sb, "OPPONENT BENCH", snapshot.OpponentState.Bench);
        sb.AppendLine($"Opponent KO score: {snapshot.OpponentState.Score}");
        sb.AppendLine($"Opponent hand count: {snapshot.OpponentState.HandCount}");
        AppendDeckEnergyPool(sb, snapshot.OpponentState.DeckEnergyPool, "Opponent ");
        sb.AppendLine();

        sb.AppendLine("LEGAL ACTIONS:");
        for (int i = 0; i < legalActions.Count; i++)
            sb.AppendLine($"  {i}. {legalActions[i]}");
        sb.AppendLine();

        sb.AppendLine($"Goal: be the first to score 4 KOs. Your score: {snapshot.MyState.Score}/4. Opponent: {snapshot.OpponentState.Score}/4.");
        sb.AppendLine();
        sb.AppendLine("OUTPUT FORMAT — follow exactly, no exceptions:");
        sb.AppendLine("THINKING: <2-3 sentences: (1) can my active deal damage? if not, should I retreat? (2) what is the biggest threat? (3) what is my plan this turn?>");
        sb.AppendLine("ACTION_SEQUENCE: <comma-separated indices>");
        sb.AppendLine("Example: ACTION_SEQUENCE: 2, 0, 5, 3");
        sb.AppendLine("You MUST output ACTION_SEQUENCE. If you skip it, your turn is lost.");
        sb.AppendLine();
        sb.AppendLine("Rules:");
        sb.AppendLine("- Attack cost notation: [FREE] = no energy required, always usable. [Colorless] = any 1 energy. [Fire,Psychic] = needs those specific types.");
        sb.AppendLine("- Only use indices listed in LEGAL ACTIONS above.");
        sb.AppendLine("- Evolving unlocks a stronger attack (shown after → on each Evolve action). If your active deals 0 damage, evolving to gain a real attack is usually better than stalling.");
        sb.AppendLine("- ONE Supporter per turn: you may play at most ONE Trainer marked Supporter per turn, shown as (Trainer: Supporter) in hand and PlayTrainer(name, Supporter) in LEGAL ACTIONS. Extra Supporter indices are rejected. Items have no such limit. Pick your single best Supporter and do not list others.");
        sb.AppendLine("- AttachEnergy appears at most once. Place it BEFORE any Attack or Retreat index. Never after Attack.");
        sb.AppendLine("- ATTACH/EVOLVE THEN ATTACK: Attack may be legal because an earlier AttachEnergy and/or Evolve in your sequence makes the final Active Pokemon ready. If your plan says you can attack after those actions, include the Attack index before EndTurn.");
        sb.AppendLine("- Read Attack labels carefully: 'use before Evolve' means the Attack index belongs to the current form and will fail if you Evolve before it.");
        sb.AppendLine("- AttachEnergy marked ONLY AFTER PlayBasic(name) is legal only if your sequence includes that PlayBasic index first; otherwise skip that AttachEnergy.");
        sb.AppendLine("- Manual Retreat can be used at most once per turn. Card effects that switch Pokemon do not count as manual Retreat.");
        sb.AppendLine("- After Attack the turn ends — do not place any action after an Attack index.");
        sb.AppendLine("- Always include EndTurn as the last index.");
        sb.AppendLine("- Do not plan more PlayBasic actions than there are free bench slots (bench max 3).");
        if (provider == EnumLlmProvider.Ollama)
        {
            sb.AppendLine("- BENCH BUILDING (high priority): Play Basic Pokémon from hand every turn you have a free bench slot. Include PlayBasic indices early in your sequence. A Basic played now can evolve and attack in 2 turns.");
            sb.AppendLine("- ENERGY TARGET: Attach energy to the Pokémon that needs it most to reach its attack cost. If your active Pokémon already meets its attack cost (or attacks for 0 damage), attach to a bench Pokémon instead.");
            sb.AppendLine("- EXTRA ENERGY: If AttachEnergy is legal and every Pokémon already has all attacks payable, still attach to someone. Prefer a scaling attacker, then Active, then the best backup; extra energy helps with stronger attacks, Retreat, EnergyDiscard recovery, and post-KO replacement.");
            sb.AppendLine("- ATTACH THEN ATTACK: If your active Pokémon is 1 energy short of its attack cost, attaching energy THIS TURN will allow it to attack in the SAME turn. Always include AttachEnergy followed by Attack in that case — evaluate attack availability AFTER the energy is attached, not before.");
            sb.AppendLine("- RETREAT THEN ATTACK: If you plan to Retreat to a bench Pokémon, evaluate whether that Pokémon can attack AFTER the retreat (counting any energy you attach this turn). If it already has enough energy (shown as [CAN ATTACK] in bench listing), ALWAYS include Attack after Retreat in your sequence. Never retreat to a Pokémon and then end the turn without attacking if it can attack.");
            sb.AppendLine("- *** MANDATORY RETREAT ***: If your active Pokémon attacks for 0 damage AND a bench Pokémon can deal damage (attack damage > 0) AND you can afford Retreat (RetreatCost ≤ energy on active), you MUST include Retreat before Attack. A 0-damage attacker that never retreats is a losing strategy.");
            sb.AppendLine("- EVOLUTION PRIORITY: Evolving a damaged Pokémon is usually better than healing it — evolution carries only existing damage, so the new Pokémon has more effective HP. Evolve before healing when possible.");
            sb.AppendLine("- THREAT ASSESSMENT: If opponent's active Pokémon can KO you next turn, prioritize surviving (evolve, retreat, or heal) over dealing damage.");
        }
        else // Gemini and OpenAI: single-request, full-turn (ACTION_SEQUENCE) providers share the same guidance.
        {
            sb.AppendLine("- ENERGY TARGET: you may attach only ONE energy per turn. Pick ONE main attacker and pour energy into it until it reaches a damaging attack's cost. Do NOT scatter energy onto bench Pokémon while your Active still cannot attack.");
            sb.AppendLine("- EXTRA ENERGY: If AttachEnergy is legal and every Pokémon already has all attacks payable, still attach to someone. Prefer a scaling attacker, then Active, then the best backup; extra energy helps with stronger attacks, Retreat, EnergyDiscard recovery, and post-KO replacement.");
            sb.AppendLine("- ATTACH THEN ATTACK: if your Active is 1 energy short of its cheapest damaging attack, attaching energy THIS turn lets it attack THIS SAME turn — include AttachEnergy then Attack before EndTurn. Evaluate attack availability AFTER the energy is attached, not before.");
            sb.AppendLine("- DON'T OVER-SETUP: flooding the bench with new Basics/evolutions is secondary to arming one attacker. Develop the bench only after your Active can attack, or with energy/actions you are not spending on the attacker this turn.");
        }

        return sb.ToString().TrimEnd();
    }

    public static string BuildOllamaActionPrompt(
        GameStateSnapshot snapshot,
        List<GameAction> legalActions,
        IReadOnlyList<string> actionsThisTurn,
        int stepNumber,
        string gameHistory = null)
    {
        var sb = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(gameHistory))
        {
            sb.AppendLine("GAME HISTORY (your previous turns):");
            sb.AppendLine(gameHistory);
            sb.AppendLine();
        }

        sb.AppendLine($"=== TURN {snapshot.TurnNumber} - OLLAMA STEP {stepNumber} ===");
        sb.AppendLine("Choose exactly ONE next action for the current board state.");
        sb.AppendLine();

        if (actionsThisTurn != null && actionsThisTurn.Count > 0)
        {
            sb.AppendLine("ACTIONS ALREADY EXECUTED THIS TURN:");
            for (int i = 0; i < actionsThisTurn.Count; i++)
                sb.AppendLine($"  {i + 1}. {actionsThisTurn[i]}");
            sb.AppendLine();
        }

        AppendPokemonSection(sb, "YOUR ACTIVE", snapshot.MyState.ActivePokemon, full: true);
        AppendBenchSection(sb, "YOUR BENCH", snapshot.MyState.Bench);

        sb.AppendLine($"Your KO score: {snapshot.MyState.Score}");
        sb.AppendLine($"Deck: {snapshot.MyState.DeckCount} cards");
        if (snapshot.MyState.AvailableEnergy != EnumPokemonType.None)
            sb.AppendLine($"Available energy to attach: {snapshot.MyState.AvailableEnergy}");
        if (snapshot.MyState.NextEnergy != EnumPokemonType.None)
            sb.AppendLine($"Next turn energy: {snapshot.MyState.NextEnergy}");
        sb.AppendLine($"Supporter used this turn: {(snapshot.MyState.UsedSupporterThisTurn ? "yes" : "no")}");
        AppendDeckEnergyPool(sb, snapshot.MyState.DeckEnergyPool);
        AppendRampReachableEnergy(sb, snapshot.MyState.RampReachableEnergyTypes, snapshot.MyState.DeckEnergyPool);

        AppendHandSection(sb, snapshot.MyState.Hand);
        AppendPlayableHandSection(sb, snapshot.MyState.Hand, legalActions);
        sb.AppendLine();

        AppendPokemonSection(sb, "OPPONENT ACTIVE", snapshot.OpponentState.ActivePokemon, full: false);
        AppendBenchSection(sb, "OPPONENT BENCH", snapshot.OpponentState.Bench);
        sb.AppendLine($"Opponent KO score: {snapshot.OpponentState.Score}");
        sb.AppendLine($"Opponent hand count: {snapshot.OpponentState.HandCount}");
        sb.AppendLine();

        var warnings = LlmActionWarnings.BuildWarnings(snapshot, legalActions);
        if (warnings.Count > 0)
        {
            sb.AppendLine("WASTED / LOW-VALUE ACTIONS THIS STEP (do NOT pick unless nothing else helps):");
            foreach (var w in warnings) sb.AppendLine(w);
            sb.AppendLine();
        }

        sb.AppendLine("LEGAL NEXT ACTIONS:");
        for (int i = 0; i < legalActions.Count; i++)
            sb.AppendLine($"  {i}. {legalActions[i]}");
        sb.AppendLine();

        sb.AppendLine($"Goal: be the first to score 4 KOs. Your score: {snapshot.MyState.Score}/4. Opponent: {snapshot.OpponentState.Score}/4.");
        sb.AppendLine();
        sb.AppendLine("OUTPUT FORMAT — follow exactly, in this order (three lines, nothing else):");
        sb.AppendLine("STATE: Active=<name HP/maxHP status>, Bench=<count>, MyKO=<n>/4, OppKO=<m>/4");
        sb.AppendLine("THINKING: <one short sentence explaining this single next action>");
        sb.AppendLine("ACTION_INDEX: <one index from LEGAL NEXT ACTIONS>");
        sb.AppendLine();
        sb.AppendLine("Example:");
        sb.AppendLine("STATE: Active=Lampent 90/100 no-status, Bench=2, MyKO=0/4, OppKO=1/4");
        sb.AppendLine("THINKING: Lampent can attack now with 2 Fire energy.");
        sb.AppendLine("ACTION_INDEX: 4");
        sb.AppendLine();
        sb.AppendLine("Rules for this step:");
        sb.AppendLine("- Choose only one index from LEGAL NEXT ACTIONS.");
        sb.AppendLine("- Do not output ACTION_SEQUENCE in this mode.");
        sb.AppendLine("- Manual Retreat can be used at most once per turn. Card effects that switch Pokemon do not count as manual Retreat.");
        sb.AppendLine("- If you can improve the board before ending, prefer a useful action over EndTurn.");
        sb.AppendLine("- Attack ends the turn. Do NOT choose Attack just because it is legal if you still have a strong hand-development action first.");
        sb.AppendLine("- If PLAYABLE HAND CARDS NOW lists a legal PlayBasic, Evolve, or clearly useful PlayTrainer, prefer one of those before Attack unless attacking now gets an immediate KO or prevents losing.");
        sb.AppendLine("- If an Evolution is legal for your Active or an important Bench Pokemon, strongly prefer Evolve before Attack.");
        sb.AppendLine("- If a Basic Pokemon from hand can be placed on a free bench slot, strongly prefer PlayBasic before Attack.");
        sb.AppendLine("- If you just played a Basic Pokemon and energy is available, re-check AttachEnergy in this new state.");
        sb.AppendLine("- Before choosing EndTurn, use a legal AttachEnergy. Even if all attacks are already payable, attach to someone for scaling damage, Retreat, EnergyDiscard recovery, or post-KO backup.");
        sb.AppendLine("- If active cannot deal useful damage and a bench Pokemon can, consider Retreat before EndTurn.");
        sb.AppendLine("- If every Pokemon already has all attacks payable, choose the best extra-energy target: scaling attacker first, then Active, then the strongest backup attacker.");
        sb.AppendLine("- Stage1/Stage2 Pokemon in hand are not dead cards: if a matching Evolve action exists in LEGAL NEXT ACTIONS, use it.");

        return sb.ToString().TrimEnd();
    }

    private static void AppendDeckEnergyPool(StringBuilder sb, List<EnumPokemonType> pool, string prefix = "Your ")
    {
        if (pool == null || pool.Count == 0) return;

        // Pool entries can repeat to encode weights. Show distinct types and weights so
        // the LLM can plan energy attachments only toward attacks the pool can satisfy.
        var counts = new Dictionary<EnumPokemonType, int>();
        foreach (var t in pool)
        {
            if (t == EnumPokemonType.None) continue;
            counts[t] = counts.TryGetValue(t, out int c) ? c + 1 : 1;
        }
        if (counts.Count == 0) return;

        var parts = new List<string>();
        foreach (var kv in counts)
            parts.Add($"{kv.Key} x{kv.Value}");
        sb.AppendLine($"{prefix}deck energy pool (random per turn from these types): {string.Join(", ", parts)}");
    }

    // Energy types reachable via EnergyRamp attacks on Pokemon the player holds/fields, beyond the
    // Energy Zone pool. Only off-pool types are surfaced — those are the ones a naive "type not in
    // pool = unreachable" reading would wrongly discard (e.g. a Grass Tropius in a Fire-zone deck).
    private static void AppendRampReachableEnergy(StringBuilder sb, List<EnumPokemonType> rampTypes, List<EnumPokemonType> pool)
    {
        if (rampTypes == null || rampTypes.Count == 0) return;

        var offPool = new List<EnumPokemonType>();
        foreach (var t in rampTypes)
        {
            if (t == EnumPokemonType.None) continue;
            if (pool != null && pool.Contains(t)) continue;
            if (!offPool.Contains(t)) offPool.Add(t);
        }
        if (offPool.Count == 0) return;

        sb.AppendLine($"Also reachable via EnergyRamp attacks (bypass the Energy Zone, add the ramping Pokemon's own type to your bench): {string.Join(", ", offPool)}. Attacks needing these types are NOT unreachable — plan the ramp combo.");
    }

    private static void AppendEvolutionPreviews(StringBuilder sb, List<EvolutionPreview> evolutions, string indent)
    {
        if (evolutions == null || evolutions.Count == 0) return;

        foreach (var evo in evolutions)
        {
            sb.Append($"{indent}Can evolve into: {evo.Name} ({evo.Stage}, HP {evo.MaxHp}, RetreatCost {evo.RetreatCost})");
            if (evo.Attacks != null && evo.Attacks.Count > 0)
            {
                var atkParts = new List<string>();
                foreach (var atk in evo.Attacks)
                {
                    string cost = atk.EnergyCost != null && atk.EnergyCost.Count > 0
                        ? string.Join(",", atk.EnergyCost)
                        : "FREE";
                    atkParts.Add($"{atk.Name}[{cost}]->{atk.Damage}dmg");
                }
                sb.Append($" | Attacks: {string.Join("; ", atkParts)}");
            }
            sb.AppendLine();
        }
    }

    private static void AppendPokemonSection(StringBuilder sb, string label, PokemonSnapshot p, bool full)
    {
        if (p == null)
        {
            sb.AppendLine($"{label}: brak");
            sb.AppendLine();
            return;
        }

        sb.AppendLine($"{label}: {p.Name} ({p.PokemonType}) [{p.Stage}]");
        sb.AppendLine($"  HP: {p.CurrentHp}/{p.MaxHp}  RetreatCost: {p.RetreatCost}");

        if (p.EnergyEquipped != null && p.EnergyEquipped.Count > 0)
        {
            var parts = new List<string>();
            foreach (var kv in p.EnergyEquipped)
                parts.Add($"{kv.Value}x{kv.Key}");
            sb.AppendLine($"  Energy: {string.Join(", ", parts)}");
        }

        if (p.IsPoisoned) sb.AppendLine($"  Status: Poisoned ({p.PoisonDamageBetweenTurns} damage between turns)");
        if (p.IsBurned) sb.AppendLine($"  Status: Burned ({p.BurnDamageBetweenTurns} damage between turns, then coin flip to recover)");
        if (p.SpecialCondition != EnumSpecialConditionType.None)
            sb.AppendLine($"  Status: {p.SpecialCondition}");

        if (p.Attacks != null)
        {
            foreach (var atk in p.Attacks)
            {
                string cost = atk.EnergyCost != null && atk.EnergyCost.Count > 0
                    ? string.Join(",", atk.EnergyCost)
                    : "FREE";
                sb.Append($"  Attack: {atk.Name} [{cost}] -> {atk.Damage} dmg");
                if (atk.EnergyDiscardCount > 0)
                    sb.Append($" [DISCARDS {atk.EnergyDiscardCount} energy after use — needs {atk.EnergyDiscardCount} turns to recharge]");
                if (full && !string.IsNullOrWhiteSpace(atk.Description))
                    sb.Append($" | {atk.Description}");
                if (atk.EffectSummaries != null && atk.EffectSummaries.Count > 0)
                    sb.Append($" | Effects: {string.Join("; ", atk.EffectSummaries)}");
                sb.AppendLine();
            }
        }

        // Energy-gap signal for the Active: use typed-cost math, not just total energy count, so a
        // Pokemon with 2xFighting is not described as ready for a [Fighting,Psychic] attack.
        if (full && p.Attacks != null)
        {
            string note = BuildEnergyProgressNote(p);
            if (!string.IsNullOrEmpty(note))
                sb.AppendLine(note);
        }

        AppendEvolutionPreviews(sb, p.PossibleEvolutions, "  ");

        sb.AppendLine();
    }

    private static string BuildEnergyProgressNote(PokemonSnapshot p)
    {
        if (p?.Attacks == null) return null;

        AttackSnapshot bestAttack = null;
        List<EnumPokemonType> bestMissing = null;
        int bestCost = int.MaxValue;
        int bestMissingCount = int.MaxValue;

        foreach (var atk in p.Attacks)
        {
            if (atk.Damage <= 0) continue;

            var missing = GetMissingEnergyForAttack(p.EnergyEquipped, atk, p.AttackCostChange);
            int cost = System.Math.Max(0, (atk.EnergyCost?.Count ?? 0) + p.AttackCostChange);

            if (bestAttack == null ||
                missing.Count < bestMissingCount ||
                (missing.Count == bestMissingCount && cost < bestCost))
            {
                bestAttack = atk;
                bestMissing = missing;
                bestCost = cost;
                bestMissingCount = missing.Count;
            }
        }

        if (bestAttack == null) return null;

        int currentEnergy = p.EnergyEquipped != null ? p.EnergyEquipped.Values.Sum() : 0;
        string progress = $"{currentEnergy}/{bestCost}";
        string attackName = bestAttack.Name;

        if (bestMissingCount == 0)
            return $"  Energy toward {attackName}: {progress} — ready to attack now.";

        string missingText = FormatMissingEnergy(bestMissing);
        if (bestMissingCount == 1)
            return $"  Energy toward {attackName}: {progress} — needs 1 specific energy: {missingText}. Attach it THIS turn if available.";

        return $"  Energy toward {attackName}: {progress} — needs {bestMissingCount} more energy: {missingText}; only 1 energy attaches per turn, so attack in a later turn.";
    }

    private static List<EnumPokemonType> GetMissingEnergyForAttack(
        Dictionary<EnumPokemonType, int> equipped,
        AttackSnapshot attack,
        int attackEnergyCostChange)
    {
        var missing = new List<EnumPokemonType>();
        if (attack == null)
            return missing;
        var attackCost = attack.EnergyCost ?? new List<EnumPokemonType>();

        var available = equipped != null
            ? new Dictionary<EnumPokemonType, int>(equipped)
            : new Dictionary<EnumPokemonType, int>();

        int jokers = available.TryGetValue(EnumPokemonType.Dragon, out int dragonCount) ? dragonCount : 0;
        available[EnumPokemonType.Dragon] = 0;

        foreach (var cost in attackCost)
        {
            if (cost == EnumPokemonType.Colorless) continue;

            if (available.TryGetValue(cost, out int count) && count > 0)
            {
                available[cost]--;
            }
            else if (jokers > 0)
            {
                jokers--;
            }
            else
            {
                missing.Add(cost);
            }
        }

        int remainingEnergy = available.Values.Sum() + jokers;
        int colorlessCost = attackCost.Count(c => c == EnumPokemonType.Colorless);
        colorlessCost = System.Math.Max(0, colorlessCost + attackEnergyCostChange);
        int missingColorless = colorlessCost - remainingEnergy;
        for (int i = 0; i < missingColorless; i++)
            missing.Add(EnumPokemonType.Colorless);

        return missing;
    }

    private static string FormatMissingEnergy(List<EnumPokemonType> missing)
    {
        if (missing == null || missing.Count == 0) return "none";

        return string.Join(", ",
            missing.GroupBy(t => t)
                .Select(g =>
                {
                    string name = g.Key == EnumPokemonType.Colorless
                        ? "any energy for Colorless"
                        : g.Key.ToString();
                    return g.Count() == 1 ? name : $"{g.Count()}x{name}";
                }));
    }

    private static void AppendHandSection(StringBuilder sb, List<CardSnapshot> hand)
    {
        if (hand == null || hand.Count == 0)
        {
            sb.AppendLine("Hand: empty");
            return;
        }

        sb.AppendLine("Hand:");
        foreach (var card in hand)
        {
            // Show the trainer subtype (Supporter/Item/Tool) so the model knows which cards share the
            // one-Supporter-per-turn limit; plain Pokemon cards keep the bare "(Pokemon)" label.
            string typeLabel = !string.IsNullOrEmpty(card.TrainerSubType) && card.TrainerSubType != "None"
                ? $"{card.CardType}: {card.TrainerSubType}"
                : card.CardType;
            sb.Append($"  - [{card.InstanceId}] {card.Name} ({typeLabel})");
            if (!string.IsNullOrWhiteSpace(card.Description))
                sb.Append($" | {card.Description}");
            sb.AppendLine();
        }
    }

    private static void AppendPlayableHandSection(StringBuilder sb, List<CardSnapshot> hand, List<GameAction> legalActions)
    {
        if (hand == null || hand.Count == 0 || legalActions == null || legalActions.Count == 0)
            return;

        var playable = new List<string>();

        for (int i = 0; i < legalActions.Count; i++)
        {
            GameAction action = legalActions[i];
            if (action?.card == null)
                continue;

            string cardName = action.card.baseData?.cardName ?? "Unknown";

            string summary = action.type switch
            {
                GameActionType.PlayBasicPokemon => $"{i}. {cardName} from hand -> bench via PlayBasic",
                GameActionType.Evolve => $"{i}. {cardName} from hand -> evolve {action.target?.baseData?.cardName}",
                GameActionType.PlayTrainer => $"{i}. {cardName} from hand -> {action}",
                _ => null
            };

            if (!string.IsNullOrWhiteSpace(summary))
                playable.Add(summary);
        }

        if (playable.Count == 0)
            return;

        sb.AppendLine("PLAYABLE HAND CARDS NOW:");
        foreach (string line in playable)
            sb.AppendLine($"  - {line}");
    }

    private static void AppendBenchSection(StringBuilder sb, string label, List<PokemonSnapshot> bench)
    {
        if (bench == null || bench.Count == 0) return;

        sb.AppendLine($"{label}:");
        foreach (var p in bench)
        {
            string canAttackTag = p.CanAttack() ? " [CAN ATTACK]" : "";
            sb.Append($"  - {p.Name} HP: {p.CurrentHp}/{p.MaxHp}  RetreatCost: {p.RetreatCost}{canAttackTag}");
            if (p.EnergyEquipped != null && p.EnergyEquipped.Count > 0)
            {
                var parts = new List<string>();
                foreach (var kv in p.EnergyEquipped)
                    parts.Add($"{kv.Value}x{kv.Key}");
                sb.Append($" | Energy: {string.Join(", ", parts)}");
            }
            sb.AppendLine();
            if (p.Attacks != null)
            {
                foreach (var atk in p.Attacks)
                {
                    string cost = atk.EnergyCost != null && atk.EnergyCost.Count > 0
                        ? string.Join(",", atk.EnergyCost)
                        : "FREE";
                    sb.Append($"      Attack: {atk.Name} [{cost}] -> {atk.Damage} dmg");
                    if (atk.EnergyDiscardCount > 0)
                        sb.Append($" [DISCARDS {atk.EnergyDiscardCount} energy after use — needs {atk.EnergyDiscardCount} turns to recharge]");
                    if (!string.IsNullOrWhiteSpace(atk.Description))
                        sb.Append($" | {atk.Description}");
                    if (atk.EffectSummaries != null && atk.EffectSummaries.Count > 0)
                        sb.Append($" | Effects: {string.Join("; ", atk.EffectSummaries)}");
                    sb.AppendLine();
                }
            }
            AppendEvolutionPreviews(sb, p.PossibleEvolutions, "      ");
        }
        sb.AppendLine();
    }

    public static string BuildSetupPrompt(List<CardInstance> availablePokemons, EnumLlmProvider provider = EnumLlmProvider.Gemini)
    {
        StringBuilder prompt = new StringBuilder();
        prompt.AppendLine("Zadanie: wybierz jednego Pokemona, ktorego wystawisz na Aktywne Pole (Active Spot).");
        prompt.AppendLine("Wybierasz tylko z kart podanych nizej.");
        prompt.AppendLine("Kieruj sie tym, co jest najlepsze na start gry.");
        if (provider == EnumLlmProvider.Ollama)
        {
            prompt.AppendLine("Uwzglednij koszt ataku: wysokie obrazenia z drogim atakiem nie sa dobre na start, jesli Pokemon nie zaatakuje szybko.");
            prompt.AppendLine("Preferuj Pokemony z niskim kosztem ataku, darmowym atakiem, akceleracja energii albo dobra przezywalnoscia.");
        }
        prompt.AppendLine("W odpowiedzi:");
        prompt.AppendLine("1. najpierw daj linie: THINKING: <krotkie uzasadnienie wyboru>,");
        prompt.AppendLine("2. w ostatniej linii podaj dokladnie: WYBOR_ID: <id karty z pola ID>.");
        prompt.AppendLine("3. nie podawaj nic po linii WYBOR_ID.");
        prompt.AppendLine("Nie podawaj numeru opcji w WYBOR_ID. Poprawny format to np. WYBOR_ID: pikachu_1.");
        prompt.AppendLine();
        prompt.AppendLine("Dostepne karty:");

        for (int i = 0; i < availablePokemons.Count; i++)
        {
            CardInstance card = availablePokemons[i];
            string id = card.baseData.cardId;
            string name = card.baseData.cardName;
            int hp = card.pokemonLogic.pokemonData.hp;
            string attackName = card.pokemonLogic.pokemonData.attacks[0].attackName;
            string attackCost = card.pokemonLogic.pokemonData.attacks[0].attackCost != null &&
                                card.pokemonLogic.pokemonData.attacks[0].attackCost.Count > 0
                ? string.Join(",", card.pokemonLogic.pokemonData.attacks[0].attackCost)
                : "FREE";
            int attackDamage = card.pokemonLogic.pokemonData.attacks[0].damage;
            string attackDescription = card.pokemonLogic.pokemonData.attacks[0].attackDescription;

            prompt.AppendLine($"- Opcja {i + 1}");
            prompt.AppendLine($"  ID: {id}");
            prompt.AppendLine($"  Nazwa: {name}");
            prompt.AppendLine($"  HP: {hp}");
            prompt.AppendLine($"  Atak: {attackName} [{attackCost}]");
            prompt.AppendLine($"  Obrazenia: {attackDamage}");
            prompt.AppendLine($"  Opis ataku: {attackDescription}");
        }

        return prompt.ToString().TrimEnd();
    }
}
