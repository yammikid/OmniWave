<p align="center">
  <img src="Assets/omniwave.png" width="128" alt="OmniWave" />
</p>

<h1 align="center">OmniWave + Dynamic Island</h1>

<p align="center">
  Музыкальный плеер для Windows 10/11 в стиле <strong>Clear Liquid Glass</strong> с плавающим <strong>Dynamic Island</strong>.
</p>

---

## Быстрый запуск (готовый `.exe`)

1. Скачайте репозиторий (**Code → Download ZIP** и распакуйте архив, либо `git clone https://github.com/yammikid/OmniWave.git`).
2. Запустите **`DynamicIsland.exe`** прямо из основной папки проекта — собирать ничего не нужно!
3. Если Windows сообщит об отсутствии среды выполнения, установите лёгкий **[.NET 8 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/8.0)** *(SDK для запуска готового `.exe` не требуется)*.

## Возможности

- **Clear Liquid Glass UI:** главное окно на базе WebView2 с адаптивным фоновым свечением от обложки трека.
- **Dynamic Island:** компактная плавающая капсула с авторазворачиванием, закреплением, эквалайзером и примагничиванием к краям экрана.
- **Умное поведение Острова:** плавное сворачивание при развёртывании плеера на весь экран и автоматический возврат в оконном режиме, а также переключатель **«Поверх всех окон» / «За всеми окнами»**.
- **Яндекс Музыка и «Моя волна»:** авторизация в аккаунте, поиск, лайки, плейлисты, экспорт избранного и потоковая волна с пополнением очереди.
- **ВК Музыка и «VK Микс»:** вход в аккаунт, синхронизация аудиозаписей и плейлистов, запуск потока «VK Микс» в очереди плеера.
- **Мультиплатформенный поиск и радио:** SoundCloud, YouTube Music, Audius, Bandcamp, Internet Archive / Hearthis и интернет-радио.
- **Liquid Glass таймлайн:** точный предпросмотр времени `mm:ss` при наведении и перетаскивании, быстрая перемотка `±15с`.
- **Звук и тексты:** 10-полосный эквалайзер с пресетами, регулировка скорости воспроизведения, синхронизированные тексты (LRCLIB), очередь, избранное и офлайн-загрузки (MP3 / FLAC).
- **Глобальные Fn-бинды и треи:** управление воспроизведением и переключением треков с аппаратных `Fn`-мультимедиа клавиш (`RegisterHotKey` + HID Consumer Control) во всех окнах и играх.

## Требования

- **Windows 10 / 11** (`x64`)
- **[.NET 8 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/8.0)** — для запуска готового `DynamicIsland.exe` *(или **[.NET 8 SDK (x64)](https://dotnet.microsoft.com/download/dotnet/8.0)**, если планируете собирать проект из исходников)*
- **Microsoft Edge WebView2 Runtime** (предустановлен в Windows 11)
- **[yt-dlp](https://github.com/yt-dlp/yt-dlp)** *(опционально — для резервного резолва аудиопотоков; можно установить через `winget install yt-dlp.yt-dlp` или положить `yt-dlp.exe` рядом с `DynamicIsland.exe`)*

## Сборка из исходников (для разработчиков)

Если хотите пересобрать проект самостоятельно:

1. Установите **[.NET 8 SDK (x64)](https://dotnet.microsoft.com/download/dotnet/8.0)** *(именно **SDK**, не Runtime; скрипт [`build.bat`](build.bat) также проверяет его наличие и при необходимости предлагает установку через `winget`)*.
2. Скачайте исходный код (`git clone https://github.com/yammikid/OmniWave.git` или **Code → Download ZIP** и распакуйте архив).
3. Запустите [`build.bat`](build.bat) — скрипт соберёт проект в папку `publish/` и автоматически запустит `publish\DynamicIsland.exe` — либо выполните команды вручную:

```powershell
# Клонирование репозитория
git clone https://github.com/yammikid/OmniWave.git
cd OmniWave

# Сборка готового приложения в папку publish
dotnet publish -c Release -r win-x64 --self-contained false -o ./publish

# Запуск собранного приложения
.\publish\DynamicIsland.exe
```

## Приватность и безопасность

Все OAuth-токены, cookie авторизации, локальные плейлисты, избранное и пользовательские настройки сохраняются **строго локально** в папке `%AppData%\OmniWave` (`settings.json`, `library.json`, кэш WebView2) и **никогда не включаются в репозиторий**.
