/// <summary>
/// Prosta klasa danych reprezentująca jedną akcję w turze.
/// Używana przez LegalActionGenerator (co można zrobić) i GameActionExecutor (jak wykonać).
/// </summary>
public enum GameActionType
{
    PlayBasicPokemon,
    Evolve,
    AttachEnergy,
    Attack,
    Retreat,
    PlayTrainer,
    EndTurn
}

public class GameAction
{
    public GameActionType type;
    public CardInstance card;    // karta do zagrania: Basic, ewolucja, Trainer
    public CardInstance target;  // cel ewolucji LUB bench Pokemon dla Retreat LUB cel AttachEnergy
    public int attackIndex;      // który atak (domyślnie 0)
    public string label;         // optional display override used in prompts



    public static GameAction PlayBasic(CardInstance card) =>
        new GameAction { type = GameActionType.PlayBasicPokemon, card = card };

    public static GameAction Evolve(CardInstance evolutionCard, CardInstance evolveTarget) =>
        new GameAction { type = GameActionType.Evolve, card = evolutionCard, target = evolveTarget };

    public static GameAction AttachEnergy(CardInstance pokemonTarget) =>
        new GameAction { type = GameActionType.AttachEnergy, target = pokemonTarget };

    public static GameAction Attack(int index = 0) =>
        new GameAction { type = GameActionType.Attack, attackIndex = index };

    public static GameAction Retreat(CardInstance benchTarget) =>
        new GameAction { type = GameActionType.Retreat, target = benchTarget };

    public static GameAction PlayTrainer(CardInstance card) =>
        new GameAction { type = GameActionType.PlayTrainer, card = card };

    public static GameAction EndTurn() =>
        new GameAction { type = GameActionType.EndTurn };

    public override string ToString()
    {
        if (!string.IsNullOrEmpty(label)) return label;
        return type switch
        {
            GameActionType.PlayBasicPokemon => $"PlayBasic({card?.baseData?.cardName})",
            GameActionType.Evolve           => $"Evolve({card?.baseData?.cardName} onto {target?.baseData?.cardName}){DescribeEvolveUnlock()}",
            GameActionType.AttachEnergy     => $"AttachEnergy(to {target?.baseData?.cardName})",
            GameActionType.Attack           => $"Attack[{attackIndex}]",
            GameActionType.Retreat          => $"Retreat(to {target?.baseData?.cardName})",
            GameActionType.PlayTrainer      => $"PlayTrainer({card?.baseData?.cardName}{DescribeTrainerSubtype()})",
            GameActionType.EndTurn          => "EndTurn",
            _                               => type.ToString()
        };
    }

    private string DescribeTrainerSubtype()
    {
        return card?.baseData is TrainerData data
            ? $", {data.trainerSubType}"
            : "";
    }

    // Surfaces the strongest attack an evolution unlocks, inline on the Evolve action, so an LLM
    // sees the payoff without cross-referencing the "Can evolve into" hint block. Example:
    // "Evolve(Medicham onto Meditite) → unlocks Psykick[Fighting,Psychic]->90dmg, HP 150".
    private string DescribeEvolveUnlock()
    {
        if (card?.baseData is not PokemonData data || data.attacks == null || data.attacks.Count == 0)
            return "";

        AttackData best = null;
        foreach (var atk in data.attacks)
            if (best == null || atk.damage > best.damage) best = atk;
        if (best == null) return "";

        string cost = best.attackCost != null && best.attackCost.Count > 0
            ? string.Join(",", best.attackCost)
            : "FREE";
        return $" → unlocks {best.attackName}[{cost}]->{best.damage}dmg, HP {data.hp}";
    }
}
