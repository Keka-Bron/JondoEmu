@echo off
cd /d "%~dp0"

echo =============================================
echo   JONDO EMULATOR
echo =============================================
echo.
echo Dossier :
echo %CD%
echo.

dotnet run --project Jondo.Unity.Launcher

echo.
echo =============================================
echo   Emulateur arrete
echo =============================================
pause