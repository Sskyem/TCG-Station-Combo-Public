# TCG Station — AI Benchmark in a Digital Card Game

> Master's thesis project — benchmarking AI agents (Algorithm, LLM, ML) in a Pokémon TCG Pocket-inspired card game built in Unity.

---

## What is this?

**TCG Station** is a local digital card game developed in Unity as a master's thesis. The core goal is to compare three types of AI agents playing against each other in a controlled, information-incomplete environment — similar to Pokémon TCG Pocket.

| Agent | Status | Description |
|---|---|---|
| `HumanBrain` | ✅ Done | Full UI interaction |
| `AlgorithmBrain` | ✅ Done | Rule-based heuristic player with a full turn loop |
| `LLMBrain` | 🔧 In progress | Gemini, OpenAI, or local Ollama provider, setup and full-turn action selection implemented; decision quality still needs benchmark testing |
| `MLBrain` | 🔧 In progress | Behavioral-cloning agent that queries the Python inference server (FastAPI `/predict`); per-category action selection implemented as a hybrid with the heuristic, decision quality depends on the trained model |

Planned benchmark matchups: `Algorithm vs Algorithm` → `LLM vs Algorithm` → `ML vs Algorithm` → `ML vs LLM`

---

## How to Run

This covers the **packaged build** (the game `.exe` / `.app`) and the bundled **ML pipeline** (`ML Pipeline/`). (To work with the Unity source instead, open the `TCG_Station/` project from the GitHub repo in Unity **6000.3.6f1**.)

### Requirements

- **Windows or macOS** — to run the game build.
- **Python 3** (3.9+) — only for the `ML Pipeline/` folder.
- *(optional)* **Ollama** running locally — for the local LLM provider. *(optional)* a **Gemini API key** or **OpenAI API key** — for hosted LLM providers (see "LLM setup" below). Human, Algorithm, and ML players need neither.

### 1. Run the game

Launch the game executable — it is standalone. `Logs Export/` is created in the build root at runtime: next to the `.exe` on Windows and next to the `.app` bundle on macOS.

The editable config files (`GameRulesConfig.json`, `BenchmarkConfig.json`) ship **next to the game**, copied into the build root automatically after each build (`CopyGameDataPostBuild`):

- **Windows:** next to `TCG Station.exe` (the build root).
- **macOS:** next to the `.app` bundle (the build root), **not** inside the package.

At runtime the build reads them from that folder (`RuntimePaths.ConfigPath`); the copies under `Assets/StreamingAssets/` are only the source-controlled defaults used inside the Unity Editor.

For machine-specific URLs, copy `GameRulesConfig.local.example.json` to
`GameRulesConfig.local.json` next to the project/build and edit only the local copy. It is
ignored by Git and merged over `GameRulesConfig.json` at runtime. Use `Custom` for the matching
preset when supplying a private ML or Ollama URL.

**Two ways to configure — in-game menu vs. JSON.** On launch the build shows a startup menu (`StartupMenuController`, in the `Initialization` scene) that covers the most common settings: play mode (*Simulation* / *Watch AI vs AI* / *Human vs AI*), each side's deck, AI types, `AlgorithmBrain` profiles, matches per pairing (Simulation), log upload, and an *Advanced* section for `pointsToWin`, `maxTurns`, `benchSize`, AI speed, and stop-on-finish. Pressing **Start** writes the choices back to `GameRulesConfig.json` + `BenchmarkConfig.json` and launches the match — so the menu is just a front-end over the same files. The menu **reloads the JSON before authoring** (`GameRulesConfig.ReloadFromJson()` on `Show()`), so fields it doesn't expose are read from the file and written back unchanged rather than clobbered with Inspector defaults — even when the Initialization scene has `loadFromJson = false`. Everything more advanced (LLM provider/model per player, `ollamaBaseUrl`, `mlServerUrl`, Gemini/OpenAI temperature and token limits, rules-file toggle, individual logger toggles, telemetry) is **not** exposed in the menu and is edited directly in `GameRulesConfig.json` (and `BenchmarkConfig.json` for per-participant benchmark setup). Headless mode (`headlessMode`, no scene visuals — faster benchmark) can be set in the JSON or via the menu's optional headless toggle in *Simulation* mode; `Application.isBatchMode` (launching the build with `-batchmode`) also forces it.

#### Settings precedence (why a setting sometimes doesn't apply)

The same setting can live in the JSON, the in-game menu, and the Unity Inspector. The menu is **not** a separate tier — it just writes the JSON on **Start** (`StartupMenuController.SaveToJson`). What actually wins, highest first:

1. **Runtime overrides** — the benchmark (`ConfigureNextMatch`) and `Auto` profile detection set player types, decks, and Algorithm profiles in `GameManager.StartGame`, regardless of the JSON.
2. **Local JSON override** (`GameRulesConfig.local.json`) — optional machine-specific values merged over the main JSON and ignored by Git.
3. **Presets** — `mlServerPreset` / `ollamaEndpointPreset`: unless set to `Custom`, they overwrite `mlServerUrl` / `ollamaBaseUrl` with localhost. Set the preset to `Custom` to use a URL from the local override.
4. **JSON** (`GameRulesConfig.json`) — wins over the Inspector **when `loadFromJson = true`** (the default). The build reads it from next to the executable at runtime, **not** from `Assets/StreamingAssets/` (that copy only matters in the Editor).
5. **Unity Inspector** — used only for keys missing from the JSON, **or** for every field when `loadFromJson = false` (then the JSON is ignored entirely). To avoid confusion, the custom Inspectors hide the JSON-overridden fields in a collapsed "Inactive" foldout while `loadFromJson` is ON.

The benchmark master switch behaves differently: `BenchmarkRunner.runEnabled` in the **Inspector** is a hard gate checked *before* the JSON loads — if it's OFF, no `BenchmarkConfig.json` can turn the benchmark on; if it's ON, the JSON can still turn it back off and override the rest.

