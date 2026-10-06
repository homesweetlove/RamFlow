@echo off
cd /d "%~dp0"
if exist ".venv\Scripts\pythonw.exe" (
  start "" ".venv\Scripts\pythonw.exe" -m ramflow ui
) else (
  set "PYTHONPATH=%~dp0src"
  start "" pythonw -m ramflow ui
)
