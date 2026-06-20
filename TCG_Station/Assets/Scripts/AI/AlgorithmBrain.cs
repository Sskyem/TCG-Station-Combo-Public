using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public class AlgorithmBrain : PlayerBrain
{
    private float setupDelay        => GameRulesConfig.Instance.aiSetupDelay;
    private float playCardDelay     => GameRulesConfig.Instance.aiPlayCardDelay;
    private float attachEnergyDelay => GameRulesConfig.Instance.aiAttachEnergyDelay;
    private float attackDelay       => GameRulesConfig.Instance.aiAttackDelay;
    private float endTurnDelay      => GameRulesConfig.Instance.aiEndTurnDelay;

    // Numeric tuning profile (scoring weights). Defaults to Standard = historical inline weights.
    // PlayerManager injects the per-seat profile via SetProfile before/around Initialize so a single
    // decision logic can be benchmarked under several archetype-oriented weight sets.
    private AlgorithmProfile profile = AlgorithmProfile.Standard();
    private EnumAlgorithmProfile profileVariant = EnumAlgorithmProfile.Standard;

    public EnumAlgorithmProfile ProfileVariant => profileVariant;

    public void SetProfile(EnumAlgorithmProfile variant)
    {
        profileVariant = variant;
        profile = AlgorithmProfile.For(variant);
    }

    // Set during the trainer phase when a SwapSelf trainer repositioned the active. Lets the turn loop
    // (and MLBrain, which reuses PerformTrainerPhase) suppress a redundant manual retreat afterwards.
    public bool SwappedActiveViaTrainerThisTurn { get; private set; }

    // TurnMeta logging is DISABLED. It produced one extra cross-category record per turn
    // (category "TurnMeta") that mixed into the per-category training/replay data and added
    // noise. The code is kept (not deleted) behind this single flag — set it to true to
    // re-enable the one-record-per-turn TurnMeta log (see LogTurnMeta and LogTopScores).
    private const bool EnableTurnMetaLogging = false;

    // Accumulates the concrete non-skip chosen labels from each LogTopScores call during the current
    // turn. Flushed into a single TurnMeta JSONL record at the end of PerformTurn so the ML pipeline
    // can compare action types (Attack vs AttachEnergy vs PlayBasic) at a cross-category level.
    private readonly List<DecisionLogger.ScoreEntry> _turnMetaEntries = new();
    private GameStateSnapshot _turnStartSnapshot;
    private int _turnStartTurnNumber;

    public override IEnumerator PerformSetupPhase(System.Action<List<CardInstance>> onSetupComplete)
    {
        Debug.Log($"[AlgorithmBrain] Setup phase for player {myPlayer.playerName}...");

        yield return new WaitForSeconds(setupDelay);

        List<CardInstance> availableBasics = GetBasicPokemonsInHand();
        List<CardInstance> cardsToPlay = new List<CardInstance>();

        if (availableBasics.Count > 0)
        {
            CardInstance selected = ChooseSetupBasic(availableBasics);
            cardsToPlay.Add(selected);
            Debug.Log($"[AlgorithmBrain] Choosing first Basic Pokemon: {selected.baseData.cardName} (attack cost: {MinAttackCost(selected)})");
        }
        else
        {
            Debug.LogWarning("[AlgorithmBrain] No Basic Pokemon in hand.");
        }

        onSetupComplete?.Invoke(cardsToPlay);
    }

    public override IEnumerator PerformTurn()
    {
        Debug.Log($"[AlgorithmBrain] Turn for player {myPlayer.playerName}, active: {myPlayer.activePokemon?.baseData?.cardName ?? "NULL"}");

        PlayerController opponent = playerManager.player1 == myPlayer ? playerManager.player2 : playerManager.player1;

        // Capture the board state at the very start of the turn for the TurnMeta record.
        // This snapshot is reused at the end so the ML model sees pre-action context.
        if (EnableTurnMetaLogging)
        {
            _turnMetaEntries.Clear();
            _turnStartTurnNumber = TurnManager.Instance != null ? TurnManager.Instance.turnCounter : 0;
            _turnStartSnapshot = DecisionLogger.Instance != null
                ? GameStateSnapshot.Create(myPlayer, opponent, _turnStartTurnNumber, myPlayer.playerId)
                : null;
        }

        // Step 1: play useful Basic Pokemon from hand. Last bench slot is scored, not filled blindly.
        bool playedCard = true;
        bool anyBasicInHand = myPlayer.hand.Exists(k => k.baseData is PokemonData d && d.stage == 0);
        if (!anyBasicInHand)
            Debug.Log("[AlgorithmBrain] Step 1: no Basic Pokemon in hand.");

        while (playedCard)
        {
            if (myPlayer.benchPokemons.Count >= GameRulesConfig.Instance.benchSize)
                break;

            playedCard = false;
            CardInstance card = ChooseBestBasicToPlay(opponent);
            if (card != null && card.baseData is PokemonData data)
            {
                Debug.Log($"[AlgorithmBrain] Step 1: playing Basic {data.cardName}.");
                playerManager.TryPlayPokemon(card);
                if (!myPlayer.hand.Contains(card))
                {
                    playedCard = true;
                    yield return new WaitForSeconds(playCardDelay);
                    if (!IsStillActiveTurn("Step 1")) yield break;
                }
            }
        }

        // Step 2: play available evolutions. Extracted so MLBrain can reuse the heuristic.
        yield return PerformEvolutionPhase(opponent);
        if (!IsStillActiveTurn("Step 2")) yield break;

        // Step 3: play legal Trainer cards. Extracted so MLBrain can reuse the heuristic.
        yield return PerformTrainerPhase(opponent);
        if (!IsStillActiveTurn("Step 3")) yield break;

        // Step 4: attach energy.
        // Jeśli aktywny Pokemon ma już energię na atak, preferuj pokemona z ławki który jej nie ma.
        EnergyZone energyZone = playerManager.GetEnergyZoneFor(myPlayer);
        if (!myPlayer.canAddEnergy)
        {
            Debug.Log("[AlgorithmBrain] Step 4: energy attachment is blocked or already used this turn.");
        }
        else if (energyZone == null || energyZone.currentEnergy == EnumPokemonType.None)
        {
            Debug.Log("[AlgorithmBrain] Step 4: energy zone is empty.");
        }
        else
        {
            CardInstance energyTarget = ChooseBestEnergyTarget(energyZone.currentEnergy, opponent);
            if (energyTarget == null)
            {
                Debug.Log("[AlgorithmBrain] Step 4: no positive-score energy target.");
            }
            else
            {
                Debug.Log($"[AlgorithmBrain] Step 4: giving {energyZone.currentEnergy} energy to {energyTarget.baseData.cardName}.");
                playerManager.GiveEnergyToPokemon(energyZone, energyTarget);
                yield return new WaitForSeconds(attachEnergyDelay);
                if (!IsStillActiveTurn("Step 4")) yield break;
            }
        }

        // Step 5: retreat jeśli wróg może nas nokautować a na ławce mamy gotowego do ataku Pokemona.
        CardInstance retreatTarget = SwappedActiveViaTrainerThisTurn ? null : ChooseBestRetreatTarget(opponent);
        if (SwappedActiveViaTrainerThisTurn)
            Debug.Log("[AlgorithmBrain] Step 5: skipping retreat — active already switched via trainer this turn.");
        if (retreatTarget != null)
        {
            Debug.Log($"[AlgorithmBrain] Step 5: retreating to {retreatTarget.baseData.cardName}.");
            playerManager.Retreat(myPlayer, retreatTarget);
            yield return new WaitForSeconds(playCardDelay);
            if (!IsStillActiveTurn("Step 5")) yield break;
        }

        // Step 6: attack only if the active Pokemon can afford at least one attack.
        if (CanDeclareAnyAttack(myPlayer.activePokemon, opponent))
        {
            int attackIndex = ChooseBestAttackIndex(opponent);
            if (attackIndex >= 0)
            {
                Debug.Log($"[AlgorithmBrain] Step 6: attacking with attack[{attackIndex}] ({myPlayer.activePokemon?.baseData?.cardName}).");
                playerManager.TryAttack(attackIndex);
            }
            else
            {
                // Distinguishes a deliberate "no good attack" decision from a missed/failed animation in
                // the logs — see the [AlgorithmScore] Attack line above for per-attack reasons.
                Debug.Log("[AlgorithmBrain] Step 6: no attack chosen (all attacks blocked or scored below skip).");
            }
            yield return new WaitForSeconds(attackDelay);
            if (!IsStillActiveTurn("Step 6")) yield break;
        }
        else
        {
            Debug.Log("[AlgorithmBrain] Step 6: skipping attack — active cannot declare any attack (energy/Sleep/Paralyze/Confuse).");
        }

        Debug.Log($"[AlgorithmBrain] {myPlayer.playerName} ends turn.");
        if (EnableTurnMetaLogging) LogTurnMeta();
        yield return new WaitForSeconds(endTurnDelay);
        TurnManager.Instance.RequestEndTurn();
    }

    // Step 2: play available evolutions. Extracted verbatim from PerformTurn so MLBrain can reuse the
    // evolution heuristic (evolutions are not represented in the BC dataset).
    public IEnumerator PerformEvolutionPhase(PlayerController opponent)
    {
        if (!myPlayer.canEvolve)
        {
            Debug.Log("[AlgorithmBrain] Step 2: evolutions are blocked this turn.");
        }
        else
        {
            bool evolvedThisPass;
            do
            {
                evolvedThisPass = false;
                foreach (CardInstance card in new List<CardInstance>(myPlayer.hand))
                {
                    if (!myPlayer.hand.Contains(card)) continue;

                    if (card.baseData is PokemonData data && data.stage > 0)
                    {
                        List<CardInstance> targets = playerManager.GetEvolvableTargets(card, myPlayer);
                        if (targets.Count > 0)
                        {
                            CardInstance target = ChooseEvolutionTarget(card, targets, opponent);
                            if (target == null)
                            {
                                Debug.Log($"[AlgorithmBrain] Step 2: delaying {card.baseData.cardName} evolution — active would lose its ready attack.");
                                continue;
                            }

                            Debug.Log($"[AlgorithmBrain] Step 2: evolving {target.baseData.cardName} into {card.baseData.cardName}.");
                            playerManager.ExecuteEvolutionPlay(card, target, myPlayer);
                            evolvedThisPass = !myPlayer.hand.Contains(card);
                            yield return new WaitForSeconds(playCardDelay);
                            if (!IsStillActiveTurn("Step 2")) yield break;
                            if (evolvedThisPass) break;
                        }
                        else
                        {
                            Debug.Log($"[AlgorithmBrain] Step 2: {card.baseData.cardName} has no legal evolution targets.");
                        }
                    }
                }
            } while (evolvedThisPass);
        }
    }

    // Step 3: play legal Trainer cards. Extracted verbatim from PerformTurn so MLBrain can reuse the
    // trainer heuristic (trainers are not in the BC dataset). Sets SwappedActiveViaTrainerThisTurn so
    // the caller can suppress a redundant manual retreat.
    // Heal/BenchHeal cards only when healing would prevent a KO. Only one Supporter per turn.
    public IEnumerator PerformTrainerPhase(PlayerController opponent)
    {
        SwappedActiveViaTrainerThisTurn = false;
        bool supporterPlayedThisTurn = false;
        foreach (CardInstance card in new List<CardInstance>(myPlayer.hand))
        {
            if (card.baseData is TrainerData td)
            {
                if (td.trainerSubType == EnumTrainerSubType.Supporter)
                {
                    if (supporterPlayedThisTurn)
                    {
                        Debug.Log($"[AlgorithmBrain] Step 3: {card.baseData.cardName} — Supporter already played this turn, skipping.");
                        continue;
                    }
                }

                if (TrainerHasRecoveryEffect(td) && !HasNonHealEffects(td) && !AnyHealTargetBelowMax(td))
                {
                    Debug.Log($"[AlgorithmBrain] Step 3: {card.baseData.cardName} — recovery-only card, no damaged or statused target, skipping.");
                    continue;
                }

                // Special case: card heals but also harms self (e.g. Chilly Pepper burns own Pokemon).
                // Net effect is negative if nothing needs healing — skip.
                if (TrainerHasHealEffect(td) && TrainerHasSelfHarmEffect(td) && !AnyHealTargetBelowMax(td))
                {
                    Debug.Log($"[AlgorithmBrain] Step 3: {card.baseData.cardName} — heals but harms self, no target needs healing, skipping.");
                    continue;
                }

                if (TrainerHasHealEffect(td) && !TrainerHasCleanseEffect(td) && !HasNonHealEffects(td) && !HealWouldPreventKO(td, opponent))
                {
                    Debug.Log($"[AlgorithmBrain] Step 3: {card.baseData.cardName} — healing would not prevent KO, skipping.");
                    continue;
                }

                if (TrainerHasSwapSelfEffect(td) && !ShouldPlaySwapSelfTrainer(opponent))
                {
                    Debug.Log($"[AlgorithmBrain] Step 3: {card.baseData.cardName} — swapping active would worsen position, skipping.");
                    continue;
                }

                if (TrainerHasSwapEnemyEffect(td) && !ShouldPlaySwapEnemyTrainer(opponent))
                {
                    Debug.Log($"[AlgorithmBrain] Step 3: {card.baseData.cardName} — switching enemy active gives no advantage, skipping.");
                    continue;
                }

                if (TrainerHasDamageReductionEffect(td) && !ShouldPlayDamageReductionTrainer(td, opponent))
                {
                    Debug.Log($"[AlgorithmBrain] Step 3: {card.baseData.cardName} — damage reduction is not needed yet, skipping.");
                    continue;
                }

                if (TrainerHasRootEffect(td) && !ShouldPlayRootTrainer(opponent))
                {
                    Debug.Log($"[AlgorithmBrain] Step 3: {card.baseData.cardName} — rooting enemy active gives no advantage, skipping.");
                    continue;
                }

                // Draw cards that pay a hand-discard cost (e.g. Rummage): skip when it would risk
                // discarding evolutions we still need, or when our hand is too thin to spare them.
                if (TrainerHasHandDiscardEffect(td) && !ShouldPlayHandDiscardDrawTrainer(td))
                {
                    Debug.Log($"[AlgorithmBrain] Step 3: {card.baseData.cardName} — hand-discard draw risks needed cards, skipping.");
                    continue;
                }

                bool success = playerManager.TryPlayTrainer(card);
                if (success)
                {
                    if (td.trainerSubType == EnumTrainerSubType.Supporter)
                        supporterPlayedThisTurn = true;
                    if (TrainerHasSwapSelfEffect(td))
                        SwappedActiveViaTrainerThisTurn = true;
                    yield return new WaitForSeconds(playCardDelay);
                    if (!IsStillActiveTurn("Step 3")) yield break;
                }
            }
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private CardInstance ChooseSetupBasic(List<CardInstance> availableBasics)
    {
        CardInstance rampBasic = availableBasics
            .Where(HasEnergyRampAttack)
            .OrderByDescending(GetMaxEnergyRampAmount)
            .ThenBy(MinAttackCost)
            .ThenByDescending(c => HasFutureEvolution(c) ? 1 : 0)
            .ThenByDescending(GetMaxPrintedDamage)
            .FirstOrDefault();

        if (rampBasic != null && RequiresEnergyRampSupport())
            return rampBasic;

        return availableBasics
            .OrderBy(MinAttackCost)
            .First();
    }

    private sealed class AlgorithmDecisionContext
    {
        public PlayerController Opponent;
        public CardInstance Active;
        public CardInstance EnemyActive;
        public EnumPokemonType CurrentEnergy;
        public int EnemyMaxDamage;
        public int OpponentMaxHp;
        public int FreeBenchSlots;
        public bool ActiveReady;
        public bool ActiveLikelyKo;
        public bool RequiresRamp;
        public string StrongestBasicName;
    }

    private sealed class AlgorithmActionScore
    {
        public CardInstance Target;
        public int AttackIndex = -1;
        public string Label;
        public int Score;
        public bool Blocked;
        public readonly List<string> Reasons = new();

        public void Add(int points, string reason)
        {
            Score += points;
            Reasons.Add($"{points:+#;-#;0} {reason}");
        }

        public void Block(string reason)
        {
            Blocked = true;
            Score = -100000;
            Reasons.Add($"BLOCK {reason}");
        }

        public string ReasonSummary => Reasons.Count == 0 ? "no reasons" : string.Join(", ", Reasons.Take(5));
    }

    private AlgorithmDecisionContext BuildDecisionContext(PlayerController opponent, EnumPokemonType currentEnergy = EnumPokemonType.None)
    {
        CardInstance active = myPlayer.activePokemon;
        int enemyMaxDamage = EstimateEnemyMaxDamage(opponent);
        return new AlgorithmDecisionContext
        {
            Opponent = opponent,
            Active = active,
            EnemyActive = opponent?.activePokemon,
            CurrentEnergy = currentEnergy,
            EnemyMaxDamage = enemyMaxDamage,
            OpponentMaxHp = GetOpponentMaxCurrentHp(opponent),
            FreeBenchSlots = GameRulesConfig.Instance.benchSize - myPlayer.benchPokemons.Count,
            ActiveReady = HasEnergyForAnyAttack(active),
            ActiveLikelyKo = active?.pokemonLogic != null && active.pokemonLogic.currentHp <= enemyMaxDamage,
            RequiresRamp = RequiresEnergyRampSupport(),
            StrongestBasicName = GetStrongestEvolutionLineBasicName()
        };
    }

    private CardInstance ChooseBestBasicToPlay(PlayerController opponent)
    {
        var context = BuildDecisionContext(opponent);
        var scores = myPlayer.hand
            .Where(card => card?.baseData is PokemonData pd && pd.stage == 0)
            .Select(card => ScorePlayBasic(card, context))
            .ToList();

        AlgorithmActionScore best = scores
            .Where(s => !s.Blocked)
            .OrderByDescending(s => s.Score)
            .ThenBy(s => GetStableCardOrder(s.Target))
            .FirstOrDefault();

        string chosen = best != null && best.Score > 0 ? best.Label : "(skip)";
        LogTopScores("PlayBasic", scores, chosen);

        return best != null && best.Score > 0 ? best.Target : null;
    }

    private AlgorithmActionScore ScorePlayBasic(CardInstance card, AlgorithmDecisionContext context)
    {
        var score = new AlgorithmActionScore
        {
            Target = card,
            Label = $"PlayBasic({card?.baseData?.cardName ?? "?"})"
        };

        if (card?.baseData is not PokemonData pd || pd.stage != 0)
        {
            score.Block("not a Basic Pokemon");
            return score;
        }

        if (context.Active != null && context.FreeBenchSlots <= 0)
        {
            score.Block("bench is full");
            return score;
        }

        bool isRamp = HasEnergyRampAttack(card);
        HashSet<EnumPokemonType> pendingRampTypes = GetPendingStrategicRampTypes(scarceOnly: true);
        bool isNeededRampType = isRamp && pendingRampTypes.Contains(pd.type);
        bool isStrongestLine = !string.IsNullOrEmpty(context.StrongestBasicName) &&
                               pd.cardName == context.StrongestBasicName;
        bool hasScalingLine = HasScalingDamageLine(card);

        if (context.FreeBenchSlots <= 1 && context.Active != null)
        {
            if (ShouldReserveBenchSlotForRampEngine(card))
            {
                score.Block("last bench slot reserved for typed EnergyRamp engine");
                return score;
            }

            if (ShouldAvoidLowValueDuplicateBasic(card, context, isNeededRampType, isStrongestLine, hasScalingLine))
            {
                score.Block("bench slot conserved; duplicate Basic already on board");
                return score;
            }

            if (ShouldReserveBenchSlotForStrongestLine(card))
            {
                score.Block("last bench slot reserved for strongest evolution line");
                return score;
            }
        }

        score.Add(profile.playBasicBaseBonus, "Basic can improve board");
        if (context.Active == null)
            score.Add(profile.fillsActiveSlotBonus, "fills empty active slot");

        if (isRamp)
        {
            score.Add(context.RequiresRamp ? profile.rampEngineNeededBonus : profile.rampEngineBonus, "EnergyRamp engine");
            if (isNeededRampType)
                score.Add(profile.neededRampTypeBonus, $"{pd.type} EnergyRamp needed for strategic attacker");
            score.Add(GetMaxEnergyRampAmount(card) * profile.rampAmountMultiplier, "ramp amount");
        }

        if (isStrongestLine)
            score.Add(HasEvolutionLineOnBoard(pd.cardName) ? profile.strongestLineOnBoardBonus : profile.strongestLineBonus, "strongest evolution line");

        if (hasScalingLine)
            score.Add(profile.scalingLineBonus, "future scaling damage line");

        if (HasFutureEvolution(card))
            score.Add(profile.futureEvolutionBonus, "has future evolution");

        score.Add(Mathf.Min(profile.printedCeilingCap, GetMaxPrintedDamage(card) / profile.printedCeilingDivisor), "printed attack ceiling");

        if (context.FreeBenchSlots == 1 && context.Active != null && !isRamp && !isStrongestLine && !hasScalingLine)
            score.Add(profile.weakLastBenchSlotPenalty, "weak use of last bench slot");

        return score;
    }

    private CardInstance ChooseBestEnergyTarget(EnumPokemonType availableEnergy, PlayerController opponent)
    {
        var context = BuildDecisionContext(opponent, availableEnergy);
        var candidates = new[] { myPlayer.activePokemon }
            .Concat(myPlayer.benchPokemons)
            .Where(c => c?.pokemonLogic != null)
            .ToList();

        var scores = candidates
            .Select(target => ScoreAttachEnergy(target, context))
            .ToList();

        AlgorithmActionScore best = scores
            .Where(s => !s.Blocked)
            .OrderByDescending(s => s.Score)
            .ThenByDescending(s => s.Target == myPlayer.activePokemon ? 1 : 0)
            .ThenBy(s => GetStableCardOrder(s.Target))
            .FirstOrDefault();

        string chosen = best != null && best.Score > 0 ? best.Label : "(skip)";
        LogTopScores("AttachEnergy", scores, chosen);

        return best != null && best.Score > 0 ? best.Target : null;
    }

    private AlgorithmActionScore ScoreAttachEnergy(CardInstance target, AlgorithmDecisionContext context)
    {
        var score = new AlgorithmActionScore
        {
            Target = target,
            Label = $"AttachEnergy(to {target?.baseData?.cardName ?? "?"})"
        };

        if (context.CurrentEnergy == EnumPokemonType.None)
        {
            score.Block("energy zone is empty");
            return score;
        }

        if (target?.pokemonLogic == null || target.baseData is not PokemonData pd)
        {
            score.Block("invalid target");
            return score;
        }

        bool isActive = target == context.Active;
        bool activeCanAttackThisTurn = !isActive || target.pokemonLogic.tempBuffsData.canAttack;
        bool activeDisabledThisTurn = isActive && !target.pokemonLogic.tempBuffsData.canAttack;
        bool readyBefore = HasEnergyForAnyAttack(target);
        bool helps = WouldEnergyHelp(target, context.CurrentEnergy);
        bool helpsLine = helps || WouldEnergyHelpEvolutionLine(target, context.CurrentEnergy);
        int missingBefore = EnergyMissingNow(target);
        int missingAfter = EnergyMissingAfterAttach(target, context.CurrentEnergy);
        bool readyAfter = missingAfter == 0;
        bool improvedAttackReadiness = missingAfter < missingBefore;
        bool discardReserve = ShouldStockpileEnergyForNextDiscardAttack(target, context.CurrentEnergy);
        bool strategicDiscardLine = StrategicDiscardLineNeedsEnergy(target, context.CurrentEnergy);
        bool protectedFinisherEnergy = BoardHasOtherStrategicDiscardLineNeedingEnergy(target, context.CurrentEnergy);
        bool backupForThreatenedDiscardActive = IsBackupForThreatenedStrategicDiscardActive(target, context);
        bool scalingImproved = false;

        if (helps)
            score.Add(profile.energyAdvancesAttackBonus, "energy type advances an attack cost");
        else if (helpsLine)
            score.Add(profile.energyAdvancesLineBonus, "energy advances a future evolution's attack cost");
        else
            score.Add(profile.energyWrongTypePenalty, "energy type does not advance a normal attack");

        if (strategicDiscardLine)
            score.Add(isActive ? profile.strategicDiscardActiveBonus : profile.strategicDiscardBenchBonus, "energy reserved for strategic discard attacker");

        if (backupForThreatenedDiscardActive)
            score.Add(profile.backupDiscardBonus, "backup discard attacker needed after likely KO");

        if (!readyBefore && readyAfter)
        {
            if (isActive && activeCanAttackThisTurn)
                score.Add(profile.activeBecomesReadyBonus, "active attacks this turn after attach");
            else if (isActive)
                score.Add(profile.reduceDeficitBase, "active becomes energy-ready but cannot attack this turn");
            else
                score.Add(profile.benchBecomesReadyBonus, "bench becomes ready");
        }
        else if (!readyBefore && improvedAttackReadiness)
            score.Add(Mathf.Max(profile.reduceDeficitFloor, profile.reduceDeficitBase - missingAfter * profile.reduceDeficitPerMissing), "reduces attack energy deficit");
        else if (readyBefore && helps)
            score.Add(profile.preparesExpensiveAttackBonus, "unlocks or prepares a more expensive attack");

        if (isActive)
        {
            if (activeCanAttackThisTurn)
                score.Add(profile.activeImmediateBonus, "active can use energy immediately");

            // Securing a lethal KO this turn beats any setup/ramp consideration: if this attach
            // newly lets the active KO the enemy active, go for it.
            if (activeCanAttackThisTurn && WouldAttachEnableKoOnActiveEnemy(target, context.CurrentEnergy, context.EnemyActive))
                score.Add(profile.attachEnablesKoBonus, "attach lets active KO the enemy active this turn");

            if (discardReserve && !context.ActiveLikelyKo)
                score.Add(profile.safeDiscardReserveBonus, "safe reserve for next high-damage discard attack");

            if (context.ActiveLikelyKo && discardReserve)
                score.Add(profile.unsafeDiscardReservePenalty, "discard reserve is unsafe because active likely KO");

            if (context.ActiveLikelyKo && !readyAfter && !discardReserve)
                score.Add(profile.activeKoUnfinishedEnergyPenalty, "active likely KO before unfinished energy matters");

            if (context.ActiveLikelyKo && activeDisabledThisTurn)
                score.Add(profile.disabledActiveLikelyKoEnergyPenalty, "active cannot attack this turn and is likely KO before using attached energy");

            if (context.ActiveLikelyKo && !helps)
                score.Add(profile.activeKoBadTypePenalty, "bad type on active that is likely KO");
        }
        else
        {
            score.Add(profile.benchThreatBonus, "builds bench threat safely");
            if (discardReserve)
                score.Add(profile.benchStockpileBonus, "bench stockpiles reserve for discard attack");
            if (context.ActiveLikelyKo)
                score.Add(profile.preserveAwayFromKoBonus, "preserves energy away from threatened active");
        }

        if (HasEnergyRampAttack(target))
        {
            score.Add(context.RequiresRamp ? profile.rampEngineNeededBonus : profile.rampEngineBonus, "EnergyRamp engine target");
            score.Add(GetMaxEnergyRampAmount(target) * profile.rampAmountMultiplier, "ramp amount");
            if (protectedFinisherEnergy)
                score.Add(isActive ? profile.finisherEnergyActivePenalty : profile.finisherEnergyBenchPenalty, "do not consume finisher energy on ramp engine");
            if (!readyBefore && readyAfter)
                score.Add(profile.rampBecomesReadyBonus, "ramp attack becomes ready");
            if (isActive && ShouldChargeActiveRampBeforeBenchRamp(target, context.CurrentEnergy))
                score.Add(profile.activeRampChainBonus, "active ramp starts chain sooner");
            if (ShouldPrioritizeActiveRampForStrategicBench(target, context.CurrentEnergy))
                score.Add(profile.activeRampStrategicBenchBonus, "active ramp can fuel strategic bench attacker");
        }

        if (HasScalingDamageLine(target) && context.OpponentMaxHp > 0)
        {
            int beforeDeficit = GetScalingDamageEnergyDeficit(target, context.OpponentMaxHp);
            int afterDeficit = GetScalingDamageEnergyDeficitAfterAttach(target, context.OpponentMaxHp, context.CurrentEnergy);
            if (beforeDeficit != int.MaxValue && afterDeficit < beforeDeficit)
            {
                scalingImproved = true;
                score.Add(profile.scalingPlanBase + Mathf.Min(profile.scalingPlanCap, (beforeDeficit - afterDeficit) * profile.scalingPlanPerDeficit), "moves scaling/Psychic plan toward biggest enemy HP + reserve");
                if (afterDeficit == 0)
                    score.Add(profile.scalingReserveCompleteBonus, "scaling/Psychic reserve is complete");
            }
        }

        if (pd.attacks != null && pd.attacks.Any(atk => GetEnergyDiscardAmount(atk) > 0))
            score.Add(helps || scalingImproved ? profile.discardSupportHelpsBonus : profile.discardSupportBonus, "extra energy supports discard-attack plan");

        score.Add(Mathf.Min(profile.attackerCeilingCap, GetMaxPrintedDamage(target) / profile.attackerCeilingDivisor), "attacker ceiling");

        if (!helpsLine && !scalingImproved && !readyAfter)
            score.Add(profile.noPayoffPenalty, "no concrete payoff after attach");

        return score;
    }

    private CardInstance ChooseBestRetreatTarget(PlayerController opponent)
    {
        CardInstance active = myPlayer.activePokemon;
        var context = BuildDecisionContext(opponent);
        var scores = myPlayer.benchPokemons
            .Select(target => ScoreRetreat(target, context))
            .ToList();

        AlgorithmActionScore best = scores
            .Where(s => !s.Blocked)
            .OrderByDescending(s => s.Score)
            .ThenBy(s => GetStableCardOrder(s.Target))
            .FirstOrDefault();

        string chosen = best != null && best.Score > 0 ? best.Label : "(skip)";
        LogTopScores("Retreat", scores, chosen);

        if (best == null || best.Score <= 0)
            return null;

        var activeData = active?.baseData as PokemonData;
        int retreatCost = activeData != null ? Mathf.Max(0, activeData.retreatCost + myPlayer.retreatEnergyCostChange) : 0;
        int activeEnergy = active?.pokemonLogic?.energyEquipped?.Values.Sum() ?? 0;
        int activeHp = active?.pokemonLogic?.currentHp ?? 0;
        int targetHp = best.Target?.pokemonLogic?.currentHp ?? 0;
        int targetEnergy = best.Target?.pokemonLogic?.energyEquipped?.Values.Sum() ?? 0;
        Debug.Log($"[AlgorithmBrain] Retreat chosen by score: active {active?.baseData?.cardName ?? "?"} HP {activeHp}, energy {activeEnergy}, cost {retreatCost}; target {best.Target?.baseData?.cardName ?? "?"} HP {targetHp}, energy {targetEnergy}. Score {best.Score}: {best.ReasonSummary}");
        return best.Target;
    }

    private AlgorithmActionScore ScoreRetreat(CardInstance target, AlgorithmDecisionContext context)
    {
        var score = new AlgorithmActionScore
        {
            Target = target,
            Label = $"Retreat(to {target?.baseData?.cardName ?? "?"})"
        };

        CardInstance active = context.Active;
        if (active?.pokemonLogic == null || target?.pokemonLogic == null)
        {
            score.Block("invalid active or target");
            return score;
        }

        if (myPlayer.usedManualRetreatThisTurn)
        {
            score.Block("manual retreat already used");
            return score;
        }

        if (!active.pokemonLogic.tempBuffsData.canRetreat)
        {
            score.Block("active cannot retreat");
            return score;
        }

        var activeData = active.baseData as PokemonData;
        if (activeData == null)
        {
            score.Block("active has no PokemonData");
            return score;
        }

        int retreatCost = Mathf.Max(0, activeData.retreatCost + myPlayer.retreatEnergyCostChange);
        int activeEnergy = active.pokemonLogic.energyEquipped?.Values.Sum() ?? 0;
        if (activeEnergy < retreatCost)
        {
            score.Block($"not enough energy to retreat ({activeEnergy}/{retreatCost})");
            return score;
        }

        // Retreat is only useful when the promoted Pokemon can actually declare an attack.
        // Energy readiness alone is insufficient for attacks with additional costs such as
        // Garchomp's DiscardHand: after evolutions the hand may be empty even with full energy.
        if (!CanDeclareAnyAttack(target, context.Opponent))
        {
            score.Block("target cannot declare an attack after retreat");
            return score;
        }

        if (HasFinalEvolutionInHand(target) && !CanWinWithReadyAttack(target))
        {
            score.Block("bench target should wait for final evolution unless it wins now");
            return score;
        }

        int activeDamage = GetMaxReadyDamage(active, context.EnemyActive);
        int targetDamage = GetMaxReadyDamage(target, context.EnemyActive);
        int activeAttackValue = GetMaxReadyAttackValue(active, context.EnemyActive, context.Opponent);
        int targetAttackValue = GetMaxReadyAttackValue(target, context.EnemyActive, context.Opponent);
        bool activeUsefulRamp = ActiveHasUsefulRampAttack(active);
        int enemyHp = context.EnemyActive?.pokemonLogic?.currentHp ?? int.MaxValue;
        bool activeCanKo = activeDamage >= enemyHp;
        bool targetCanKo = targetDamage >= enemyHp;
        bool targetLikelyKo = context.EnemyMaxDamage > 0 &&
                              target.pokemonLogic.currentHp <= context.EnemyMaxDamage;

        // Attack (Step 6) runs after retreat (Step 5), so an active that can KO the enemy this turn
        // should stay and attack — retreating wastes the KO and the retreat energy. The only
        // exception is when the bench target can ALSO KO and the active is about to be KO'd anyway:
        // then retreating preserves the dying active's investment and still secures the KO.
        if (activeCanKo && !(targetCanKo && context.ActiveLikelyKo))
        {
            score.Block("active can KO current active this turn — attack instead of retreating");
            return score;
        }

        if (context.ActiveLikelyKo)
            score.Add(profile.activeLikelyKoBonus, "active likely KO next turn");

        if (targetLikelyKo && !targetCanKo)
            score.Add(profile.targetLikelyKoPenalty, "retreat target likely KO next turn");
        else if (!targetLikelyKo)
            score.Add(Mathf.Min(profile.targetSurvivalBufferCap, target.pokemonLogic.currentHp / profile.targetSurvivalBufferDivisor), "retreat target survival buffer");

        bool targetImprovesBoard = targetAttackValue > 0 || ActiveHasUsefulRampAttack(target);
        if (activeAttackValue == 0 && !activeUsefulRamp && targetImprovesBoard)
            score.Add(profile.activeNoDamageBonus, "active has no damage and no useful ramp");

        if (activeUsefulRamp)
            score.Add(profile.activeUsefulRampPenalty, "active has useful ramp attack");

        if (retreatCost > 0)
            score.Add(-Mathf.Min(profile.retreatEnergyPenaltyCap, retreatCost * profile.retreatEnergyPenaltyPerCost), "retreat spends active energy");

        if (BestReadyAttackHasEffect(target, context.EnemyActive, EnumCardEffectType.SwapSelf))
            score.Add(activeCanKo ? profile.targetSwapAfterRetreatKoPenalty : profile.targetSwapPenalty, "target attack switches after manual retreat");

        int damageDelta = targetAttackValue - activeAttackValue;
        score.Add(Mathf.Clamp(damageDelta, profile.damageDeltaClampMin, profile.damageDeltaClampMax), "attack value delta vs active");

        if (targetCanKo && !activeCanKo)
            score.Add(profile.benchCanKoBonus, "bench can KO current active");

        if (targetAttackValue >= activeAttackValue + profile.benchMuchStrongerDamageDelta &&
            targetAttackValue >= activeAttackValue * 2)
            score.Add(profile.benchMuchStrongerBonus, "bench is much stronger attacker");

        if (!context.ActiveLikelyKo && damageDelta <= 0 && activeUsefulRamp)
            score.Add(profile.retreatLosesRampPenalty, "retreat would lose better ramp plan");

        return score;
    }

    private int ChooseBestAttackIndex(PlayerController opponent)
    {
        CardInstance active = myPlayer.activePokemon;
        var pd = active?.baseData as PokemonData;
        if (active?.pokemonLogic == null || pd?.attacks == null || pd.attacks.Count == 0)
            return -1;

        if (!CanDeclareAnyAttack(active, opponent))
            return -1;

        var scores = new List<AlgorithmActionScore>();
        for (int i = 0; i < pd.attacks.Count; i++)
        {
            AttackData attack = pd.attacks[i];
            var score = new AlgorithmActionScore
            {
                Target = active,
                AttackIndex = i,
                Label = $"Attack[{i}] {attack?.attackName ?? "?"}"
            };

            if (!CardActions.CanAffordAttack(active.pokemonLogic, attack))
            {
                score.Block("cannot afford attack");
                scores.Add(score);
                continue;
            }

            if (!HasEnoughCardsForAttack(myPlayer, attack))
            {
                score.Block("not enough cards in hand to pay attack discard cost");
                scores.Add(score);
                continue;
            }

            int damage = EstimateAttackDamage(active, opponent?.activePokemon, attack);
            score.Add(damage, "estimated damage");

            // Bench/snipe damage (e.g. Flygon's "Sand Sweep") is otherwise invisible to scoring because
            // EstimateAttackDamage only measures the hit on the Active. Score it separately so it never
            // inflates the Active-KO threshold below.
            int benchDamage = EstimateBenchDamageValue(attack, opponent);
            if (benchDamage > 0)
                score.Add(benchDamage, "bench/snipe damage");

            if (opponent?.activePokemon?.pokemonLogic != null && damage >= opponent.activePokemon.pokemonLogic.currentHp)
                score.Add(profile.attackKoBonus, "attack KOs current active");

            if (AttackRisksImportantEvolutionInHand(attack) &&
                (opponent?.activePokemon?.pokemonLogic == null || damage < opponent.activePokemon.pokemonLogic.currentHp))
            {
                score.Block("hand-discard attack risks needed evolution in hand");
                scores.Add(score);
                continue;
            }

            if (HasEnergyRampEffect(attack) && ChooseRampBenchTargetForActiveUtility(active) != null)
                score.Add(profile.rampEnablesUtilityBonus + GetMaxEnergyRampAmount(active) * profile.rampAmountMultiplier, "attack enables useful EnergyRamp");

            if (GetEnergyDiscardAmount(attack) > 0)
                score.Add(damage >= profile.discardAttackDamageThreshold ? profile.discardTradeoffHighBonus : profile.discardTradeoffLowPenalty, "energy-discard attack tradeoff");

            int debuffScore = ScoreOffensiveDebuffs(attack, active, opponent?.activePokemon, damage);
            if (debuffScore != 0)
                score.Add(debuffScore, "attack utility effects");

            if (damage == 0 && benchDamage == 0 && !HasEnergyRampEffect(attack) && debuffScore <= 0)
                score.Add(profile.noDamageNoUtilityPenalty, "no damage, no ramp, no useful status");

            scores.Add(score);
        }

        AlgorithmActionScore best = scores
            .Where(s => !s.Blocked)
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.AttackIndex)
            .FirstOrDefault();

        string chosen = best != null && best.Score > 0 ? best.Label : "(skip)";
        LogTopScores("Attack", scores, chosen);

        return best != null && best.Score > 0 ? best.AttackIndex : -1;
    }

    private int EnergyMissingNow(CardInstance card)
    {
        var pd = card?.baseData as PokemonData;
        if (card?.pokemonLogic == null || pd?.attacks == null || pd.attacks.Count == 0)
            return int.MaxValue;

        return pd.attacks
            .Select(atk => EnergyMissingForAttack(card.pokemonLogic, atk))
            .DefaultIfEmpty(int.MaxValue)
            .Min();
    }

    private int GetStableCardOrder(CardInstance card)
    {
        if (card == null) return int.MaxValue;
        int handIndex = myPlayer.hand?.IndexOf(card) ?? -1;
        if (handIndex >= 0) return handIndex;
        if (myPlayer.activePokemon == card) return 1000;
        int benchIndex = myPlayer.benchPokemons?.IndexOf(card) ?? -1;
        return benchIndex >= 0 ? 2000 + benchIndex : int.MaxValue;
    }

    private void LogTopScores(string group, List<AlgorithmActionScore> scores, string chosenLabel = null)
    {
        if (scores == null || scores.Count == 0) return;

        string summary = string.Join(" | ", scores
            .OrderBy(s => s.Blocked ? 1 : 0)
            .ThenByDescending(s => s.Score)
            .Take(3)
            .Select(s => $"{s.Label}: {(s.Blocked ? "BLOCK" : s.Score.ToString())} [{s.ReasonSummary}]"));

        Debug.Log($"[AlgorithmScore] {group}: {summary}");

        // ML pipeline: per-decision JSONL for behavioral cloning dataset.
        if (DecisionLogger.Instance != null)
        {
            PlayerController opponent = playerManager.player1 == myPlayer ? playerManager.player2 : playerManager.player1;
            int turn = TurnManager.Instance != null ? TurnManager.Instance.turnCounter : 0;
            var snapshot = GameStateSnapshot.Create(myPlayer, opponent, turn, myPlayer.playerId);

            var entries = new List<DecisionLogger.ScoreEntry>(scores.Count);
            foreach (var s in scores)
                entries.Add(new DecisionLogger.ScoreEntry(s.Label, s.Score, s.Blocked, new List<string>(s.Reasons), s.Target?.instanceId ?? -1));

            string effectiveChosen = chosenLabel ?? InferChosenLabel(scores);
            int chosenTargetInstanceId = InferChosenTargetInstanceId(scores, effectiveChosen);
            DecisionLogger.Instance.LogDecision(group, turn, myPlayer.playerId, snapshot, entries, effectiveChosen, chosenTargetInstanceId);

            // Collect into TurnMeta: one candidate per executed non-skip action across all categories.
            // Skipped categories are omitted here; a synthetic "(skip)" is added by LogTurnMeta.
            if (EnableTurnMetaLogging && effectiveChosen != "(skip)")
            {
                AlgorithmActionScore chosenScore = scores.FirstOrDefault(s => s.Label == effectiveChosen);
                _turnMetaEntries.Add(new DecisionLogger.ScoreEntry(
                    effectiveChosen,
                    chosenScore?.Score ?? 0,
                    false,
                    new List<string> { $"category:{group}" },
                    chosenScore?.Target?.instanceId ?? -1));
            }
        }
    }

    private static string InferChosenLabel(List<AlgorithmActionScore> scores)
    {
        var best = scores
            .Where(s => !s.Blocked)
            .OrderByDescending(s => s.Score)
            .FirstOrDefault();
        return best != null && best.Score > 0 ? best.Label : "(skip)";
    }

    private static int InferChosenTargetInstanceId(List<AlgorithmActionScore> scores, string chosenLabel)
    {
        if (string.IsNullOrEmpty(chosenLabel) || chosenLabel == "(skip)")
            return -1;

        var best = scores
            .Where(s => !s.Blocked && s.Label == chosenLabel)
            .OrderByDescending(s => s.Score)
            .FirstOrDefault();

        if (best == null)
            best = scores.FirstOrDefault(s => s.Label == chosenLabel);
        return best?.Target?.instanceId ?? -1;
    }

    // Writes one TurnMeta record covering the whole turn: all executed non-skip actions as
    // candidates (one per LogTopScores call) plus a synthetic "(skip)". The chosen_label is the
    // highest-priority action type executed this turn: Attack > Retreat > AttachEnergy > PlayBasic.
    // The snapshot is from the START of the turn so the ML model sees pre-action board context.
    private void LogTurnMeta()
    {
        if (!EnableTurnMetaLogging) return;
        if (DecisionLogger.Instance == null || _turnStartSnapshot == null) return;
        if (!GameRulesConfig.Instance.enableMlDecisionLogs) return;

        var candidates = new List<DecisionLogger.ScoreEntry>(_turnMetaEntries);
        candidates.Add(new DecisionLogger.ScoreEntry("(skip)", 0, false, new List<string> { "synthetic no-action" }));

        // Primary action: highest-priority category that was actually executed this turn.
        string primary = "(skip)";
        string[] priority = { "Attack", "Retreat", "AttachEnergy", "PlayBasic" };
        foreach (string prefix in priority)
        {
            DecisionLogger.ScoreEntry match = _turnMetaEntries.FirstOrDefault(e =>
                e.label != null && e.label.StartsWith(prefix));
            if (match != null)
            {
                primary = match.label;
                break;
            }
        }

        DecisionLogger.Instance.LogDecision(
            "TurnMeta", _turnStartTurnNumber, myPlayer.playerId,
            _turnStartSnapshot, candidates, primary);
    }

    private bool AttackRisksImportantEvolutionInHand(AttackData attack)
    {
        if (GetHandDiscardCost(myPlayer, attack) <= 0) return false;
        return myPlayer.hand.Any(IsImportantEvolutionToKeep);
    }

    private bool IsImportantEvolutionToKeep(CardInstance evolutionCard)
    {
        if (evolutionCard?.baseData is not PokemonData evolutionData || evolutionData.stage <= 0)
            return false;

        if (CanContinueEvolutionTurnAfterTurn(evolutionCard, evolutionData))
            return true;

        // A future copy still in the deck makes a currently unusable evolution replaceable.
        // Do not skip an entire attack merely to protect a card whose missing intermediate
        // evolution has not been found yet.
        return !myPlayer.deck.Any(card =>
            card?.baseData is PokemonData deckPokemon &&
            deckPokemon.cardName == evolutionData.cardName);
    }

    private bool CanContinueEvolutionTurnAfterTurn(CardInstance evolutionCard, PokemonData evolutionData)
    {
        if (evolutionData == null || string.IsNullOrEmpty(evolutionData.evolvesFrom))
            return false;

        if (BoardHasPokemonNamed(evolutionData.evolvesFrom))
            return true;

        string requiredParent = evolutionData.evolvesFrom;
        var usedHandCards = new HashSet<CardInstance> { evolutionCard };

        while (!string.IsNullOrEmpty(requiredParent))
        {
            CardInstance parentCard = myPlayer.hand.FirstOrDefault(card =>
                card != null &&
                !usedHandCards.Contains(card) &&
                card.baseData is PokemonData handPokemon &&
                handPokemon.cardName == requiredParent);

            if (parentCard?.baseData is not PokemonData parentData)
                return false;

            usedHandCards.Add(parentCard);
            if (BoardHasPokemonNamed(parentData.evolvesFrom))
                return true;

            requiredParent = parentData.evolvesFrom;
        }

        return false;
    }

    private bool BoardHasPokemonNamed(string pokemonName)
    {
        if (string.IsNullOrEmpty(pokemonName)) return false;

        if (myPlayer.activePokemon?.pokemonLogic?.pokemonData?.cardName == pokemonName)
            return true;

        return myPlayer.benchPokemons.Any(card =>
            card?.pokemonLogic?.pokemonData?.cardName == pokemonName);
    }

    private bool ShouldReserveBenchSlotForRampEngine(CardInstance basicCard)
    {
        if (!RequiresEnergyRampSupport()) return false;
        if (basicCard?.baseData is not PokemonData basicData || basicData.stage != 0) return false;

        int freeBenchSlots = GameRulesConfig.Instance.benchSize - myPlayer.benchPokemons.Count;
        if (freeBenchSlots > 1) return false;

        HashSet<EnumPokemonType> pendingWithCandidate = GetPendingStrategicRampTypes(scarceOnly: true);
        if (pendingWithCandidate.Count == 0) return false;

        EnumPokemonType candidateRampType = GetRampEnergyType(basicCard);
        if (candidateRampType != EnumPokemonType.None && pendingWithCandidate.Contains(candidateRampType))
            return false;

        // Only hold the last slot if the needed ramp engine is actually in hand (playable now).
        // Reserving against an engine still buried in the deck would freeze the slot indefinitely.
        return GetPendingStrategicRampTypes(basicCard, scarceOnly: true)
            .Any(type => HasBasicRampEngineInHandForType(type, basicCard));
    }

    private bool HasBasicRampEngineInHandForType(EnumPokemonType type, CardInstance excludedCard)
    {
        if (type == EnumPokemonType.None) return false;

        return myPlayer.hand.Any(card =>
            card != excludedCard &&
            card?.baseData is PokemonData pd &&
            pd.stage == 0 &&
            GetRampEnergyType(card) == type);
    }

    private bool ShouldAvoidLowValueDuplicateBasic(
        CardInstance basicCard,
        AlgorithmDecisionContext context,
        bool isNeededRampType,
        bool isStrongestLine,
        bool hasScalingLine)
    {
        if (context.FreeBenchSlots > 2) return false;
        if (isNeededRampType) return false;
        if (basicCard?.baseData is not PokemonData pd || pd.stage != 0) return false;
        if (!HasSamePokemonOnBoard(pd.cardName)) return false;

        bool duplicateHasCorePlanValue = hasScalingLine && !HasEvolutionLineOnBoard(pd.cardName);
        return !duplicateHasCorePlanValue && !isStrongestLine;
    }

    private bool ShouldReserveBenchSlotForStrongestLine(CardInstance basicCard)
    {
        if (basicCard?.baseData is not PokemonData basicData || basicData.stage != 0)
            return false;

        int freeBenchSlots = GameRulesConfig.Instance.benchSize - myPlayer.benchPokemons.Count;
        if (freeBenchSlots > 1) return false;

        string strongestBasicName = GetStrongestEvolutionLineBasicName();
        if (string.IsNullOrEmpty(strongestBasicName)) return false;

        if (HasEvolutionLineOnBoard(strongestBasicName)) return false;
        return basicData.cardName != strongestBasicName;
    }

    private string GetStrongestEvolutionLineBasicName()
    {
        List<PokemonData> pokemonCards = GetOwnedPokemonData()
            .GroupBy(p => p.cardName)
            .Select(g => g.First())
            .ToList();

        var basics = pokemonCards.Where(p => p.stage == 0).ToList();
        if (basics.Count == 0) return null;

        PokemonData bestBasic = null;
        int bestScore = int.MinValue;

        foreach (PokemonData basic in basics)
        {
            int score = GetEvolutionLineStrengthScore(basic, pokemonCards);
            if (score > bestScore)
            {
                bestScore = score;
                bestBasic = basic;
            }
        }

        return bestBasic?.cardName;
    }

    private List<PokemonData> GetOwnedPokemonData()
    {
        return GetOwnedCards()
            .Where(c => c?.baseData is PokemonData)
            .Select(c => c.baseData as PokemonData)
            .ToList();
    }

    private IEnumerable<CardInstance> GetOwnedCards()
    {
        if (myPlayer.activePokemon != null)
            yield return myPlayer.activePokemon;

        foreach (CardInstance card in myPlayer.benchPokemons)
            yield return card;

        foreach (CardInstance card in myPlayer.hand)
            yield return card;

        foreach (CardInstance card in myPlayer.deck)
            yield return card;

        foreach (CardInstance card in myPlayer.discardPile)
            yield return card;
    }

    private HashSet<EnumPokemonType> GetPendingStrategicRampTypes(CardInstance excludedCard = null, bool scarceOnly = false)
    {
        HashSet<EnumPokemonType> neededTypes = GetStrategicRampEnergyTypes();

        neededTypes.RemoveWhere(HasRampEngineForTypeOnBoard);
        neededTypes.RemoveWhere(type => !HasAvailableBasicRampEngineForType(type, excludedCard));

        var scarceTypes = new HashSet<EnumPokemonType>(neededTypes.Where(IsEnergyTypeScarceForDeck));
        return scarceOnly ? scarceTypes : neededTypes;
    }

    private HashSet<EnumPokemonType> GetStrategicRampEnergyTypes()
    {
        var neededTypes = new HashSet<EnumPokemonType>();
        List<PokemonData> pokemonCards = GetOwnedPokemonData()
            .GroupBy(p => p.cardName)
            .Select(g => g.First())
            .ToList();

        foreach (PokemonData pokemon in pokemonCards)
        {
            if (pokemon?.attacks == null) continue;

            foreach (AttackData attack in pokemon.attacks)
            {
                if (!AttackWantsRampSupport(attack)) continue;
                if (attack.attackCost == null) continue;

                foreach (EnumPokemonType costType in attack.attackCost)
                {
                    if (costType == EnumPokemonType.None || costType == EnumPokemonType.Colorless)
                        continue;

                    neededTypes.Add(costType);
                }
            }
        }

        return neededTypes;
    }

    private bool AttackWantsRampSupport(AttackData attack)
    {
        if (attack == null) return false;

        bool hasStrategicScaling = attack.effects != null &&
                                   attack.effects.Any(e =>
                                       e.cardEffectType == EnumCardEffectType.PowerUp ||
                                       (e.cardEffectType == EnumCardEffectType.Psychic && e.effectAmount > 0));
        if (hasStrategicScaling) return true;

        return GetEnergyDiscardAmount(attack) > 0 && EstimatePrintedAttackCeiling(attack) >= profile.discardAttackDamageThreshold;
    }

    private bool StrategicDiscardLineNeedsEnergy(CardInstance card, EnumPokemonType energyType)
    {
        if (card?.pokemonLogic == null || energyType == EnumPokemonType.None)
            return false;

        return GetCurrentAndFuturePokemonData(card).Any(pd =>
            pd.attacks != null &&
            pd.attacks.Any(atk =>
                GetEnergyDiscardAmount(atk) > 0 &&
                EstimatePrintedAttackCeiling(atk) >= profile.discardAttackDamageThreshold &&
                StrategicDiscardAttackBenefitsFromEnergy(card, atk, energyType)));
    }

    private bool BoardHasOtherStrategicDiscardLineNeedingEnergy(CardInstance excludedCard, EnumPokemonType energyType)
    {
        if (energyType == EnumPokemonType.None)
            return false;

        return new[] { myPlayer.activePokemon }
            .Concat(myPlayer.benchPokemons)
            .Any(card => card != excludedCard && StrategicDiscardLineNeedsEnergy(card, energyType));
    }

    private bool IsBackupForThreatenedStrategicDiscardActive(CardInstance target, AlgorithmDecisionContext context)
    {
        if (target == null || target == context.Active || !context.ActiveLikelyKo)
            return false;

        if (!HasStrategicDiscardLine(context.Active))
            return false;

        return StrategicDiscardLineNeedsEnergy(target, context.CurrentEnergy);
    }

    private bool HasStrategicDiscardLine(CardInstance card)
    {
        if (card?.pokemonLogic == null) return false;

        return GetCurrentAndFuturePokemonData(card).Any(pd =>
            pd.attacks != null &&
            pd.attacks.Any(atk =>
                GetEnergyDiscardAmount(atk) > 0 &&
                EstimatePrintedAttackCeiling(atk) >= profile.discardAttackDamageThreshold));
    }

    private bool StrategicDiscardAttackBenefitsFromEnergy(CardInstance card, AttackData attack, EnumPokemonType energyType)
    {
        if (AttackNeedsTypedEnergy(attack, energyType))
            return true;

        int missingBefore = EnergyMissingForAttack(
            card.pokemonLogic.energyEquipped,
            attack,
            card.pokemonLogic.tempBuffsData.attackEnergyCostChange);

        Dictionary<EnumPokemonType, int> energy = CopyEnergy(card.pokemonLogic.energyEquipped);
        AddSimulatedEnergy(energy, energyType);

        int missingAfter = EnergyMissingForAttack(
            energy,
            attack,
            card.pokemonLogic.tempBuffsData.attackEnergyCostChange);

        if (missingAfter < missingBefore)
            return true;

        // Colorless energy does not pay typed costs like Fire/Psychic, but it is still useful
        // as a discard buffer once the future attacker comes online.
        if (energyType == EnumPokemonType.Colorless)
        {
            int totalBefore = card.pokemonLogic.energyEquipped?.Values.Sum() ?? 0;
            int wantedReserve = (attack.attackCost?.Count ?? 0) + GetEnergyDiscardAmount(attack);
            return totalBefore < wantedReserve;
        }

        return false;
    }

    private static bool AttackNeedsTypedEnergy(AttackData attack, EnumPokemonType energyType)
    {
        return attack?.attackCost != null &&
               attack.attackCost.Any(cost => cost == energyType);
    }

    private bool HasRampEngineForTypeOnBoard(EnumPokemonType type)
    {
        if (type == EnumPokemonType.None) return false;

        if (GetRampEnergyType(myPlayer.activePokemon) == type)
            return true;

        return myPlayer.benchPokemons.Any(card => GetRampEnergyType(card) == type);
    }

    private bool HasAvailableBasicRampEngineForType(EnumPokemonType type, CardInstance excludedCard = null)
    {
        if (type == EnumPokemonType.None) return false;

        return GetFutureAccessibleCards().Any(card =>
            card != excludedCard &&
            card?.baseData is PokemonData pd &&
            pd.stage == 0 &&
            GetRampEnergyType(card) == type);
    }

    private bool IsEnergyTypeScarceForDeck(EnumPokemonType type)
    {
        if (type == EnumPokemonType.None || type == EnumPokemonType.Colorless)
            return false;

        if (myPlayer.deckEnergies != null && myPlayer.deckEnergies.Count > 0)
            return !myPlayer.deckEnergies.Contains(type);

        EnergyZone zone = playerManager?.GetEnergyZoneFor(myPlayer);
        return zone == null || (zone.currentEnergy != type && zone.nextEnergy != type);
    }

    private IEnumerable<CardInstance> GetFutureAccessibleCards()
    {
        foreach (CardInstance card in myPlayer.hand)
            yield return card;

        foreach (CardInstance card in myPlayer.deck)
            yield return card;
    }

    private int GetEvolutionLineStrengthScore(PokemonData basic, List<PokemonData> allPokemon)
    {
        int best = GetPokemonStrengthScore(basic);
        Queue<string> pending = new Queue<string>();
        HashSet<string> visited = new HashSet<string>();

        pending.Enqueue(basic.cardName);
        visited.Add(basic.cardName);

        while (pending.Count > 0)
        {
            string currentName = pending.Dequeue();
            foreach (PokemonData evo in allPokemon.Where(p => p.evolvesFrom == currentName))
            {
                best = Mathf.Max(best, GetPokemonStrengthScore(evo));
                if (visited.Add(evo.cardName))
                    pending.Enqueue(evo.cardName);
            }
        }

        return best;
    }

    private int GetPokemonStrengthScore(PokemonData pokemon)
    {
        int maxDamage = pokemon.attacks?
            .Select(EstimatePrintedAttackCeiling)
            .DefaultIfEmpty(0)
            .Max() ?? 0;

        return maxDamage * profile.strengthDamageWeight + pokemon.hp * profile.strengthHpWeight + pokemon.stage * profile.strengthStageWeight;
    }

    private bool HasEvolutionLineOnBoard(string basicName)
    {
        return IsPokemonInEvolutionLine(myPlayer.activePokemon, basicName) ||
               myPlayer.benchPokemons.Any(card => IsPokemonInEvolutionLine(card, basicName));
    }

    private bool HasSamePokemonOnBoard(string cardName)
    {
        if (string.IsNullOrEmpty(cardName)) return false;

        bool activeMatches = myPlayer.activePokemon?.baseData is PokemonData activeData &&
                             activeData.cardName == cardName;
        if (activeMatches) return true;

        return myPlayer.benchPokemons.Any(card =>
            card?.baseData is PokemonData benchData &&
            benchData.cardName == cardName);
    }

    private bool IsPokemonInEvolutionLine(CardInstance card, string basicName)
    {
        if (card?.baseData is not PokemonData pokemon) return false;
        if (pokemon.cardName == basicName) return true;

        List<PokemonData> pokemonCards = GetOwnedPokemonData()
            .GroupBy(p => p.cardName)
            .Select(g => g.First())
            .ToList();

        string current = pokemon.evolvesFrom;
        while (!string.IsNullOrEmpty(current))
        {
            if (current == basicName) return true;
            PokemonData parent = pokemonCards.FirstOrDefault(p => p.cardName == current);
            current = parent?.evolvesFrom;
        }

        return false;
    }

    private bool RequiresEnergyRampSupport()
    {
        return GetOwnedPokemonData().Any(pd =>
            pd.attacks != null &&
            (HasScalingDamageEffect(pd) ||
             pd.attacks.Any(atk => GetEnergyDiscardAmount(atk) > 0 && EstimatePrintedAttackCeiling(atk) >= profile.discardAttackDamageThreshold)));
    }

    private bool HasEnergyRampAttack(CardInstance card)
    {
        var pd = card?.baseData as PokemonData;
        return pd?.attacks != null && pd.attacks.Any(HasEnergyRampEffect);
    }

    private EnumPokemonType GetRampEnergyType(CardInstance card)
    {
        if (!HasEnergyRampAttack(card)) return EnumPokemonType.None;
        return card?.baseData is PokemonData pd ? pd.type : EnumPokemonType.None;
    }

    private bool HasFutureEvolution(CardInstance card)
    {
        if (card?.baseData is not PokemonData pd) return false;
        return GetOwnedPokemonData().Any(candidate => candidate.evolvesFrom == pd.cardName);
    }

    private bool HasEnergyRampEffect(AttackData attack)
    {
        return attack?.effects != null &&
               attack.effects.Any(e => e.cardEffectType == EnumCardEffectType.EnergyRamp);
    }

    private int GetMaxEnergyRampAmount(CardInstance card)
    {
        var pd = card?.baseData as PokemonData;
        if (pd?.attacks == null) return 0;

        return pd.attacks
            .Where(HasEnergyRampEffect)
            .Select(atk => atk.effects
                .Where(e => e.cardEffectType == EnumCardEffectType.EnergyRamp)
                .Select(e => Mathf.Max(0, e.effectAmount))
                .DefaultIfEmpty(0)
                .Sum())
            .DefaultIfEmpty(0)
            .Max();
    }

    private bool ShouldChargeActiveRampBeforeBenchRamp(CardInstance active, EnumPokemonType availableEnergy)
    {
        if (active == null || !HasEnergyRampAttack(active)) return false;
        if (HasEnergyForAnyAttack(active)) return false;
        if (!WouldEnergyHelp(active, availableEnergy)) return false;
        if (ActiveLikelyKoNextTurn() && EnergyMissingAfterAttach(active, availableEnergy) > 0) return false;

        bool benchHasBetterRamp = myPlayer.benchPokemons.Any(b =>
            HasEnergyRampAttack(b) &&
            GetMaxEnergyRampAmount(b) > GetMaxEnergyRampAmount(active));
        if (!benchHasBetterRamp) return true;

        if (HasSwapSelfTrainerInHand())
            return false;

        // Without Leaf/SwapSelf, a benched ramp engine cannot produce energy yet.
        // Charging the active ramp engine starts the chain sooner: Moltress -> Tropius -> finisher.
        return true;
    }

    private bool ShouldPrioritizeActiveRampForStrategicBench(CardInstance active, EnumPokemonType availableEnergy)
    {
        if (active == null || active != myPlayer.activePokemon) return false;
        if (!HasEnergyRampAttack(active)) return false;
        if (!WouldEnergyHelp(active, availableEnergy)) return false;
        if (HasEnergyForAnyAttack(active)) return false;
        if (ActiveLikelyKoNextTurn() && EnergyMissingAfterAttach(active, availableEnergy) > 0) return false;

        EnumPokemonType rampType = GetRampEnergyType(active);
        if (rampType == EnumPokemonType.None) return false;

        return myPlayer.benchPokemons.Any(c => StrategicDiscardLineNeedsEnergy(c, rampType));
    }

    private bool HasSwapSelfTrainerInHand()
    {
        return myPlayer.hand.Any(card =>
            card?.baseData is TrainerData trainer &&
            TrainerHasSwapSelfEffect(trainer));
    }

    private bool ActiveLikelyKoNextTurn()
    {
        CardInstance active = myPlayer.activePokemon;
        if (active?.pokemonLogic == null) return false;

        PlayerController opponent = playerManager.player1 == myPlayer ? playerManager.player2 : playerManager.player1;
        return active.pokemonLogic.currentHp <= EstimateEnemyMaxDamage(opponent);
    }

    private bool HasScalingDamageLine(CardInstance card)
    {
        return GetCurrentAndFuturePokemonData(card).Any(HasScalingDamageEffect);
    }

    // Scaling that the AI can actually feed with its own energy: PowerUp scales with our energy,
    // and a positive Psychic gets stronger as the enemy energizes (worth being ready early).
    // A negative Psychic (e.g. Swalot's Swollow Up, -30 per enemy energy) gets WEAKER against
    // energized targets — attaching or reserving more of our energy never improves it, so it must
    // not be treated as a scaling line that wants energy ramp/reserve.
    private bool HasScalingDamageEffect(PokemonData pd)
    {
        return pd?.attacks != null && pd.attacks.Any(atk =>
            atk.effects != null &&
            atk.effects.Any(e => e.cardEffectType == EnumCardEffectType.PowerUp ||
                                 (e.cardEffectType == EnumCardEffectType.Psychic && e.effectAmount > 0)));
    }

    private int GetScalingDamageEnergyDeficitAfterAttach(CardInstance card, int opponentMaxHp, EnumPokemonType availableEnergy)
    {
        int deficit = GetScalingDamageEnergyDeficit(card, opponentMaxHp);
        return availableEnergy == EnumPokemonType.None ? deficit : Mathf.Max(0, deficit - 1);
    }

    private int GetScalingDamageEnergyDeficit(CardInstance card, int opponentMaxHp)
    {
        int required = GetBestScalingDamageRequirement(card, opponentMaxHp);
        if (required == int.MaxValue) return int.MaxValue;

        int totalEnergy = card.pokemonLogic.energyEquipped?.Values.Sum() ?? 0;
        return Mathf.Max(0, required - totalEnergy);
    }

    private int GetBestScalingDamageRequirement(CardInstance card, int opponentMaxHp)
    {
        if (card?.pokemonLogic == null || opponentMaxHp <= 0) return int.MaxValue;

        int best = int.MaxValue;
        foreach (PokemonData pd in GetCurrentAndFuturePokemonData(card))
        {
            if (pd?.attacks == null) continue;
            foreach (AttackData attack in pd.attacks)
                best = Mathf.Min(best, GetScalingDamageRequirement(attack, opponentMaxHp));
        }

        return best;
    }

    private int GetScalingDamageRequirement(AttackData attack, int opponentMaxHp)
    {
        if (attack?.effects == null) return int.MaxValue;

        int attackCost = attack.attackCost?.Count ?? 0;
        int best = int.MaxValue;
        foreach (EffectData effect in attack.effects)
        {
            if (effect.cardEffectType == EnumCardEffectType.PowerUp && effect.effectAmount > 0)
            {
                int missingDamage = Mathf.Max(0, opponentMaxHp - attack.damage);
                int energyForKo = Mathf.CeilToInt(missingDamage / (float)effect.effectAmount);
                best = Mathf.Min(best, Mathf.Max(attackCost, energyForKo) + profile.scalingEnergyBuffer);
            }
            else if (effect.cardEffectType == EnumCardEffectType.Psychic && effect.effectAmount > 0)
            {
                best = Mathf.Min(best, attackCost + profile.scalingEnergyBuffer);
            }
        }

        return best;
    }

    private IEnumerable<PokemonData> GetCurrentAndFuturePokemonData(CardInstance card)
    {
        if (card?.baseData is not PokemonData current) yield break;

        List<PokemonData> ownedPokemon = GetOwnedPokemonData()
            .GroupBy(p => p.cardName)
            .Select(g => g.First())
            .ToList();

        yield return current;
        foreach (PokemonData candidate in ownedPokemon)
        {
            if (candidate.cardName == current.cardName) continue;
            if (IsFutureEvolution(candidate, current, ownedPokemon))
                yield return candidate;
        }
    }

    private bool IsFutureEvolution(PokemonData candidate, PokemonData current, List<PokemonData> ownedPokemon)
    {
        string parent = candidate.evolvesFrom;
        while (!string.IsNullOrEmpty(parent))
        {
            if (parent == current.cardName) return true;
            PokemonData parentData = ownedPokemon.FirstOrDefault(p => p.cardName == parent);
            parent = parentData?.evolvesFrom;
        }

        return false;
    }

    private int GetOpponentMaxCurrentHp(PlayerController opponent)
    {
        if (opponent == null) return 0;

        int max = 0;
        if (opponent.activePokemon?.pokemonLogic != null)
            max = Mathf.Max(max, opponent.activePokemon.pokemonLogic.currentHp);

        foreach (CardInstance bench in opponent.benchPokemons)
            if (bench?.pokemonLogic != null)
                max = Mathf.Max(max, bench.pokemonLogic.currentHp);

        return max;
    }

    private int EnergyMissingAfterAttach(CardInstance card, EnumPokemonType availableEnergy)
    {
        var pd = card?.baseData as PokemonData;
        if (card?.pokemonLogic == null || pd?.attacks == null || pd.attacks.Count == 0)
            return int.MaxValue;

        Dictionary<EnumPokemonType, int> energy = CopyEnergy(card.pokemonLogic.energyEquipped);
        AddSimulatedEnergy(energy, availableEnergy);

        return pd.attacks
            .Select(atk => EnergyMissingForAttack(energy, atk, card.pokemonLogic.tempBuffsData.attackEnergyCostChange))
            .DefaultIfEmpty(int.MaxValue)
            .Min();
    }

    private int GetMaxPrintedDamage(CardInstance card)
    {
        var pd = card?.baseData as PokemonData;
        if (pd?.attacks == null || pd.attacks.Count == 0) return 0;
        return pd.attacks.Select(atk => atk.damage).DefaultIfEmpty(0).Max();
    }

    // A negative Psychic attack (e.g. Swalot's Swollow Up) loses damage per enemy energy, so its
    // printed maximum is only reachable against a 0-energy target. Discount it by a typical mid-game
    // enemy energy count (profile.expectedEnemyEnergy) so line-strength ranking reflects realistic,
    // not best-case, damage.
    private int EstimatePrintedAttackCeiling(AttackData attack)
    {
        if (attack == null) return 0;
        int damage = attack.damage;
        if (attack.effects == null) return damage;

        foreach (EffectData effect in attack.effects)
        {
            if (effect.cardEffectType == EnumCardEffectType.PowerUp && effect.effectAmount > 0)
                damage += effect.effectAmount * profile.powerUpCeilingMultiplier;
            else if (effect.cardEffectType == EnumCardEffectType.Psychic && effect.effectAmount < 0)
                damage += effect.effectAmount * profile.expectedEnemyEnergy;
        }

        return Mathf.Max(0, damage);
    }

    private bool ShouldStockpileEnergyForNextDiscardAttack(CardInstance card, EnumPokemonType availableEnergy)
    {
        var pd = card?.baseData as PokemonData;
        if (card?.pokemonLogic == null || pd?.attacks == null) return false;

        foreach (AttackData attack in pd.attacks)
        {
            int discard = GetEnergyDiscardAmount(attack);
            if (discard <= 0 || attack.damage < profile.discardAttackDamageThreshold) continue;
            if (!CardActions.CanAffordAttack(card.pokemonLogic, attack)) continue;
            if (!WouldEnergyHelp(card, availableEnergy) && !CanAbsorbDiscardWithExtraEnergy(card, attack, availableEnergy))
                continue;

            if (CanAbsorbDiscardWithExtraEnergy(card, attack, availableEnergy))
                return true;
        }

        return false;
    }

    private bool CanAbsorbDiscardWithExtraEnergy(CardInstance card, AttackData attack, EnumPokemonType availableEnergy)
    {
        Dictionary<EnumPokemonType, int> energy = CopyEnergy(card.pokemonLogic.energyEquipped);
        AddSimulatedEnergy(energy, availableEnergy);

        int discard = GetEnergyDiscardAmount(attack);
        int totalEnergy = energy.Values.Sum();
        int attackCost = attack.attackCost?.Count ?? 0;

        return totalEnergy - discard >= attackCost &&
               EnergyMissingForAttack(energy, attack, card.pokemonLogic.tempBuffsData.attackEnergyCostChange) == 0;
    }

    private int GetEnergyDiscardAmount(AttackData attack)
    {
        return attack?.effects?
            .Where(e => e.cardEffectType == EnumCardEffectType.EnergyDiscard)
            .Select(e => Mathf.Max(0, e.effectAmount))
            .DefaultIfEmpty(0)
            .Sum() ?? 0;
    }

    private CardInstance ChooseEvolutionTarget(CardInstance evolutionCard, List<CardInstance> targets, PlayerController opponent)
    {
        CardInstance active = myPlayer.activePokemon;
        if (evolutionCard?.pokemonLogic == null || targets == null || targets.Count == 0)
            return null;

        if (active != null && targets.Contains(active))
        {
            string activeName = active.pokemonLogic?.pokemonData?.cardName;
            CardInstance sameBenchTarget = targets.FirstOrDefault(t =>
                t != active &&
                t?.pokemonLogic?.pokemonData?.cardName == activeName);

            if (sameBenchTarget != null)
                return sameBenchTarget;

            if (ActiveEvolutionWouldBreakReadyAttack(active, evolutionCard) &&
                !ActiveEvolutionSurvivesUntilReady(active, evolutionCard, opponent))
                return null;

            if (ActiveEvolutionWastesThisTurnAttack(active, evolutionCard, opponent))
                return null;
        }

        return targets[0];
    }

    /// Tempo guard for evolving the Active. The evolution carries the same energy, so if the
    /// cheaper current form can attack THIS turn (e.g. Gulpin's 1-cost Smelly Fart) but the
    /// pricier evolution cannot (e.g. Swalot's 3-cost Swollow Up), evolving simply skips a free
    /// attack — better to swing now and evolve once the evolution can actually fire. We still
    /// evolve immediately when the Active is about to be KO'd, where the bigger HP pool matters.
    private bool ActiveEvolutionWastesThisTurnAttack(CardInstance active, CardInstance evolutionCard, PlayerController opponent)
    {
        if (ActiveLikelyKoNextTurn()) return false;
        if (CanAttackAfterEvolution(active, evolutionCard, includeCurrentEnergy: true, includeNextEnergy: false))
            return false;
        return CurrentFormHasWorthwhileAttackThisTurn(active, opponent);
    }

    private bool CurrentFormHasWorthwhileAttackThisTurn(CardInstance active, PlayerController opponent)
    {
        var pd = active?.baseData as PokemonData;
        if (active?.pokemonLogic == null || pd?.attacks == null || pd.attacks.Count == 0)
            return false;
        if (!active.pokemonLogic.tempBuffsData.canAttack)
            return false;

        Dictionary<EnumPokemonType, int> energy = CopyEnergy(active.pokemonLogic.energyEquipped);
        EnergyZone zone = playerManager.GetEnergyZoneFor(myPlayer);
        if (myPlayer.canAddEnergy && zone != null)
            AddSimulatedEnergy(energy, zone.currentEnergy);

        int costChange = active.pokemonLogic.tempBuffsData.attackEnergyCostChange;
        return pd.attacks.Any(atk =>
            EnergyMissingForAttack(energy, atk, costChange) == 0 &&
            AttackHasPositiveTurnValue(active, opponent, atk));
    }

    private bool AttackHasPositiveTurnValue(CardInstance attacker, PlayerController opponent, AttackData attack)
    {
        int damage = EstimateAttackDamage(attacker, opponent?.activePokemon, attack);
        if (damage > 0) return true;
        if (EstimateBenchDamageValue(attack, opponent) > 0) return true;
        if (HasEnergyRampEffect(attack) && ChooseRampBenchTargetForActiveUtility(attacker) != null) return true;
        return ScoreOffensiveDebuffs(attack, attacker, opponent?.activePokemon, damage) > 0;
    }

    private bool ActiveEvolutionWouldBreakReadyAttack(CardInstance active, CardInstance evolutionCard)
    {
        if (!HasEnergyForAnyAttack(active)) return false;
        PlayerController opponent = playerManager.player1 == myPlayer ? playerManager.player2 : playerManager.player1;
        if (!CurrentFormHasWorthwhileAttackThisTurn(active, opponent)) return false;
        return !CanAttackAfterEvolution(active, evolutionCard, includeCurrentEnergy: true, includeNextEnergy: false);
    }

    private bool CanAttackAfterEvolution(CardInstance target, CardInstance evolutionCard, bool includeCurrentEnergy, bool includeNextEnergy)
    {
        var evolutionData = evolutionCard?.baseData as PokemonData;
        if (target?.pokemonLogic == null || evolutionData?.attacks == null || evolutionData.attacks.Count == 0)
            return false;

        Dictionary<EnumPokemonType, int> energy = CopyEnergy(target.pokemonLogic.energyEquipped);
        EnergyZone zone = playerManager.GetEnergyZoneFor(myPlayer);
        if (includeCurrentEnergy && myPlayer.canAddEnergy && zone != null)
            AddSimulatedEnergy(energy, zone.currentEnergy);
        if (includeNextEnergy && zone != null)
            AddSimulatedEnergy(energy, zone.nextEnergy);

        return evolutionData.attacks.Any(atk =>
            EnergyMissingForAttack(energy, atk, target.pokemonLogic.tempBuffsData.attackEnergyCostChange) == 0);
    }

    private bool ActiveEvolutionSurvivesUntilReady(CardInstance active, CardInstance evolutionCard, PlayerController opponent)
    {
        var activeData = active?.baseData as PokemonData;
        var evolutionData = evolutionCard?.baseData as PokemonData;
        if (active?.pokemonLogic == null || activeData == null || evolutionData == null) return false;

        int damageTaken = activeData.hp - active.pokemonLogic.currentHp;
        int evolvedHp = evolutionData.hp - damageTaken;
        if (evolvedHp <= 0) return false;

        int enemyDamage = EstimateEnemyMaxDamage(opponent);
        if (enemyDamage <= 0) return true;

        int turnsToReady = EstimateTurnsToReadyAfterEvolution(active, evolutionCard);
        if (turnsToReady < 0) return false;

        return evolvedHp > enemyDamage * turnsToReady;
    }

    private int EstimateTurnsToReadyAfterEvolution(CardInstance target, CardInstance evolutionCard)
    {
        if (CanAttackAfterEvolution(target, evolutionCard, includeCurrentEnergy: true, includeNextEnergy: false))
            return 0;
        if (CanAttackAfterEvolution(target, evolutionCard, includeCurrentEnergy: true, includeNextEnergy: true))
            return 1;

        return -1;
    }

    private static Dictionary<EnumPokemonType, int> CopyEnergy(Dictionary<EnumPokemonType, int> source)
    {
        return source != null
            ? new Dictionary<EnumPokemonType, int>(source)
            : new Dictionary<EnumPokemonType, int>();
    }

    private static void AddSimulatedEnergy(Dictionary<EnumPokemonType, int> energy, EnumPokemonType type)
    {
        if (type == EnumPokemonType.None) return;
        if (!energy.ContainsKey(type))
            energy[type] = 0;
        energy[type]++;
    }

    /// Czy dodanie jednej energii danego typu przybliży tego Pokemona do wykonania ataku.
    private bool WouldEnergyHelp(CardInstance card, EnumPokemonType energyType)
    {
        var pd = card?.baseData as PokemonData;
        if (pd?.attacks == null || pd.attacks.Count == 0) return false;
        var equipped = card.pokemonLogic?.energyEquipped;
        if (equipped == null) return false;

        foreach (var atk in pd.attacks)
        {
            if (atk.attackCost == null || atk.attackCost.Count == 0) continue;

            // Dragon joker fills any slot, so it helps whenever the cost is not fully paid
            if (CardActions.IsWildcardEnergy(energyType))
            {
                if (equipped.Values.Sum() < atk.attackCost.Count) return true;
                continue;
            }

            // Typowany koszt — energia pomaga tylko jeśli atak wymaga dokładnie tego typu
            if (energyType != EnumPokemonType.Colorless)
            {
                int needed = atk.attackCost.Count(c => c == energyType);
                int have = equipped.TryGetValue(energyType, out int v) ? v : 0;
                if (needed > have) return true;
            }

            // Colorless — każda energia może pokryć ten slot
            bool hasColorlessSlot = atk.attackCost.Any(c => c == EnumPokemonType.Colorless);
            if (hasColorlessSlot)
            {
                int totalCost = atk.attackCost.Count;
                int totalHave = equipped.Values.Sum();
                if (totalHave < totalCost) return true;
            }
        }
        return false;
    }

    /// Like WouldEnergyHelp, but only counts attacks that deal printed damage.
    /// Prevents ramping toward a pure-utility engine (e.g. a 0-damage EnergyRamp attack),
    /// which would otherwise create a fake "useful ramp" payoff and stall the turn loop.
    private bool WouldEnergyHelpDamagingAttack(CardInstance card, EnumPokemonType energyType)
    {
        var pd = card?.baseData as PokemonData;
        if (pd?.attacks == null || pd.attacks.Count == 0) return false;
        var equipped = card.pokemonLogic?.energyEquipped;
        if (equipped == null) return false;

        foreach (var atk in pd.attacks)
        {
            if (atk.attackCost == null || atk.attackCost.Count == 0) continue;
            if (EstimatePrintedAttackCeiling(atk) <= 0) continue;

            // Dragon joker fills any slot, so it helps whenever the cost is not fully paid
            if (CardActions.IsWildcardEnergy(energyType))
            {
                if (equipped.Values.Sum() < atk.attackCost.Count) return true;
                continue;
            }

            if (energyType != EnumPokemonType.Colorless)
            {
                int needed = atk.attackCost.Count(c => c == energyType);
                int have = equipped.TryGetValue(energyType, out int v) ? v : 0;
                if (needed > have) return true;
            }

            bool hasColorlessSlot = atk.attackCost.Any(c => c == EnumPokemonType.Colorless);
            if (hasColorlessSlot && equipped.Values.Sum() < atk.attackCost.Count) return true;
        }
        return false;
    }

    /// True if the energy advances an attack cost on the current form OR any owned future evolution
    /// of this Pokémon. Energy carries over on evolution, so banking a deck-relevant energy (e.g.
    /// Darkness on a Quilfish that becomes Overqwil's DC attacker, or a Colorless slot of Cloyster)
    /// is productive even when the current form's attack does not use that type — better than wasting
    /// the once-per-turn drop.
    private bool WouldEnergyHelpEvolutionLine(CardInstance card, EnumPokemonType energyType)
    {
        var equipped = card?.pokemonLogic?.energyEquipped;
        if (equipped == null) return false;

        foreach (PokemonData pd in GetCurrentAndFuturePokemonData(card))
        {
            if (pd?.attacks == null) continue;
            foreach (var atk in pd.attacks)
            {
                if (atk.attackCost == null || atk.attackCost.Count == 0) continue;

                // Dragon joker fills any slot
                if (CardActions.IsWildcardEnergy(energyType))
                {
                    if (equipped.Values.Sum() < atk.attackCost.Count) return true;
                    continue;
                }

                if (energyType != EnumPokemonType.Colorless)
                {
                    int needed = atk.attackCost.Count(c => c == energyType);
                    int have = equipped.TryGetValue(energyType, out int v) ? v : 0;
                    if (needed > have) return true;
                }

                bool hasColorlessSlot = atk.attackCost.Any(c => c == EnumPokemonType.Colorless);
                if (hasColorlessSlot && equipped.Values.Sum() < atk.attackCost.Count) return true;
            }
        }
        return false;
    }

    private bool ActiveHasUsefulRampAttack(CardInstance active)
    {
        var pd = active?.baseData as PokemonData;
        if (active?.pokemonLogic == null || pd?.attacks == null) return false;
        if (myPlayer.benchPokemons.Count == 0) return false;

        return pd.attacks.Any(atk =>
            CardActions.CanAffordAttack(active.pokemonLogic, atk) &&
            HasEnergyRampEffect(atk) &&
            ChooseRampBenchTargetForActiveUtility(active) != null);
    }

    private CardInstance ChooseRampBenchTargetForActiveUtility(CardInstance active)
    {
        if (active?.baseData is not PokemonData pd) return null;
        EnumPokemonType energyType = pd.type;
        if (energyType == EnumPokemonType.None) return null;

        PlayerController opponent = playerManager.player1 == myPlayer ? playerManager.player2 : playerManager.player1;
        int opponentMaxHp = GetOpponentMaxCurrentHp(opponent);

        CardInstance discardTarget = myPlayer.benchPokemons
            .Where(c => StrategicDiscardLineNeedsEnergy(c, energyType))
            .OrderBy(c => EnergyMissingAfterAttach(c, energyType))
            .ThenByDescending(c => GetMaxPrintedDamage(c))
            .FirstOrDefault();
        if (discardTarget != null)
            return discardTarget;

        CardInstance scalingTarget = myPlayer.benchPokemons
            .Where(c => HasScalingDamageLine(c))
            .Where(c => GetScalingDamageEnergyDeficit(c, opponentMaxHp) > 0)
            .FirstOrDefault(c => ScalingDamageEnergyStillHelps(c, energyType, opponentMaxHp));
        if (scalingTarget != null)
            return scalingTarget;

        return myPlayer.benchPokemons.FirstOrDefault(c => WouldEnergyHelpDamagingAttack(c, energyType));
    }

    private bool ScalingDamageEnergyStillHelps(CardInstance card, EnumPokemonType energyType, int opponentMaxHp)
    {
        if (card?.pokemonLogic == null || energyType == EnumPokemonType.None || opponentMaxHp <= 0)
            return false;

        int before = GetScalingDamageEnergyDeficit(card, opponentMaxHp);
        if (before <= 0) return false;

        Dictionary<EnumPokemonType, int> energy = CopyEnergy(card.pokemonLogic.energyEquipped);
        AddSimulatedEnergy(energy, energyType);

        int after = int.MaxValue;
        foreach (PokemonData pd in GetCurrentAndFuturePokemonData(card))
        {
            if (pd?.attacks == null) continue;
            foreach (AttackData attack in pd.attacks)
            {
                int required = GetScalingDamageRequirement(attack, opponentMaxHp);
                if (required == int.MaxValue) continue;
                int totalEnergy = energy.Values.Sum();
                after = Mathf.Min(after, Mathf.Max(0, required - totalEnergy));
            }
        }

        return after < before;
    }

    /// Czy Pokemon na tej karcie może sobie pozwolić na co najmniej jeden atak.
    /// Returns the minimum attack cost (number of energy) across all attacks on this card. 0 if no attacks.
    private static int MinAttackCost(CardInstance card)
    {
        var pokData = card?.baseData as PokemonData;
        if (pokData?.attacks == null || pokData.attacks.Count == 0) return int.MaxValue;
        return pokData.attacks.Min(atk => atk.attackCost?.Count ?? 0);
    }

    private bool HasEnergyForAnyAttack(CardInstance card)
    {
        if (card?.pokemonLogic == null) return false;
        var pokData = card.baseData as PokemonData;
        if (pokData?.attacks == null || pokData.attacks.Count == 0) return false;
        return pokData.attacks.Any(atk => CardActions.CanAffordAttack(card.pokemonLogic, atk));
    }

    private bool CanDeclareAnyAttack(CardInstance card, PlayerController opponent)
    {
        if (card?.pokemonLogic == null || opponent?.activePokemon == null) return false;
        if (TurnManager.Instance.turnCounter == 1 && myPlayer.playerId == 2) return false;
        if (!card.pokemonLogic.tempBuffsData.canAttack) return false;

        var pokData = card.baseData as PokemonData;
        if (pokData?.attacks == null || pokData.attacks.Count == 0) return false;

        return pokData.attacks.Any(atk =>
            CardActions.CanAffordAttack(card.pokemonLogic, atk) &&
            HasEnoughCardsForAttack(myPlayer, atk));
    }

    /// Szacuje maksymalne obrażenia jakie wróg może zadać aktualnie dostępnymi atakami.
    private int EstimateEnemyMaxDamage(PlayerController opponent)
    {
        CardInstance enemyActive = opponent?.activePokemon;
        if (enemyActive?.pokemonLogic == null) return 0;
        if (!enemyActive.pokemonLogic.tempBuffsData.canAttack) return 0;
        var pokData = enemyActive.baseData as PokemonData;
        if (pokData?.attacks == null) return 0;

        return pokData.attacks
            .Where(atk => CardActions.CanAffordAttack(enemyActive.pokemonLogic, atk))
            .Where(atk => HasEnoughCardsForAttack(opponent, atk))
            .Select(atk => EstimateAttackDamage(enemyActive, myPlayer.activePokemon, atk))
            .DefaultIfEmpty(0)
            .Max();
    }

    // Effects that provide no value on their own when Pokemon is healthy and unaffected by status.
    // Self-debuffs (Slow, DebuffSelf) are included: they are drawbacks on heal cards and should not
    // override the "skip when target is at full HP" logic.
    private static readonly System.Collections.Generic.HashSet<EnumCardEffectType> HealAdjacentEffects = new()
    {
        EnumCardEffectType.Heal,
        EnumCardEffectType.BenchHeal,
        EnumCardEffectType.Cleanse,
        EnumCardEffectType.Slow,
        EnumCardEffectType.DebuffSelf,
    };

    /// Czy karta trenera ma efekt leczący.
    private bool TrainerHasHealEffect(TrainerData td)
    {
        if (td.effects == null) return false;
        return td.effects.Any(e =>
            e.cardEffectType == EnumCardEffectType.Heal ||
            e.cardEffectType == EnumCardEffectType.BenchHeal);
    }

    private bool TrainerHasCleanseEffect(TrainerData td)
    {
        if (td.effects == null) return false;
        return td.effects.Any(e => e.cardEffectType == EnumCardEffectType.Cleanse);
    }

    private bool TrainerHasRecoveryEffect(TrainerData td)
    {
        return TrainerHasHealEffect(td) || TrainerHasCleanseEffect(td);
    }

    /// Returns true if the card has any effect beyond heal/cleanse (e.g. draw, swap).
    private bool HasNonHealEffects(TrainerData td)
    {
        if (td.effects == null) return false;
        return td.effects.Any(e => !HealAdjacentEffects.Contains(e.cardEffectType));
    }

    /// Returns true if at least one heal target (active for Heal, bench for BenchHeal) is below max HP,
    /// or if the card has Cleanse and the active Pokemon has a special condition.
    private bool AnyHealTargetBelowMax(TrainerData td)
    {
        if (td.effects == null) return false;

        bool hasHeal = td.effects.Any(e => e.cardEffectType == EnumCardEffectType.Heal);
        bool hasBenchHeal = td.effects.Any(e => e.cardEffectType == EnumCardEffectType.BenchHeal);
        bool hasCleanse = td.effects.Any(e => e.cardEffectType == EnumCardEffectType.Cleanse);

        if (hasCleanse)
        {
            CardInstance active = myPlayer.activePokemon;
            if (active?.pokemonLogic != null)
            {
                bool hasStatus = active.pokemonLogic.isPoisoned ||
                                 active.pokemonLogic.isBurned ||
                                 active.pokemonLogic.otherSpecialCondition != EnumSpecialConditionType.None;
                if (hasStatus) return true;
            }
        }

        if (hasHeal)
        {
            CardInstance active = myPlayer.activePokemon;
            if (active?.pokemonLogic != null)
            {
                int maxHp = (active.baseData as PokemonData)?.hp ?? 0;
                if (active.pokemonLogic.currentHp < maxHp) return true;
            }
        }

        if (hasBenchHeal)
        {
            foreach (var b in myPlayer.benchPokemons)
            {
                int maxHp = (b.baseData as PokemonData)?.hp ?? 0;
                if (b.pokemonLogic?.currentHp < maxHp) return true;
            }
        }

        return false;
    }

    /// Czy karta trenera ma efekt zamiany aktywnego z ławką.
    private bool TrainerHasSwapSelfEffect(TrainerData td)
    {
        if (td.effects == null) return false;
        return td.effects.Any(e => e.cardEffectType == EnumCardEffectType.SwapSelf);
    }

    /// Czy karta trenera wymienia aktywnego Pokemona przeciwnika (np. Sabrina).
    private bool TrainerHasSwapEnemyEffect(TrainerData td)
    {
        if (td.effects == null) return false;
        return td.effects.Any(e => e.cardEffectType == EnumCardEffectType.SwapEnemy);
    }

    /// Czy karta trenera zakorzenia aktywnego Pokemona przeciwnika (np. Net).
    private bool TrainerHasRootEffect(TrainerData td)
    {
        if (td.effects == null) return false;
        return td.effects.Any(e =>
            e.cardEffectType == EnumCardEffectType.Root &&
            e.cardEffectTarget == EnumCardEffectTarget.EnemyActivePokemon);
    }

    /// Czy karta trenera każe graczowi odrzucić karty z ręki (np. Rummage).
    private bool TrainerHasHandDiscardEffect(TrainerData td)
    {
        if (td.effects == null) return false;
        return td.effects.Any(e =>
            e.cardEffectType == EnumCardEffectType.DiscardHand &&
            (e.cardEffectTarget == EnumCardEffectTarget.Self ||
             e.cardEffectTarget == EnumCardEffectTarget.ActivePokemon));
    }

    private bool TrainerHasDamageReductionEffect(TrainerData td)
    {
        if (td.effects == null) return false;
        return td.effects.Any(e => e.cardEffectType == EnumCardEffectType.DmgTakenRed);
    }

    /// Returns true if the card has an effect that damages or burns the player's own Pokemon
    /// (e.g. Chilly Pepper which burns the user's own Active Pokemon).
    private bool TrainerHasSelfHarmEffect(TrainerData td)
    {
        if (td.effects == null) return false;
        return td.effects.Any(e =>
            (e.cardEffectType == EnumCardEffectType.Burn ||
             e.cardEffectType == EnumCardEffectType.Poison) &&
            (e.cardEffectTarget == EnumCardEffectTarget.Self ||
             e.cardEffectTarget == EnumCardEffectTarget.ActivePokemon));
    }

    private int GetMaxDamageReduction(TrainerData td)
    {
        return td.effects?
            .Where(e => e.cardEffectType == EnumCardEffectType.DmgTakenRed)
            .Select(e => Mathf.Abs(e.effectAmount))
            .DefaultIfEmpty(0)
            .Max() ?? 0;
    }

    /// Zwraca true tylko jeśli:
    /// Returns true only when:
    /// (a) active would be KO'd by the best available enemy attack, AND
    /// (b) healing from this card is enough to survive (net of any self-inflicted burn).
    private bool HealWouldPreventKO(TrainerData td, PlayerController opponent)
    {
        CardInstance active = myPlayer.activePokemon;
        if (active?.pokemonLogic == null) return false;

        int enemyDmg = EstimateEnemyMaxDamage(opponent);
        int currentHp = active.pokemonLogic.currentHp;
        int maxHp = (active.baseData as PokemonData)?.hp ?? currentHp;

        // Active not threatened — no need to spend a card to heal.
        if (currentHp > enemyDmg) return false;

        int healAmount = td.effects?
            .Where(e => e.cardEffectType == EnumCardEffectType.Heal)
            .Select(e => e.effectAmount)
            .DefaultIfEmpty(0)
            .Max() ?? 0;

        int effectiveHp = Mathf.Min(currentHp + healAmount, maxHp);

        // If this card also burns self (e.g. Chilly Pepper), subtract one burn tick.
        bool selfBurn = td.effects?.Any(e =>
            (e.cardEffectType == EnumCardEffectType.Burn ||
             e.cardEffectType == EnumCardEffectType.Poison) &&
            e.cardEffectTarget == EnumCardEffectTarget.Self) ?? false;
        if (selfBurn) effectiveHp -= profile.selfBurnPenalty;

        return effectiveHp > enemyDmg;
    }

    /// Zagraj kartę typu SwapSelf tylko wtedy, gdy zamiana realnie pomaga:
    /// aktywny nie może atakować lub grozi mu KO, a na ławce jest gotowy lepszy cel.
    private bool ShouldPlaySwapSelfTrainer(PlayerController opponent)
    {
        CardInstance active = myPlayer.activePokemon;
        if (active?.pokemonLogic == null || myPlayer.benchPokemons.Count == 0) return false;

        CardInstance enemyActive = opponent?.activePokemon;
        CardInstance swapTarget = ChooseAutomaticSwapSelfTarget();
        if (swapTarget == null) return false;

        EnergyZone zone = playerManager.GetEnergyZoneFor(myPlayer);
        EnumPokemonType availableEnergy = myPlayer.canAddEnergy && zone != null
            ? zone.currentEnergy
            : EnumPokemonType.None;

        // Trainers are played before the normal energy attachment. Compare the actual target that
        // CardActions will promote with the active after that pending attachment; otherwise Leaf
        // sees an temporarily unready Flygon and replaces it with an already-ready Metapod.
        int activeProjectedValue = GetProjectedAttackValueAfterTrainer(
            active, enemyActive, opponent, availableEnergy);
        int targetProjectedValue = GetProjectedAttackValueAfterTrainer(
            swapTarget, enemyActive, opponent, availableEnergy);

        if (activeProjectedValue >= targetProjectedValue)
            return false;

        // A clearly better target may replace an active that still cannot produce a useful attack
        // after this turn's attachment. If both can attack, preserve the current active unless it is
        // threatened with KO; this avoids spending Leaf for marginal reshuffling.
        if (activeProjectedValue <= 0)
            return targetProjectedValue > 0;

        int enemyDmg = EstimateEnemyMaxDamage(opponent);
        return active.pokemonLogic.currentHp <= enemyDmg;
    }

    /// Mirrors CardActions.ChooseBestSwapSelfTarget so the trainer heuristic evaluates the Pokemon
    /// that the automatic SwapSelf effect will actually promote.
    private CardInstance ChooseAutomaticSwapSelfTarget()
    {
        CardInstance bestTarget = null;
        int bestPrintedDamage = int.MinValue;

        foreach (CardInstance benchCard in myPlayer.benchPokemons)
        {
            var pokemonData = benchCard?.baseData as PokemonData;
            if (benchCard?.pokemonLogic == null || pokemonData?.attacks == null) continue;

            int maxDamage = pokemonData.attacks
                .Where(attack => CardActions.CanAffordAttack(benchCard.pokemonLogic, attack))
                .Select(attack => attack.damage)
                .DefaultIfEmpty(int.MinValue)
                .Max();

            if (maxDamage == int.MinValue) continue;
            if (bestTarget == null || maxDamage > bestPrintedDamage)
            {
                bestTarget = benchCard;
                bestPrintedDamage = maxDamage;
            }
        }

        return bestTarget;
    }

    private int GetProjectedAttackValueAfterTrainer(
        CardInstance attacker,
        CardInstance defender,
        PlayerController opponent,
        EnumPokemonType availableEnergy)
    {
        var pokemonData = attacker?.baseData as PokemonData;
        if (attacker?.pokemonLogic == null ||
            pokemonData?.attacks == null ||
            !attacker.pokemonLogic.tempBuffsData.canAttack)
        {
            return 0;
        }

        Dictionary<EnumPokemonType, int> energy = CopyEnergy(attacker.pokemonLogic.energyEquipped);
        AddSimulatedEnergy(energy, availableEnergy);
        int costChange = attacker.pokemonLogic.tempBuffsData.attackEnergyCostChange;
        int handCountAfterTrainer = Mathf.Max(0, myPlayer.hand.Count - 1);

        return pokemonData.attacks
            .Where(attack => EnergyMissingForAttack(energy, attack, costChange) == 0)
            .Where(attack => handCountAfterTrainer >= GetProjectedHandDiscardCost(attack, handCountAfterTrainer))
            .Select(attack =>
            {
                int damage = EstimateAttackDamage(attacker, defender, attack);
                int value = damage + EstimateBenchDamageValue(attack, opponent);
                value += ScoreOffensiveDebuffs(attack, attacker, defender, damage);
                if (HasEnergyRampEffect(attack) && ChooseRampBenchTargetForActiveUtility(attacker) != null)
                    value += profile.rampEnablesUtilityBonus;
                return value;
            })
            .DefaultIfEmpty(0)
            .Max();
    }

    private int GetProjectedHandDiscardCost(AttackData attack, int projectedHandCount)
    {
        if (attack?.effects == null) return 0;

        return attack.effects
            .Where(effect => effect.cardEffectType == EnumCardEffectType.DiscardHand)
            .Select(effect => effect.effectAmount >= 100
                ? projectedHandCount
                : Mathf.Max(0, effect.effectAmount))
            .DefaultIfEmpty(0)
            .Sum();
    }

    /// Sabrina forces the opponent's Active to swap with a RANDOM benched Pokemon. It is a tempo
    /// disruption tool: worth it only when the current enemy Active is a real threat we cannot KO
    /// this turn and the opponent has a bench to be dragged up (so their set-up attacker is demoted
    /// and replaced by something likely less prepared). Never burn it when we can already KO the
    /// enemy Active, or when it is harmless, or when the bench is empty (the effect does nothing).
    private bool ShouldPlaySwapEnemyTrainer(PlayerController opponent)
    {
        CardInstance enemyActive = opponent?.activePokemon;
        if (enemyActive?.pokemonLogic == null) return false;
        if (opponent.benchPokemons == null || opponent.benchPokemons.Count == 0) return false;

        // If our active can KO the enemy active this turn, just KO it instead of swapping it away.
        CardInstance active = myPlayer.activePokemon;
        if (active?.pokemonLogic != null &&
            GetMaxReadyDamage(active, enemyActive) >= enemyActive.pokemonLogic.currentHp)
            return false;

        // Only disrupt a meaningful threat: an enemy active that is ready to attack and would hurt us.
        bool enemyReady = HasEnergyForAnyAttack(enemyActive);
        int enemyDmg = EstimateEnemyMaxDamage(opponent);
        return enemyReady && enemyDmg > 0;
    }

    /// Net roots the enemy Active (denies retreat). Only useful when the enemy Active would
    /// otherwise want to escape — i.e. it is damaged or threatened, can pay a retreat cost, and has
    /// a bench to retreat to. Trapping a fresh, fully-healthy active with nowhere it wants to go is
    /// a wasted card.
    private bool ShouldPlayRootTrainer(PlayerController opponent)
    {
        CardInstance enemyActive = opponent?.activePokemon;
        if (enemyActive?.pokemonLogic == null) return false;
        if (opponent.benchPokemons == null || opponent.benchPokemons.Count == 0) return false;
        if (!enemyActive.pokemonLogic.tempBuffsData.canRetreat) return false;

        var enemyData = enemyActive.baseData as PokemonData;
        int retreatCost = enemyData != null
            ? Mathf.Max(0, enemyData.retreatCost + opponent.retreatEnergyCostChange)
            : 0;
        int enemyEnergy = enemyActive.pokemonLogic.energyEquipped?.Values.Sum() ?? 0;
        if (enemyEnergy < retreatCost) return false; // cannot retreat anyway, root adds nothing

        int maxHp = enemyData?.hp ?? enemyActive.pokemonLogic.currentHp;
        bool damaged = enemyActive.pokemonLogic.currentHp < maxHp;

        // We benefit from trapping it if we threaten it (can pressure/KO it soon) or it is hurt.
        CardInstance active = myPlayer.activePokemon;
        int ourDamage = active != null ? GetMaxReadyDamage(active, enemyActive) : 0;
        return damaged || ourDamage > 0;
    }

    /// Draw cards that pay a random hand-discard cost (e.g. Rummage: draw 2, discard 2 random). The
    /// draw resolves before the discard, so the card count stays flat and it can essentially always
    /// pay — the only real downside is randomly pitching cards we wanted to keep. Skip it when the
    /// hand holds an evolution we still plan to use, so we don't gamble it away for a neutral cycle.
    private bool ShouldPlayHandDiscardDrawTrainer(TrainerData td)
    {
        int discardAmount = td.effects?
            .Where(e => e.cardEffectType == EnumCardEffectType.DiscardHand)
            .Select(e => GetHandDiscardAmount(myPlayer, e.effectAmount))
            .DefaultIfEmpty(0)
            .Sum() ?? 0;

        if (discardAmount <= 0) return true; // no real discard cost — treat as a plain draw

        return !myPlayer.hand.Any(IsImportantEvolutionToKeep);
    }

    /// Play damage reduction only when the opponent is ready or one energy away
    /// from a meaningful attack, or when the reduction changes a KO into survival.
    private bool ShouldPlayDamageReductionTrainer(TrainerData td, PlayerController opponent)
    {
        CardInstance active = myPlayer.activePokemon;
        CardInstance enemyActive = opponent?.activePokemon;
        if (active?.pokemonLogic == null || enemyActive?.pokemonLogic == null) return false;

        int reduction = GetMaxDamageReduction(td);
        if (reduction <= 0) return false;

        var enemyData = enemyActive.baseData as PokemonData;
        if (enemyData?.attacks == null || enemyData.attacks.Count == 0) return false;

        int currentHp = active.pokemonLogic.currentHp;

        foreach (AttackData attack in enemyData.attacks)
        {
            if (attack == null) continue;

            int estimatedDamage = EstimateAttackDamage(enemyActive, active, attack);
            if (estimatedDamage <= 0) continue;

            bool attackReady = CardActions.CanAffordAttack(enemyActive.pokemonLogic, attack) &&
                               HasEnoughCardsForAttack(opponent, attack);
            int damageAfterReduction = Mathf.Max(0, estimatedDamage - reduction);
            if (attackReady && currentHp <= estimatedDamage && currentHp > damageAfterReduction)
                return true;

            int missingEnergy = EnergyMissingForAttack(enemyActive.pokemonLogic, attack);
            if (missingEnergy <= 1 && HasEnoughCardsForAttack(opponent, attack) && estimatedDamage * 2 >= reduction)
                return true;
        }

        return false;
    }

    private int EnergyMissingForAttack(Pokemon pokemon, AttackData attack)
    {
        if (pokemon == null || attack == null)
            return 0;

        return EnergyMissingForAttack(
            pokemon.energyEquipped,
            attack,
            pokemon.tempBuffsData.attackEnergyCostChange);
    }

    private int EnergyMissingForAttack(Dictionary<EnumPokemonType, int> equipped, AttackData attack, int attackEnergyCostChange)
    {
        if (attack == null)
            return 0;
        var attackCost = attack.attackCost ?? new List<EnumPokemonType>();

        var available = CopyEnergy(equipped);
        int jokers = available.TryGetValue(EnumPokemonType.Dragon, out int dragonCount) ? dragonCount : 0;
        available[EnumPokemonType.Dragon] = 0; // Dragon is a joker, spent flexibly below
        int missingTyped = 0;

        foreach (var cost in attackCost)
        {
            if (cost == EnumPokemonType.Colorless) continue;

            if (available.TryGetValue(cost, out int count) && count > 0)
                available[cost]--;
            else
                missingTyped++;
        }

        // Dragon jokers cover unpaid typed slots first, then spill into Colorless below.
        int typedCoveredByJoker = Mathf.Min(jokers, missingTyped);
        missingTyped -= typedCoveredByJoker;
        jokers -= typedCoveredByJoker;

        int colorlessCost = attackCost.Count(c => c == EnumPokemonType.Colorless);
        int adjustedColorless = Mathf.Max(0, colorlessCost + attackEnergyCostChange);
        int remainingEnergy = available.Values.Sum() + jokers;
        int missingColorless = Mathf.Max(0, adjustedColorless - remainingEnergy);

        return missingTyped + missingColorless;
    }

    private bool HasFinalEvolutionInHand(CardInstance benchPokemon)
    {
        if (benchPokemon?.baseData is not PokemonData pd) return false;
        return myPlayer.hand.Any(card =>
            card?.baseData is PokemonData handPd &&
            handPd.stage > pd.stage &&
            handPd.evolvesFrom == pd.cardName &&
            !HasFurtherEvolutionInOwnedCards(handPd));
    }

    private bool HasFurtherEvolutionInOwnedCards(PokemonData pokemon)
    {
        return GetOwnedPokemonData().Any(pd => pd.evolvesFrom == pokemon.cardName);
    }

    private bool CanWinWithReadyAttack(CardInstance attacker)
    {
        if (attacker?.pokemonLogic == null) return false;
        PlayerController opponent = playerManager.player1 == myPlayer ? playerManager.player2 : playerManager.player1;
        CardInstance enemyActive = opponent?.activePokemon;
        if (enemyActive?.pokemonLogic == null) return false;
        if (myPlayer.score + 1 < GameRulesConfig.Instance.pointsToWin) return false;

        return GetMaxReadyDamage(attacker, enemyActive) >= enemyActive.pokemonLogic.currentHp;
    }

    /// Maksymalne obrażenia jakie ten Pokemon może zadać aktualnie dostępnymi atakami.
    private int GetMaxReadyDamage(CardInstance card, CardInstance defender = null)
    {
        var pd = card?.baseData as PokemonData;
        if (pd?.attacks == null || pd.attacks.Count == 0) return 0;
        PlayerController owner = GetBoardOwner(card);
        return pd.attacks
            .Where(atk => CardActions.CanAffordAttack(card.pokemonLogic, atk))
            .Where(atk => HasEnoughCardsForAttack(owner, atk))
            .Select(atk => EstimateAttackDamage(card, defender, atk))
            .DefaultIfEmpty(0)
            .Max();
    }

    /// Total immediate pressure used for comparing attackers during Retreat decisions.
    /// Active damage remains separate for KO checks; bench damage is added only to the
    /// relative value comparison so attacks such as Flygon's Sand Sweep are not underrated.
    private int GetMaxReadyAttackValue(
        CardInstance card,
        CardInstance defender,
        PlayerController opponent)
    {
        var pd = card?.baseData as PokemonData;
        if (card?.pokemonLogic == null || pd?.attacks == null || pd.attacks.Count == 0)
            return 0;

        PlayerController owner = GetBoardOwner(card);
        return pd.attacks
            .Where(attack => CardActions.CanAffordAttack(card.pokemonLogic, attack))
            .Where(attack => HasEnoughCardsForAttack(owner, attack))
            .Select(attack =>
                EstimateAttackDamage(card, defender, attack) +
                EstimateBenchDamageValue(attack, opponent))
            .DefaultIfEmpty(0)
            .Max();
    }

    private bool BestReadyAttackHasEffect(CardInstance card, CardInstance defender, EnumCardEffectType effectType)
    {
        var pd = card?.baseData as PokemonData;
        if (card?.pokemonLogic == null || pd?.attacks == null || pd.attacks.Count == 0) return false;

        PlayerController owner = GetBoardOwner(card);
        int bestDamage = int.MinValue;
        bool bestHasEffect = false;

        foreach (AttackData attack in pd.attacks)
        {
            if (!CardActions.CanAffordAttack(card.pokemonLogic, attack)) continue;
            if (!HasEnoughCardsForAttack(owner, attack)) continue;

            int damage = EstimateAttackDamage(card, defender, attack);
            bool hasEffect = attack.effects != null &&
                             attack.effects.Any(e => e.cardEffectType == effectType);

            if (damage > bestDamage)
            {
                bestDamage = damage;
                bestHasEffect = hasEffect;
            }
            else if (damage == bestDamage && hasEffect)
            {
                bestHasEffect = true;
            }
        }

        return bestHasEffect;
    }

    private int EstimateAttackDamage(CardInstance attacker, CardInstance defender, AttackData attack)
    {
        int damage = attack?.damage ?? 0;
        if (attack?.effects != null)
        {
            int selfDiscardCount = attack.effects
                .Where(e => e.cardEffectType == EnumCardEffectType.EnergyDiscard &&
                            e.cardEffectTarget == EnumCardEffectTarget.Self)
                .Select(e => Mathf.Max(0, e.effectAmount))
                .DefaultIfEmpty(0)
                .Sum();

            foreach (var effect in attack.effects)
            {
                if (effect.cardEffectType == EnumCardEffectType.PowerUp)
                {
                    int energyAfterDiscard = Mathf.Max(
                        0,
                        (attacker?.pokemonLogic?.energyEquipped?.Values.Sum() ?? 0) - selfDiscardCount);
                    damage += effect.effectAmount * energyAfterDiscard;
                }
                else if (effect.cardEffectType == EnumCardEffectType.Psychic)
                {
                    damage += effect.effectAmount * (defender?.pokemonLogic?.energyEquipped?.Values.Sum() ?? 0);
                }
            }
        }

        PlayerController attackerOwner = GetBoardOwner(attacker);
        int modifiedDamage = damage
            + (attackerOwner?.doMoreDamageToActive ?? 0)
            + (attacker?.pokemonLogic?.tempBuffsData?.doMoreDamageToActive ?? 0)
            + (defender?.pokemonLogic?.tempBuffsData?.takeMoreDamageFromAttacks ?? 0);

        modifiedDamage = Mathf.Max(0, modifiedDamage);
        return modifiedDamage * GetMultiattackHitCount(attack);
    }

    /// Value of an attack's bench-damage effects (e.g. Flygon's "Sand Sweep" snipe). Kept separate from
    /// EstimateAttackDamage so bench AoE boosts an attack's ranking without ever counting toward the
    /// Active-KO threshold. Scaled by how many enemy Pokemon are actually on the bench.
    private int EstimateBenchDamageValue(AttackData attack, PlayerController opponent)
    {
        if (attack?.effects == null || opponent == null) return 0;

        int benchCount = opponent.benchPokemons?.Count(b => b?.pokemonLogic != null) ?? 0;
        if (benchCount == 0) return 0;

        int value = 0;
        foreach (var effect in attack.effects)
        {
            if (effect.cardEffectType != EnumCardEffectType.BenchDmg) continue;
            switch (effect.cardEffectTarget)
            {
                case EnumCardEffectTarget.EnemyBenchPokemon:
                case EnumCardEffectTarget.AllOpponents:
                case EnumCardEffectTarget.All:
                    value += Mathf.Max(0, effect.effectAmount) * benchCount;
                    break;
            }
        }
        return value;
    }

    /// Flat damage gained per attached energy from PowerUp effects on this attack
    /// (e.g. Hydra Breath scaling). Used to project damage after a simulated energy attach.
    private int GetAttackPowerUpPerEnergy(AttackData attack)
    {
        if (attack?.effects == null) return 0;
        return attack.effects
            .Where(e => e.cardEffectType == EnumCardEffectType.PowerUp)
            .Select(e => e.effectAmount)
            .DefaultIfEmpty(0)
            .Sum();
    }

    /// True if attaching one `energyType` to `attacker` would NEWLY enable a KO of the opponent's
    /// active this turn — an attack that becomes affordable AND lethal after the attach, but was
    /// not lethal (or not affordable) before. Non-mutating: simulates energy without touching state.
    /// Gated tightly so it only fires on a genuine new lethal; every other attach decision is unchanged.
    private bool WouldAttachEnableKoOnActiveEnemy(CardInstance attacker, EnumPokemonType energyType, CardInstance enemyActive)
    {
        var pd = attacker?.baseData as PokemonData;
        var enemyLogic = enemyActive?.pokemonLogic;
        if (attacker?.pokemonLogic == null || pd?.attacks == null || pd.attacks.Count == 0)
            return false;
        if (enemyLogic == null || enemyLogic.currentHp <= 0 || energyType == EnumPokemonType.None)
            return false;

        int enemyHp = enemyLogic.currentHp;
        int costChange = attacker.pokemonLogic.tempBuffsData.attackEnergyCostChange;

        Dictionary<EnumPokemonType, int> energyBefore = CopyEnergy(attacker.pokemonLogic.energyEquipped);
        Dictionary<EnumPokemonType, int> energyAfter = CopyEnergy(attacker.pokemonLogic.energyEquipped);
        AddSimulatedEnergy(energyAfter, energyType);

        foreach (AttackData atk in pd.attacks)
        {
            if (atk == null) continue;
            if (EnergyMissingForAttack(energyAfter, atk, costChange) != 0) continue; // not affordable after attach

            bool affordableBefore = EnergyMissingForAttack(energyBefore, atk, costChange) == 0;
            int damageBefore = EstimateAttackDamage(attacker, enemyActive, atk);
            int damageAfter = damageBefore + GetAttackPowerUpPerEnergy(atk) * GetMultiattackHitCount(atk);

            bool koBefore = affordableBefore && damageBefore >= enemyHp;
            bool koAfter = damageAfter >= enemyHp;
            if (koAfter && !koBefore)
                return true;
        }

        return false;
    }

    /// Scores the non-damage payoff an attack inflicts: enemy debuffs (Expose, Slow, EnergyDiscard),
    /// damage-over-time (Poison, Burn), enemy disables (Paralyze, Asleep, Confuse, Root), and self riders
    /// (Counterattack, LeechLife, Heal, BenchHeal, DmgTakenRed). Lets status/stall decks value their
    /// 0-damage wincon attacks instead of avoiding them. Skips effects that already wouldn't matter
    /// (attack already KOs, status already on, or healing has no damaged target).
    private int ScoreOffensiveDebuffs(AttackData attack, CardInstance attacker, CardInstance enemyActive, int estimatedDamage)
    {
        if (attack?.effects == null) return 0;

        var enemyLogic = enemyActive?.pokemonLogic;
        bool willKo = enemyLogic != null && estimatedDamage >= enemyLogic.currentHp;
        if (willKo) return 0;

        int total = 0;
        var buffs = enemyLogic?.tempBuffsData;

        foreach (var effect in attack.effects)
        {
            bool targetsEnemyActive = effect.cardEffectTarget == EnumCardEffectTarget.EnemyActivePokemon;

            switch (effect.cardEffectType)
            {
                case EnumCardEffectType.Expose:
                {
                    int amount = Mathf.Abs(effect.effectAmount);
                    int current = buffs?.takeMoreDamageFromAttacksDebuff ?? 0;
                    int delta = Mathf.Max(0, amount - current);
                    if (delta > 0)
                        total += Mathf.Min(profile.exposeCap, delta * profile.exposePerAmount);
                    break;
                }
                case EnumCardEffectType.Slow:
                {
                    if ((buffs?.attackEnergyCostChange ?? 0) < 1)
                        total += profile.slowBonus;
                    break;
                }
                case EnumCardEffectType.EnergyDiscard:
                {
                    if (effect.cardEffectTarget == EnumCardEffectTarget.Self) break;
                    int enemyEnergy = enemyLogic?.energyEquipped?.Values.Sum() ?? 0;
                    if (enemyEnergy <= 0) break;
                    int discarded = Mathf.Min(enemyEnergy, Mathf.Max(1, effect.effectAmount));
                    total += discarded * profile.energyDiscardPerEnergy;
                    break;
                }
                case EnumCardEffectType.Poison:
                {
                    if (!targetsEnemyActive) break;
                    int amount = Mathf.Abs(effect.effectAmount);
                    if (amount <= 0) break;
                    // Damage-over-time: ~2 ticks of value; re-poisoning an already poisoned target is worth little.
                    total += (enemyLogic?.isPoisoned ?? false) ? profile.poisonReapplyBonus : Mathf.Min(profile.poisonCap, amount * profile.poisonPerAmount);
                    break;
                }
                case EnumCardEffectType.Burn:
                {
                    if (!targetsEnemyActive) break;
                    int amount = Mathf.Abs(effect.effectAmount);
                    if (amount <= 0) break;
                    bool alreadyBurned = enemyLogic != null && enemyLogic.isBurned;
                    total += alreadyBurned ? profile.burnReapplyBonus : Mathf.Min(profile.burnCap, amount * profile.burnPerAmount);
                    break;
                }
                case EnumCardEffectType.Paralyze:
                {
                    if (!targetsEnemyActive) break;
                    bool already = enemyLogic != null && enemyLogic.otherSpecialCondition == EnumSpecialConditionType.Paralyzed;
                    if (!already) total += profile.paralyzeBonus; // skips the opponent's next attack
                    break;
                }
                case EnumCardEffectType.Asleep:
                {
                    if (!targetsEnemyActive) break;
                    bool already = enemyLogic != null && enemyLogic.otherSpecialCondition == EnumSpecialConditionType.Asleep;
                    if (!already) total += profile.asleepBonus;
                    break;
                }
                case EnumCardEffectType.Confuse:
                {
                    if (!targetsEnemyActive) break;
                    bool already = enemyLogic != null && enemyLogic.otherSpecialCondition == EnumSpecialConditionType.Confused;
                    if (!already) total += profile.confuseBonus;
                    break;
                }
                case EnumCardEffectType.Root:
                {
                    if (!targetsEnemyActive) break;
                    total += profile.rootBonus; // denies retreat
                    break;
                }
                case EnumCardEffectType.Counterattack:
                {
                    int amount = Mathf.Abs(effect.effectAmount);
                    if (amount > 0) total += Mathf.Min(profile.counterattackCap, amount / profile.counterattackDivisor + profile.counterattackBase); // retaliation when hit
                    break;
                }
                case EnumCardEffectType.LeechLife:
                {
                    total += profile.leechLifeBonus; // self-heal rider
                    break;
                }
                case EnumCardEffectType.Heal:
                {
                    int amount = UsefulSelfHealAmount(attacker, effect);
                    if (amount > 0)
                        total += Mathf.Min(profile.attackHealCap, (amount / 10) * profile.attackHealPer10Hp);
                    break;
                }
                case EnumCardEffectType.BenchHeal:
                {
                    int amount = UsefulBenchHealAmount(effect);
                    if (amount > 0)
                        total += Mathf.Min(profile.attackBenchHealCap, (amount / 10) * profile.attackHealPer10Hp);
                    break;
                }
                case EnumCardEffectType.DmgTakenRed:
                {
                    int amount = Mathf.Abs(effect.effectAmount);
                    if (amount > 0)
                        total += Mathf.Min(profile.attackDamageReductionCap, (amount / 10) * profile.attackDamageReductionPer10);
                    break;
                }
            }
        }

        return total;
    }

    private int UsefulSelfHealAmount(CardInstance attacker, EffectData effect)
    {
        if (attacker?.pokemonLogic == null || effect == null) return 0;
        if (effect.cardEffectTarget != EnumCardEffectTarget.Self &&
            effect.cardEffectTarget != EnumCardEffectTarget.ActivePokemon) return 0;

        int maxHp = (attacker.baseData as PokemonData)?.hp ?? attacker.pokemonLogic.currentHp;
        int missing = Mathf.Max(0, maxHp - attacker.pokemonLogic.currentHp);
        return Mathf.Min(missing, Mathf.Abs(effect.effectAmount));
    }

    private int UsefulBenchHealAmount(EffectData effect)
    {
        if (myPlayer?.benchPokemons == null || effect == null) return 0;

        int healAmount = Mathf.Abs(effect.effectAmount);
        if (healAmount <= 0) return 0;

        int useful = 0;
        foreach (CardInstance bench in myPlayer.benchPokemons)
        {
            if (bench?.pokemonLogic == null) continue;
            int maxHp = (bench.baseData as PokemonData)?.hp ?? bench.pokemonLogic.currentHp;
            int missing = Mathf.Max(0, maxHp - bench.pokemonLogic.currentHp);
            useful += Mathf.Min(missing, healAmount);
        }

        return useful;
    }

    private int GetMultiattackHitCount(AttackData attack)
    {
        if (attack?.effects == null) return 1;

        return attack.effects
            .Where(e => e.cardEffectType == EnumCardEffectType.Multiattack)
            .Select(e => Mathf.Max(1, e.effectAmount))
            .DefaultIfEmpty(1)
            .Max();
    }

    private bool HasEnoughCardsForAttack(PlayerController player, AttackData attack)
    {
        if (player == null) return false;
        return player.hand.Count >= GetHandDiscardCost(player, attack);
    }

    private int GetHandDiscardCost(PlayerController player, AttackData attack)
    {
        if (attack?.effects == null) return 0;

        return attack.effects
            .Where(e => e.cardEffectType == EnumCardEffectType.DiscardHand)
            .Select(e => GetHandDiscardAmount(player, e.effectAmount))
            .DefaultIfEmpty(0)
            .Sum();
    }

    private int GetHandDiscardAmount(PlayerController player, int amount)
    {
        if (player == null) return 0;
        return amount >= 100 ? player.hand.Count : Mathf.Max(0, amount);
    }

    private PlayerController GetBoardOwner(CardInstance card)
    {
        if (card == null || playerManager == null) return null;

        if (playerManager.player1 != null &&
            (playerManager.player1.activePokemon == card ||
             playerManager.player1.benchPokemons.Contains(card)))
        {
            return playerManager.player1;
        }

        if (playerManager.player2 != null &&
            (playerManager.player2.activePokemon == card ||
             playerManager.player2.benchPokemons.Contains(card)))
        {
            return playerManager.player2;
        }

        return null;
    }

    private bool IsStillActiveTurn(string stepName)
    {
        if (playerManager.activePlayer == myPlayer) return true;

        Debug.Log($"[AlgorithmBrain] Stale coroutine for {myPlayer.playerName}; no longer active after {stepName}.");
        return false;
    }
}
