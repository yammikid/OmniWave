using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace DynamicIsland.Services;

public class AppSettings
{
    public static string DefaultDownloadFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
        "OmniWave");

    public bool StartMinimizedToIsland { get; set; }
    public bool AutoPauseOnSwitch { get; set; } = true;
    public int Volume { get; set; } = 80;
    public bool EqualizerEnabled { get; set; }
    public string EqualizerPreset { get; set; } = "Flat";
    public float EqualizerPreamp { get; set; }
    public float[] EqualizerBands { get; set; } = new float[10];
    public float PlaybackRate { get; set; } = 1.0f;
    public string DownloadFolder { get; set; } = DefaultDownloadFolder;

    public double? PlayerWindowLeft { get; set; }
    public double? PlayerWindowTop { get; set; }
    public bool IsCompactMode { get; set; }
    public bool WindowAlwaysOnTop { get; set; } = true;
    public bool MinimizeToTray { get; set; } = true;
    public bool CompactEdgeAutoHide { get; set; } = true;
    public string CompactDockedEdge { get; set; } = "";
    public int ChromaIntensity { get; set; } = 67;
    public int WindowScalePercent { get; set; } = 100;
    public int WindowOpacityPercent { get; set; } = 100;
    public bool ZeroBlurMode { get; set; } = true;

    public string? VkAccessToken { get; set; }
    public string? VkCookieHeader { get; set; }
    public long VkUserId { get; set; }
    public string? VkUserName { get; set; }

    public string? YandexToken { get; set; }
    public long YandexUserId { get; set; }
    public string? YandexUserName { get; set; }

    public string? SpotifyAccessToken { get; set; }
    public string? SpotifyClientToken { get; set; }
    public string? SpotifySpDcCookie { get; set; }
    public string? SpotifyRefreshToken { get; set; }
    public long SpotifyTokenExpiresUnixMs { get; set; }
    public string? SpotifyUserId { get; set; }
    public string? SpotifyUserName { get; set; }

    public bool ProxyEnabled { get; set; }
    public string ProxyType { get; set; } = "http"; // "http", "https", "socks5", "socks4"
    public string ProxyHost { get; set; } = "";
    public int ProxyPort { get; set; } = 8080;
    public string ProxyUsername { get; set; } = "";
    public string ProxyPassword { get; set; } = "";
    public bool ProxyBypassRuServices { get; set; } = true;

    public bool AliceApiEnabled { get; set; } = true;
    public int AliceApiPort { get; set; } = 47510;
    public string AliceApiToken { get; set; } = "";

    public void Normalize()
    {
        if (PlayerWindowLeft.HasValue && (double.IsNaN(PlayerWindowLeft.Value) || double.IsInfinity(PlayerWindowLeft.Value)))
            PlayerWindowLeft = null;
        if (PlayerWindowTop.HasValue && (double.IsNaN(PlayerWindowTop.Value) || double.IsInfinity(PlayerWindowTop.Value)))
            PlayerWindowTop = null;

        var edge = (CompactDockedEdge ?? "").Trim().ToLowerInvariant();
        CompactDockedEdge = edge is "left" or "right" or "top" ? edge : "";

        ChromaIntensity = Math.Clamp(ChromaIntensity, 0, 100);
        if (WindowScalePercent <= 0) WindowScalePercent = 100;
        WindowScalePercent = Math.Clamp(WindowScalePercent, 80, 140);
        WindowOpacityPercent = 100;

        VkUserId = Math.Max(0, VkUserId);
        YandexUserId = Math.Max(0, YandexUserId);
        SpotifyTokenExpiresUnixMs = Math.Max(0, SpotifyTokenExpiresUnixMs);

        Volume = Math.Clamp(Volume, 0, 100);
        EqualizerPreset = string.IsNullOrWhiteSpace(EqualizerPreset) ? "Flat" : EqualizerPreset;
        EqualizerPreamp = Math.Clamp(float.IsNaN(EqualizerPreamp) ? 0f : EqualizerPreamp, -12f, 12f);

        var bands = new float[10];
        if (EqualizerBands != null)
        {
            for (int i = 0; i < Math.Min(10, EqualizerBands.Length); i++)
            {
                var v = EqualizerBands[i];
                bands[i] = Math.Clamp(float.IsNaN(v) ? 0f : v, -12f, 12f);
            }
        }
        EqualizerBands = bands;

        PlaybackRate = Math.Clamp(float.IsNaN(PlaybackRate) ? 1.0f : PlaybackRate, 0.75f, 1.5f);
        if (string.IsNullOrWhiteSpace(DownloadFolder))
            DownloadFolder = DefaultDownloadFolder;

        ProxyType = ProxyService.NormalizeType(ProxyType);
        ProxyHost = (ProxyHost ?? "").Trim();
        ProxyPort = ProxyService.NormalizePort(ProxyType, ProxyPort);
        ProxyUsername = (ProxyUsername ?? "").Trim();
        ProxyPassword ??= "";

        if (AliceApiPort <= 0) AliceApiPort = 47510;
        AliceApiPort = Math.Clamp(AliceApiPort, 1024, 65535);
        AliceApiToken = (AliceApiToken ?? "").Trim();
        if (string.IsNullOrWhiteSpace(AliceApiToken))
            AliceApiToken = GenerateAliceToken();
    }

    public static string GenerateAliceToken()
        => System.Security.Cryptography.RandomNumberGenerator.GetHexString(24).ToLowerInvariant();
}

