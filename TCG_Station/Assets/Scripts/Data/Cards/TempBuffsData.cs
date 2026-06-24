using UnityEngine;

public class TempBuffsData
{
    [Header("Combat & Damage")]
    public int doMoreDamageToActive = 0; // Negative value means less damage
    public int takeMoreDamageFromAttacks = 0; // Legacy net value: positive means more damage taken, negative means less
    public int takeMoreDamageFromAttacksDebuff = 0;
    public int takeLessDamageFromAttacksBuff = 0;
    public int recoilDamage = 0; // Damage taken by the attacker themselves
    public int counterAttackDamage = 0; // Attacker takes this dmg when they attack this pokemon
    public bool canAttack = true;
    public bool canBeAttacked = true;
    public bool canBeKnockedOut = true; // If false, the unit stays at 10 HP instead of fainting

    [Header("Energy & Economy")]
    public int attackEnergyCostChange = 0; // Negative value means lower cost
    public int retreatEnergyCostChange = 0; // Negative value means lower cost
    public bool doubleEnergyValue = false; // Each attached energy counts as two
    public bool canAddEnergy = true;
    public bool canRetreat = true;

    [Header("Healing & HP")]
    public int healingMultiplier = 1;
    public int healPerTurn = 0; // Passive regeneration at start/end of turn
    public int hpChange = 0; // Negative value means less HP

    [Header("Status Susceptibility")]
    public bool canBePoisoned = true;
    public bool canBeBurned = true;
    public bool canBeParalyzed = true;
    public bool canBeAsleep = true;
    public bool canBeConfused = true;

    [Header("Status Modifiers")]
    public int takeMoreDamageFromPoison = 0;
    public int takeMoreDamageFromBurn = 0;

    [Header("Progression & Special Rules")]
    public bool canEvolve = true;
    public bool canBeDamagedByAbility = true;
    public bool canBeDamagedByEffectOfAttack = true;
    public bool canBeAffectedByTrainers = true;
    public bool canAttachTool = true;

    [Header("Custom")]
    public bool rooted = false;
    public bool canBeRooted = true;

    public void SetAttackDamageTakenDebuff(int amount)
    {
        takeMoreDamageFromAttacksDebuff = Mathf.Max(0, amount);
        RecalculateAttackDamageTakenModifier();
    }

    public void SetAttackDamageTakenReduction(int amount)
    {
        takeLessDamageFromAttacksBuff = Mathf.Max(0, amount);
        RecalculateAttackDamageTakenModifier();
    }

    public void RecalculateAttackDamageTakenModifier()
    {
        takeMoreDamageFromAttacks = takeMoreDamageFromAttacksDebuff - takeLessDamageFromAttacksBuff;
    }




    
}
