using Newtonsoft.Json;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class CardValidator : MonoBehaviour
{
    public static CardValidator Instance { get; private set; }
    private GameManager gameManager;
    public TMP_Text loadingResults;
    public GameObject loadingImage;
    public GameObject loadingText;


    void Awake()
    {
        if (Instance == null) Instance = this;
        gameManager = GameManager.Instance;
    }



    public bool IsCardCorrect(CardData cardData)
    {
        // Pokemon Card
        if (cardData is PokemonData pokemonData)
        {
            if (!CheckName(pokemonData.cardName) ||
            !CheckId(pokemonData.cardName, pokemonData.cardId))
            {
                return false;
            }

            if (CheckType(pokemonData.cardName, pokemonData.type) &&
                CheckStage(pokemonData.stage) &&
                CheckPreEvolution(pokemonData.evolvesFrom, pokemonData.stage) &&
                CheckHP(pokemonData.hp) &&
                CheckAttacks(pokemonData) &&
                CheckRetreatCost(pokemonData.retreatCost))
            {
                return true;
            }
            else
            {
                return false;
            }

        }

        // Trainer Card
        else
        {
            return true;
        }

    }



    public bool AreAllCardsCorrect()
    {
        Debug.Log("<color=green><b>-------------------- Start CardValidator --------------------</b></color>");

        if (gameManager.jsonLoader == null || gameManager.jsonLoader.cardLibrary.Count == 0)
        {
            Debug.LogError("Brak załadowanej biblioteki kart!");
            return false;
        }



        int totalCards = gameManager.jsonLoader.cardLibrary.Count;
        int incorrectCards = 0;
        foreach (var cardEntry in gameManager.jsonLoader.cardLibrary)
        {
            Debug.Log($"Validating card: {cardEntry.Value.cardName} (ID: {cardEntry.Value.cardId})");
            if (!IsCardCorrect(cardEntry.Value))
            {
                incorrectCards++;
            }
        }
        Debug.Log($"Card validation completed: {totalCards - incorrectCards}/{totalCards} cards are correct.");
        Debug.Log("<color=black><b>-------------------- End CardValidator --------------------</b></color>");

        DisplayValidationResults();
        Destroy(loadingImage);
        Destroy(loadingText);

        return incorrectCards == 0;
    }


    public void DisplayValidationResults()
    {
        int totalCards = gameManager.jsonLoader.cardLibrary.Count;
        int incorrectCards = 0;
        foreach (var cardEntry in gameManager.jsonLoader.cardLibrary)
        {
            if (!IsCardCorrect(cardEntry.Value))
            {
                incorrectCards++;
            }
        }
        loadingResults.text = $"Card validation completed: {totalCards - incorrectCards}/{totalCards} cards are correct.";
    }


    #region Card Validation Methods

    public static bool CheckType(string pokemonName, EnumPokemonType pokemonType)
    {
        if (!System.Enum.IsDefined(typeof(EnumPokemonType), pokemonType))
        {
            UnityEngine.Debug.LogWarning($"{pokemonName} is WRONG type: {pokemonType}");
            return false;
        }
        return true;
    }

    public static bool CheckStage(int stage)
    {
        if (stage == 0 || stage == 1 || stage == 2)
        {
            return true;
        }
        else
        {
            UnityEngine.Debug.LogWarning($"Wrong stage: {stage}");
            return false;
        }
    }

    public bool CheckPreEvolution(string preEvolution, int stage)
    {
        if (stage == 0 && string.IsNullOrEmpty(preEvolution))
        {
            return true;
        }
        else
        {
            if (string.IsNullOrEmpty(preEvolution))
            {
                Debug.LogWarning($"Stage {stage} card must have a pre-evolution specified!");
                return false;
            }

            int totalCards = gameManager.jsonLoader.cardLibrary.Count;
            foreach (var cardEntry in gameManager.jsonLoader.cardLibrary)
            {
                if (cardEntry.Value.cardName == preEvolution)
                {
                    if (cardEntry.Value is PokemonData preEvolutionData)
                    {
                        if (preEvolutionData.stage != stage - 1)
                        {
                            Debug.LogWarning($"Pre-evolution {preEvolution} must be stage {stage - 1}!");
                            return false;
                        }
                        return true;
                    }
                    else if (cardEntry.Value is TrainerData)
                    {
                        Debug.LogWarning($"Pre-evolution {preEvolution} is not a Pokemon card!");
                        return false;
                    }
                    else
                    {
                        Debug.LogWarning($"Pre-evolution {preEvolution} not found in card library!");
                        return false;
                    }
                }
            }
            return true;
        }
    }

    public bool CheckHP(int hp)
    {
        if (hp > 0)
        {
            if (hp > 400)
            {
                Debug.LogWarning($"HP value: {hp} is unusually high! Please double-check if it's correct.");
                return false;
            }
            return true;
        }
        else
        {
            Debug.LogWarning($"HP must be greater than 0! Current HP: {hp}");
            return false;
        }
    }

    //public bool CheckEX(string isEX)
    //{
    //    if (isEX != "true" || isEX != "false")
    //    {
    //        return false;
    //    }
    //    return true;
    //}

    public bool CheckAttacks(PokemonData pokemonData)
    {
        if (pokemonData.attacks == null || pokemonData.attacks.Count == 0)
        {
            Debug.LogWarning($"Pokemon {pokemonData.cardName} must have at least one attack!");
            return false;
        }
        return true;
    }

    public bool CheckRetreatCost(int retreatCost)
    {
        if (retreatCost > 4)
        {
            Debug.LogWarning($"Retreat cost of {retreatCost} is unusually high! Please double-check if it's correct.");
        }
        if (retreatCost < 0)
        {
            Debug.LogWarning($"Retreat cost cannot be negative! Current value: {retreatCost}");
            return false;
        }
        return true;
    }

    public bool CheckName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            Debug.LogWarning($"Card name cannot be empty!");
            return false;
        }
        return true;
    }

    public bool CheckId(string name, string id)
    {
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(id))
            return false;

        string lowerName = name.ToLower();

        if (id.StartsWith(lowerName + "_"))
        {
            return true;
        }
        else
        {
            Debug.LogWarning($"Card ID '{id}' does not match expected format for card name '{name}'");
            return false;
        }
    }

    public bool CheckImageName(string pokemonId, string imageName)
    {
        string pngVersion = pokemonId + ".png";
        string jpgVersion = pokemonId + ".jpg";

        if (pngVersion == imageName || jpgVersion == imageName)
        {
            return true;
        }
        else
        {
            Debug.LogWarning($"Image name '{imageName}' does not match expected format for Pokemon ID '{pokemonId}'. Expected: '{pngVersion}' or '{jpgVersion}'");
            return false;
        }
    }

    #endregion
}
