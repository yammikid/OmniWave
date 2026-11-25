using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace DynamicIsland.Services;

public class AppSettings
{
    public static string DefaultDownloadFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
        "OmniWave");

    public double? AnchorCenterX { get; set; }
    public double? AnchorTopY { get; set; }
    public bool StartMinimizedToIsland { get; set; }
    public bool AutoPauseOnSwitch { get; set; } = true;
    public bool ShowEqualizerOnIsland { get; set; } = true;
    public bool CoverGlowOnIsland { get; set; } = true;
    public double IslandScale { get; set; } = 1.0;
    public double IslandOpacity { get; set; } = 0.94;
    public bool PinIslandExpanded { get; set; }
    public bool IslandAlwaysOnTop { get; set; } = true;
    public int Volume { get; set; } = 80;
    public bool EqualizerEnabled { get; set; }
    public string EqualizerPreset { get; set; } = "Flat";
    public float EqualizerPreamp { get; set; }
    public float[] EqualizerBands { get; set; } = new float[10];
    public float PlaybackRate { get; set; } = 1.0f;
    public string DownloadFolder { get; set; } = DefaultDownloadFolder;

    public bool EnableIslandAcrylic { get; set; } = true;
    public bool EnableIslandMarquee { get; set; } = true;
    public bool IsSidebarCollapsed { get; set; } = false;

    public string? VkAccessToken { get; set; }
    public string? VkCookieHeader { get; set; }
    public long VkUserId { get; set; }
    public string? VkUserName { get; set; }

    public string? YandexToken { get; set; }
    public long YandexUserId { get; set; }
    public string? YandexUserName { get; set; }

    public string IslandDockEdge { get; set; } = "None"; // "None", "Left", "Right", "Top", "Bottom"
    public double? IslandDockOffset { get; set; }

    public bool ProxyEnabled { get; set; }
    public string ProxyType { get; set; } = "http"; // "http", "https", "socks5", "socks4"
    public string ProxyHost { get; set; } = "";
    public int ProxyPort { get; set; } = 8080;
    public string ProxyUsername { get; set; } = "";
    public string ProxyPassword { get; set; } = "";
    public bool ProxyBypassRuServices { get; set; } = true;

    public void Normalize()
    {
        if (AnchorCenterX.HasValue && (double.IsNaN(AnchorCenterX.Value) || double.IsInfinity(AnchorCenterX.Value)))
            AnchorCenterX = null;
        if (AnchorTopY.HasValue && (double.IsNaN(AnchorTopY.Value) || double.IsInfinity(AnchorTopY.Value)))
            AnchorTopY = null;
        if (IslandDockOffset.HasValue && (double.IsNaN(IslandDockOffset.Value) || double.IsInfinity(IslandDockOffset.Value)))
            IslandDockOffset = null;

        IslandDockEdge = IslandDockEdge is "Left" or "Right" or "Top" or "Bottom" ? IslandDockEdge : "None";
        VkUserId = Math.Max(0, VkUserId);
        YandexUserId = Math.Max(0, YandexUserId);

        IslandScale = Math.Clamp(double.IsNaN(IslandScale) ? 1.0 : IslandScale, 0.85, 1.30);
        IslandOpacity = Math.Clamp(double.IsNaN(IslandOpacity) ? 0.94 : IslandOpacity, 0.50, 1.0);
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
    }
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
        try
        {
            if (File.Exists(_settingsPath))
            {
                var json = File.ReadAllText(_settingsPath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json);
                if (loaded != null)
                {
                    loaded.Normalize();
                    if (loaded.EqualizerEnabled &&
                        string.Equals(loaded.EqualizerPreset, "Flat", StringComparison.OrdinalIgnoreCase) &&
                        MathF.Abs(loaded.EqualizerPreamp) < 0.01f &&
                        loaded.EqualizerBands.All(b => MathF.Abs(b) < 0.01f))
                    {
                        loaded.EqualizerEnabled = false;
                    }
                    return loaded;
                }
            }
        }
        catch { }

        var defaults = new AppSettings();
        defaults.Normalize();
        return defaults;
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
