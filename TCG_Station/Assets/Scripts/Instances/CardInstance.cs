using System;

[Serializable]
public class CardInstance
{
    /// <summary> Pokemon poza ławką (Active, ręka, talia, stos itd.). </summary>
    public const int NotOnBench = -1;

    private static int globalIdCounter = 1;
    public int instanceId;
    public CardData baseData;

    /// <summary>
    /// Indeks slotu na ławce (0 .. benchSize-1), zgodny z kolejnością na liście benchPokemons gracza.
    /// Dla Active zawsze NotOnBench. Używane m.in. pod LLM (numer slotu do ewolucji / wymiany).
    /// </summary>
    public int benchSlotIndex = NotOnBench;

    // Aktualne statystyki (HP, energie) — wypełnione tylko gdy karta jest Pokemonem
    public Pokemon pokemonLogic;

    public CardInstance(CardData data)
    {
        this.baseData = data;
        this.instanceId = globalIdCounter++;

        // Przy tworzeniu instancji Pokemona od razu inicjalizujemy jego logikę (HP, energie)
        if (data is PokemonData pokemonData)
        {
            this.pokemonLogic = new Pokemon(pokemonData);
        }
    }

    public static void ResetIdCounter()
    {
        globalIdCounter = 1;
    }
}