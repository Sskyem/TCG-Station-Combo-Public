using System.Collections.Generic;
using Newtonsoft.Json;

public class GameStateSnapshot
{
    public int TurnNumber;
    public int ActivePlayerId;
    public PlayerSnapshot MyState;
    public PlayerSnapshot OpponentState;

    public static GameStateSnapshot Create(PlayerController me, PlayerController opponent, int turnNumber, int activePlayerId)
    {
        var pm = PlayerManager.Instance;
        var myZone       = pm?.GetEnergyZoneFor(me);
        var opponentZone = pm?.GetEnergyZoneFor(opponent);

        EnumPokemonType myEnergy       = myZone?.currentEnergy     ?? EnumPokemonType.None;
        EnumPokemonType myNextEnergy   = myZone?.nextEnergy         ?? EnumPokemonType.None;
        EnumPokemonType opponentEnergy = opponentZone?.currentEnergy ?? EnumPokemonType.None;

        return new GameStateSnapshot
        {
            TurnNumber = turnNumber,
            ActivePlayerId = activePlayerId,
            MyState = PlayerSnapshot.From(me, myEnergy, myNextEnergy, fullHand: true),
            OpponentState = PlayerSnapshot.From(opponent, opponentEnergy, EnumPokemonType.None, fullHand: false),
        };
    }
}

public class PlayerSnapshot
{
    public int PlayerId;
    public int Score;
    public PokemonSnapshot ActivePokemon;
    public List<PokemonSnapshot> Bench;
    public List<CardSnapshot> Hand;
    public int HandCount;
    public int DeckCount;
    public int DiscardCount;
    public EnumPokemonType AvailableEnergy;
    public EnumPokemonType NextEnergy;
    public bool CanAddEnergy;
    public bool UsedSupporterThisTurn;
    public int AttackDamageBonus;
    public int AttackCostChange;
    public int RetreatCostChange;

    // B1: which energy types the Energy Zone can roll this game (entries may repeat to encode weights).
    public List<EnumPokemonType> DeckEnergyPool;

    // Energy types reachable via EnergyRamp attacks on Pokemon the player currently holds or fields.
    // Ramp adds the ramping Pokemon's OWN type and bypasses the Energy Zone, so these may include
    // types absent from DeckEnergyPool (e.g. a Grass Tropius in a Fire-zone deck).
    [JsonIgnore] public List<EnumPokemonType> RampReachableEnergyTypes;

    public static PlayerSnapshot From(PlayerController p, EnumPokemonType availableEnergy, EnumPokemonType nextEnergy, bool fullHand)
    {
        var snap = new PlayerSnapshot
        {
            PlayerId = p.playerId,
            Score = p.score,
            ActivePokemon = p.activePokemon != null ? PokemonSnapshot.From(p.activePokemon, p.hand) : null,
            Bench = new List<PokemonSnapshot>(),
            Hand = new List<CardSnapshot>(),
            HandCount = p.hand.Count,
            DeckCount = p.deck.Count,
            DiscardCount = p.discardPile.Count,
            AvailableEnergy = availableEnergy,
            NextEnergy = nextEnergy,
            CanAddEnergy = p.canAddEnergy,
            UsedSupporterThisTurn = p.usedSupporterThisTurn,
            AttackDamageBonus = p.doMoreDamageToActive,
            AttackCostChange = p.attackEnergyCostChange,
            RetreatCostChange = p.retreatEnergyCostChange,
            DeckEnergyPool = p.deckEnergies != null
                ? new List<EnumPokemonType>(p.deckEnergies)
                : new List<EnumPokemonType>(),
        };

        foreach (var bench in p.benchPokemons)
            snap.Bench.Add(PokemonSnapshot.From(bench, p.hand));

        if (fullHand)
            foreach (var card in p.hand)
                snap.Hand.Add(CardSnapshot.From(card));

        snap.RampReachableEnergyTypes = CollectRampReachableEnergyTypes(p);

        return snap;
    }