Common reasons a change "doesn't apply": editing the `Assets/StreamingAssets/` copy instead of the one next to the build; `loadFromJson = false` (Inspector wins); or a non-`Custom` preset reverting a custom URL. (The startup menu used to overwrite hand-edited JSON fields it doesn't expose on Start; it now reloads the file first, so those fields are preserved.)

Choose who plays each side in **`GameRulesConfig.json`** there (or in the startup menu):
```json
"player1Type": "Human",        // Human | Algorithm | LLM | ML
"player2Type": "Algorithm"
```
- **Human vs Algorithm** — play manually against the heuristic bot.
- **Algorithm vs Algorithm** — watch a full AI match.
- `ML` needs the inference server running (section 3); `LLM` needs a provider (LLM setup below).

### 2. AI-vs-AI benchmark (batch matches)

To run many AI matches back-to-back, edit **`BenchmarkConfig.json`** in the same build-root folder next to the game (see paths above): set `"runEnabled": true` and list the `participants` (each `{ "deck", "profile" }`; `profile: "Auto"` detects the archetype). On launch the matches run round-robin and write logs to `Logs Export/ML/` (`games.jsonl` + per-game decision files).

### 3. ML pipeline (training + `/predict` server + dashboard)

Everything is driven from a web dashboard at **`http://localhost:8000`**.

- **One-click start:** from the sibling `ML Pipeline/` folder, run **`start_dashboard_WINDOWS.cmd`** (Windows) or **`start_dashboard_MACOS.sh`** (macOS/Linux). It creates a `.venv`, installs the minimal dependencies, starts the API, and opens the dashboard. From there you install full ML deps, validate the dataset, train, fine-tune, evaluate, and pick the active model — no manual commands needed.
- The game's `ML` player and the in-scene advisor buttons call this server at the `mlServerUrl` in `GameRulesConfig.json` (default `http://127.0.0.1:8000/predict`), so start it **before** running an `ML` player.
- Full details live in [`../ML Pipeline/README.md`](../ML%20Pipeline/README.md). For build logs, either set `TCG_BUILD_ROOT` to the folder containing the build or set `TCG_LOGS_DIR` directly to `Logs Export/ML`.

### 4. LLM setup (only for the `LLM` player or the LLM advisor)

- **Gemini:** put your key in a file named **`GEMINI_API_KEY.txt`** in the build folder, next to the game executable (it is read from the process working directory at runtime). It is **not** baked into the build; without it, Human/Algorithm/Ollama/ML still work.
- **OpenAI:** put your key in a file named **`OPENAI_API_KEY.txt`** in the build folder, next to the game executable. The key is read at runtime by `OpenAiApiClient` and is not baked into the build.
- **Ollama (local):** run Ollama and pull a model (e.g. `ollama pull gemma3:12b` or `qwen3:8b`); point `ollamaBaseUrl` in `GameRulesConfig.json` at it (default `http://127.0.0.1:11434/api/chat`).
- Provider and model are selected per player via `player1LlmProvider` / `player2LlmProvider` (and the matching `*GeminiModel` / `*OllamaModel` / `*OpenAiModel`) in `GameRulesConfig.json`.

### Planned LLM telemetry dashboard

The current stable branch logs LLM decisions and prompt/response text. A follow-up branch extends this into a fuller telemetry layer for LLM experiments. Planned scope:

- Per-call provider/model metrics: latency, retry count, fallback use, and token counts when the provider returns them.
- A dashboard view for comparing LLM providers and models across games, not only inspecting individual prompt files.
- Cost/quality analysis for benchmark runs: connect per-call telemetry with match outcomes, turn count, selected actions, and parsed `THINKING`.
- Better support for provider comparison experiments such as Gemini vs OpenAI vs local Ollama models.

This is intentionally documented as future work on `main`; it is not required for the stable build or for Algorithm/ML benchmarks.

---

## Game Rules

The project is inspired by Pokémon TCG Pocket, with a configurable benchmark format implemented in Unity:

- **Deck:** configurable through `GameRulesConfig`; current `Assets/StreamingAssets/GameRulesConfig.json` uses `deckSize = 30`
- **Starting hand:** configurable through `GameRulesConfig`; current game scene uses `starterHandSize = 7` and setup guarantees at least 1 Basic Pokémon
- **Win condition:** 4 KO points by default (`GameRulesConfig`), configurable
- **Turn limit:** configurable through `GameRulesConfig`; current game scene uses `maxTurns = 45`
- **Turn structure:** between-turn effects → switch active player → start-of-turn effects → draw a card → refresh Energy Zone → play cards / attach energy / attack / end turn
- **Opening turn:** the active player draws a card on every turn, including the first
- **Bench:** configurable through `GameRulesConfig`; current game scene uses `benchSize = 4`
- **Active:** 1 Pokémon — must always be filled; if the Active is knocked out, player promotes from bench

### Pokémon Zones

- **Deck** — the player's draw pile.
- **Hand** — cards currently available to play.
- **Discard** — cards that have been used, knocked out, or otherwise removed from play.
- **Energy Zone** — shows the current energy available this turn and the next upcoming energy.
- **Active** — the Pokémon currently battling and using attacks.
- **Bench** — configurable number of supporting Pokémon that can be evolved, receive energy, or be promoted to Active.

### Evolution

- Basic (Stage 0) → Stage 1 → Stage 2
- Cannot evolve a Pokémon on the same turn it was played
- Evolution preserves the HP difference and attached energy, but it does **not** preserve status effects or temporary buffs/debuffs. In the current simplified implementation the evolved Pokémon starts without Poison, Burn, Paralyze, Asleep, Confuse, Root, Slow, Expose, or similar temporary effects
- `evolvesFrom` field in JSON defines the evolution chain (e.g. Gastly → Haunter → Gengar)

### Energy

- Each turn the active player refreshes their Energy Zone
- Energy zone shows current (this turn) and next (upcoming) energy type
- One energy can be attached per turn to any Pokémon (Active or Bench)
- Some effects can create and attach energy directly, such as `EnergyRamp`
- Attack costs are validated per energy color (`Fire`, `Water`, `Colorless`, etc.)
- **`Dragon` energy is a joker:** a single Dragon energy attached to a Pokémon pays *any* cost slot — typed or `Colorless` (unlike normal typed energy, which only pays its own color, and unlike `Colorless` energy, which only pays `Colorless` slots). This is honored consistently by `CardActions.CanAffordAttack`, `LegalActionGenerator`, `GameStateSnapshot.CanAttack`, and the `AlgorithmBrain` energy-deficit calculators (single source of truth: `CardActions.IsWildcardEnergy`)

### Combat

- `PlayerManager.TryAttack(attackIndex)` is the entry point for attack actions and supports any attack slot
- `AlgorithmBrain` selects among a Pokémon's attacks with a deterministic scorer (`ChooseBestAttackIndex`); the legal-action system exposes each attack as a separate `Attack[i]` action for the LLM
- `CanAffordAttack()` validates energy cost (including the `Dragon` joker) before execution
- `ExecuteAttack()` deals damage and resolves card effects
- Ending the turn is handled by the caller (`AlgorithmBrain` or UI flow), not inside `ExecuteAttack()`
- Knockouts award 1 point; reaching `pointsToWin` ends the game

### Status Effects

| Status | Effect |
|---|---|
| Poison | Takes damage each turn |
| Burn | Takes damage each turn |
| Paralyze | Cannot attack or retreat during the affected Pokémon owner's next turn; it recovers at that turn's end unless removed earlier |
| Asleep | Cannot attack or retreat; coin flip to wake up |
| Confuse | Coin flip on attack; failure hits self |
| Root | Cannot retreat |
| Slow | Attack energy cost increased by 1 |

### Retreat

- Pay the Pokémon's retreat cost in energy to swap Active with a Bench Pokémon
- The Pokémon moved to the Bench loses all buffs, debuffs, and status effects
- Some card effects allow free swaps (`FreeSwapActive`, `SwapSelf`, `SwapEnemy`)
- Free swaps to the Bench also clear all buffs, debuffs, and status effects from the old Active Pokémon

---

## Architecture

The codebase is split into five layers:

| Layer | Classes | Responsibility |
|---|---|---|
| `CORE` | `PlayerManager`, `TurnManager`, `BattleManager`, `PlayerController`, `CardInstance`, `Pokemon` | Game state, action validation, turn flow, damage, KO |
| `AI` | `PlayerBrain`, `HumanBrain`, `AlgorithmBrain`, `LLMBrain`, `ILLMClient`, `GeminiApiClient`, `OpenAiApiClient`, `OllamaApiClient`, `LlmPromptBuilder`, `LlmRulesProvider` | Player decision making and LLM transport |
| `UI` | `BoardVisualizer`, `VisualCard`, `Attack`, `Energy`, `EnergyZone`, `Retreat` | Rendering and user input |
| `DATA` | `CardData`, `PokemonData`, `TrainerData`, `AttackData`, `EffectData`, `DeckData` | JSON-loaded definitions |
| `SYSTEMS` | `GameResultLogger`, `DecisionLogger`, `MatchupStatsLogger`, `HumanReadableBattleLogger`, `BattleResultExporter`, `LogUploader`, `GameRulesConfig` | Benchmark logging, data export, telemetry, configuration |

**Single Source of Truth:** Game state lives in `PlayerController`, `CardInstance`, `Pokemon`, `TurnManager`, and `BattleManager`. The view (`VisualCard`, `BoardVisualizer`) only renders — it holds no game logic.

### Game Boot Sequence

```
GameManager.Start()
  → FolderCreator.CreateFolders()
  → JsonLoader.LoadCards()
  → JsonLoader.LoadDecks()
  → StartGame()
      → BoardVisualizer.StartBoardVisualizer()
      → PlayerManager.StartPlayerManager()
      → TurnManager.StartTurnManager()
```

### Turn Loop

```
TurnManager.ChangeTurn()
  1. BattleManager.ProcessBetweenTurns()   — status ticks (Poison, Burn, etc.)
  2. BattleManager.ProcessEndOfTurn()      — end-of-owner-turn recovery, e.g. Paralyze
  3. Switch active player
  4. BattleManager.ProcessStartOfTurn()    — status restrictions, Sleep coin flip, buff reset
  5. Draw 1 card (from turn 2 onward)
  6. Refresh Energy Zone
  7. brain.PerformTurn()                   — AI or human acts
```

### Event Flow (Logic → UI)

```
VisualCard clicked
  → CardInputEvents.RaiseCardClicked(cardInstance)
  → PlayerManager.HandleCardClicked(cardInstance)
  → game logic executes
  → domain events fired (OnPokemonPlayedToBoard, OnPokemonHpChanged, etc.)
  → BoardVisualizer updates visuals
```

Key events emitted by the logic layer:

- `OnPokemonPlayedToBoard`, `OnPokemonEvolved`, `OnPokemonRetreated`, `OnPokemonEnergyChanged` — from `PlayerManager`
- `OnPokemonKnockedOut`, `OnGameOver`, `OnPokemonHpChanged`, `OnPokemonStatusChanged` — from `BattleManager`
- `OnCardDrewFromEffect` — from `CardActions`

---

## AI Agents

All agents inherit from `PlayerBrain` and implement:
- `PerformSetupPhase()` — choose starting Pokémon
- `PerformTurn()` — play a full turn

### `HumanBrain`
Waits for UI clicks. No automatic actions.

### `AlgorithmBrain`
- **Setup:** waits 1s, picks a Basic Pokémon with the cheapest attack, with ramp support preferred when the deck needs it
- **PerformTurn:** full sequential heuristic:
  1. Play all Basic Pokémon from hand (active slot first, then bench)
  2. Play evolutions for any eligible Pokémon on board
  3. Play Trainers with filters for wasted recovery cards, cleanse, and damage reduction
  4. Attach energy using deterministic priorities: maintain strong `EnergyDiscard` attackers, charge `PowerUp`/`Psychic` evolution lines to KO the opponent's highest current HP + 2 spare energy, then complete normal attack costs. Energy that doesn't fit the current form's attack but fits a future evolution in the line (it carries over on evolution) is still banked rather than wasted — the Energy Zone only ever produces deck-relevant energy
  5. Retreat only when it improves combat output or avoids a likely KO (a retreat that swaps into an equally useless Pokémon is not rewarded)
  6. Attack with a deterministic scorer over ready attacks: immediate damage and KO first, then non-damage payoff — damage-over-time (`Poison`, `Burn`), enemy disables (`Paralyze`, `Asleep`, `Confuse`, `Root`), `Expose`/`Slow`/enemy `EnergyDiscard`, and self riders (`Counterattack`, `LeechLife`). This lets status-based decks (e.g. poison/counter) value their 0-damage wincon attacks instead of skipping them
  7. End the turn through `TurnManager.RequestEndTurn()`

#### Weight profiles (`AlgorithmProfile`)

`AlgorithmBrain` runs a single decision logic; a **profile only swaps the numbers it scores with — never the control flow** (`Assets/Scripts/AI/AlgorithmProfile.cs`). The point is that one fixed set of weights extracts a different fraction of each archetype's potential, which would bias an AI-vs-AI benchmark toward "how well this one bot plays a given archetype" instead of "how strong the deck is". Each profile starts from `Standard` and overrides only the knobs relevant to its strategy; untouched weights stay identical to `Standard`.

| Profile | Emphasis (deltas from `Standard`) |
|---|---|
| `Standard` | The historical baseline weights. Every other profile is a delta from it. |
| `Ramp` | Patient setup: stronger energy-engine bonuses, bigger bench stockpiling, more scaling headroom (`scalingEnergyBuffer` 2→3), tolerates 0-damage ramp turns. |
| `TempoAggro` | Active readiness + immediate damage first (`activeBecomesReadyBonus` 170→210, `activeImmediateBonus` 10→25); cheaper to retreat into a ready attacker; less stockpiling. |
| `ControlStatus` | Values disruption (`Paralyze` 40→70, poison/burn ceilings raised, `Root` 15→35) and enemy energy denial (`energyDiscardPerEnergy` 20→30). |
| `HealStall` | Weights HP/survival (`strengthHpWeight` 10→40), rewards heal/leech/damage-reduction riders, tolerates non-lethal turns. |

**Selecting a profile (per seat):** `player1AlgorithmProfile` / `player2AlgorithmProfile` in `GameRulesConfig.json` (or the startup menu). Ignored for non-`Algorithm` seats. Set a seat to `Auto` to have the archetype detected from the deck.

**Auto-detection (`DeckArchetypeDetector`)** is a transparent heuristic (no ML) that counts weighted signals across the deck's printed cards, by card copies:
- *Ramp* — attacks with `EnergyRamp`, plus heavy finishers (attack cost ≥ 4)
- *ControlStatus* — status-inflicting attacks (`Poison`/`Burn`/`Paralyze`/`Asleep`/`Confuse`/`Root`/`Slow`/`Expose`) or enemy `EnergyDiscard`
- *HealStall* — printed `Heal`/`BenchHeal`/`LeechLife`/`DmgTakenRed` on Pokémon attacks (trainer healing counts only as support, since any deck can run it)
- *TempoAggro* — cheap attackers (cost ≤ 2, damage ≥ 30) on a low average energy curve

It maps the strongest signal to a profile, falling back to `Standard` when none is clear. The pick and the raw signal counts are logged (`[DeckArchetypeDetector] '<deck>' → <Profile> (ramp=…, control=…, …)`) so the choice is inspectable. `Auto` is resolved once in `GameManager.StartGame`.

### `LLMBrain`
- **Setup:** builds a structured prompt with Basic Pokémon in hand, sends it through the selected LLM provider, parses `WYBOR_ID` from response, fallback to first Basic if parsing fails
- **Providers:** selected per player by `GameRulesConfig.player1LlmProvider` / `player2LlmProvider`; fallback `llmProvider` is used only when per-player keys are absent
- **Local rules context:** `LlmRulesProvider` loads provider-specific rules from `Assets/StreamingAssets/LLM_RULES_Gemini.txt`, `LLM_RULES_OpenAI.txt`, or `LLM_RULES_Ollama.txt`; if rules files are disabled, a short built-in fallback is used
- **Thinking UI:** responses include `THINKING: ...`; `LLMBrain` displays parsed thinking in `BoardVisualizer.llmThinkingLog` with the current player/provider/model header
- **Gemini/OpenAI turn mode:** one prompt per turn, legal actions generated with sequence planning, response format `ACTION_SEQUENCE: i, j, k`
- **Ollama turn mode:** step-by-step local loop, one prompt per action, response format `ACTION_INDEX: <number>` after every refreshed state
- **Prompt logs:** LLM prompts and responses are written to `Logs Export/LLM Prompts/llm_<player>_<timestamp>.txt`
- **Decision logs:** `LLMLogger` writes one JSONL line per completed LLM turn to `Logs Export/ML/llm_decisions.jsonl` (provider, model, mode, chosen actions, parsed `THINKING`, legal-action count), so LLM turns line up with `AlgorithmBrain` decisions in the same `game_id`. Guarded by `enableLlmDecisionLogs`
- Planned: provider/model benchmark comparison (`Gemini`, `OpenAI`, `Gemma3_12b`, `Qwen3_8b`)

### LLM Configuration

LLM settings are read from `Assets/StreamingAssets/GameRulesConfig.json` when `GameRulesConfig.loadFromJson` is enabled in the Unity Inspector.

Prefer enum names over numeric enum values in JSON:

```json
"player1Type": "Algorithm",
"player2Type": "LLM",

"llmProvider": "Gemini",
"geminiModel": "Flash25Lite",
"ollamaModel": "Gemma3_12b",
"openAiModel": "Gpt4oMini",

"player1LlmProvider": "Gemini",
"player1GeminiModel": "Flash25Lite",
"player1OllamaModel": "Gemma3_12b",
"player1OpenAiModel": "Gpt4oMini",

"player2LlmProvider": "Ollama",
"player2GeminiModel": "Flash25Lite",
"player2OllamaModel": "Qwen3_8b",
"player2OpenAiModel": "Gpt4oMini",

"ollamaBaseUrl": "http://127.0.0.1:11434/api/chat",
"llmUseRulesFile": true,

"geminiTemperature": 0.2,
"geminiMaxOutputTokens": 2048,
"openAiTemperature": 0.2,
"openAiMaxOutputTokens": 2048,

"llmAutoDelay": true,
"llmTurnDelay": 0.0
```

The legacy fallback fields (`llmProvider`, `geminiModel`, `ollamaModel`, `openAiModel`) are kept for compatibility; full per-player configuration should use `player1*` and `player2*` keys.

#### Allowed config values

Enum values in `GameRulesConfig.json` must use one of the exact, case-sensitive names below. Prefer these names instead of numeric values, because numbers depend on the enum ordering in the source code.

| Config field(s) | Allowed values |
|---|---|
| `player1Type`, `player2Type` | `Human`, `LLM`, `ML`, `Algorithm` |
| `player1AlgorithmProfile`, `player2AlgorithmProfile` | `Standard`, `Ramp`, `TempoAggro`, `ControlStatus`, `HealStall`, `Auto` |
| `llmProvider`, `player1LlmProvider`, `player2LlmProvider`, `llmAdvisorProvider` | `Gemini`, `Ollama`, `OpenAI` |
| `geminiModel`, `player1GeminiModel`, `player2GeminiModel`, `llmAdvisorGeminiModel` | `Flash25`, `Flash25Lite`, `Pro25`, `Flash20`, `Flash20Lite`, `Flash31Lite`, `Flash35`, `Flash15`, `Flash30`, `Gemma4_26b`, `Gemma4_31b` |
| `openAiModel`, `player1OpenAiModel`, `player2OpenAiModel`, `llmAdvisorOpenAiModel` | `Gpt4oMini`, `Gpt4o`, `Gpt41Mini`, `Gpt41`, `Gpt5Mini`, `Gpt5`, `O4Mini` |
| `ollamaModel`, `player1OllamaModel`, `player2OllamaModel`, `llmAdvisorOllamaModel` | `Gemma3_12b`, `Qwen3_8b`, `Gemma4_12b_It_Q4_K_M`, `Gemma4_E4b_It_Q4_K_M` |
| `mlServerPreset`, `ollamaEndpointPreset` | `Localhost`, `Custom` |

`player1DeckName`, `player2DeckName`, and benchmark participant `deck` values must match an available deck JSON filename without the `.json` extension. Benchmark participant `profile` uses the same Algorithm profile values listed above.

`mlServerUrl` is **not an LLM setting**. It belongs to `MLBrain`, `MLSuggestionButton`, and `AdvisorEventReporter`; it points Unity at the Python FastAPI `/predict` server from `ML Pipeline/`. It appears in the same `GameRulesConfig.json` because all runtime AI configuration is stored in one file.

`llmAutoDelay` applies a provider-appropriate inter-turn delay automatically for Gemini models (30 RPM → 2 s, 15 RPM → 4 s, 5 RPM → 12 s). OpenAI and Ollama currently use 0 s from this auto-delay helper; set `llmAutoDelay: false` and specify `llmTurnDelay` manually if needed.

For OpenAI, model enum names map to API model ids in `OpenAiApiClient`: `Gpt4oMini` → `gpt-4o-mini`, `Gpt4o` → `gpt-4o`, `Gpt41Mini` → `gpt-4.1-mini`, `Gpt41` → `gpt-4.1`, `Gpt5Mini` → `gpt-5-mini`, `Gpt5` → `gpt-5`, and `O4Mini` → `o4-mini`. GPT-5 and o-series entries are treated as reasoning models: the client omits custom temperature and uses `openAiMaxOutputTokens` as `max_completion_tokens`.

For local Ollama, `Gemma3_12b` maps to `gemma3:12b` and fits an RTX 3060 12 GB at a 4096 context in current testing. `Qwen3_8b` maps to `qwen3:8b` and is the recommended comparison model for faster local decisions and stricter format-following tests.

Numeric values still work, but they depend on enum ordering. Current `EnumPlayerType` values are:

```text
0 = Human
1 = LLM
2 = ML
3 = Algorithm
```

### Telemetry — Remote Log Upload

`LogUploader` can upload per-game results and decision JSONL files to a compatible remote receiver after each battle. Toggle via `GameRulesConfig`:

```json
"logUploadEnabled": true
```

Copy `LogUploader.local.example.json` to `LogUploader.local.json` next to the project/build and provide `serverUrl` plus `apiKey`. The local file is ignored by Git and is never part of a source archive.

Upload is fire-and-forget — failures log a warning and never block gameplay. A per-machine `client_id.txt` UUID is created next to the executable on first run.

No receiver implementation or hosted endpoint is included in the public export. With `logUploadEnabled: false` (the default), all logs remain local under `Logs Export/`.

### `MLBrain`

Implemented as a **behavioral-cloning agent** (`Assets/Scripts/AI/MLBrain.cs`) that imitates `AlgorithmBrain` by querying the Python inference server (FastAPI `serve.py`, `/predict`, endpoint from `GameRulesConfig.mlServerUrl`).

- **Hybrid turn:** `PlayBasic`, `AttachEnergy`, `Retreat`, and `Attack` are chosen by the ML model (one `/predict` query per decision); `setup`, `evolution`, and `trainer` phases reuse the `AlgorithmBrain` heuristic because they are not part of the BC dataset
- **Safe fallback:** a `-1` / skip response (model passes, or the request fails) is a no-op for that category, so a missing or unhealthy server never softlocks the turn
- **Representation:** content-based card/state features, not learned `cardId` embeddings. Cards are encoded from deterministic JSON attributes (HP, type, stage, retreat cost, attack costs, effect slots, targets, effect amounts) so the model generalizes across deck/card rotations and numeric balance changes
- **Training target:** `state → action` cloned from `AlgorithmBrain`'s logged decisions; per the project's policy, models train on **winning games only** (`--winners-only`)

Future option: add reward weighting from win/loss or fine-tune with self-play RL after the behavioral-cloning baseline is solid.

### In-scene advisors

`AdvisorButtonPanel`, `MLSuggestionButton`, and `LLMSuggestionButton` add on-board buttons that ask an agent for a suggested action during a human game: `MLSuggestionButton` POSTs the live snapshot + legal actions to the ML server's `/predict`, `LLMSuggestionButton` asks the configured LLM. `AdvisorEventReporter` forwards what happened to the dashboard via `POST /api/advisor/event`, where the `Advisors` tab displays it.

### Python ML pipeline

The Unity-side `MLBrain` and advisor buttons integrate with the sibling Python project in [`../ML Pipeline/`](../ML%20Pipeline/README.md). That folder documents training, evaluation, replay, the dashboard, and the `/predict` API contract.

---

## Project Structure

```text
TCG_Station/
├── Assets/
│   └── Scripts/
│       ├── AI/                  # PlayerBrain, AlgorithmBrain, LLMBrain, MLBrain, Gemini/OpenAI/Ollama clients,
│       │                        # advisor buttons (AdvisorButtonPanel, ML/LLMSuggestionButton, AdvisorEventReporter)
│       ├── Benchmark/           # BenchmarkRunner — round-robin AI-vs-AI matches with scene reload
│       ├── Data/                # enums and serializable gameplay data models
│       ├── Debug/               # debug helpers and logging utilities
│       ├── Initializers/        # FolderCreator, JsonLoader
│       ├── Instances/           # CardInstance, Pokemon, Attack, CardActions, VisualCard
│       └── Systems/             # game flow, actions, snapshots, logging, telemetry, configuration
├── Cards/
│   ├── Pokemons/                # Pokemon card JSON files
│   ├── Items/                   # Item trainer JSON files
│   ├── Supporters/              # Supporter trainer JSON files
│   ├── Stadiums/                # Stadium trainer JSON files
│   └── Tools/                   # Tool trainer JSON files
├── Decks/                       # deck JSON files
├── Packages/                    # Unity package manifest and embedded packages
└── ProjectSettings/             # Unity project configuration
```

---

## Card Format

Cards are defined in JSON. Place files in `Cards/Pokemons/<name>_<number>.json`.

### Full Pokémon Card Schema

```json
{
  "cardName": "Haunter",
  "cardId": "haunter_1",
  "deckCardId": 2,
  "imageName": "haunter_1.png",
  "cardType": "Pokemon",
  "artAuthor": "Author Name",

  "stage": 1,
  "evolvesFrom": "Gastly",
  "hp": 80,
  "type": "Psychic",
  "isEX": false,

  "attacks": [
    {
      "attackName": "Shadow Ball",
      "damage": 60,
      "attackDescription": "Poison the Defending Pokémon.",
      "attackCost": ["Psychic", "Colorless"],
      "effects": [
        {
          "cardEffectType": "Poison",
          "cardEffectTarget": "EnemyActivePokemon",
          "effectAmount": 0
        }
      ]
    }
  ],

  "retreatCost": 1
}
```

> Note: `PokemonData` has no `weakness*`/`resistance*` fields — they are not part of the data model. `isEX` and `abilityData` exist on the model (`abilityData` is parsed but abilities are not yet executed).

### Field Reference

| Field | Type | Description |
|---|---|---|
| `cardId` | string | Unique ID across all cards. Format: `name_number` e.g. `charmander_1` |
| `stage` | int | `0` = Basic, `1` = Stage 1, `2` = Stage 2 |
| `evolvesFrom` | string | `cardName` of the previous stage. Empty string for Basic |
| `hp` | int | Hit points |
| `type` | string | Pokémon energy type (see list below) |
| `isEX` | bool | Whether this is an EX card (defaults to `false`; optional in JSON) |
| `retreatCost` | int | Colorless energy cost to retreat |
| `abilityData` | object | Optional ability data; parsed but abilities are not yet executed |

### Energy Types

```
None  Colorless  Fire  Water  Grass  Lightning  Fighting  Psychic  Dragon  Darkness  Metal
```

`Colorless` and `Dragon` are never produced by the Energy Zone (they only appear via effects like `EnergyRamp` from a `Colorless`/`Dragon`-type Pokémon). `Dragon` energy acts as a **joker** that pays any cost slot; `Colorless` energy pays only `Colorless` slots.

### Attack Effects

Each attack can have multiple effects applied in order:

| Effect | Target | Description |
|---|---|---|
| `Heal` | `Self` / `BenchPokemon` | Restore HP |
| `BenchHeal` | `Self` | Heal all bench Pokémon |
| `DealDamage` | any | Direct damage bypassing attack calculation |
| `BenchDmg` | `BenchPokemon` / `EnemyBenchPokemon` | Damage all bench Pokémon. KO on own bench awards a point to the opponent but does not trigger Active promotion |
| `DmgTakenRed` | `Self` | Reduce incoming attack damage. AlgorithmBrain uses Trainer cards with this effect only when the reduction prevents KO or when the opponent is at most 1 energy away from an attack dealing at least half of the blocked damage |
| `Expose` | enemy | Apply a persistent in-memory debuff that makes the target take more damage. It has no dedicated status icon |
| `PowerUp` | `Self` | Deal more damage this turn. AI treats PowerUp lines as scaling finishers and charges them toward KO of the opponent's highest-current-HP Pokémon + 2 spare energy |
| `Multiattack` | `Opponent` | Hit N times total. If an early hit KOs the opponent Active and the opponent promotes a new Active, the remaining hits continue into that new Active |
| `Counterattack` | `Self` | Deal damage when attacked |
| `LeechLife` | `Self` | Heal equal to damage dealt |
| `Psychic` | `EnemyActivePokemon` | `effectAmount × opponent energy count` damage. AI treats Psychic as a scaling attack and keeps 2 spare energy above the KO threshold when planning energy |
| `DrawCard` | `Self` | Draw cards from deck |
| `DiscardHand` | `Self` / `Opponent` | Discard cards from hand |
| `EnergyRamp` | `Self` | Create energy of the attacker's own type and attach it to a benched Pokémon. AI prioritizes benched evolution lines with `PowerUp`/`Psychic` before normal attack-cost completion |
| `EnergyDiscard` | any | Remove an energy from target |
| `Poison` | enemy | Apply Poison status |
| `Burn` | enemy | Apply Burn status |
| `Paralyze` | enemy | Apply Paralyze status |
| `Asleep` | enemy | Apply Asleep status |
| `Confuse` | enemy | Apply Confuse status |
| `Root` | enemy | Apply Root status (can't retreat) |
| `Slow` | enemy | Increase attack energy cost by 1 |
| `Cleanse` | `Self` | Remove all buffs, debuffs, and status effects. AlgorithmBrain treats cleanse-only Trainer cards such as `Poke Pill` as recovery cards and skips them when the Active Pokemon has no status/debuff to remove |
| `SwapSelf` | `Self` | Player selects a bench Pokémon to swap with Active |
| `SwapEnemy` | `Opponent` | Randomly swap opponent's Active with a bench Pokémon |
| `DebuffSelf` | `Self` | Apply Poison + Burn + Root + Slow combo |

Effect object format:
```json
{ "cardEffectType": "Heal", "cardEffectTarget": "Self", "effectAmount": 30 }
```

### Deck Format

```json
{
  "deckName": "Fire Deck",
  "energyTypes": ["Fire"],
  "cards": [
    { "cardId": "charmander_1", "count": 2 },
    { "cardId": "charmeleon_1", "count": 2 },
    { "cardId": "charizard_1", "count": 2 }
  ]
}
```

Place deck files in `Decks/<name>.json`.

### Common Mistakes

| Problem | Fix |
|---|---|
| Card doesn't appear in game | File must be in `Cards/Pokemons/` |
| Evolution doesn't work | `evolvesFrom` must match exactly the `cardName` of the previous stage |
| Attack cost invalid | Must be an array: `["Fire", "Colorless"]`, not a plain string |
| Effect not triggering | Check `cardEffectType` spelling — case sensitive |
| Duplicate card error | Every `cardId` must be unique across all cards |
| Deck loads fewer than `deckSize` cards | A deck entry references a `cardId` with no card file (a dangling id / typo) — it's silently skipped at load. Run the validator below. |

**Validate decks before building:** `tools/validate_decks.py` (stdlib-only Python, cross-platform) checks every deck in `Decks/` against the card library and reports dangling `cardId`s, totals that don't match `deckSize`, duplicate ids within a deck, and bad counts. It also validates the card library's **evolution chains** — a stage > 0 Pokémon whose `evolvesFrom` names no real previous form (a typo) or is empty (an orphan that can never be played). It exits non-zero on any problem, so it can gate a build or run in CI.

```bash
python3 tools/validate_decks.py          # uses deckSize from GameRulesConfig.json (else 30)
python3 tools/validate_decks.py --quiet  # only print problems + summary
```

---

## Benchmark Plan

### Data Collection

Planned detailed benchmark logging should produce:

- **Decision log (JSONL):** per action — game state snapshot, list of legal actions, chosen action, latency, LLM token usage
- **Battle summary (JSON):** per game — deck compositions, winner, turns, drawn cards, played cards

Currently implemented logging:

| Logger | Output | Content |
|---|---|---|
| `GameResultLogger` | `Logs Export/ML/games.jsonl` | One JSONL line per game: game_id, decks, brains, winner, `end_reason` (`game_over` / `timeout`), turns, scores, timestamps. Timed-out benchmark matches are adjudicated by score so they still produce a winner row |
| `DecisionLogger` | `Logs Export/ML/Decisions/<game_id>_decisions.jsonl` | One line per Algorithm decision group: game state snapshot, candidates with scores, chosen action. Closes each game with a terminal `GameEnd` record (final winning state incl. the deciding KO), excluded from training |
| `LLMLogger` | `Logs Export/ML/llm_decisions.jsonl` | One line per completed LLM turn: provider, model, mode, chosen actions, parsed `THINKING`, legal-action count |
| `MatchupStatsLogger` | `Logs Export/Matchups/MatchupStats.json` + `.txt` | Cumulative win/loss counts per deck×brain matchup across runs |
| `HumanReadableBattleLogger` | `Logs Export/Readable/` | Human-readable battle narrative per game |
| `BattleResultExporter` | `Logs Export/Deckbuilder/battle_YYYYMMDD_HHMMSS.json` | Deck compositions, drawn/played cards, winner, turn count |

All loggers are guarded by toggles in `GameRulesConfig` (`enableMlDecisionLogs`, `enableMlGameResultLogs`, `enableLlmDecisionLogs`, `enableMatchupStatsLogs`, `enableReadableBattleLogs`, `enableDeckbuilderBattleLogs`).

AI-vs-AI data collection is driven by `BenchmarkRunner` (`Assets/Scripts/Benchmark/`), a round-robin runner that reloads the scene between matches; it is active only when the component is in the scene with `runEnabled: true` (config in `Assets/StreamingAssets/BenchmarkConfig.json`). `GameManager`, `PlayerManager`, and `GameRulesConfig` are intentionally **not** `DontDestroyOnLoad` so each match starts clean after the reload.

`BattleResultExporter` output example (files written next to the executable):

```text
<folder_exe>/Logs Export/Deckbuilder/battle_YYYYMMDD_HHMMSS.json
```

In the Unity Editor, `<folder_exe>` resolves to the project root. In a Windows build,
it resolves to the folder containing the `.exe`; in a macOS build, it resolves to the folder containing the `.app` bundle.

This export is the implemented bridge to Maciek's separate deck-builder project, not an internal ML training log for `MLBrain`. Maciek's project sends this game the card definitions and deck JSON files; this game simulates those decks and sends back per-match `Real Battles` data so the deck builder can analyze deck/card performance and, in future work, train directly from real outcomes. The exchange is documented in [`PARTNER_DECKBUILDER.md`](<../Deckbuilder i prezentacja/PARTNER_DECKBUILDER.md>).

What this project receives from Maciek:

- Card JSON definitions that are loaded from `Cards/`
- Deck JSON definitions that are loaded from `Decks/` and selected by `GameRulesConfig` / `BenchmarkConfig`

What this project sends back:

- `Logs Export/Deckbuilder/battle_*.json` with both deck compositions, winner, turn count, drawn cards, and played cards
- The file is meant for the partner deck-builder loop; the behavioral-cloning pipeline uses the separate `Logs Export/ML/` logs

Current JSON shape:

```json
{
  "battle_id": "battle_20260524_143012",
  "deck_a": {
    "deck_id": "deck_123",
    "energy_types": ["Water", "Dark"],
    "cards": {
      "overqwil_1": 3,
      "quilfish_1": 3,
      "skrelp_1": 3
    }
  },
  "deck_b": {
    "deck_id": "deck_987",
    "energy_types": ["Fire"],
    "cards": {
      "charizard_1": 3,
      "charmander_1": 3
    }
  },
  "winner": "A",
  "turns": 11,
  "drawn_cards_a": ["quilfish_1", "skrelp_1", "overqwil_1"],
  "played_cards_a": ["quilfish_1", "skrelp_1"],
  "drawn_cards_b": ["charmander_1"],
  "played_cards_b": ["charmander_1"]
}
```

- `turns` is `TurnManager.turnCounter` at game end.
- `drawn_cards_*` includes the opening hand and later draws.
- `played_cards_*` includes Basic Pokémon, evolved Pokémon, and Trainer cards.
- `winner` is `"A"`, `"B"`, or `"Draw"`.

### Algorithm Heuristic

`AlgorithmBrain.PerformTurn()` is implemented as a sequential heuristic, not a generic one-pass weighted loop over all legal actions:

1. Play Basic Pokémon from hand, using a deterministic scorer for board building
2. Play eligible evolutions
3. Play Trainers, skipping wasted recovery-only cards and using damage reduction only when it matters
4. Attach energy through a scorer that considers immediate attacks, `EnergyDiscard`, ramp support, `PowerUp`/`Psychic` scaling lines, and KO risk
5. Retreat only when the board state makes it worthwhile
6. Attack with a scorer that ranks ready attacks by immediate damage/KO, then non-damage payoff (DoT, disables, debuffs, counter/heal riders), and end the turn

### LLM Token Optimization

- **Provider-specific rules prompt** — rules are loaded from `LLM_RULES_Gemini.txt`, `LLM_RULES_OpenAI.txt`, or `LLM_RULES_Ollama.txt` and attached to provider requests
- **No Gemini Context Caching** — not implemented and not assumed in the current benchmark plan; this avoids relying on a feature that is not available in the free plan
- **Gemini/OpenAI sequence mode** — one hosted API request per turn, returning `ACTION_SEQUENCE`, to reduce repeated paid calls
- **Ollama step mode** — more prompts are acceptable locally, so Ollama chooses one `ACTION_INDEX` after every refreshed state
- **Thinking process visible in UI** — setup uses `THINKING: ...` and `WYBOR_ID: ...`; Gemini/OpenAI turns use `THINKING: ...` and `ACTION_SEQUENCE: ...`; Ollama turns use `STATE: ...`, `THINKING: ...`, and `ACTION_INDEX: ...`

### Implementation Order

1. ✅ `GameAction` + `LegalActionGenerator` + `GameActionExecutor` + `GameStateSnapshot` — shared foundation
2. ✅ Benchmark loggers: `DecisionLogger`, `GameResultLogger`, `LLMLogger`, `MatchupStatsLogger`, `HumanReadableBattleLogger`; remote upload via `LogUploader`
3. ✅ `BenchmarkRunner` round-robin `Algorithm vs Algorithm` — dataset for behavioral cloning (winners-only training data)
4. ✅ Python ML pipeline (`ML Pipeline/`): `tcg_ml` feature/dataset/model library, `feature_spec.json`, `train_bc.py` (save-best, winners-only), `evaluate_model.py`
5. ✅ Python inference server (`serve.py` + dashboard) + Unity `MLBrain` HTTP client (endpoint `GameRulesConfig.mlServerUrl`)
6. 🔧 `MLBrain` benchmark vs Algorithm — tune the trained model's decision quality
7. 📋 `LLMBrain` quality testing, provider/model comparison, and LLM telemetry dashboard

---

## Status — June 2026

- [x] Full local game loop (setup, hand, play, evolve, energy, attack, status, retreat, KO, win)
- [x] `HumanBrain`, `AlgorithmBrain`, `LLMBrain` — setup phase working; AI turn logic implemented for Algorithm and LLM
- [x] LLM provider abstraction (`Gemini` / `OpenAI` / local `Ollama`), per-player config, auto rate limiting
- [x] LLM thinking display (`THINKING: ...`) and provider-specific external rules files
- [x] 20+ card effects in `CardActions`
- [x] JSON-driven cards and decks
- [x] `GameAction` / `LegalActionGenerator` / `GameActionExecutor` / `GameStateSnapshot`
- [x] `AlgorithmBrain.PerformTurn()` — full sequential heuristic (Basic → evolve → energy → attack → end turn)
- [x] `LLMBrain.PerformTurn()` — Gemini/OpenAI sequence mode (`ACTION_SEQUENCE`) and Ollama step mode (`ACTION_INDEX`)
- [x] Benchmark loggers: `DecisionLogger` (+ terminal `GameEnd`), `GameResultLogger` (+ `end_reason`), `LLMLogger`, `MatchupStatsLogger`, `HumanReadableBattleLogger`
- [x] `BenchmarkRunner` — round-robin AI-vs-AI matches with scene reload
- [x] Optional remote log upload client (`LogUploader`; receiver configured locally)
- [x] In-scene advisors (`MLSuggestionButton`, `LLMSuggestionButton`) + dashboard `Advisors` tab
- [x] Python ML pipeline (`ML Pipeline/`): feature/dataset/model library, BC trainer (save-best, winners-only, MPS/CUDA), held-out evaluation
- [x] Python ML inference server + training/metrics/replay dashboard (`serve.py`)
- [x] `MLBrain` — hybrid behavioral-cloning agent querying `/predict`
- [ ] LLM telemetry dashboard: token/latency/retry/fallback metrics per call, joined with benchmark outcomes for provider/model comparison
- [ ] `MLBrain` decision-quality tuning and benchmark vs `AlgorithmBrain`
- [ ] `LLMBrain` quality testing and provider/model comparison

---

## Tech Stack

- **Engine:** Unity 6 (6000.3.6f1, C#)
- **LLM:** Gemini, OpenAI, or local Ollama via HTTPS/HTTP REST (`UnityWebRequest`)
- **ML pipeline:** Python 3 + PyTorch (behavioral cloning), FastAPI inference server + web dashboard; GPU via CUDA or Apple Silicon MPS
- **Card/deck data:** JSON
- **Game logs:** JSONL (`games.jsonl`, per-game decision logs, `llm_decisions.jsonl`)
- **Optional log upload:** HTTP client configured through ignored local settings
