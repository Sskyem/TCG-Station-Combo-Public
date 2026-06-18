using System.Collections.Generic;
using TMPro;
using UnityEngine;

public class PlayerController : MonoBehaviour
{
    [Header("Identity")]
    public EnumPlayerType playerType;
    public int playerId; // Set 1 for Host/Player 1, 2 for Client/Player 2
    public string playerName;
    public int score = 0;
    // Total cards drawn this game (incl. the opening hand). Field initializer resets it per fresh
    // PlayerController — the benchmark reloads the scene per match, so each game starts at 0.
    public int cardsDrawnThisGame = 0;
    public PlayerBrain brain;

    [Header("Card Collections")]
    public List<CardInstance> deck = new List<CardInstance>();
    public List<EnumPokemonType> deckEnergies = new List<EnumPokemonType>();
    public List<CardInstance> hand = new List<CardInstance>();
    public List<CardInstance> discardPile = new List<CardInstance>();

    [Header("Pokemon Slots")]
    public CardInstance activePokemon = null;
    public string activePokemonPreview = ""; // for testing
    public List<CardInstance> benchPokemons = new List<CardInstance>();

    [Header("Turn Management")]
    public int individualTurnsTaken = 0;
    public bool doneStartupSetup = false;
    public bool usedSupporterThisTurn = false;
    public bool usedManualRetreatThisTurn = false;
    public bool hasAttackedThisTurn = false;

    [Header("Action Restrictions")]
    public bool canUseItems = true;
    public bool canUseSupporters = true;
    public bool canUseTools = true;
    public bool canUseStadiums = true;
    public bool canBeAffectedByTrainers = true;
    public bool doneTurn = false;

    [Header("Global Modifiers")]
    public int doMoreDamageToActive = 0;
    public int attackEnergyCostChange = 0;
    public int retreatEnergyCostChange = 0;
    public bool canAddEnergy = true;
    public bool canEvolve = true;

    [Header("Flags for Logic")]
    public bool chooseMode = false; // gracz musi wybrać nowego Active po nokaucie lub efekcie (nie dotyczy setupu)
    public bool hasHadFirstTurn = false; // flaga pierwszej tury — ewolucje zablokowane aż do następnej

    public PlayerManager playerManager;

    private void Awake()
    {
        activePokemon = null;
    }

    public void ResetPlayerModifiers()
    {
        this.canUseItems = true;
        this.canUseSupporters = true;
        this.canUseTools = true;
        this.canUseStadiums = true;
        this.canBeAffectedByTrainers = true;
        this.usedManualRetreatThisTurn = false;
        this.doMoreDamageToActive = 0;
        this.attackEnergyCostChange = 0;
        this.retreatEnergyCostChange = 0;
        this.canAddEnergy = true;
        this.canEvolve = true;
    }

    public bool IsCardOwnerInLogic(CardInstance cardInstance)
    {
        // Szukamy po referencji — najpierw stół (Active + ławka), potem ręka
        if (cardInstance?.pokemonLogic != null)
        {
            if (activePokemon == cardInstance) return true;
            if (benchPokemons.Contains(cardInstance)) return true;
        }

        if (cardInstance != null && hand.Contains(cardInstance))
        {
            return true;
        }

        return false;
    }


}

