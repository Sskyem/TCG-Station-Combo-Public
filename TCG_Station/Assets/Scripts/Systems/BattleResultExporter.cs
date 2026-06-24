using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

public class BattleResultExporter : MonoBehaviour
{
    public static BattleResultExporter Instance { get; private set; }

    private readonly Dictionary<PlayerController, PlayerBattleData> playerData = new Dictionary<PlayerController, PlayerBattleData>();
    private string battleId;
    private bool exported;

    private class PlayerBattleData
    {
        public string Label;
        public string DeckId;
        public List<string> EnergyTypes = new List<string>();
        public Dictionary<string, int> Cards = new Dictionary<string, int>();
        public List<string> DrawnCards = new List<string>();
        public List<string> PlayedCards = new List<string>();
    }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this);
            return;
        }

        Instance = this;
    }

    private void OnEnable()
    {
        CardActions.OnCardDrewFromEffect -= RegisterDrawnCards;
        PlayerManager.OnPokemonPlayedToBoard -= RegisterPokemonPlayed;
        PlayerManager.OnPokemonEvolved -= RegisterPokemonEvolved;
        PlayerManager.OnTrainerPlayed -= RegisterTrainerPlayed;
        BattleManager.OnGameOver -= ExportBattleResult;

        CardActions.OnCardDrewFromEffect += RegisterDrawnCards;
        PlayerManager.OnPokemonPlayedToBoard += RegisterPokemonPlayed;
        PlayerManager.OnPokemonEvolved += RegisterPokemonEvolved;
        PlayerManager.OnTrainerPlayed += RegisterTrainerPlayed;
        BattleManager.OnGameOver += ExportBattleResult;
    }

    private void OnDisable()
    {
        CardActions.OnCardDrewFromEffect -= RegisterDrawnCards;
        PlayerManager.OnPokemonPlayedToBoard -= RegisterPokemonPlayed;
        PlayerManager.OnPokemonEvolved -= RegisterPokemonEvolved;
        PlayerManager.OnTrainerPlayed -= RegisterTrainerPlayed;
        BattleManager.OnGameOver -= ExportBattleResult;
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    public void BeginBattle(string id = null)
    {
        battleId = id ?? GameManager.CreateBattleId();
        exported = false;
        playerData.Clear();
    }

    public void RegisterPlayerDeck(PlayerController player, string label, string deckId, DeckData deckData)
    {
        if (player == null || deckData == null) return;

        PlayerBattleData data = GetOrCreatePlayerData(player);
        data.Label = label;
        data.DeckId = deckId;
        data.EnergyTypes = deckData.energyTypes != null
            ? deckData.energyTypes.Select(type => type.ToString()).ToList()
            : new List<string>();
        data.Cards = new Dictionary<string, int>();

        if (deckData.cards != null)
        {
            foreach (DeckCardData card in deckData.cards)
            {
                if (card == null || string.IsNullOrEmpty(card.cardId)) continue;
                data.Cards[card.cardId] = card.count;
            }
        }
    }

    public void RegisterDrawnCards(PlayerController player, List<CardInstance> cards)
    {
        if (player == null || cards == null || cards.Count == 0) return;

        PlayerBattleData data = GetOrCreatePlayerData(player);
        foreach (CardInstance card in cards)
        {
            string cardId = GetCardId(card);
            if (!string.IsNullOrEmpty(cardId))
                data.DrawnCards.Add(cardId);
        }
    }

    private void RegisterPokemonPlayed(CardInstance card, string spotType, int playerId)
    {
        RegisterPlayedCard(GetPlayerById(playerId), card);
    }

    private void RegisterPokemonEvolved(CardInstance evolutionCard, CardInstance oldTargetInstance, PlayerController owner)
    {
        RegisterPlayedCard(owner, evolutionCard);
    }

    private void RegisterTrainerPlayed(CardInstance card, int playerId)
    {
        RegisterPlayedCard(GetPlayerById(playerId), card);
    }

    private void RegisterPlayedCard(PlayerController player, CardInstance card)
    {
        if (player == null) return;

        string cardId = GetCardId(card);
        if (string.IsNullOrEmpty(cardId)) return;

        GetOrCreatePlayerData(player).PlayedCards.Add(cardId);
    }

    private void ExportBattleResult(PlayerController winner)
    {
        if (GameRulesConfig.Instance != null && !GameRulesConfig.Instance.enableDeckbuilderBattleLogs) return;
        if (exported) return;
        exported = true;

        PlayerManager playerManager = PlayerManager.Instance;
        if (playerManager == null) return;

        PlayerBattleData deckA = GetOrCreatePlayerData(playerManager.player1);
        PlayerBattleData deckB = GetOrCreatePlayerData(playerManager.player2);
        string winnerLabel = winner == playerManager.player1 ? "A" : winner == playerManager.player2 ? "B" : "Draw";
        int turns = TurnManager.Instance != null ? TurnManager.Instance.turnCounter : 0;
        int firstId = TurnManager.Instance != null ? TurnManager.Instance.firstPlayerId : 0;
        string firstPlayerLabel = firstId == 1 ? "A" : firstId == 2 ? "B" : "unknown";

        string json = BuildJson(deckA, deckB, winnerLabel, turns, firstPlayerLabel);
        string exportDirectory = GetExportDirectory();

        string path = Path.Combine(exportDirectory, $"{battleId}.json");
        File.WriteAllText(path, json, Encoding.UTF8);
        Debug.Log($"[BattleResultExporter] Exported battle result: {path}");
    }

    private string GetExportDirectory()
    {
        string exportDirectory = Path.Combine(RuntimePaths.LogsRoot(), "Deckbuilder");
        Directory.CreateDirectory(exportDirectory);
        return exportDirectory;
    }

    private PlayerBattleData GetOrCreatePlayerData(PlayerController player)
    {
        if (playerData.TryGetValue(player, out PlayerBattleData data))
            return data;

        data = new PlayerBattleData();
        playerData[player] = data;
        return data;
    }

    private PlayerController GetPlayerById(int playerId)
    {
        PlayerManager playerManager = PlayerManager.Instance;
        if (playerManager == null) return null;
        return playerId == 1 ? playerManager.player1 : playerId == 2 ? playerManager.player2 : null;
    }

    private static string GetCardId(CardInstance card)
    {
        return card?.baseData != null ? card.baseData.cardId : null;
    }

    private string BuildJson(PlayerBattleData deckA, PlayerBattleData deckB, string winnerLabel, int turns, string firstPlayerLabel)
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("{");
        AppendProperty(sb, 1, "battle_id", battleId, comma: true);
        AppendProperty(sb, 1, "first_player", firstPlayerLabel, comma: true);
        AppendDeck(sb, 1, "deck_a", deckA, comma: true);
        AppendDeck(sb, 1, "deck_b", deckB, comma: true);
        AppendProperty(sb, 1, "winner", winnerLabel, comma: true);
        AppendNumberProperty(sb, 1, "turns", turns, comma: true);
        AppendArrayProperty(sb, 1, "drawn_cards_a", deckA.DrawnCards, comma: true);
        AppendArrayProperty(sb, 1, "played_cards_a", deckA.PlayedCards, comma: true);
        AppendArrayProperty(sb, 1, "drawn_cards_b", deckB.DrawnCards, comma: true);
        AppendArrayProperty(sb, 1, "played_cards_b", deckB.PlayedCards, comma: false);
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static void AppendDeck(StringBuilder sb, int indent, string name, PlayerBattleData data, bool comma)
    {
        AppendIndent(sb, indent);
        sb.Append('"').Append(name).AppendLine("\": {");
        AppendProperty(sb, indent + 1, "deck_id", data.DeckId, comma: true);
        AppendArrayProperty(sb, indent + 1, "energy_types", data.EnergyTypes, comma: true);
        AppendCardCounts(sb, indent + 1, "cards", data.Cards, comma: false);
        AppendIndent(sb, indent);
        sb.Append('}');
        if (comma) sb.Append(',');
        sb.AppendLine();
    }

    private static void AppendCardCounts(StringBuilder sb, int indent, string name, Dictionary<string, int> cards, bool comma)
    {
        AppendIndent(sb, indent);
        sb.Append('"').Append(name).AppendLine("\": {");

        int index = 0;
        foreach (KeyValuePair<string, int> card in cards.OrderBy(kv => kv.Key))
        {
            AppendIndent(sb, indent + 1);
            sb.Append('"').Append(Escape(card.Key)).Append("\": ").Append(card.Value);
            if (++index < cards.Count) sb.Append(',');
            sb.AppendLine();
        }

        AppendIndent(sb, indent);
        sb.Append('}');
        if (comma) sb.Append(',');
        sb.AppendLine();
    }

    private static void AppendArrayProperty(StringBuilder sb, int indent, string name, List<string> values, bool comma)
    {
        AppendIndent(sb, indent);
        sb.Append('"').Append(name).Append("\": [");

        for (int i = 0; i < values.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append('"').Append(Escape(values[i])).Append('"');
        }

        sb.Append(']');
        if (comma) sb.Append(',');
        sb.AppendLine();
    }

    private static void AppendProperty(StringBuilder sb, int indent, string name, string value, bool comma)
    {
        AppendIndent(sb, indent);
        sb.Append('"').Append(name).Append("\": \"").Append(Escape(value ?? string.Empty)).Append('"');
        if (comma) sb.Append(',');
        sb.AppendLine();
    }

    private static void AppendNumberProperty(StringBuilder sb, int indent, string name, int value, bool comma)
    {
        AppendIndent(sb, indent);
        sb.Append('"').Append(name).Append("\": ").Append(value);
        if (comma) sb.Append(',');
        sb.AppendLine();
    }

    private static void AppendIndent(StringBuilder sb, int indent)
    {
        sb.Append(' ', indent * 2);
    }

    private static string Escape(string value)
    {
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
