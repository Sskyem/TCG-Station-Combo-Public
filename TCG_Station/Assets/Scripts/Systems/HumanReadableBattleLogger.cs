using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

/// <summary>
/// Exports a human-readable plain-text match log to "Logs Export/Logs readable/".
/// Attach to the same GameObject as BattleResultExporter.
/// </summary>
public class HumanReadableBattleLogger : MonoBehaviour
{
    public static HumanReadableBattleLogger Instance { get; private set; }

    private string battleId;
    private string startTime;
    private bool exported;

    private readonly List<string> lines = new List<string>();
    private readonly List<string> currentTurnLines = new List<string>();
    private int currentTurnNumber;
    private string currentTurnPlayerName;

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;
    }

    private void OnEnable()
    {
        TurnManager.OnTurnStarted            -= OnTurnStarted;
        TurnManager.OnSetupComplete          -= OnSetupComplete;
        BattleManager.OnAttackExecuted       -= OnAttackExecuted;
        BattleManager.OnPokemonKnockedOut    -= OnPokemonKnockedOut;
        BattleManager.OnPokemonPromoted      -= OnPokemonPromoted;
        BattleManager.OnGameOver             -= OnGameOver;
        PlayerManager.OnPokemonPlayedToBoard -= OnPokemonPlayed;
        PlayerManager.OnPokemonEvolved       -= OnPokemonEvolved;
        PlayerManager.OnTrainerPlayed        -= OnTrainerPlayed;
        PlayerManager.OnPokemonRetreated     -= OnPokemonRetreated;
        PlayerManager.OnEnergyAttached       -= OnEnergyAttached;

        TurnManager.OnTurnStarted            += OnTurnStarted;
        TurnManager.OnSetupComplete          += OnSetupComplete;
        BattleManager.OnAttackExecuted       += OnAttackExecuted;
        BattleManager.OnPokemonKnockedOut    += OnPokemonKnockedOut;
        BattleManager.OnPokemonPromoted      += OnPokemonPromoted;
        BattleManager.OnGameOver             += OnGameOver;
        PlayerManager.OnPokemonPlayedToBoard += OnPokemonPlayed;
        PlayerManager.OnPokemonEvolved       += OnPokemonEvolved;
        PlayerManager.OnTrainerPlayed        += OnTrainerPlayed;
        PlayerManager.OnPokemonRetreated     += OnPokemonRetreated;
        PlayerManager.OnEnergyAttached       += OnEnergyAttached;
    }

    private void OnDisable()
    {
        TurnManager.OnTurnStarted            -= OnTurnStarted;
        TurnManager.OnSetupComplete          -= OnSetupComplete;
        BattleManager.OnAttackExecuted       -= OnAttackExecuted;
        BattleManager.OnPokemonKnockedOut    -= OnPokemonKnockedOut;
        BattleManager.OnPokemonPromoted      -= OnPokemonPromoted;
        BattleManager.OnGameOver             -= OnGameOver;
        PlayerManager.OnPokemonPlayedToBoard -= OnPokemonPlayed;
        PlayerManager.OnPokemonEvolved       -= OnPokemonEvolved;
        PlayerManager.OnTrainerPlayed        -= OnTrainerPlayed;
        PlayerManager.OnPokemonRetreated     -= OnPokemonRetreated;
        PlayerManager.OnEnergyAttached       -= OnEnergyAttached;
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    // ── Public API ────────────────────────────────────────────────────────

    public void BeginBattle(string id)
    {
        if (GameRulesConfig.Instance != null && !GameRulesConfig.Instance.enableReadableBattleLogs) return;

        battleId = id;
        startTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        exported = false;
        lines.Clear();
        currentTurnLines.Clear();
        currentTurnNumber = 0;
        currentTurnPlayerName = "";
    }

    // ── Event handlers ────────────────────────────────────────────────────

    private void OnSetupComplete()
    {
        PlayerManager pm = PlayerManager.Instance;
        if (pm == null) return;

        PlayerController p1 = pm.player1;
        PlayerController p2 = pm.player2;
        string d1 = GameRulesConfig.Instance?.player1DeckName ?? "?";
        string d2 = GameRulesConfig.Instance?.player2DeckName ?? "?";

        lines.Add(new string('=', 56));
        lines.Add($"  BATTLE  {battleId}");
        lines.Add($"  {startTime}");
        lines.Add(new string('=', 56));
        lines.Add($"P1  {p1.playerName}  [{BrainLabel(p1)}]  {d1}");
        lines.Add($"P2  {p2.playerName}  [{BrainLabel(p2)}]  {d2}");
        lines.Add("");
        lines.Add("--- SETUP ---");
        lines.Add($"  P1 active: {p1.activePokemon?.baseData?.cardName ?? "?"}");
        lines.Add($"  P2 active: {p2.activePokemon?.baseData?.cardName ?? "?"}");
        lines.Add("");
    }

    private void OnTurnStarted(int turnNumber, PlayerController player)
    {
        FlushCurrentTurn();
        currentTurnNumber = turnNumber;
        currentTurnPlayerName = $"{PlayerTag(player)} {player.playerName}";
        currentTurnLines.Clear();
    }

    private void OnPokemonPlayed(CardInstance card, string spotType, int playerId)
    {
        string spot = spotType == "Active" ? "active" : "bench";
        currentTurnLines.Add($"  [Play]    {card.baseData.cardName} -> {spot}");
    }

    private void OnPokemonEvolved(CardInstance evo, CardInstance old, PlayerController owner)
    {
        string oldName = old?.baseData?.cardName ?? "?";
        currentTurnLines.Add($"  [Evolve]  {oldName} -> {evo.baseData.cardName}");
    }

    private void OnTrainerPlayed(CardInstance card, int playerId)
    {
        currentTurnLines.Add($"  [Trainer] {card.baseData.cardName}");
    }

    private void OnEnergyAttached(CardInstance card, EnumPokemonType energyType)
    {
        string target = card?.baseData?.cardName ?? "?";
        currentTurnLines.Add($"  [Energy]  {energyType} -> {target}");
    }

    private void OnAttackExecuted(Pokemon attacker, PlayerController attackerOwner,
                                   Pokemon defender, string moveName, int damageDealt)
    {
        string tag = PlayerTag(attackerOwner);
        currentTurnLines.Add($"  [Attack]  {tag} {attacker.pokemonData.cardName}" +
                             $" ({moveName}) -> {defender.pokemonData.cardName}: {damageDealt} dmg");
    }

    private void OnPokemonKnockedOut(Pokemon pokemon, PlayerController owner)
    {
        PlayerManager pm = PlayerManager.Instance;
        PlayerController opponent = pm.player1 == owner ? pm.player2 : pm.player1;
        currentTurnLines.Add($"  [KO]      {PlayerTag(owner)} {pokemon.pokemonData.cardName} knocked out!" +
                             $" {PlayerTag(opponent)} scores {opponent.score}/{GameRulesConfig.Instance.pointsToWin}");
    }

    private void OnPokemonPromoted(CardInstance card, PlayerController owner)
    {
        currentTurnLines.Add($"  [Active]  {PlayerTag(owner)} {card.baseData.cardName} promoted to active");
    }

    private void OnPokemonRetreated(PlayerController owner, CardInstance retreating, CardInstance newActive)
    {
        currentTurnLines.Add($"  [Retreat] {PlayerTag(owner)} {retreating?.baseData?.cardName ?? "?"}" +
                             $" -> bench, {newActive?.baseData?.cardName ?? "?"} to active");
    }

    private void OnGameOver(PlayerController winner)
    {
        if (exported) return;
        exported = true;

        FlushCurrentTurn();

        PlayerManager pm = PlayerManager.Instance;
        int turns = TurnManager.Instance != null ? TurnManager.Instance.turnCounter : 0;
        string winnerTag = winner == null ? "DRAW" : (winner == pm.player1 ? "P1" : "P2");
        int p1score = pm.player1 != null ? pm.player1.score : 0;
        int p2score = pm.player2 != null ? pm.player2.score : 0;

        lines.Add("");
        lines.Add(new string('=', 56));
        lines.Add($"  RESULT");
        lines.Add(new string('=', 56));
        lines.Add(winner != null
            ? $"  Winner: {winnerTag} {winner.playerName}"
            : $"  Winner: DRAW");
        lines.Add($"  Score:  P1 {p1score} — P2 {p2score}");
        lines.Add($"  Turns:  {turns}");
        lines.Add(new string('=', 56));

        WriteFile();
    }

    // ── Internal ──────────────────────────────────────────────────────────

    private void FlushCurrentTurn()
    {
        if (currentTurnNumber == 0) return;

        lines.Add($"--- Turn {currentTurnNumber} · {currentTurnPlayerName} ---");
        if (currentTurnLines.Count == 0)
            lines.Add("  (no actions)");
        else
            lines.AddRange(currentTurnLines);
        lines.Add("");
    }

    private void WriteFile()
    {
        if (GameRulesConfig.Instance != null && !GameRulesConfig.Instance.enableReadableBattleLogs) return;

        string dir = Path.Combine(
            RuntimePaths.LogsRoot(),
            "Logs readable");
        Directory.CreateDirectory(dir);

        string path = Path.Combine(dir, $"{battleId}.txt");
        File.WriteAllText(path, string.Join(Environment.NewLine, lines), Encoding.UTF8);
        Debug.Log($"[HumanReadableBattleLogger] Exported: {path}");
    }

    private static string PlayerTag(PlayerController p)
    {
        if (p == null) return "??";
        PlayerManager pm = PlayerManager.Instance;
        return pm != null && p == pm.player1 ? "P1" : "P2";
    }

    private static string BrainLabel(PlayerController p)
    {
        if (p?.brain == null) return "?";
        string t = p.brain.GetType().Name;
        return t.Replace("Brain", "");
    }
}
