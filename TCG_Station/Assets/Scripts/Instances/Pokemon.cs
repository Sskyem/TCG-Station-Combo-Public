using System;
using System.Collections.Generic;
using System.Diagnostics;

public class Pokemon
{
    public PokemonData pokemonData;
    public int currentHp;
    public Dictionary<EnumPokemonType, int> energyEquipped = new Dictionary<EnumPokemonType, int>();
    public bool isPoisoned = false;
    public bool isBurned = false;
    // Mutually exclusive group: Paralyzed, Asleep, Confused (only one at a time).
    // Poison and Burn are tracked separately (isPoisoned, isBurned) and may coexist with this group and each other.
    public EnumSpecialConditionType otherSpecialCondition;
    public bool hasToolEquipped = false;

    public int uniquePokemonID = 0;
    public int turnPlacedOnBoard = 0;
    public bool rootPersistsThroughNextOwnerTurn = false;
    public bool paralysisPersistsThroughNextOwnerTurn = false;
    // Slowed (extra attack energy cost) lasts until the Pokemon retreats/leaves the active spot,
    // so it must survive the start-of-turn buff reset (mirrors rootPersistsThroughNextOwnerTurn).
    public bool slowPersistsThroughNextOwnerTurn = false;

    private static int currentId = 1; // globalny licznik — każdy Pokemon dostaje unikalne ID

    public List<Pokemon> preEvolutions = new List<Pokemon>();
    public TempBuffsData tempBuffsData = new TempBuffsData();

    public Pokemon(PokemonData pokemonData)
    {
        this.pokemonData = pokemonData;
        this.currentHp = pokemonData.hp;
        this.uniquePokemonID = currentId++;

        InitializeEnergy();
    }




    private void InitializeEnergy()
    {
        foreach (EnumPokemonType type in Enum.GetValues(typeof(EnumPokemonType)))
        {
            if (type == EnumPokemonType.None) continue;

            energyEquipped.Add(type, 0);
        }
    }

    public void ResetBuffs()
    {
        tempBuffsData = new TempBuffsData();
    }

    public void ClearBuffsDebuffsAndStatuses()
    {
        isPoisoned = false;
        isBurned = false;
        otherSpecialCondition = EnumSpecialConditionType.None;
        rootPersistsThroughNextOwnerTurn = false;
        paralysisPersistsThroughNextOwnerTurn = false;
        slowPersistsThroughNextOwnerTurn = false;
        ResetBuffs();
    }

    public static void ResetIdCounter()
    {
        currentId = 1;
    }
}
