namespace DynamicIsland.Services;

public record SourceItem(string SourceId, string Name, string Brand);

public class MediaState
{
    public List<SourceItem> Sources { get; set; } = new();
    public string? ActiveId { get; set; }
    public string ActiveBrand { get; set; } = "default";
    public string SourceBadgeText { get; set; } = "";
    public string Title { get; set; } = "Ничего не играет";
    public string Artist { get; set; } = "Откройте ВК, Яндекс или поиск";
    public bool IsPlaying { get; set; }
    public string CoverUrl { get; set; } = "";
    public byte[]? CoverBytes { get; set; }
    public bool CoverUpdated { get; set; }
    public long CurrentMs { get; set; }
    public long TotalMs { get; set; }
}
