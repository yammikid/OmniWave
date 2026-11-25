using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using DynamicIsland.Services;
using Microsoft.Win32;

namespace DynamicIsland;

public partial class AccountWindow : Window
{
    private readonly SettingsService _settings;
    private readonly YandexMusicProvider _yandex;
    private readonly VkMusicProvider _vk;
    private readonly SpotifyMusicProvider _spotify;
    private readonly LibraryService _library;
    private readonly Func<Task<string>> _syncAccountsAsync;

    private bool _yandexClearedSession;
    private bool _vkClearedSession;
    private bool _spotifyClearedSession;

    public AccountWindow(
        SettingsService settings,
        YandexMusicProvider yandex,
        VkMusicProvider vk,
        SpotifyMusicProvider spotify,
        LibraryService library,
        Func<Task<string>> syncAccountsAsync)
    {
        InitializeComponent();
        _settings = settings;
        _yandex = yandex;
        _vk = vk;
        _spotify = spotify;
        _library = library;
        _syncAccountsAsync = syncAccountsAsync;

        YandexManualTokenBox.TextChanged += (_, _) =>
        {
            YandexManualTokenPlaceholder.Visibility = string.IsNullOrEmpty(YandexManualTokenBox.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;
        };

        VkManualCookieBox.TextChanged += (_, _) =>
        {
            VkManualCookiePlaceholder.Visibility = string.IsNullOrEmpty(VkManualCookieBox.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;
        };

        SpotifyManualTokenBox.TextChanged += (_, _) =>
        {
            SpotifyManualTokenPlaceholder.Visibility = string.IsNullOrEmpty(SpotifyManualTokenBox.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;
        };

        RefreshConnectionState();
    }

    private void RefreshConnectionState()
    {
        var s = _settings.Current;

        if (_yandex.IsAuthenticated)
        {
            YandexStatusText.Text = $"Подключён: {s.YandexUserName ?? $"uid{s.YandexUserId}"}";
            YandexStatusText.Foreground = new SolidColorBrush(Color.FromRgb(255, 204, 0));
            BtnSyncYandex.Visibility = Visibility.Visible;
            BtnDisconnectYandex.Visibility = Visibility.Visible;
        }
        else
        {
            YandexStatusText.Text = "Не подключён";
            YandexStatusText.Foreground = new SolidColorBrush(Color.FromRgb(136, 137, 156));
            BtnSyncYandex.Visibility = Visibility.Collapsed;
            BtnDisconnectYandex.Visibility = Visibility.Collapsed;
        }

        if (_vk.IsAuthenticated)
        {
            VkStatusText.Text = $"Подключён: {s.VkUserName ?? $"id{s.VkUserId}"}";
            VkStatusText.Foreground = new SolidColorBrush(Color.FromRgb(77, 163, 255));
            BtnSyncVk.Visibility = Visibility.Visible;
            BtnDisconnectVk.Visibility = Visibility.Visible;
        }
        else
        {
            VkStatusText.Text = "Не подключён";
            VkStatusText.Foreground = new SolidColorBrush(Color.FromRgb(136, 137, 156));
            BtnSyncVk.Visibility = Visibility.Collapsed;
            BtnDisconnectVk.Visibility = Visibility.Collapsed;
        }

        if (_spotify.IsAuthenticated)
        {
            SpotifyStatusText.Text = $"Подключён: {(!string.IsNullOrWhiteSpace(s.SpotifyUserName) ? s.SpotifyUserName : s.SpotifyUserId)}";
            SpotifyStatusText.Foreground = new SolidColorBrush(Color.FromRgb(29, 185, 84));
            BtnSyncSpotify.Visibility = Visibility.Visible;
            BtnDisconnectSpotify.Visibility = Visibility.Visible;
        }
        else
        {
            SpotifyStatusText.Text = "Не подключён";
            SpotifyStatusText.Foreground = new SolidColorBrush(Color.FromRgb(136, 137, 156));
            BtnSyncSpotify.Visibility = Visibility.Collapsed;
            BtnDisconnectSpotify.Visibility = Visibility.Collapsed;
        }

        if (YtDlpMusicProvider.HasYouTubeCookies)
        {
            YouTubeStatusText.Text = "Cookies YouTube подключены";
            YouTubeStatusText.Foreground = new SolidColorBrush(Color.FromRgb(255, 77, 106));
            BtnClearYouTube.Visibility = Visibility.Visible;
        }
        else
        {
            YouTubeStatusText.Text = "Не подключён (опционально)";
            YouTubeStatusText.Foreground = new SolidColorBrush(Color.FromRgb(136, 137, 156));
            BtnClearYouTube.Visibility = Visibility.Collapsed;
        }
    }

    #region Яндекс Музыка

    private async void LoginYandex_Click(object sender, RoutedEventArgs e)
    {
        BtnLoginYandex.IsEnabled = false;
        BtnConnectYandexManual.IsEnabled = false;
        TxtStatus.Text = "Выполните вход в открывшемся окне Яндекса...";

        try
        {
            var token = await AuthBrowserWindow.ShowYandexAuthAsync(this, _yandexClearedSession);
            if (string.IsNullOrWhiteSpace(token))
            {
                TxtStatus.Text = "Авторизация Яндекс Музыки отменена.";
                return;
            }

            TxtStatus.Text = "Проверка токена и загрузка треков из Яндекс Музыки...";
            var (ok, message) = await _yandex.VerifyAndSaveTokenAsync(token);
            await ApplyYandexAuthResultAsync(ok, message);
        }
        finally
        {
            BtnLoginYandex.IsEnabled = true;
            BtnConnectYandexManual.IsEnabled = true;
        }
    }

    private void ToggleYandexManual_Click(object sender, RoutedEventArgs e)
    {
        YandexManualPanel.Visibility = YandexManualPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private async void ConnectYandexManual_Click(object sender, RoutedEventArgs e)
    {
        var raw = YandexManualTokenBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            TxtStatus.Text = "Вставьте OAuth-токен (y0_...) или итоговый URL.";
            return;
        }

        BtnLoginYandex.IsEnabled = false;
        BtnConnectYandexManual.IsEnabled = false;
        TxtStatus.Text = "Проверка OAuth-токена Яндекс Музыки...";

        try
        {
            var (ok, message) = await _yandex.VerifyAndSaveTokenAsync(raw);
            if (await ApplyYandexAuthResultAsync(ok, message))
            {
                YandexManualTokenBox.Text = "";
                YandexManualPanel.Visibility = Visibility.Collapsed;
            }
        }
        finally
        {
            BtnLoginYandex.IsEnabled = true;
            BtnConnectYandexManual.IsEnabled = true;
        }
    }

    private async Task<bool> ApplyYandexAuthResultAsync(bool ok, string message)
    {
        if (!ok)
        {
            TxtStatus.Text = message;
            return false;
        }

        _yandexClearedSession = false;
        RefreshConnectionState();
        TxtStatus.Text = $"{message}. Загрузка «Мне нравится» и плейлистов...";
        var playlists = await _yandex.FetchUserPlaylistsAsync();
        _library.ReplaceExternalPlaylists("yandex", playlists);
        int totalTracks = playlists.Sum(p => p.Tracks.Count);
        TxtStatus.Text = $"{message} • Плейлистов: {playlists.Count}, треков: {totalTracks}";
        return true;
    }

    private async void SyncYandex_Click(object sender, RoutedEventArgs e)
    {
        if (!_yandex.IsAuthenticated) return;
        BtnSyncYandex.IsEnabled = false;
        TxtStatus.Text = "Обновление «Мне нравится» и плейлистов Яндекс Музыки...";
        try
        {
            var pls = await _yandex.FetchUserPlaylistsAsync();
            _library.ReplaceExternalPlaylists("yandex", pls);
            TxtStatus.Text = $"Яндекс Музыка: плейлистов {pls.Count}, треков {pls.Sum(p => p.Tracks.Count)}";
        }
        catch (Exception ex)
        {
            TxtStatus.Text = $"Ошибка синхронизации Яндекс: {ex.Message}";
        }
        finally
        {
            BtnSyncYandex.IsEnabled = true;
        }
    }

    private void DisconnectYandex_Click(object sender, RoutedEventArgs e)
    {
        _yandexClearedSession = true;
        _yandex.Disconnect();
        _library.RemoveExternalPlaylists("yandex");
        RefreshConnectionState();
        TxtStatus.Text = "Вы вышли из аккаунта Яндекс Музыки.";
    }

    private void ImportYandexJson_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Импорт списка треков (формат yandex2spotify JSON)",
            Filter = "JSON файлы (*.json)|*.json|Все файлы (*.*)|*.*"
        };

        if (dlg.ShowDialog(this) != true) return;

        try
        {
            var json = File.ReadAllText(dlg.FileName);
            var tracks = YandexMusicProvider.ParseYandex2SpotifyJson(json);
            if (tracks.Count == 0)
            {
                TxtStatus.Text = "В JSON-файле не найдено треков формата [{\"artist\":\"...\",\"track\":\"...\"}].";
                return;
            }

            var name = $"Импорт • {Path.GetFileNameWithoutExtension(dlg.FileName)}";
            _library.AddImportedPlaylist(name, tracks);
            TxtStatus.Text = $"Импортирован плейлист «{name}» ({tracks.Count} треков).";
        }
        catch (Exception ex)
        {
            TxtStatus.Text = $"Ошибка импорта JSON: {ex.Message}";
        }
    }

    #endregion

    #region ВК Музыка

    private async void LoginVk_Click(object sender, RoutedEventArgs e)
    {
        BtnLoginVk.IsEnabled = false;
        BtnConnectVkManual.IsEnabled = false;
        TxtStatus.Text = "Выполните вход в открывшемся окне ВКонтакте...";

        try
        {
            var (tokenOrInput, cookieHeader, userId, userName) = await AuthBrowserWindow.ShowVkAuthAsync(this, _vkClearedSession);
            if (string.IsNullOrWhiteSpace(tokenOrInput) && string.IsNullOrWhiteSpace(cookieHeader))
            {
                TxtStatus.Text = "Авторизация ВКонтакте отменена.";
                return;
            }

            TxtStatus.Text = "Проверка профиля ВКонтакте и загрузка музыки...";
            var (ok, message) = await _vk.VerifyAndSaveSessionAsync(tokenOrInput, cookieHeader, userId, userName);
            await ApplyVkAuthResultAsync(ok, message);
        }
        finally
        {
            BtnLoginVk.IsEnabled = true;
            BtnConnectVkManual.IsEnabled = true;
        }
    }

    private void ToggleVkManual_Click(object sender, RoutedEventArgs e)
    {
        VkManualPanel.Visibility = VkManualPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private async void ConnectVkManual_Click(object sender, RoutedEventArgs e)
    {
        var raw = VkManualCookieBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            TxtStatus.Text = "Вставьте токен (vk1.a...), URL с access_token= или cookie remixsid=...";
            return;
        }

        BtnLoginVk.IsEnabled = false;
        BtnConnectVkManual.IsEnabled = false;
        TxtStatus.Text = "Проверка профиля и токена ВКонтакте...";

        try
        {
            var (ok, message) = await _vk.VerifyAndSaveSessionAsync(raw, null, 0, null);
            if (await ApplyVkAuthResultAsync(ok, message))
            {
                VkManualCookieBox.Text = "";
                VkManualPanel.Visibility = Visibility.Collapsed;
            }
        }
        finally
        {
            BtnLoginVk.IsEnabled = true;
            BtnConnectVkManual.IsEnabled = true;
        }
    }

    private async Task<bool> ApplyVkAuthResultAsync(bool ok, string message)
    {
        if (!ok)
        {
            TxtStatus.Text = message;
            return false;
        }

        _vkClearedSession = false;
        RefreshConnectionState();
        TxtStatus.Text = $"{message}. Загрузка «Моей музыки» и плейлистов...";
        var playlists = await _vk.FetchUserPlaylistsAsync();
        _library.ReplaceExternalPlaylists("vk", playlists);
        TxtStatus.Text = $"{message} • Плейлистов: {playlists.Count}, треков: {playlists.Sum(p => p.Tracks.Count)}";
        return true;
    }

    private async void SyncVk_Click(object sender, RoutedEventArgs e)
    {
        if (!_vk.IsAuthenticated) return;
        BtnSyncVk.IsEnabled = false;
        TxtStatus.Text = "Обновление плейлистов ВК Музыки...";
        try
        {
            var pls = await _vk.FetchUserPlaylistsAsync();
            _library.ReplaceExternalPlaylists("vk", pls);
            TxtStatus.Text = $"ВК Музыка: плейлистов {pls.Count}, треков {pls.Sum(p => p.Tracks.Count)}";
        }
        catch (Exception ex)
        {
            TxtStatus.Text = $"Ошибка синхронизации ВК: {ex.Message}";
        }
        finally
        {
            BtnSyncVk.IsEnabled = true;
        }
    }

    private void DisconnectVk_Click(object sender, RoutedEventArgs e)
    {
        _vkClearedSession = true;
        _vk.Disconnect();
        _library.RemoveExternalPlaylists("vk");
        RefreshConnectionState();
        TxtStatus.Text = "Вы вышли из аккаунта ВКонтакте.";
    }

    #endregion

    #region Spotify

    private async void LoginSpotify_Click(object sender, RoutedEventArgs e)
    {
        BtnLoginSpotify.IsEnabled = false;
        BtnConnectSpotifyManual.IsEnabled = false;
        TxtStatus.Text = "Выполните вход в открывшемся окне Spotify...";

        try
        {
            var (token, spDc, expMs, userName, userId, clientToken) = await AuthBrowserWindow.ShowSpotifyAuthAsync(this, _spotifyClearedSession);
            if (string.IsNullOrWhiteSpace(token) && string.IsNullOrWhiteSpace(spDc))
            {
                TxtStatus.Text = "Авторизация Spotify отменена.";
                return;
            }

            TxtStatus.Text = "Проверка профиля Spotify и загрузка медиатеки...";
            var (ok, message) = await _spotify.VerifyAndSaveSessionAsync(token, spDc, expMs, userName, userId, clientToken);
            await ApplySpotifyAuthResultAsync(ok, message);
        }
        finally
        {
            BtnLoginSpotify.IsEnabled = true;
            BtnConnectSpotifyManual.IsEnabled = true;
        }
    }

    private void ToggleSpotifyManual_Click(object sender, RoutedEventArgs e)
    {
        SpotifyManualPanel.Visibility = SpotifyManualPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private async void ConnectSpotifyManual_Click(object sender, RoutedEventArgs e)
    {
        var raw = SpotifyManualTokenBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            TxtStatus.Text = "Вставьте cookie sp_dc=... или Bearer-токен (BQ...).";
            return;
        }

        BtnLoginSpotify.IsEnabled = false;
        BtnConnectSpotifyManual.IsEnabled = false;
        TxtStatus.Text = "Проверка токена и сессии Spotify...";

        try
        {
            var (ok, message) = await _spotify.VerifyAndSaveSessionAsync(raw, null, 0, null);
            if (await ApplySpotifyAuthResultAsync(ok, message))
            {
                SpotifyManualTokenBox.Text = "";
                SpotifyManualPanel.Visibility = Visibility.Collapsed;
            }
        }
        finally
        {
            BtnLoginSpotify.IsEnabled = true;
            BtnConnectSpotifyManual.IsEnabled = true;
        }
    }

    private async Task<bool> ApplySpotifyAuthResultAsync(bool ok, string message)
    {
        if (!ok)
        {
            TxtStatus.Text = message;
            return false;
        }

        _spotifyClearedSession = false;
        RefreshConnectionState();
        TxtStatus.Text = $"{message}. Загрузка «Любимых треков» и плейлистов...";
        var playlists = await _spotify.FetchUserPlaylistsAsync();
        _library.ReplaceExternalPlaylists("spotify", playlists);
        TxtStatus.Text = $"{message} • Плейлистов: {playlists.Count}, треков: {playlists.Sum(p => p.Tracks.Count)}";
        return true;
    }

    private async void SyncSpotify_Click(object sender, RoutedEventArgs e)
    {
        if (!_spotify.IsAuthenticated) return;
        BtnSyncSpotify.IsEnabled = false;
        TxtStatus.Text = "Обновление плейлистов Spotify...";
        try
        {
            var pls = await _spotify.FetchUserPlaylistsAsync();
            _library.ReplaceExternalPlaylists("spotify", pls);
            TxtStatus.Text = $"Spotify: плейлистов {pls.Count}, треков {pls.Sum(p => p.Tracks.Count)}";
        }
        catch (Exception ex)
        {
            TxtStatus.Text = $"Ошибка синхронизации Spotify: {ex.Message}";
        }
        finally
        {
            BtnSyncSpotify.IsEnabled = true;
        }
    }

    private void DisconnectSpotify_Click(object sender, RoutedEventArgs e)
    {
        _spotifyClearedSession = true;
        _spotify.Disconnect();
        _library.RemoveExternalPlaylists("spotify");
        RefreshConnectionState();
        TxtStatus.Text = "Вы вышли из аккаунта Spotify.";
    }

    #endregion

    #region YouTube

    private bool _ytClearedSession;

    private async void LoginYouTube_Click(object sender, RoutedEventArgs e)
    {
        BtnLoginYouTube.IsEnabled = false;
        BtnImportYouTubeCookies.IsEnabled = false;
        TxtStatus.Text = "Выполните вход в открывшемся окне YouTube...";

        try
        {
            bool saved = await AuthBrowserWindow.ShowYouTubeAuthAsync(this, _ytClearedSession);
            if (saved)
            {
                _ytClearedSession = false;
                RefreshConnectionState();
                TxtStatus.Text = "YouTube cookies сохранены для работы через прокси.";
            }
            else
            {
                TxtStatus.Text = "Вход в YouTube отменён.";
            }
        }
        finally
        {
            BtnLoginYouTube.IsEnabled = true;
            BtnImportYouTubeCookies.IsEnabled = true;
        }
    }

    private void ImportYouTubeCookies_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Импорт Netscape cookies.txt для YouTube",
            Filter = "Cookies файлы (*.txt)|*.txt|Все файлы (*.*)|*.*"
        };

