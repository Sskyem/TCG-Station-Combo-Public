@echo off
setlocal EnableExtensions EnableDelayedExpansion
cd /d "%~dp0"
set "VENV=.venv-windows"

if exist ".env" (
  for /f "usebackq tokens=1,* delims==" %%A in (".env") do (
    set "env_key=%%A"
    set "env_value=%%B"
    if not "!env_key!"=="" if not "!env_key:~0,1!"=="#" (
      set "!env_key!=!env_value!"
    )
  )
)

if not defined TCG_GAME_ROOT if exist "..\TCG_Station\Assets" if exist "..\TCG_Station\Cards" (
  set "TCG_GAME_ROOT=%~dp0..\TCG_Station"
)

if not defined TCG_BUILD_ROOT if not defined TCG_GAME_ROOT if exist "..\Windows\Cards" if exist "..\Windows\Decks" (
  set "TCG_BUILD_ROOT=%~dp0..\Windows"
)

if not defined TCG_GAME_ROOT if defined TCG_BUILD_ROOT (
  set "TCG_GAME_ROOT=%TCG_BUILD_ROOT%"
)

if not defined TCG_CARDS_DIR if defined TCG_BUILD_ROOT if exist "%TCG_BUILD_ROOT%\Cards" (
  set "TCG_CARDS_DIR=%TCG_BUILD_ROOT%\Cards"
)

if not defined TCG_DECKS_DIR if defined TCG_BUILD_ROOT if exist "%TCG_BUILD_ROOT%\Decks" (
  set "TCG_DECKS_DIR=%TCG_BUILD_ROOT%\Decks"
)

if not defined TCG_LOGS_DIR if defined TCG_BUILD_ROOT if exist "%TCG_BUILD_ROOT%\Logs Export\ML" (
  set "TCG_LOGS_DIR=%TCG_BUILD_ROOT%\Logs Export\ML"
)

if not defined TCG_LOGS_DIR if exist "..\Windows\Logs Export\ML" (
  set "TCG_LOGS_DIR=%~dp0..\Windows\Logs Export\ML"
)

if not defined TCG_LOGS_DIR if exist "..\MacOS\Logs Export\ML" (
  set "TCG_LOGS_DIR=%~dp0..\MacOS\Logs Export\ML"
)

if not defined TCG_GAME_ROOT if not defined TCG_BUILD_ROOT (
  echo Could not find the TCG Station project/build path.
  echo.
  echo Create a file named .env next to this script and set one of these:
  echo   TCG_GAME_ROOT=D:\path\to\TCG_Station
  echo   TCG_BUILD_ROOT=D:\path\to\folder-with-TCG-Station-exe
  echo.
  echo The source project folder must contain Assets and Cards.
  echo The build folder should contain TCG Station.exe, Cards, Decks, and Logs Export.
  echo.
  pause
  exit /b 1
)

if not exist "%VENV%\Scripts\activate.bat" (
  echo Creating local Windows Python environment in %VENV%...
  python -m venv "%VENV%"
  if errorlevel 1 (
    echo Failed to create %VENV%. Install Python and try again.
    pause
    exit /b 1
  )
)
call "%VENV%\Scripts\activate"
python -c "import fastapi, uvicorn, pydantic, numpy, torch" >nul 2>nul
if errorlevel 1 (
  echo Installing dashboard and model dependencies...
  python -m pip install --upgrade pip
  python -m pip install -r requirements.txt
)
start "" http://localhost:8000
python scripts\serve.py --host 0.0.0.0 --port 8000

echo.
echo ===============================================================
echo  Server stopped. This window stays open so you can read output.
echo  Close this window to fully shut down, or run start_dashboard
echo  again to restart the server.
echo ===============================================================
pause >nul
