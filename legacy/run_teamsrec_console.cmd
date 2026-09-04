@echo off
rem Start the teamsrec prototype with a console window (for testing; log also goes to D:\meetings\teamsrec.log).
set "PATH=%LOCALAPPDATA%\Microsoft\WinGet\Links;%PATH%"
"%~dp0.venv\Scripts\python.exe" "%~dp0teamsrec.py"
