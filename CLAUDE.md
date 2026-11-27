# OmniWave (WPF / .NET 8 on .NET 9 SDK, Chroma Card Zero-Blur)

Локальный музыкальный плеер для Windows 10/11 в стиле **Chroma Card (Zero-Blur)** с двумя нативными режимами окна (**Expanded Card `400×604`** ↔ **Compact Chroma Pill `324×66`**), системным треем, кросс-платформенным поиском (Яндекс Музыка, VK Музыка, Spotify, SoundCloud, YouTube, Audius, Bandcamp, Radio), встроенным прокси-маршрутизатором (HTTP/HTTPS/SOCKS5/SOCKS4), офлайн-загрузками, гибернацией в трее (WebView2 Suspend) и перехватом Windows GSMTC.

## Требования и сборка

1. **.NET 8 SDK:**
   ```powershell
   winget install Microsoft.DotNet.SDK.8
   ```
2. **WebView2 Runtime:** предустановлен в Windows 10/11 (используется для UI `PlayerWindow` и мостов авторизации/сессий).
3. **yt-dlp** (в комплекте в корне репозитория как `yt-dlp.exe` для резервного резолва и загрузок).
4. **Команды разработки и сборки:**
   ```powershell
   # Быстрая проверка сборки
   dotnet build -c Release -r win-x64 --no-restore

   # Сборка единого DynamicIsland.exe (~37 MB) для корня приложения
   dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=false -o ./publish_tmp --no-restore
   ```
   *(или просто запустите `build.bat`)*
