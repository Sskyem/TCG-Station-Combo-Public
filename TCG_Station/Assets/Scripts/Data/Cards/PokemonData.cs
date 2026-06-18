using System.Collections.Generic;

[System.Serializable]
public class PokemonData : CardData
{
    public int stage;
    public string evolvesFrom = null;
    public int hp = 60;
    public EnumPokemonType type = EnumPokemonType.Colorless;
    public bool isEX = false;

    public AbilityData abilityData = null;

    public List<AttackData> attacks;

    

    public int retreatCost = 1;
}