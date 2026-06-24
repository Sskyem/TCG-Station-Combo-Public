using System;
using System.Collections.Generic;

[Serializable]
public class DeckData
{
    public string deckName;
    public List<DeckCardData> cards;
    public List<EnumPokemonType> energyTypes;
}