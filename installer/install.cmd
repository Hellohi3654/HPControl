@echo off
rem HP Control installer: asks for admin once, installs the service, then opens the app as you (not elevated).
setlocal
if not exist "%~dp0bin\HPControl.Service.exe" (
  echo Can't find the program files next to this script.
  echo If you opened the zip directly, right-click it, choose "Extract All", then run install.cmd from the extracted folder.
  pause
  exit /b 1
)
echo Installing HP Control (approve the admin prompt)...
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "Start-Process powershell.exe -Verb RunAs -Wait -ArgumentList '-NoProfile -ExecutionPolicy Bypass -File \"%~dp0install.ps1\"'"
if exist "%ProgramFiles%\HP Control\HPControl.exe" (
  start "" "%ProgramFiles%\HP Control\HPControl.exe" --agent
  timeout /t 1 /nobreak >nul
  start "" "%ProgramFiles%\HP Control\HPControl.exe"
)
endlocal
