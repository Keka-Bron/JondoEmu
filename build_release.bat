@echo off
setlocal

rem Toujours travailler depuis le dossier ou se trouve ce script.
cd /d "%~dp0"

echo ============================================================
echo   Build et publication de Jondo.Unity en Release
echo ============================================================
echo.
echo Fermez Jondo Emulator Launcher avant de continuer.
echo.

dotnet build "Jondo.Unity.sln" -c Release
set "BUILD_EXIT_CODE=%ERRORLEVEL%"

echo.
if not "%BUILD_EXIT_CODE%"=="0" (
    echo [ERREUR] Le build a echoue avec le code %BUILD_EXIT_CODE%.
    goto :end
)

echo [OK] Le build Release a reussi.
echo.
echo Publication et remplacement de Jondo Emulator Launcher.exe...
echo.

dotnet publish "Jondo.Unity.Launcher\Jondo.Unity.Launcher.csproj" -c Release
set "PUBLISH_EXIT_CODE=%ERRORLEVEL%"

echo.
if not "%PUBLISH_EXIT_CODE%"=="0" (
    echo [ERREUR] La publication a echoue avec le code %PUBLISH_EXIT_CODE%.
    set "BUILD_EXIT_CODE=%PUBLISH_EXIT_CODE%"
    goto :end
)

echo [OK] Publication terminee.
echo [OK] Jondo Emulator Launcher.exe a ete remplace a la racine.
set "BUILD_EXIT_CODE=0"

:end
echo.
pause
exit /b %BUILD_EXIT_CODE%
