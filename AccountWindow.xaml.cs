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
    private readonly LibraryService _library;
    private readonly Func<Task<string>> _syncAccountsAsync;

    private bool _yandexClearedSession;
    private bool _vkClearedSession;

    public AccountWindow(
        SettingsService settings,
        YandexMusicProvider yandex,
        VkMusicProvider vk,
        LibraryService library,
        Func<Task<string>> syncAccountsAsync)
    {
        InitializeComponent();
        _settings = settings;
        _yandex = yandex;
        _vk = vk;
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
