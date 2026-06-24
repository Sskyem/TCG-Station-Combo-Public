@echo off
cd /d "%~dp0"
if not exist ".venv\Scripts\activate.bat" (
  python -m venv .venv
)
call .venv\Scripts\activate
python -c "import fastapi, uvicorn, pydantic, numpy, torch" >nul 2>nul
if errorlevel 1 (
  python -m pip install --upgrade pip
  python -m pip install -r requirements.txt
)
start "" http://localhost:8000
python scripts\serve.py --host 0.0.0.0 --port 8000
