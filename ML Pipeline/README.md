# ML Pipeline

Behavioral-cloning ML pipeline for `TCG_Station`'s `MLBrain`.

This folder trains and serves the Python model. The Unity game lives in the sibling `TCG_Station/` folder of this combo repo and writes the JSONL logs consumed here.

## Paths

The pipeline is intentionally outside the Unity project. Path resolution order:

1. dashboard override: paths chosen in the **ML data paths** panel (persisted to `ui_paths.json`)
2. CLI args: `--root`, `--build-root`, `--logs-dir`, `--cards-dir`, `--decks-dir`
3. environment variables: `TCG_GAME_ROOT`, `TCG_BUILD_ROOT`, `TCG_LOGS_DIR`, `TCG_CARDS_DIR`, `TCG_DECKS_DIR`
4. fallback: sibling `TCG_Station` source-project folder next to this one

### Choosing the ML logs folder from the dashboard

The **Dataset → ML data paths** panel lets you point the pipeline at any build's
`Logs Export/ML` folder live, without restarting — useful when the recipient runs the
macOS or the Windows build and the logs sit next to the `.app`/`.exe`. Paste either the
`Logs Export/ML` folder or the build root that contains it (it auto-appends `Logs Export/ML`).
**Check** validates the path and counts decision files; **Apply** switches Scan / Train /
Evaluate to it and saves the choice to `ui_paths.json` (git-ignored, survives restarts);
**Reset to default** clears the override and falls back to the CLI/env defaults. Training and
evaluation subprocesses receive the chosen paths explicitly, so they always read the same data.

> The dashboard reads one `Logs Export/ML` tree at a time. To train on logs from **both**
> macOS and Windows builds together, first merge them into one tree (copy each build's
> `Decisions/` into separate subfolders, e.g. `Decisions/received/macos` and
> `Decisions/received/windows`, and concatenate the two `games.jsonl` files), then point the
> panel at the merged tree.

Use `TCG_GAME_ROOT` for the Unity source project. Use `TCG_BUILD_ROOT` for a packaged build folder:

```powershell
# Windows: folder containing TCG Station.exe
$env:TCG_BUILD_ROOT = "D:\Builds\TCG_Station\Windows"
```

```bash
# macOS: folder containing "TCG Station.app", not the .app bundle itself
export TCG_BUILD_ROOT="/Users/me/Builds/TCG_Station/macOS"
```

Direct overrides are also supported:

```bash
python scripts/validate_dataset.py --logs-dir "/path/to/Builds/macOS/Logs Export/ML" --cards-dir "/path/to/Builds/macOS/Cards"
```

Unity writes logs next to the executable on Windows and next to the `.app` bundle on macOS:

```text
<build-root>/
  TCG Station.exe              # Windows
  TCG Station.app              # macOS
  Cards/
  Decks/
  Logs Export/
    ML/
      games.jsonl
      Decisions/
```

## Setup

Windows:

```cmd
start_dashboard_WINDOWS.cmd
```

macOS/Linux:

```bash
./start_dashboard_MACOS.sh
```

After the dashboard opens, you can finish the environment setup entirely from the GUI:
open **Environment** and click **Install / repair dependencies**. No manual `pip` or
training/evaluation commands are required when using the dashboard workflow.

## Dashboard workflow

The normal workflow is the web dashboard at `http://localhost:8000`. The command-line scripts
remain useful for debugging and reproducible thesis runs, but day-to-day setup, dataset
validation, training, evaluation, model loading, replay, and advisor monitoring are exposed in
the browser UI.

### Environment tab

- **Check environment** shows whether the current Python process can see the required runtime
  pieces.
- **Install / repair dependencies** runs the setup script from the dashboard. Choose
  `Auto-detect`, `GPU / CUDA`, or `CPU only` in the setup profile dropdown before starting it.
- The process log panel shows the live setup output, so you do not need to run `pip` commands
  manually unless you are debugging the environment.

### Dataset tab

- **Scan dataset** validates the currently selected `Logs Export/ML` tree and summarizes
  decision files, usable decisions, invalid records, trainable categories, and per-source
  distribution charts.
- **ML data paths** lets you paste a build root or a direct `Logs Export/ML` path. Use
  **Check** first to see the resolved path and decision-file count, then **Apply** to make
  Scan / Train / Evaluate use it immediately. The choice is saved to `ui_paths.json`.
- **Synchronize** can merge missing decision logs and `games.jsonl` metadata between local
  build folders. Server-fetch and card/deck patch controls require environment-specific
  endpoints that are intentionally left as placeholders in the public source.

### Training tab

- **Training** starts a fresh behavioral-cloning run. Set max games, validation split, seed,
  winners-only mode, Algorithm profile filters, decision-log sources, epochs, learning rate,
  gradient accumulation, early stopping, and device directly in the form.
- **Suggest parameters** fills the form from the scanned dataset and warns about thin coverage
  or category imbalance.
- **Fine-tune from checkpoint** continues from an existing `.pt` model, typically with a lower
  learning rate.
- **Two-stage training (All -> Winners)** runs the current Training form on all decisions first,
  then immediately fine-tunes on winner decisions using the Fine-tune form.
- **Metrics** plots loss/accuracy curves and can compare multiple saved runs.

### Models tab

- **Refresh list** shows every model checkpoint in `models/` with sidecar metadata.
- **Load latest** or a row-level load button selects the checkpoint used by the live `/predict`
  endpoint.
- **Evaluate latest** runs the evaluation script from the dashboard and writes an evaluation
  report under `reports/`.
- A `MISSING BASE` label on a fine-tuned model only means the original base checkpoint is absent
  for lineage display; a loaded model still works normally.

### Replay tab

