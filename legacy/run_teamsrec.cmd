@echo off
rem Start the teamsrec prototype in the background (no console window).
set "PATH=%LOCALAPPDATA%\Microsoft\WinGet\Links;%PATH%"
start "" "%~dp0.venv\Scripts\pythonw.exe" "%~dp0teamsrec.py"