    // Scans the player's Active, bench, and hand for Pokemon whose attacks include an EnergyRamp
    // effect. Each such Pokemon ramps energy of its OWN type, so its type is reachable on the bench
    // even when the Energy Zone (DeckEnergyPool) never rolls it.
    private static List<EnumPokemonType> CollectRampReachableEnergyTypes(PlayerController p)
    {
        var types = new List<EnumPokemonType>();

        void Consider(CardInstance card)
        {
            if (card?.baseData is not PokemonData pd || pd.attacks == null) return;
            EnumPokemonType type = pd.type;
            if (type == EnumPokemonType.None || types.Contains(type)) return;
            foreach (var atk in pd.attacks)
            {
                if (atk.effects == null) continue;
                foreach (var e in atk.effects)
                {
                    if (e.cardEffectType == EnumCardEffectType.EnergyRamp)
                    {
                        types.Add(type);
                        return;
                    }
                }
            }
        }

        Consider(p.activePokemon);
        foreach (var bench in p.benchPokemons) Consider(bench);
        foreach (var card in p.hand) Consider(card);

        return types;
    }
}

public class PokemonSnapshot
{
    public int InstanceId;
    public string Name;
    public int CurrentHp;
    public int MaxHp;
    public EnumPokemonType PokemonType;
    public string Stage;
    public int RetreatCost;
    public List<AttackSnapshot> Attacks;
    public Dictionary<EnumPokemonType, int> EnergyEquipped;
    public bool IsPoisoned;
    public bool IsBurned;
    public EnumSpecialConditionType SpecialCondition;
    public int PoisonDamageBetweenTurns;
    public int BurnDamageBetweenTurns;
    public bool HasToolEquipped;
    public bool CanEvolve;
    public int TurnPlacedOnBoard;
    public int AttackCostChange;

    // B2: evolved forms currently sitting in the owner's hand that could evolve this Pokemon.
    public List<EvolutionPreview> PossibleEvolutions;

    public static PokemonSnapshot From(CardInstance card, List<CardInstance> ownerHand = null)
    {
        var p = card.pokemonLogic;
        var data = p.pokemonData;

        var snap = new PokemonSnapshot
        {
            InstanceId = card.instanceId,
            Name = data.cardName,
            CurrentHp = p.currentHp,
            MaxHp = data.hp,
            PokemonType = data.type,
            Stage = StageLabel(data.stage),
            RetreatCost = data.retreatCost,
            Attacks = new List<AttackSnapshot>(),
            EnergyEquipped = new Dictionary<EnumPokemonType, int>(),
            IsPoisoned = p.isPoisoned,
            IsBurned = p.isBurned,
            SpecialCondition = p.otherSpecialCondition,
            PoisonDamageBetweenTurns = p.isPoisoned
                ? (p.tempBuffsData.takeMoreDamageFromPoison > 0
                    ? p.tempBuffsData.takeMoreDamageFromPoison
                    : GameRulesConfig.Instance.poisonDamagePerTurn)
                : 0,
            BurnDamageBetweenTurns = p.isBurned
                ? (p.tempBuffsData.takeMoreDamageFromBurn > 0
                    ? p.tempBuffsData.takeMoreDamageFromBurn
                    : GameRulesConfig.Instance.burnDamagePerTurn)
                : 0,
            HasToolEquipped = p.hasToolEquipped,
            CanEvolve = p.tempBuffsData.canEvolve,
            TurnPlacedOnBoard = p.turnPlacedOnBoard,
            AttackCostChange = p.tempBuffsData.attackEnergyCostChange,
            PossibleEvolutions = new List<EvolutionPreview>(),
        };

        if (data.attacks != null)
            foreach (var atk in data.attacks)
                snap.Attacks.Add(AttackSnapshot.From(atk));

        foreach (var kv in p.energyEquipped)
            if (kv.Value > 0)
                snap.EnergyEquipped[kv.Key] = kv.Value;

        if (ownerHand != null)
        {
            foreach (var handCard in ownerHand)
            {
                if (handCard?.baseData is PokemonData handPd &&
                    handPd.stage == data.stage + 1 &&
                    !string.IsNullOrEmpty(handPd.evolvesFrom) &&
                    string.Equals(handPd.evolvesFrom, data.cardName, System.StringComparison.OrdinalIgnoreCase))
                {
                    snap.PossibleEvolutions.Add(EvolutionPreview.From(handPd));
                }
            }
        }

        return snap;
    }

