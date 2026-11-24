# OmniWave + Dynamic Island (WPF / .NET 8 on .NET 9 SDK)

Музыкальный плеер в стиле **Ultra Clear Liquid Glass** с плавающей панелью **Dynamic Island**, примагничиванием (докингом) к краям экрана, быстрым поиском и стримингом из открытых источников, интеграцией VK Музыки без браузерного расширения, офлайн-загрузками и синхронизацией с системными плеерами Windows.

## Требования и запуск

1. **.NET 8 SDK:**
   ```powershell
   winget install Microsoft.DotNet.SDK.8
   ```
2. **WebView2 Runtime:** предустановлен в Windows 10/11 (используется для GPU-рендеринга главного окна).
3. **yt-dlp** (резервный резолвер аудиопотоков):
   ```powershell
   winget install yt-dlp.yt-dlp
   ```
   *(либо положите `yt-dlp.exe` в папку рядом с собранным `DynamicIsland.exe`)*
4. **Команды разработки и сборки:**
   ```powershell
   # Запуск в режиме разработки
   dotnet run

   # Сборка готового .exe в папку publish
   dotnet publish -c Release -r win-x64 --self-contained false -o ./publish
   ```