- Pick a game from `games.jsonl` to inspect the recorded decision sequence.
- Use the slider and previous/next buttons to step through decisions.
- Filter by winner/player and use **Only disagreements** to focus on places where the loaded
  model and the logged Algorithm choice differ.
- Card images are served from the configured `Cards/` folder when available.

### Advisors tab

- **API Model** chooses the checkpoint served by `/predict`, the same endpoint used by Unity's
  `MLBrain` and `MLSuggestionButton`.
- **Advisor Activity** displays events posted by Unity's `MLSuggestionButton` and
  `LLMSuggestionButton` through `POST /api/advisor/event`. It is a live debugging view for
  in-scene recommendations; it does not execute game actions by itself.

### Analysis and Process Log

- **Benchmark Data Analysis** summarizes win rates and card/deck usage from `games.jsonl`,
  with filters for benchmark vs interactive games and matchup type.
- **Process Log** shows live setup, training, evaluation, and local synchronization logs. Use it instead of
  watching terminal output during the dashboard workflow.

Manual setup:

```bash
python -m venv .venv
source .venv/bin/activate
python -m pip install --upgrade pip
pip install -r requirements.txt
```

On Windows use `.venv\Scripts\activate` instead of `source`.

### Dashboard quick start with a packaged build

When using the dashboard with a packaged Unity build in a portable handoff, keep the
folders side by side:

```text
Builds/
  MacOS/                 # or Windows/
  ML Pipeline/
```

Start the dashboard from `ML Pipeline/`:

```cmd
start_dashboard_WINDOWS.cmd
```

```bash
./start_dashboard_MACOS.sh
```

The launcher creates a local `.venv-*`, installs the dashboard dependencies, and opens
`http://localhost:8000`. To install the full ML environment through the page, open the
**Environment** tab and click **Install / repair dependencies**. That is enough for the
GUI workflow; direct terminal commands are only for manual/debug use.

The launchers auto-detect a sibling packaged build and use its logs as the main dataset:

```text
Builds/MacOS/Logs Export/ML
Builds/Windows/Logs Export/ML
```

If both are present, the macOS launcher prefers `MacOS/`, and the Windows launcher
prefers `Windows/`. You can still override paths with a local `.env` file:

```text
TCG_BUILD_ROOT=../MacOS
TCG_CARDS_DIR=../MacOS/Cards
TCG_DECKS_DIR=../MacOS/Decks
```

With only `TCG_BUILD_ROOT` set, the dashboard reads logs from
`../MacOS/Logs Export/ML`.

For a Windows build, change those paths from `../MacOS` to the Windows build folder name. You can also set these paths from the dashboard in **Dataset → ML data paths**.

## Common Commands (manual/debug)

You usually do not need these when using the dashboard. They call the same underlying scripts
that the UI starts from its buttons, so keep them for debugging, automation, or exact command
reproduction.

Validate logs:

```bash
python scripts/validate_dataset.py
```

Train:

```bash
python scripts/train_bc.py --device auto --max-games 0 --epochs 3
```

Evaluate:

```bash
python scripts/evaluate_model.py --split val
```

Run the dashboard/API:

```bash
python scripts/serve.py --host 0.0.0.0 --port 8000
```

Unity `MLBrain` calls `/predict` at the URL configured in `GameRulesConfig.json`, default `http://127.0.0.1:8000/predict`.

## Model and API

The model is an action scorer over per-candidate feature vectors. Each legal action, plus a synthetic `(skip)` candidate, is encoded as:

```text
state_features + action_features
```

The MLP scores each candidate independently; the highest score is selected. Training uses cross-entropy over the candidate set. The train/validation split is game-level, not decision-level, so decisions from the same game do not leak into both training and validation. `evaluate_model.py --split val` reconstructs the exact held-out games from the model sidecar.

In the dashboard model list, `MISSING BASE` on a fine-tuned checkpoint only means
that the older base checkpoint named in the sidecar is not present locally for
comparison/lineage display. It does not prevent loading or using the shown model;
if the row says `(loaded)`, prediction uses that checkpoint normally.

`/predict` request:

```json
{
  "snapshot": { "...": "GameStateSnapshot" },
  "legal_actions": [
    { "label": "AttachEnergy (Pikachu)", "category": "AttachEnergy", "target_instance_id": 12 },
    { "label": "Attack[0] Thunderbolt", "category": "Attack", "target_instance_id": -1 }
  ]
}
```

`/predict` response:

```json
{
  "action_index": 0,
  "action_label": "AttachEnergy (Pikachu)",
  "confidence": 0.87,
  "top3": [
    { "action_index": 0, "action_label": "AttachEnergy (Pikachu)", "confidence": 0.87 },
    { "action_index": 1, "action_label": "Attack[0] Thunderbolt", "confidence": 0.10 }
  ],
  "inference_ms": 12.4
}
```

`action_index` is the index into the request's `legal_actions`. `-1` means the service chose the synthetic `(skip)` candidate or fell back because it could not choose a request action.

## Repository Layout

```text
src/tcg_ml/              # cards, logs, features, dataset, model, analysis
scripts/                 # train/eval/serve/validate/backfill utilities
models/                  # tracked model checkpoints and JSON sidecars
reports/                 # tracked evaluation reports
runs/                    # local run logs, ignored by git
feature_spec.json        # feature schema and normalization config
```

## Notes

- `--sources` selects subfolders under `Logs Export/ML/Decisions`, e.g. `benchmark`, `interactive`, `received`, or `legacy`.
- `--winners-only` trains only on decisions made by the winning player, using `games.jsonl` metadata.
- `--profile` filters AlgorithmBrain decisions by profile label from `games.jsonl`.
- `.env.example` documents the local path variables; copy it to `.env` if you use an environment loader.
