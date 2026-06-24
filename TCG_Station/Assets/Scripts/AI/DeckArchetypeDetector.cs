using System.Collections.Generic;
using UnityEngine;

// Auto-detects a deck's play-style archetype and maps it to an AlgorithmProfile.
//
// Why: a user who does not know how the numeric profiles work can tick "auto-detect" and let the AI pick
// a fitting profile for each deck instead of choosing Standard/Ramp/etc. by hand. The mapping is a simple,
// transparent heuristic over the deck's printed cards (no ML); the chosen profile and the raw signal
// counts are logged so the decision is inspectable.
//
// Signals (counted across the deck, weighted by card copies):
//   - Ramp          : attacks with EnergyRamp, plus heavy finishers (attack cost >= 4).
//   - ControlStatus : attacks inflicting status (Poison/Burn/Paralyze/Asleep/Confuse/Root/Slow/Expose)
//                     or discarding the enemy's energy.
//   - HealStall     : printed healing, BenchHeal, LeechLife, and damage-reduction riders on Pokemon attacks;
//                     trainer healing is counted as support, not as the deck's main plan.
//   - TempoAggro    : cheap aggressive attackers (cost <= 2, damage >= 30) on a low energy curve.
// If no signal is clear, falls back to Standard.
public static class DeckArchetypeDetector
{
    private static readonly HashSet<EnumCardEffectType> StatusEffects = new HashSet<EnumCardEffectType>
    {
        EnumCardEffectType.Poison,
        EnumCardEffectType.Burn,
        EnumCardEffectType.Paralyze,
        EnumCardEffectType.Asleep,
        EnumCardEffectType.Confuse,
        EnumCardEffectType.Root,
        EnumCardEffectType.Slow,
        EnumCardEffectType.Expose,
    };

    public static EnumAlgorithmProfile Detect(
        string deckName,
        Dictionary<string, CardData> cardLibrary,
        Dictionary<string, DeckData> deckLibrary)
    {
        if (cardLibrary == null || deckLibrary == null || string.IsNullOrEmpty(deckName) ||
            !deckLibrary.TryGetValue(deckName, out DeckData deck) || deck?.cards == null)
        {
            Debug.LogWarning($"[DeckArchetypeDetector] Cannot inspect deck '{deckName}', defaulting to Standard.");
            return EnumAlgorithmProfile.Standard;
        }

        int ramp = 0, control = 0, healAttack = 0, healTrainer = 0, aggro = 0, finisher = 0;
        int attackCount = 0, totalCost = 0;

        foreach (DeckCardData dc in deck.cards)
        {
            if (dc == null || string.IsNullOrEmpty(dc.cardId) ||
                !cardLibrary.TryGetValue(dc.cardId, out CardData card) || card == null)
                continue;

            int copies = Mathf.Max(1, dc.count);

            if (card is PokemonData pokemon)
            {
                if (pokemon.attacks == null) continue;
                foreach (AttackData atk in pokemon.attacks)
                {
                    int cost = atk?.attackCost?.Count ?? 0;
                    attackCount += copies;
                    totalCost += cost * copies;

                    if (cost >= 4) finisher += copies;
                    if (cost <= 2 && atk != null && atk.damage >= 30) aggro += copies;

                    if (atk?.effects == null) continue;
                    foreach (EffectData e in atk.effects)
                    {
                        if (e == null) continue;
                        if (e.cardEffectType == EnumCardEffectType.EnergyRamp) ramp += copies;
                        else if (e.cardEffectType == EnumCardEffectType.Heal ||
                                 e.cardEffectType == EnumCardEffectType.BenchHeal ||
                                 e.cardEffectType == EnumCardEffectType.LeechLife ||
                                 e.cardEffectType == EnumCardEffectType.DmgTakenRed) healAttack += copies;
                        else if (e.cardEffectType == EnumCardEffectType.EnergyDiscard &&
                                 e.cardEffectTarget == EnumCardEffectTarget.EnemyActivePokemon) control += copies;
                        else if (StatusEffects.Contains(e.cardEffectType)) control += copies;
                    }
                }
            }
            else if (card is TrainerData trainer && trainer.effects != null)
            {
                bool hasHealSupport = false;
                foreach (EffectData e in trainer.effects)
                {
                    if (e == null) continue;
                    if (e.cardEffectType == EnumCardEffectType.Heal ||
                        e.cardEffectType == EnumCardEffectType.BenchHeal ||
                        e.cardEffectType == EnumCardEffectType.DmgTakenRed)
                    {
                        hasHealSupport = true;
                        break;
                    }
                }
                if (hasHealSupport) healTrainer += copies;
            }
        }

        float avgCost = attackCount > 0 ? (float)totalCost / attackCount : 0f;

        // Weighted scores: ramp engines keep priority, but generic heal trainers must not hide a
        // low-curve damage plan or an explicit status/control plan. HealStall needs healing/defense
        // printed on Pokemon attacks, because every deck can run support healing.
        int rampScore = ramp * 4 + finisher * 2;
        int controlScore = control * 3;
        int healScore = healAttack * 3 + healTrainer;
        int aggroScore = aggro * 2;
        bool strongAggroCurve = aggro >= 9 && avgCost <= 2.2f;
        bool strongHealPlan = healAttack >= 6 &&
                              (healAttack >= 12 || aggro < 9 || avgCost >= 2.3f) &&
                              healScore >= rampScore &&
                              healScore >= controlScore - 6;

        EnumAlgorithmProfile pick;
        if (ramp >= 6 && rampScore >= controlScore &&
            (!strongHealPlan || rampScore >= healScore) &&
            (!strongAggroCurve || rampScore >= aggroScore))
            pick = EnumAlgorithmProfile.Ramp;
        else if (ramp >= 3 && finisher >= 2 && rampScore >= controlScore &&
                 !strongHealPlan && !strongAggroCurve)
            pick = EnumAlgorithmProfile.Ramp;
        else if (control >= 6 && controlScore >= rampScore &&
                 (!strongAggroCurve || controlScore >= aggroScore) &&
                 (!strongHealPlan || controlScore >= healScore - 6))
            pick = EnumAlgorithmProfile.ControlStatus;
        else if (strongHealPlan)
            pick = EnumAlgorithmProfile.HealStall;
        else if (strongAggroCurve)
            pick = EnumAlgorithmProfile.TempoAggro;
        else if (control >= 3)
            pick = EnumAlgorithmProfile.ControlStatus;
        else if (ramp >= 3 && finisher >= 2)
            pick = EnumAlgorithmProfile.Ramp;
        else
            pick = EnumAlgorithmProfile.Standard;

        Debug.Log($"[DeckArchetypeDetector] '{deckName}' → {pick} " +
                  $"(ramp={ramp}, control={control}, healAttack={healAttack}, healTrainer={healTrainer}, " +
                  $"aggro={aggro}, finisher={finisher}, avgCost={avgCost:F2})");
        return pick;
    }
}
