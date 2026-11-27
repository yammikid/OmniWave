@echo off
setlocal EnableExtensions
chcp 65001 >nul
cd /d "%~dp0"

if exist "%LocalAppData%\Microsoft\dotnet\dotnet.exe" set "PATH=%LocalAppData%\Microsoft\dotnet;%PATH%"
if exist "%ProgramFiles%\dotnet\dotnet.exe" set "PATH=%ProgramFiles%\dotnet;%PATH%"

call :CheckSdk
if %errorlevel% equ 0 goto :BuildStart

echo.
echo [OmniWave] Не найден .NET 8 SDK ^(для сборки из исходников нужен именно SDK, а не Runtime^).
where winget >nul 2>nul
if %errorlevel% neq 0 goto :NoSdk

echo [OmniWave] Попытка автоматической установки .NET 8 SDK через winget...
winget install -e --id Microsoft.DotNet.SDK.8 --accept-package-agreements --accept-source-agreements
if exist "%LocalAppData%\Microsoft\dotnet\dotnet.exe" set "PATH=%LocalAppData%\Microsoft\dotnet;%PATH%"
if exist "%ProgramFiles%\dotnet\dotnet.exe" set "PATH=%ProgramFiles%\dotnet;%PATH%"

call :CheckSdk
if %errorlevel% equ 0 goto :BuildStart

:NoSdk
echo.
echo [ОШИБКА] Не удалось найти или установить .NET 8 SDK ^(x64^).
echo.
echo 1. Скачайте и установите ".NET 8.0 SDK (x64)":
echo    https://dotnet.microsoft.com/download/dotnet/8.0
echo    ^(или выполните в PowerShell: winget install Microsoft.DotNet.SDK.8^)
echo 2. Закройте это окно и снова запустите build.bat.
echo.
pause
exit /b 1

:CheckSdk
where dotnet >nul 2>nul
if %errorlevel% neq 0 exit /b 1
dotnet --list-sdks 2>nul | findstr /r "^[89]\. ^1[0-9]\." >nul
if %errorlevel% neq 0 exit /b 1
exit /b 0

:BuildStart
echo Clearing old build cache ^(bin, obj, publish_tmp^)...
if exist bin rmdir /s /q bin
if exist obj rmdir /s /q obj
if exist publish_tmp rmdir /s /q publish_tmp

echo Building OmniWave ^(Chroma Card Zero-Blur^)...
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=false -o ./publish_tmp
set "BUILD_EXIT=%errorlevel%"
if %BUILD_EXIT% neq 0 goto :BuildFailed
if not exist "%~dp0publish_tmp\DynamicIsland.exe" goto :MissingExe

copy /y "%~dp0publish_tmp\DynamicIsland.exe" "%~dp0DynamicIsland.exe" >nul
rmdir /s /q "%~dp0publish_tmp"

echo.
echo Done! Starting DynamicIsland.exe...
start "" "%~dp0DynamicIsland.exe"
pause
exit /b 0

:BuildFailed
if exist "%~dp0publish_tmp" rmdir /s /q "%~dp0publish_tmp"
echo.
echo Build failed ^(exit code %BUILD_EXIT%^)! Please check the errors above.
pause
exit /b %BUILD_EXIT%

:MissingExe
if exist "%~dp0publish_tmp" rmdir /s /q "%~dp0publish_tmp"
echo.
echo [ОШИБКА] Сборка завершилась, но publish_tmp\DynamicIsland.exe не найден!
pause
exit /b 1