    // Returns true if this Pokémon has enough energy to use at least one attack.
    public bool CanAttack()
    {
        if (Attacks == null || EnergyEquipped == null) return false;
        foreach (var atk in Attacks)
        {
            var attackCost = atk.EnergyCost ?? new List<EnumPokemonType>();
            var remaining = new Dictionary<EnumPokemonType, int>(EnergyEquipped);
            // Dragon energy is a joker: it pays any typed slot, so set it aside and spend it flexibly.
            int jokers = remaining.TryGetValue(EnumPokemonType.Dragon, out int dragonCount) ? dragonCount : 0;
            remaining[EnumPokemonType.Dragon] = 0;

            bool canUse = true;
            int colorlessNeeded = 0;
            foreach (var required in attackCost)
            {
                if (required == EnumPokemonType.Colorless) { colorlessNeeded++; continue; }
                if (remaining.TryGetValue(required, out int have) && have > 0)
                    remaining[required]--;
                else if (jokers > 0)
                    jokers--;
                else { canUse = false; break; }
            }
            if (!canUse) continue;

            colorlessNeeded = System.Math.Max(0, colorlessNeeded + AttackCostChange);
            int leftover = jokers;
            foreach (var kv in remaining) leftover += kv.Value;
            if (leftover >= colorlessNeeded) return true;
        }
        return false;
    }

    static string StageLabel(int stage) => stage switch { 0 => "Basic", 1 => "Stage1", 2 => "Stage2", _ => "Unknown" };
}

public class EvolutionPreview
{
    public string Name;
    public string Stage;
    public int MaxHp;
    public int RetreatCost;
    public List<AttackSnapshot> Attacks;

    public static EvolutionPreview From(PokemonData pd)
    {
        var preview = new EvolutionPreview
        {
            Name = pd.cardName,
            Stage = pd.stage switch { 1 => "Stage1", 2 => "Stage2", _ => "Stage?" },
            MaxHp = pd.hp,
            RetreatCost = pd.retreatCost,
            Attacks = new List<AttackSnapshot>(),
        };
        if (pd.attacks != null)
            foreach (var atk in pd.attacks)
                preview.Attacks.Add(AttackSnapshot.From(atk));
        return preview;
    }
}

public class AttackSnapshot
{
    public string Name;
    public int Damage;
    // Human-readable prose for LLM prompts only; ML reads attack content from Cards/*.json, so kept out of the JSONL dataset.
    [JsonIgnore] public string Description;
    [JsonIgnore] public List<string> EffectSummaries;
    public List<EnumPokemonType> EnergyCost;
    public int EnergyDiscardCount;

    public static AttackSnapshot From(AttackData atk)
    {
        int discardCount = 0;
        var effectSummaries = new List<string>();
        if (atk.effects != null)
        {
            foreach (var e in atk.effects)
            {
                if (e.cardEffectType == EnumCardEffectType.EnergyDiscard)
                    discardCount += e.effectAmount;

                string summary = BuildEffectSummary(e);
                if (!string.IsNullOrWhiteSpace(summary))
                    effectSummaries.Add(summary);
            }
        }

        return new AttackSnapshot
        {
            Name = atk.attackName,
            Damage = atk.damage,
            Description = atk.attackDescription,
            EffectSummaries = effectSummaries,
            EnergyCost = new List<EnumPokemonType>(atk.attackCost),
            EnergyDiscardCount = discardCount,
        };
    }

