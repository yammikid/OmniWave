using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace DynamicIsland.Services;

public class TrackItem : INotifyPropertyChanged
{
    private bool _isLiked;
    private bool _isCurrentlyPlaying;
    private ImageSource? _coverImage;
    private bool _isDownloading;
    private int _downloadProgress;
    private bool _isDownloaded;
    private string? _localFilePath;

    public string Id { get; set; } = "";
    public string Source { get; set; } = "";      // "SoundCloud", "YouTube", "Интернет"
    public string BrandKey { get; set; } = "";    // "soundcloud", "youtube", "web"
    public string BrandColor { get; set; } = "#FFCC00";
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string PageUrl { get; set; } = "";
    public string CoverUrl { get; set; } = "";
    public TimeSpan Duration { get; set; }

    [JsonIgnore]
    public string UniqueKey => $"{BrandKey}:{PageUrl}";

    [JsonIgnore]
    public string DurationText => Duration.TotalSeconds > 0
        ? (Duration.TotalHours >= 1 ? Duration.ToString(@"h\:mm\:ss") : Duration.ToString(@"m\:ss"))
        : (string.Equals(BrandKey, "radio", StringComparison.OrdinalIgnoreCase) ? "LIVE" : "");

    [JsonIgnore]
    public bool IsLiked
    {
        get => _isLiked;
        set
        {
            if (_isLiked == value) return;
            _isLiked = value;
            OnPropertyChanged();
        }
    }

    [JsonIgnore]
    public bool IsCurrentlyPlaying
    {
        get => _isCurrentlyPlaying;
        set
        {
            if (_isCurrentlyPlaying == value) return;
            _isCurrentlyPlaying = value;
            OnPropertyChanged();
        }
    }

    [JsonIgnore]
    public ImageSource? CoverImage
    {
        get => _coverImage;
        set
        {
            if (Equals(_coverImage, value)) return;
            _coverImage = value;
            OnPropertyChanged();
        }
    }

    [JsonIgnore]
    public bool IsDownloading
    {
        get => _isDownloading;
        set
        {
            if (_isDownloading == value) return;
            _isDownloading = value;
            OnPropertyChanged();
        }
    }

    [JsonIgnore]
    public int DownloadProgress
    {
        get => _downloadProgress;
        set
        {
            int clamped = Math.Clamp(value, 0, 100);
            if (_downloadProgress == clamped) return;
            _downloadProgress = clamped;
            OnPropertyChanged();
        }
    }

    [JsonIgnore]
    public bool IsDownloaded
    {
        get => _isDownloaded;
        set
        {
            if (_isDownloaded == value) return;
            _isDownloaded = value;
            OnPropertyChanged();
        }
    }

    [JsonIgnore]
    public string? LocalFilePath
    {
        get => _localFilePath;
        set
        {
            if (_localFilePath == value) return;
            _localFilePath = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public TrackItem Clone() => new()
    {
        Id = Id,
        Source = Source,
        BrandKey = BrandKey,
        BrandColor = BrandColor,
        Title = Title,
        Artist = Artist,
        PageUrl = PageUrl,
        CoverUrl = CoverUrl,
        Duration = Duration,
        IsLiked = IsLiked,
        IsCurrentlyPlaying = false,
        CoverImage = CoverImage,
        IsDownloaded = IsDownloaded,
        LocalFilePath = LocalFilePath
    };
}

public interface IMusicProvider
{
    string Name { get; }
    string BrandKey { get; }
    Task<List<TrackItem>> SearchAsync(string query, int limit = 8, CancellationToken ct = default);
    Task<string?> ResolveStreamUrlAsync(TrackItem track, CancellationToken ct = default);
}
