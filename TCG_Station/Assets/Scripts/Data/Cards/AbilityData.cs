using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System.Collections.Generic;

[System.Serializable]
public class AbilityData
{
    public string abilityName;
    public string effectDescription;

    public List<EffectData> effects;
}