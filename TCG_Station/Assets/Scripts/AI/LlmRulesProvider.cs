using System.IO;
using UnityEngine;

public static class LlmRulesProvider
{
    private const string FallbackRules =
        "Jestes agentem grajacym w uproszczona gre karciana inspirowana Pokemon TCG Pocket.\n" +
        "Przestrzegaj tylko zasad podanych przez silnik gry.\n" +
        "Nie wymyslaj dodatkowych regul.\n" +
        "Manualny Retreat mozna wykonac tylko raz na ture; zamiany przez efekty kart typu SwapSelf nie zuzywaja tego limitu.\n" +
        "Jesli AttachEnergy jest legalne, nadal daj komus energie nawet gdy wszystkie Pokemony maja juz oplacone koszty atakow; dodatkowa energia pomaga w skalowaniu, Retreat, po EnergyDiscard i po KO.\n" +
        "EnergyRamp dodaje energie typu atakujacego Pokemona do jednego wlasnego Pokemona na lawce i nie uzywa Energy Zone.\n" +
        "EnergyDiscard usuwa losowa energie z celu. DiscardHand odrzuca losowe karty z reki wskazanego gracza.\n" +
        "Slow zwieksza koszt ataku o 1. Expose zwieksza otrzymywane obrazenia. Cleanse usuwa statusy, buffy i debuffy.\n" +
        "W setupie masz wybrac tylko jednego Basic Pokemona na Active Spot.\n" +
        "Odpowiedz ma zawierac krotkie uzasadnienie i linie WYBOR_ID: <id>.\n";

    public static string GetRulesText()
    {
        GameRulesConfig config = GameRulesConfig.Instance;
        EnumLlmProvider provider = config != null ? config.llmProvider : EnumLlmProvider.Gemini;
        return GetRulesText(provider);
    }

    public static string GetRulesText(EnumLlmProvider provider)
    {
        GameRulesConfig config = GameRulesConfig.Instance;
        if (config == null || !config.llmUseRulesFile)
            return FallbackRules;

        string fileName = ResolveFileName(provider);
        string path = Path.Combine(Application.streamingAssetsPath, fileName);
        if (!File.Exists(path))
        {
            Debug.LogWarning($"[LlmRulesProvider] Rules file not found: {path}. Using fallback.");
            return FallbackRules;
        }

        string rules = File.ReadAllText(path).Trim();
        return string.IsNullOrWhiteSpace(rules) ? FallbackRules : rules;
    }

    // The rules file is chosen purely by provider; if it is missing, GetRulesText falls back
    // to FallbackRules above. (There is no generic LLM_RULES.txt anymore — each provider has its own.)
    private static string ResolveFileName(EnumLlmProvider provider) => provider switch
    {
        EnumLlmProvider.Ollama => "LLM_RULES_Ollama.txt",
        EnumLlmProvider.OpenAI => "LLM_RULES_OpenAI.txt",
        _                      => "LLM_RULES_Gemini.txt",
    };
}