        if (dlg.ShowDialog(this) != true) return;

        try
        {
            var content = File.ReadAllText(dlg.FileName);
            if (!content.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) &&
                !content.Contains(".google.com", StringComparison.OrdinalIgnoreCase))
            {
                TxtStatus.Text = "Выбранный файл не содержит cookies YouTube / Google.";
                return;
            }

            var dir = Path.GetDirectoryName(YtDlpMusicProvider.YouTubeCookiesPath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(YtDlpMusicProvider.YouTubeCookiesPath, content, new System.Text.UTF8Encoding(false));
            _ytClearedSession = false;
            RefreshConnectionState();
            TxtStatus.Text = "Файл cookies.txt для YouTube успешно подключён.";
        }
        catch (Exception ex)
        {
            TxtStatus.Text = $"Ошибка импорта cookies.txt: {ex.Message}";
        }
    }

    private void ClearYouTube_Click(object sender, RoutedEventArgs e)
    {
        _ytClearedSession = true;
        YtDlpMusicProvider.ClearYouTubeCookies();
        RefreshConnectionState();
        TxtStatus.Text = "YouTube cookies удалены.";
    }

    #endregion

    private async void SyncNow_Click(object sender, RoutedEventArgs e)
    {
        BtnSyncNow.IsEnabled = false;
        TxtStatus.Text = "Синхронизация плейлистов...";
        TxtStatus.Text = await _syncAccountsAsync();
        BtnSyncNow.IsEnabled = true;
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch { }
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
