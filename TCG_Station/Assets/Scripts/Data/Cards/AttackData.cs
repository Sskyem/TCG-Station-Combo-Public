using System.Collections.Generic;

[System.Serializable]
public class AttackData
{
    public string attackName = "missing attackName";
    public int damage = 1;
    public string attackDescription = "";
    public List<EnumPokemonType> attackCost = new List<EnumPokemonType>();
    public List<EffectData> effects = new List<EffectData>();
}
