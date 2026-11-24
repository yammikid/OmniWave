@echo off
chcp 65001 >nul
cd /d "%~dp0"
echo Clearing old build cache (bin, obj)...
if exist bin rmdir /s /q bin
if exist obj rmdir /s /q obj
echo Building OmniWave + Dynamic Island...
dotnet publish -c Release -r win-x64 --self-contained false -o ./publish
if errorlevel 1 (
    echo.
    echo Build failed! Please send a screenshot of this window.
    pause
    exit /b 1
)
echo.
echo Done! Starting publish\DynamicIsland.exe...
start "" "%~dp0publish\DynamicIsland.exe"
pause
