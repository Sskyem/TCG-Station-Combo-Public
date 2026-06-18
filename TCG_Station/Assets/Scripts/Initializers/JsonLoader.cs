using Newtonsoft.Json;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using System.Linq;

public class JsonLoader : MonoBehaviour
{
    public Dictionary<string, CardData> cardLibrary = new Dictionary<string, CardData>();

    public Dictionary<string, DeckData> deckLibrary = new Dictionary<string, DeckData>();
    //public Dictionary<string, List<EnumPokemonType>> deckEnergies = new Dictionary<string, List<EnumPokemonType>>();

    public void LoadCards()
    {
        Debug.Log("<color=green><b>-------------------- Start JsonLoader --------------------</b></color>");
        string cardsRootPath = RuntimePaths.CardsRoot();

        
        string[] cardFiles = Directory.GetFiles(cardsRootPath, "*.json", SearchOption.AllDirectories);

        if (cardFiles.Length == 0)
        {
            Debug.LogWarning($"No card files found in: {cardsRootPath}");
            return;
        }

        foreach (string cardFile in cardFiles)
        {
            string jsonContent = File.ReadAllText(cardFile);
            CardData loadedCard = null;

            if (cardFile.Contains("Pokemons"))
            {
                loadedCard = JsonConvert.DeserializeObject<PokemonData>(jsonContent);
            }
            else if (cardFile.Contains("Supporters") || cardFile.Contains("Items") ||
                     cardFile.Contains("Tools") || cardFile.Contains("Stadiums"))
            {
                loadedCard = JsonConvert.DeserializeObject<TrainerData>(jsonContent);
            }

            if (loadedCard != null)
            {
                if (string.IsNullOrEmpty(loadedCard.cardId))
                {
                    Debug.LogError($"BłąD KRYTYCZNY w pliku:\n{cardFile}\nSystem nie widzi pola 'cardId'!");
                    continue;
                }
                // ------------------------

                if (!cardLibrary.ContainsKey(loadedCard.cardId))
                {
                    cardLibrary.Add(loadedCard.cardId, loadedCard);
                    Debug.Log($"Loaded: {loadedCard.cardName} [{loadedCard.cardType}]");
                }
                else
                {
                    Debug.LogWarning($"Duplikat ID: {loadedCard.cardId} w pliku {cardFile}");
                }
            }
        }

        Debug.Log($"Total cards loaded: {cardLibrary.Count}");
        Debug.Log("<color=black><b>-------------------- END JsonLoader --------------------</b></color>");
    }

    public void LoadDecks()
    {
        Debug.Log("<color=cyan><b>-------------------- Start LoadDecks --------------------</b></color>");

        string decksPath = RuntimePaths.DecksRoot();

        string[] deckFiles = Directory.GetFiles(decksPath, "*.json");

        if (deckFiles.Length == 0)
        {
            Debug.LogWarning($"No deck files found in: {decksPath}");
            return;
        }

        foreach (string deckFile in deckFiles)
        {
            string jsonContent = File.ReadAllText(deckFile);

            DeckData loadedDeck = JsonConvert.DeserializeObject<DeckData>(jsonContent);

            if (loadedDeck == null)
            {
                Debug.LogError($"Failed to load deck: {deckFile}");
                continue;
            }

            if (string.IsNullOrEmpty(loadedDeck.deckName))
            {
                Debug.LogError($"Deck has no name: {deckFile}");
                continue;
            }

            if (deckLibrary.ContainsKey(loadedDeck.deckName))
            {
                Debug.LogWarning($"Duplicate deck name: {loadedDeck.deckName}");
                continue;
            }

            deckLibrary.Add(loadedDeck.deckName, loadedDeck);

            Debug.Log($"Loaded deck: {loadedDeck.deckName} | Cards: {loadedDeck.cards.Count}");
        }

        Debug.Log($"Total decks loaded: {deckLibrary.Count}");
        Debug.Log("<color=cyan><b>-------------------- END LoadDecks --------------------</b></color>");
    }
}
