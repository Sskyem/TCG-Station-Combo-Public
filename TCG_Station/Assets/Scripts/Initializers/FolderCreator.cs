using UnityEngine;
using System.IO;

public class FolderCreator : MonoBehaviour
{
    public void CreateFolders()
    {
        string cardsPath = RuntimePaths.CardsRoot();
        string decksPath = RuntimePaths.DecksRoot();

        CreateIfMissing(cardsPath);
        CreateIfMissing(decksPath);

        string[] subFolders = { "IMAGES", "Items", "Pokemons", "Stadiums", "Supporters", "Tools" };

        foreach (string subFolder in subFolders)
        {
            string subFolderPath = Path.Combine(cardsPath, subFolder);
            CreateIfMissing(subFolderPath);
        }

        Debug.Log("<color=green><b>Folders for Cards and Decks were created</b></color>");
    }

    private void CreateIfMissing(string path)
    {
        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
        }
    }
}
