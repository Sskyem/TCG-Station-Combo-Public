using System.Collections.Generic;
using System.Linq;

/// Pre-computed verdicts about low-value legal actions, surfaced to the LLM prompt
/// so the model receives an explicit "do not pick" hint instead of having to derive
/// it from raw HP/energy values. Mirrors the skip rules used by AlgorithmBrain.
public static class LlmActionWarnings
{
    private static readonly HashSet<EnumCardEffectType> HealAdjacentEffects = new()
    {
        EnumCardEffectType.Heal,
        EnumCardEffectType.BenchHeal,
        EnumCardEffectType.Cleanse,
        EnumCardEffectType.Slow,
        EnumCardEffectType.DebuffSelf,
    };

    public static List<string> BuildWarnings(GameStateSnapshot snapshot, List<GameAction> legalActions)
    {
        var lines = new List<string>();
        if (legalActions == null) return lines;

        for (int i = 0; i < legalActions.Count; i++)
        {
            string reason = ClassifyWaste(legalActions[i], snapshot, legalActions);
            if (reason != null)
                lines.Add($"  {i}. {legalActions[i]} — {reason}");
        }
        return lines;
    }

    private static string ClassifyWaste(GameAction action, GameStateSnapshot snapshot, List<GameAction> legalActions)
    {
        if (action == null) return null;

        if (action.type == GameActionType.PlayTrainer &&
            action.card?.baseData is TrainerData td)
        {
            if (IsHealOnlyWithNoTarget(td, snapshot))
                return "heal/cleanse with no valid target (active and bench at full HP, no status)";
        }

        if (action.type == GameActionType.EndTurn && HasAttachEnergyAvailable(legalActions))
        {
            return "energy is still available and AttachEnergy is legal";
        }

        return null;
    }

    private static bool HasAttachEnergyAvailable(List<GameAction> legalActions)
    {
        if (legalActions == null) return false;

        foreach (var action in legalActions)
        {
            if (action?.type == GameActionType.AttachEnergy && action.target != null)
                return true;
        }

        return false;
    }

    private static bool IsHealOnlyWithNoTarget(TrainerData td, GameStateSnapshot snapshot)
    {
        if (td.effects == null || td.effects.Count == 0) return false;

        bool hasHeal     = td.effects.Any(e => e.cardEffectType == EnumCardEffectType.Heal);
        bool hasBenchHeal = td.effects.Any(e => e.cardEffectType == EnumCardEffectType.BenchHeal);
        bool hasCleanse  = td.effects.Any(e => e.cardEffectType == EnumCardEffectType.Cleanse);

        bool hasRecovery = hasHeal || hasBenchHeal || hasCleanse;
        if (!hasRecovery) return false;

        bool hasNonHeal = td.effects.Any(e => !HealAdjacentEffects.Contains(e.cardEffectType));
        if (hasNonHeal) return false;

        return !AnyHealTargetBelowMax(hasHeal, hasBenchHeal, hasCleanse, snapshot);
    }

    private static bool AnyHealTargetBelowMax(bool hasHeal, bool hasBenchHeal, bool hasCleanse, GameStateSnapshot snapshot)
    {
        var me = snapshot?.MyState;
        if (me == null) return false;

        if (hasCleanse && me.ActivePokemon != null)
        {
            if (me.ActivePokemon.IsPoisoned ||
                me.ActivePokemon.SpecialCondition != EnumSpecialConditionType.None)
                return true;
        }

        if (hasHeal && me.ActivePokemon != null)
        {
            if (me.ActivePokemon.CurrentHp < me.ActivePokemon.MaxHp)
                return true;
        }

        if (hasBenchHeal && me.Bench != null)
        {
            foreach (var b in me.Bench)
                if (b != null && b.CurrentHp < b.MaxHp) return true;
        }

        return false;
    }
}
