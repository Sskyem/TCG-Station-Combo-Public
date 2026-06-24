[System.Serializable]
public class CardData
{
    public string cardName;
    public string cardId;
    public int deckCardId = 0;
    public string imageName = null;

    public EnumCardType cardType; // Tutaj wpiszemy czy to Pokemon czy Trainer

    public string artAuthor = null;
    public bool fullArt = false;
}