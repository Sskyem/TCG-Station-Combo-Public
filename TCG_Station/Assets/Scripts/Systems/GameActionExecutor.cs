using UnityEngine;

/// <summary>
/// Wykonuje GameAction przez wywołanie odpowiedniej metody PlayerManager.
/// Nie waliduje legalności — zakłada że akcja pochodzi z LegalActionGenerator.
/// </summary>
public static class GameActionExecutor
{
    public static void Execute(GameAction action, PlayerController player, PlayerManager pm)
    {
        DecisionLogger.Instance?.LogGameActionChoice(player, pm, action);

        switch (action.type)
        {
            case GameActionType.PlayBasicPokemon:
                pm.TryPlayPokemon(action.card);
                break;

            case GameActionType.Evolve:
                // Re-validate: card must still be in hand, target must still be on board and eligible.
                if (!player.hand.Contains(action.card))
                {
                    Debug.LogWarning($"[GameActionExecutor] Evolve skipped — {action.card?.baseData?.cardName} no longer in hand (already used this turn?).");
                    break;
                }
                if (action.target == null || !action.target.pokemonLogic.tempBuffsData.canEvolve)
                {
                    Debug.LogWarning($"[GameActionExecutor] Evolve skipped — target {action.target?.baseData?.cardName} is no longer eligible.");
                    break;
                }
                pm.ExecuteEvolutionPlay(action.card, action.target, player);
                break;

            case GameActionType.AttachEnergy:
                if (!player.canAddEnergy)
                {
                    Debug.LogWarning("[GameActionExecutor] AttachEnergy skipped — energy already attached this turn.");
                    break;
                }
                EnergyZone zone = pm.GetEnergyZoneFor(player);
                if (zone == null)
                {
                    Debug.LogWarning("[GameActionExecutor] Brak EnergyZone dla gracza.");
                    break;
                }
                CardInstance attachTarget = action.target;
                bool isActive = player.activePokemon == attachTarget;
                bool isBenched = player.benchPokemons != null && player.benchPokemons.Contains(attachTarget);
                if (!isActive && !isBenched)
                {
                    if (player.hand != null && player.hand.Contains(attachTarget))
                    {
                        Debug.LogWarning($"[GameActionExecutor] AttachEnergy skipped — target '{attachTarget?.baseData?.cardName}' is still in hand. Play it first.");
                        break;
                    }

                    // Target evolved away this turn — fall back to current active Pokemon.
                    Debug.LogWarning($"[GameActionExecutor] AttachEnergy target '{attachTarget?.baseData?.cardName}' not in player's slots (evolved?) — falling back to active Pokemon.");
                    attachTarget = player.activePokemon;
                    if (attachTarget == null) break;
                }
                pm.GiveEnergyToPokemon(zone, attachTarget);
                break;

            case GameActionType.Attack:
                pm.TryAttack(action.attackIndex);
                break;

            case GameActionType.Retreat:
                pm.Retreat(player, action.target);
                break;

            case GameActionType.PlayTrainer:
                pm.TryPlayTrainer(action.card);
                break;

            case GameActionType.EndTurn:
                TurnManager.Instance.RequestEndTurn();
                break;

            default:
                Debug.LogWarning($"[GameActionExecutor] Nieznany typ akcji: {action.type}");
                break;
        }
    }
}
