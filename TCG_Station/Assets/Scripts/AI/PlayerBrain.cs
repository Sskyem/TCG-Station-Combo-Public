using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

// To jest baza. Każdy rodzaj sterowania (Człowiek, LLM, ML) będzie dziedziczył z tej klasy
// i implementował te metody po swojemu.
public abstract class PlayerBrain : MonoBehaviour
{
    // Ten mózg kontroluje konkretnego gracza
    protected PlayerController myPlayer;
    protected PlayerManager playerManager;

    public virtual void Initialize(PlayerController controller)
    {
        myPlayer = controller;
        playerManager = PlayerManager.Instance;
    }

    // --- FAZA SETUPU ---

    // Zwraca listę CardInstance pokemonów, które mózg chce położyć na stół (pierwszy element to zazwyczaj ActiveSpot)
    // To jest Coroutine, ponieważ LLM potrzebuje czasu na odpowiedź!
    public abstract IEnumerator PerformSetupPhase(System.Action<List<CardInstance>> onSetupComplete);


    // --- TURA ---

    // Wywoływane przez TurnManager po zmianie tury.
    // AI overriduje (decyzje, auto-end); HumanBrain używa domyślnego no-op (kończy turę przez HUD button).
    public virtual IEnumerator PerformTurn()
    {
        yield break;
    }

    // --- LOGIKA POMOCNICZA DLA AI ---

    // Narzędzie, którego AI może użyć, żeby dowiedzieć się, jakie pokemony ma na ręce
    protected List<CardInstance> GetBasicPokemonsInHand()
    {
        List<CardInstance> basicPokemons = new List<CardInstance>();

        foreach (CardInstance card in myPlayer.hand)
        {
            if (card.pokemonLogic != null && card.pokemonLogic.pokemonData.stage == 0)
            {
                basicPokemons.Add(card);
            }
        }

        return basicPokemons;
    }
}