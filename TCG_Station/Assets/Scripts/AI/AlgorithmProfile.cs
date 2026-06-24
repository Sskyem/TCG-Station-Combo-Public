// AlgorithmProfile — numeric tuning profile for AlgorithmBrain.
//
// Purpose: AlgorithmBrain is a hand-tuned heuristic. A single fixed set of weights extracts a
// different fraction of each archetype's potential, which biases an AI-vs-AI benchmark toward
// "how well this one bot plays a given archetype" rather than "how strong the deck is".
//
// This class hoists every tuned constant out of AlgorithmBrain into named fields so several
// archetype-oriented profiles can share ONE decision logic and differ only by numbers. The field
// initializers below ARE the Standard profile (byte-for-byte the previous inline literals), so
// Standard() reproduces the old behavior exactly. Archetype presets start from Standard and override
// only the knobs relevant to their strategy — untouched fields stay identical to Standard.
//
// IMPORTANT: changing a value here changes weights only, never control flow. Keep it that way.
public class AlgorithmProfile
{
    // ── ScorePlayBasic ──────────────────────────────────────────────────────
    public int playBasicBaseBonus = 20;
    public int fillsActiveSlotBonus = 120;
    public int rampEngineNeededBonus = 90;   // also reused in ScoreAttachEnergy ramp-target branch
    public int rampEngineBonus = 35;         // also reused in ScoreAttachEnergy ramp-target branch
    public int neededRampTypeBonus = 85;
    public int rampAmountMultiplier = 10;    // per-point of EnergyRamp amount (PlayBasic/AttachEnergy/Attack)
    public int strongestLineOnBoardBonus = 15;
    public int strongestLineBonus = 90;
    public int scalingLineBonus = 55;
    public int futureEvolutionBonus = 25;
    public int printedCeilingCap = 45;
    public int printedCeilingDivisor = 3;
    public int weakLastBenchSlotPenalty = -45;

    // ── ScoreAttachEnergy ───────────────────────────────────────────────────
    public int energyAdvancesAttackBonus = 30;
    public int energyAdvancesLineBonus = 15;
    public int energyWrongTypePenalty = -70;
    public int strategicDiscardActiveBonus = 90;
    public int strategicDiscardBenchBonus = 140;
    public int backupDiscardBonus = 130;
    public int activeBecomesReadyBonus = 170;
    public int benchBecomesReadyBonus = 140;
    public int reduceDeficitBase = 70;
    public int reduceDeficitPerMissing = 15;
    public int reduceDeficitFloor = 20;
    public int preparesExpensiveAttackBonus = 20;
    public int activeImmediateBonus = 10;
    public int attachEnablesKoBonus = 1000;
    public int safeDiscardReserveBonus = 140;
    public int unsafeDiscardReservePenalty = -120;
    public int activeKoUnfinishedEnergyPenalty = -130;
    public int activeKoBadTypePenalty = -100;
    public int disabledActiveLikelyKoEnergyPenalty = -300;
    public int benchThreatBonus = 25;
    public int benchStockpileBonus = 70;
    public int preserveAwayFromKoBonus = 25;
    public int finisherEnergyActivePenalty = -180;
    public int finisherEnergyBenchPenalty = -120;
    public int rampBecomesReadyBonus = 60;
    public int activeRampChainBonus = 60;
    public int activeRampStrategicBenchBonus = 180;
    public int scalingPlanBase = 100;
    public int scalingPlanCap = 60;
    public int scalingPlanPerDeficit = 20;
    public int scalingReserveCompleteBonus = 80;
    public int discardSupportHelpsBonus = 35;
    public int discardSupportBonus = 10;
    public int attackerCeilingCap = 35;
    public int attackerCeilingDivisor = 4;
    public int noPayoffPenalty = -40;

    // ── ScoreRetreat ────────────────────────────────────────────────────────
    public int activeLikelyKoBonus = 85;
    public int targetLikelyKoPenalty = -90;
    public int targetSurvivalBufferCap = 45;
    public int targetSurvivalBufferDivisor = 4;
    public int activeNoDamageBonus = 65;
    public int activeUsefulRampPenalty = -95;
    public int retreatEnergyPenaltyCap = 120;
    public int retreatEnergyPenaltyPerCost = 35;
    public int targetSwapAfterRetreatKoPenalty = -80;
    public int targetSwapPenalty = -25;
    public int damageDeltaClampMin = -80;
    public int damageDeltaClampMax = 120;
    public int benchCanKoBonus = 140;
    public int benchMuchStrongerBonus = 70;
    public int benchMuchStrongerDamageDelta = 40;
    public int retreatLosesRampPenalty = -80;

    // ── ChooseBestAttackIndex ───────────────────────────────────────────────
    public int attackKoBonus = 1000;
    public int rampEnablesUtilityBonus = 180;
    public int discardTradeoffHighBonus = 35;
    public int discardTradeoffLowPenalty = -15;
    public int importantEvolutionDiscardPenalty = 120;
    public int noDamageNoUtilityPenalty = -60;

