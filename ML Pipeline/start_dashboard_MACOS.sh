#!/usr/bin/env bash
set -e
cd "$(dirname "$0")"

# This project folder may be shared with a Windows machine (network drive), and the
# Windows launcher creates its own `.venv-windows`. A virtualenv is NOT portable across
# operating systems, so macOS uses a separate venv directory to avoid the two
# clobbering each other in the same shared folder.
VENV=".venv-macos"

if [ -f ".env" ]; then
  while IFS='=' read -r env_key env_value || [ -n "$env_key" ]; do
    env_key="${env_key#"${env_key%%[![:space:]]*}"}"
    env_key="${env_key%"${env_key##*[![:space:]]}"}"
    env_value="${env_value%$'\r'}"
    case "$env_key" in
      ""|\#*) continue ;;
    esac
    export "$env_key=$env_value"
  done < ".env"
fi

if [ -z "${TCG_BUILD_ROOT:-}" ] && [ -z "${TCG_GAME_ROOT:-}" ] && [ -d "../MacOS/Cards" ] && [ -d "../MacOS/Decks" ]; then
  export TCG_BUILD_ROOT="$PWD/../MacOS"
fi

if [ -z "${TCG_GAME_ROOT:-}" ] && [ -n "${TCG_BUILD_ROOT:-}" ]; then
  export TCG_GAME_ROOT="$TCG_BUILD_ROOT"
fi

if [ -z "${TCG_CARDS_DIR:-}" ] && [ -n "${TCG_BUILD_ROOT:-}" ] && [ -d "$TCG_BUILD_ROOT/Cards" ]; then
  export TCG_CARDS_DIR="$TCG_BUILD_ROOT/Cards"
fi

if [ -z "${TCG_DECKS_DIR:-}" ] && [ -n "${TCG_BUILD_ROOT:-}" ] && [ -d "$TCG_BUILD_ROOT/Decks" ]; then
  export TCG_DECKS_DIR="$TCG_BUILD_ROOT/Decks"
fi

if [ -z "${TCG_LOGS_DIR:-}" ] && [ -n "${TCG_BUILD_ROOT:-}" ] && [ -d "$TCG_BUILD_ROOT/Logs Export/ML" ]; then
  export TCG_LOGS_DIR="$TCG_BUILD_ROOT/Logs Export/ML"
fi

if [ -z "${TCG_LOGS_DIR:-}" ] && [ -d "../MacOS/Logs Export/ML" ]; then
  export TCG_LOGS_DIR="$PWD/../MacOS/Logs Export/ML"
fi

if [ -z "${TCG_LOGS_DIR:-}" ] && [ -d "../Windows/Logs Export/ML" ]; then
  export TCG_LOGS_DIR="$PWD/../Windows/Logs Export/ML"
fi

# Prefer a modern interpreter, fall back to whatever python3 is on PATH.
PYTHON=""
for cand in python3.13 python3.12 python3.11 python3; do
  if command -v "$cand" >/dev/null 2>&1; then PYTHON="$cand"; break; fi
done
if [ -z "$PYTHON" ]; then
  echo "No python3 interpreter found on PATH. Install Python 3.11+ first." >&2
  exit 1
fi

if [ ! -f "$VENV/bin/activate" ]; then
  echo "Creating local macOS Python environment in $VENV (using $PYTHON)..."
  rm -rf "$VENV"
  "$PYTHON" -m venv "$VENV"
fi

source "$VENV/bin/activate"
VENV_PYTHON="$PWD/$VENV/bin/python"

if [ ! -x "$VENV_PYTHON" ]; then
  echo "Virtual environment exists but $VENV_PYTHON is missing." >&2
  echo "Delete $VENV and run this launcher again." >&2
  exit 1
fi

if ! "$VENV_PYTHON" -c "import fastapi, uvicorn, pydantic, numpy" 2>/dev/null; then
  echo "Installing dashboard dependencies..."
  "$VENV_PYTHON" -m pip install --upgrade pip
  "$VENV_PYTHON" -m pip install "fastapi" "uvicorn[standard]" "pydantic" "numpy"
fi

open "http://localhost:8000" 2>/dev/null || true
"$VENV_PYTHON" scripts/serve.py --host 0.0.0.0 --port 8000