    private static string BuildEffectSummary(EffectData effect)
    {
        string target = effect.cardEffectTarget.ToString();
        int amount = effect.effectAmount;

        return effect.cardEffectType switch
        {
            EnumCardEffectType.Heal => amount >= 0
                ? $"Heal {target} by {amount} HP"
                : $"Deal {-amount} effect damage to {target}",
            EnumCardEffectType.BenchHeal => amount >= 0
                ? $"Heal each {target} bench Pokemon by {amount} HP"
                : $"Deal {-amount} effect damage to each {target} bench Pokemon",
            EnumCardEffectType.DealDamage => $"Deal {amount} effect damage to {target}",
            EnumCardEffectType.BenchDmg => $"Deal {amount} effect damage to each {target} bench Pokemon",
            EnumCardEffectType.DmgTakenRed => $"Reduce attack damage taken by {target} by {System.Math.Abs(amount)}",
            EnumCardEffectType.EnergyRamp => $"EnergyRamp (bonus, skipped if no bench): attach {amount} energy of the ATTACKING Pokemon's type to one of your Benched Pokemon; does not consume the Energy Zone and does NOT remove energy from the attacker",
            EnumCardEffectType.EnergyDiscard => $"Discard {amount} random energy from {target}",
            EnumCardEffectType.DrawCard => $"Draw {amount} card(s)",
            EnumCardEffectType.DiscardHand => amount >= 100
                ? $"Discard all cards from {target} hand"
                : $"Discard {amount} random card(s) from {target} hand",
            EnumCardEffectType.Multiattack => $"Hit {amount} total time(s)",
            EnumCardEffectType.Counterattack => $"Set counterattack for {amount} damage",
            EnumCardEffectType.SwapSelf => "Swap your Active Pokemon with your best Benched Pokemon",
            EnumCardEffectType.SwapEnemy => "Swap opponent Active Pokemon with a random opponent Benched Pokemon",
            EnumCardEffectType.Psychic => $"Add {amount} damage for each energy on opponent Active Pokemon",
            EnumCardEffectType.PowerUp => $"Add {amount} damage for each energy on your attacking Pokemon",
            EnumCardEffectType.LeechLife => "Heal your attacking Pokemon by damage dealt to opponent Pokemon",
            EnumCardEffectType.Poison => amount > GameRulesConfig.Instance.poisonDamagePerTurn
                ? $"Severe Poison {target}: takes {amount} damage between turns"
                : $"Poison {target}: takes {(amount > 0 ? amount : GameRulesConfig.Instance.poisonDamagePerTurn)} damage between turns",
            EnumCardEffectType.Root => $"Root {target}: cannot retreat",
            EnumCardEffectType.Paralyze => $"Paralyze {target}: cannot attack or retreat next turn",
            EnumCardEffectType.Expose => $"Expose {target}: takes {System.Math.Abs(amount)} more attack damage",
            EnumCardEffectType.Slow => $"Slow {target}: attack costs +1 energy",
            EnumCardEffectType.Asleep => $"Asleep {target}: cannot attack or retreat",
            EnumCardEffectType.Confuse => $"Confuse {target}",
            EnumCardEffectType.Burn => $"Burn {target}",
            EnumCardEffectType.DebuffSelf => amount switch
            {
                0 => "Apply Poison, Burn, Root, and Slow to opponent Active Pokemon",
                1 => "Apply Poison, Burn, Root, and Slow to both Active Pokemon",
                2 => "Apply Poison, Burn, Root, and Slow to your Active Pokemon",
                _ => "Apply combined Poison, Burn, Root, and Slow debuffs",
            },
            EnumCardEffectType.Cleanse => $"Remove buffs, debuffs, and statuses from {target}",
            _ => null,
        };
    }
}

public class CardSnapshot
{
    public int InstanceId;
    public string Name;
    public string CardType;
    // Trainer subtype (Supporter/Item/Tool) when CardType is Trainer; "None" otherwise. Surfaced so the
    // LLM can respect the one-Supporter-per-turn rule instead of planning several Supporters at once.
    public string TrainerSubType;
    // Human-readable prose for LLM prompts only; ML resolves card content from Cards/*.json via Name, so kept out of the JSONL dataset.
    [JsonIgnore] public string Description; // trainer effect or pokemon summary

    public static CardSnapshot From(CardInstance card)
    {
        string desc = null;
        string trainerSubType = "None";
        if (card.baseData is TrainerData td)
        {
            desc = td.effectDescription;
            trainerSubType = td.trainerSubType.ToString();
        }
        else if (card.baseData is PokemonData pd)
        {
            string stage = pd.stage == 0 ? "Basic" : pd.stage == 1 ? "Stage1" : "Stage2";
            string attacks = "";
            if (pd.attacks != null && pd.attacks.Count > 0)
            {
                var parts = new System.Collections.Generic.List<string>();
                foreach (var atk in pd.attacks)
                {
                    string cost = atk.attackCost != null && atk.attackCost.Count > 0
                        ? string.Join(",", atk.attackCost)
                        : "0";
                    string atkDesc = !string.IsNullOrWhiteSpace(atk.attackDescription)
                        ? $" | {atk.attackDescription}"
                        : "";
                    AttackSnapshot atkSnapshot = AttackSnapshot.From(atk);
                    string effectSummary = atkSnapshot.EffectSummaries != null && atkSnapshot.EffectSummaries.Count > 0
                        ? $" | Effects: {string.Join("; ", atkSnapshot.EffectSummaries)}"
                        : "";
                    parts.Add($"{atk.attackName}[{cost}]→{atk.damage}dmg{atkDesc}{effectSummary}");
                }
                attacks = " | Ataki: " + string.Join("; ", parts);
            }
            desc = $"{stage} HP:{pd.hp} Typ:{pd.type}{attacks}";
        }

        return new CardSnapshot
        {
            InstanceId = card.instanceId,
            Name = card.baseData.cardName,
            CardType = card.baseData.cardType.ToString(),
            TrainerSubType = trainerSubType,
            Description = desc,
        };
    }
}