public class SettingsService
{
    private const string RunRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AutoStartValueName = "OmniWaveDynamicIsland";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _settingsPath;
    private readonly object _lock = new();

    public AppSettings Current { get; private set; }
    public event Action<AppSettings>? SettingsChanged;

    public SettingsService()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "OmniWave");
        Directory.CreateDirectory(dir);
        _settingsPath = Path.Combine(dir, "settings.json");

        Current = Load();
        ProxyService.Apply(Current);
    }

    private AppSettings Load()
    {
        bool fileExisted = File.Exists(_settingsPath);
        try
        {
            if (fileExisted)
            {
                var json = File.ReadAllText(_settingsPath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json);
                if (loaded != null)
                {
                    bool hadToken = !string.IsNullOrWhiteSpace(loaded.AliceApiToken);
                    bool hadScale = loaded.WindowScalePercent > 0;
                    loaded.Normalize();
                    if (loaded.EqualizerEnabled &&
                        string.Equals(loaded.EqualizerPreset, "Flat", StringComparison.OrdinalIgnoreCase) &&
                        MathF.Abs(loaded.EqualizerPreamp) < 0.01f &&
                        loaded.EqualizerBands.All(b => MathF.Abs(b) < 0.01f))
                    {
                        loaded.EqualizerEnabled = false;
                    }
                    if (!hadToken || !hadScale)
                        TryWriteSettingsUnlocked(loaded);
                    return loaded;
                }
            }
        }
        catch { }

        var defaults = new AppSettings();
        defaults.Normalize();
        if (!fileExisted)
            TryWriteSettingsUnlocked(defaults);
        return defaults;
    }

    private void TryWriteSettingsUnlocked(AppSettings settings)
    {
        try
        {
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(_settingsPath, json);
        }
        catch { }
    }

    public void Save(bool notify = true)
    {
        lock (_lock)
        {
            Current.Normalize();
            ProxyService.Apply(Current);
            try
            {
                var json = JsonSerializer.Serialize(Current, JsonOptions);
                File.WriteAllText(_settingsPath, json);
            }
            catch { }
        }

        if (notify)
            SettingsChanged?.Invoke(Current);
    }

    public void Update(Action<AppSettings> mutate, bool notify = true)
    {
        lock (_lock)
        {
            mutate(Current);
            Current.Normalize();
            ProxyService.Apply(Current);
            try
            {
                var json = JsonSerializer.Serialize(Current, JsonOptions);
                File.WriteAllText(_settingsPath, json);
            }
            catch { }
        }

        if (notify)
            SettingsChanged?.Invoke(Current);
    }

    public bool IsAutoStartEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunRegistryPath, writable: false);
            var val = key?.GetValue(AutoStartValueName) as string;
            return !string.IsNullOrWhiteSpace(val);
        }
        catch
        {
            return false;
        }
    }

    public void SetAutoStart(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunRegistryPath, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunRegistryPath, writable: true);
            if (key == null) return;

            if (enabled)
            {
                var exePath = Environment.ProcessPath;
                if (!string.IsNullOrWhiteSpace(exePath))
                    key.SetValue(AutoStartValueName, $"\"{exePath}\"");
            }
            else
            {
                key.DeleteValue(AutoStartValueName, throwOnMissingValue: false);
            }
        }
        catch { }
    }
}
