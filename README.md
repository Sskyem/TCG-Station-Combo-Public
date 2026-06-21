# TCG Station Combo

Monorepo for a master's-thesis project that combines:

- a Unity digital card game and AI benchmark in [`TCG_Station/`](TCG_Station/)
- a Python behavioral-cloning pipeline, FastAPI inference server, and dashboard in [`ML Pipeline/`](ML%20Pipeline/)

The project compares rule-based, LLM-based, and ML-based agents in a Pokémon TCG Pocket-inspired game with hidden information.

## Repository Layout

```text
TCG-Station-Combo/
├── TCG_Station/        # Unity game, cards/decks, agents, benchmark logging
├── ML Pipeline/        # Python ML pipeline, dashboard, training/evaluation, /predict
└── README.md           # this index
```

## Where to Start

- **Run or inspect the Unity game:** read [`TCG_Station/README.md`](TCG_Station/README.md).
- **Run the ML dashboard, train/evaluate models, or use `/predict`:** read [`ML Pipeline/README.md`](ML%20Pipeline/README.md).

## Quick Start

### Run the game

1. Download the release package for Windows or macOS.
2. Extract the downloaded archive.
3. Open `TCG Station.exe` on Windows or `TCG Station.app` on macOS.
4. Choose the game mode, decks, and players in the startup menu, then press **Start**.

Human and Algorithm players work without the ML dashboard. Start the dashboard first when using an ML player or ML advisor.

### Use an LLM player

1. Choose `LLM` as a player type in the startup menu.
2. Configure the provider and model in `GameRulesConfig.json` next to the game:

   - **Gemini:** paste your API key into `GEMINI_API_KEY.txt`.
   - **OpenAI:** paste your API key into `OPENAI_API_KEY.txt`.
   - **Ollama:** start Ollama locally and download the configured model, for example `ollama pull gemma3:12b`.

3. Keep the API-key file or Ollama service available while the game is running, then press **Start**.

The API key files are included as empty placeholders. Do not commit or share them after adding real keys. Provider names and the rules for selecting models are described in [`TCG_Station/README.md`](TCG_Station/README.md#allowed-config-values).

### Run the ML dashboard

1. Open the `ML Pipeline/` folder.
2. Start the launcher for your system:

   - Windows: double-click `start_dashboard_WINDOWS.cmd`
   - macOS/Linux: run `./start_dashboard_MACOS.sh`

3. Wait for the browser to open, or visit [http://localhost:8000](http://localhost:8000).
4. In the dashboard, open **Dataset → ML data paths** and select the game's `Logs Export/ML` folder if it was not detected automatically.

## Folder Layout for Builds

When using the packaged game together with the ML dashboard, keep the two folders side by side:

```text
Builds/
├── MacOS/              # or Windows/
└── ML Pipeline/
```

The Unity build writes logs in its own folder. The ML dashboard can read those logs through `TCG_BUILD_ROOT`, `TCG_LOGS_DIR`, or the dashboard path picker; the path picker is the recommended option for packaged builds. See the subfolder READMEs for exact setup details.