    // ── Structural-numeric constants ────────────────────────────────────────
    public int strengthDamageWeight = 10000;   // GetPokemonStrengthScore
    public int strengthHpWeight = 10;
    public int strengthStageWeight = 1;
    public int discardAttackDamageThreshold = 90; // discard-attack "high value" cutoff
    public int scalingEnergyBuffer = 2;            // GetScalingDamageRequirement headroom
    public int expectedEnemyEnergy = 2;            // negative-Psychic ceiling discount
    public int powerUpCeilingMultiplier = 5;       // EstimatePrintedAttackCeiling PowerUp factor
    public int selfBurnPenalty = 20;               // trainer self-burn HP discount

    // ── ScoreOffensiveDebuffs ───────────────────────────────────────────────
    public int exposeCap = 60;
    public int exposePerAmount = 3;
    public int slowBonus = 30;
    public int energyDiscardPerEnergy = 20;
    public int poisonReapplyBonus = 5;
    public int poisonCap = 60;
    public int poisonPerAmount = 2;
    public int burnReapplyBonus = 5;
    public int burnCap = 50;
    public int burnPerAmount = 2;
    public int paralyzeBonus = 40;
    public int asleepBonus = 30;
    public int confuseBonus = 25;
    public int rootBonus = 15;
    public int counterattackCap = 40;
    public int counterattackDivisor = 2;
    public int counterattackBase = 10;
    public int leechLifeBonus = 10;
    public int attackHealCap = 30;
    public int attackHealPer10Hp = 4;
    public int attackBenchHealCap = 25;
    public int attackDamageReductionCap = 35;
    public int attackDamageReductionPer10 = 4;

    // ── Presets ─────────────────────────────────────────────────────────────

    /// Standard = the historical inline weights (field initializers). Do not change these values.
    public static AlgorithmProfile Standard() => new AlgorithmProfile();

    /// Ramp: patient setup, fuel energy engines, build bench finishers before committing.
    public static AlgorithmProfile Ramp()
    {
        var p = new AlgorithmProfile();
        p.rampEngineBonus = 60;
        p.rampEngineNeededBonus = 120;
        p.rampAmountMultiplier = 14;
        p.benchStockpileBonus = 90;
        p.activeRampStrategicBenchBonus = 220;
        p.scalingEnergyBuffer = 3;
        p.noDamageNoUtilityPenalty = -40; // tolerate 0-damage ramp turns
        return p;
    }

    /// Tempo-Aggro: prioritise active readiness and immediate damage; reposition cheaply; less stockpiling.
    public static AlgorithmProfile TempoAggro()
    {
        var p = new AlgorithmProfile();
        p.activeBecomesReadyBonus = 210;
        p.benchBecomesReadyBonus = 120;
        p.activeImmediateBonus = 25;
        p.retreatEnergyPenaltyPerCost = 25; // cheaper to swap into a ready attacker
        p.benchStockpileBonus = 45;
        p.safeDiscardReserveBonus = 100;
        p.scalingPlanBase = 80;
        return p;
    }

    /// Control-Status: value disruption (Poison/Burn/Paralyze/Sleep/Confuse/Root) and energy denial.
    public static AlgorithmProfile ControlStatus()
    {
        var p = new AlgorithmProfile();
        p.paralyzeBonus = 70;
        p.asleepBonus = 55;
        p.confuseBonus = 45;
        p.poisonPerAmount = 3;
        p.poisonCap = 80;
        p.burnPerAmount = 3;
        p.burnCap = 70;
        p.rootBonus = 35;
        p.energyDiscardPerEnergy = 30;
        p.exposePerAmount = 4;
        return p;
    }

    /// Heal-Stall: value HP and survival; tolerate non-lethal turns; reward self-heal riders.
    public static AlgorithmProfile HealStall()
    {
        var p = new AlgorithmProfile();
        p.strengthHpWeight = 40;            // HP matters when ranking the "strongest" line
        p.targetSurvivalBufferCap = 70;
        p.activeNoDamageBonus = 80;
        p.noDamageNoUtilityPenalty = -30;   // do not force a KO race
        p.leechLifeBonus = 25;
        p.attackHealCap = 55;
        p.attackHealPer10Hp = 7;
        p.attackBenchHealCap = 45;
        p.attackDamageReductionCap = 60;
        p.attackDamageReductionPer10 = 6;
        return p;
    }

    public static AlgorithmProfile For(EnumAlgorithmProfile variant)
    {
        switch (variant)
        {
            case EnumAlgorithmProfile.Ramp:          return Ramp();
            case EnumAlgorithmProfile.TempoAggro:    return TempoAggro();
            case EnumAlgorithmProfile.ControlStatus: return ControlStatus();
            case EnumAlgorithmProfile.HealStall:     return HealStall();
            case EnumAlgorithmProfile.Standard:
            default:                                 return Standard();
        }
    }
}
