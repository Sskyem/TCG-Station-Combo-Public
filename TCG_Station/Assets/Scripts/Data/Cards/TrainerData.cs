// Klasa Trenera (dziedziczy po ogólnej Karcie)
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System.Collections.Generic;




[System.Serializable]
public class TrainerData : CardData
{
    public EnumTrainerSubType trainerSubType;
    public string effectDescription;
    public List<EffectData> effects;
}