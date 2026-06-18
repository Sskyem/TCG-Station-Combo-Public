using Newtonsoft.Json;
using Newtonsoft.Json.Converters;


[JsonConverter(typeof(StringEnumConverter))]
public enum EnumCardEffectType
{
    None,
    Heal,
    DrawCard,
    DealDamage,
    DiscardHand,
    ApplyStatus,
    SwitchActive,
    SearchDeck,
    AttachEnergyFromDiscardPile,
    ItemLock,

    BenchHeal,
    BenchDmg,
    DmgTakenRed,
    EnergyRamp,
    EnergyDiscard,
    Multiattack,
    Counterattack,
    SwapSelf,
    SwapEnemy,
    Psychic,
    PowerUp,
    LeechLife,
    Poison,
    Root,
    Paralyze,
    Expose,
    Slow,
    Asleep,
    Confuse,
    Burn,
    DebuffSelf,
    Cleanse,
}


[JsonConverter(typeof(StringEnumConverter))]
public enum EnumCardType
{
    Pokemon,
    Trainer,
}


[JsonConverter(typeof(StringEnumConverter))]
public enum EnumSpecialConditionType
{
    None,
    Burned,
    Paralyzed,
    Poisoned,
    Asleep,
    Confused
}


[JsonConverter(typeof(StringEnumConverter))]
public enum EnumTrainerSubType
{
    None,
    Item,
    Supporter,
    Tool,
    Stadium
}


[JsonConverter(typeof(StringEnumConverter))]
public enum EnumPokemonType // For InitializeEnergy() in Pokemon.cs
{
    None,
    Colorless,
    Fire,
    Water,
    Grass,
    Lightning,
    Fighting,
    Psychic,
    Dragon,
    Darkness,
    Metal,
}


[JsonConverter(typeof(StringEnumConverter))]
public enum EnumCardEffectTarget
{
    None,

    Self,
    Opponent,

    ActivePokemon,
    BenchPokemon,

    EnemyActivePokemon,
    EnemyBenchPokemon,

    AllAllies,
    AllOpponents,

    All,
    Any
}


[JsonConverter(typeof(StringEnumConverter))]
public enum EnumEnergySource
{
    None,
    EnergyZone,
    DiscardPile,
    Pokemon,
}




[JsonConverter(typeof(StringEnumConverter))]
public enum EnumScenes
{
    Board,
    Board2D,
    BoardScroll,
    BoardScrollTest
}



public enum EnumPlayerType
{
    Human,
    LLM,
    ML,
    Algorithm
}


// Numeric tuning profile for AlgorithmBrain. Standard = historical inline weights; the others are
// archetype-oriented weight sets sharing the same decision logic (see AlgorithmProfile).
public enum EnumAlgorithmProfile
{
    Standard,
    Ramp,
    TempoAggro,
    ControlStatus,
    HealStall,
    // Sentinel: pick a concrete profile by auto-detecting the deck's archetype (DeckArchetypeDetector).
    // Resolved to a concrete profile before play; never reaches the scoring logic.
    Auto
}


public enum EnumLlmProvider
{
    Gemini,
    Ollama,
    OpenAI
}

// ChatGPT models. Sequential full-turn mode, same as Gemini. The o-series / GPT-5 entries are
// reasoning models: they reject a custom temperature and spend output tokens on hidden reasoning
// (OpenAiApiClient omits temperature and keeps the token cap generous for them).
public enum EnumOpenAiModel
{
    Gpt4oMini = 0,  // gpt-4o-mini   - cheap + fast (best default for benchmarks)
    Gpt4o = 1,      // gpt-4o
    Gpt41Mini = 2,  // gpt-4.1-mini
    Gpt41 = 3,      // gpt-4.1
    Gpt5Mini = 4,   // gpt-5-mini    - reasoning
    Gpt5 = 5,       // gpt-5         - reasoning
    O4Mini = 6,     // o4-mini       - reasoning
}

public enum EnumGeminiModel
{
    Flash25,      // gemini-2.5-flash       — 5 RPM,  20 RPD
    Flash25Lite,  // gemini-2.5-flash-lite  — 30 RPM, 1500 RPD (best for benchmarks)
    Pro25,        // gemini-2.5-pro         — 5 RPM,  25 RPD
    Flash20,      // gemini-2.0-flash       — 0 RPM on free tier (may not work)
    Flash20Lite,  // gemini-2.0-flash-lite  — 30 RPM, 1500 RPD
    Flash31Lite,  // gemini-3.1-flash-lite  — 15 RPM, 500 RPD
    Flash35,      // gemini-3.5-flash       — 5 RPM,  20 RPD
    Flash15,      // gemini-1.5-flash       — 15 RPM, 1500 RPD (legacy)
    Flash30,      // gemini-3.0-flash       — 5 RPM,  20 RPD   (VERIFY API id against console)
    Gemma4_26b,   // gemma-4-26b-a4b-it     — 15 RPM, 1500 RPD (great for benchmarks)
    Gemma4_31b,   // gemma-4-31b-it         — 15 RPM, 1500 RPD (great for benchmarks)
}


public enum EnumOllamaModel
{
    Gemma3_12b = 0,
    Qwen3_8b = 1,
    Gemma4_12b_It_Q4_K_M = 2,
    Gemma4_E4b_It_Q4_K_M = 3,
}

public enum EnumOllamaEndpointPreset
{
    Localhost,
    RemotePreset1,
    RemotePreset2,
    Custom
}


public enum EnumMlServerPreset
{
    Localhost,
    RemotePreset1,
    RemotePreset2,
    Custom
}



public enum EnumplayerSide
{
    player,
    enemy
}

public enum EnumSelectionAction
{
    None,
    AttachEnergy,
    EnergyRamp,
    Retreat,
    EvolveOntoTarget,
    SwapSelf,
}
