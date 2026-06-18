using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System.Collections.Generic;
using static EnumCardEffectTarget;



[System.Serializable]
public class EffectData
{
    public EnumCardEffectType cardEffectType;
    public EnumCardEffectTarget cardEffectTarget;
    public int effectAmount;
}

