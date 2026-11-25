using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DynamicIsland.Services;

public class UserPlaylist
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Новый плейлист";
    public bool IsPinned { get; set; }
    public bool IsExternal { get; set; }
    public string PlatformBrand { get; set; } = "local"; // "local", "yandex", "vk", "spotify"
    public string ExternalId { get; set; } = "";
    public string CoverUrl { get; set; } = "";
    public List<TrackItem> Tracks { get; set; } = new();

    [JsonIgnore]
    public int TrackCount => Tracks.Count;

    [JsonIgnore]
    public string CountText => FormatTrackCount(Tracks.Count);

    [JsonIgnore]
    public bool ShowPlatformBadge => IsExternal &&
        (string.Equals(PlatformBrand, "yandex", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(PlatformBrand, "vk", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(PlatformBrand, "spotify", StringComparison.OrdinalIgnoreCase));

    [JsonIgnore]
    public string PlatformBadgeText => PlatformBrand?.ToLowerInvariant() switch
    {
        "yandex" => "Яндекс",
        "vk" => "ВК",
        "spotify" => "Spotify",
        _ => ""
    };

    [JsonIgnore]
    public string PlatformBadgeColor => PlatformBrand?.ToLowerInvariant() switch
    {
        "yandex" => "#FFCC00",
        "vk" => "#0077FF",
        "spotify" => "#1DB954",
        _ => "Transparent"
    };

    [JsonIgnore]
    public string PlatformBadgeForeground => PlatformBrand?.ToLowerInvariant() switch
    {
        "yandex" => "#111116",
        "vk" => "#FFFFFF",
        "spotify" => "#0B0B0E",
        _ => "#FFFFFF"
    };

    private static string FormatTrackCount(int count)
    {
        int mod100 = count % 100;
        int mod10 = count % 10;
        string noun = (mod100 is >= 11 and <= 14) ? "треков"
            : mod10 == 1 ? "трек"
            : (mod10 is >= 2 and <= 4) ? "трека"
            : "треков";
        return $"{count} {noun}";
    }
}

public class LibraryData
{
    public List<TrackItem> Favorites { get; set; } = new();
    public List<UserPlaylist> Playlists { get; set; } = new();
}

public class LibraryService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _libraryPath;
    private readonly object _lock = new();
    private LibraryData _data;

    public event Action? LibraryChanged;

    public LibraryService()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "OmniWave");
        Directory.CreateDirectory(dir);
        _libraryPath = Path.Combine(dir, "library.json");
        _data = Load();
    }

    private LibraryData Load()
    {
        try
        {
            if (File.Exists(_libraryPath))
            {
                var json = File.ReadAllText(_libraryPath);
                var data = JsonSerializer.Deserialize<LibraryData>(json);
                if (data != null)
                {
                    data.Favorites ??= new List<TrackItem>();
                    data.Playlists ??= new List<UserPlaylist>();
                    foreach (var fav in data.Favorites)
                        fav.IsLiked = true;
                    var pinned = data.Playlists.Where(p => p.IsPinned).ToList();
                    var unpinned = data.Playlists.Where(p => !p.IsPinned).ToList();
                    data.Playlists.Clear();
                    data.Playlists.AddRange(pinned);
                    data.Playlists.AddRange(unpinned);
                    return data;
                }
            }
        }
        catch { }

        return new LibraryData();
    }

    private void SaveUnlocked()
    {
        try
        {
            var json = JsonSerializer.Serialize(_data, JsonOptions);
            File.WriteAllText(_libraryPath, json);
        }
        catch { }
    }

    public bool IsLiked(TrackItem? track)
    {
        if (track == null) return false;
        lock (_lock)
        {
            return _data.Favorites.Any(t => t.UniqueKey == track.UniqueKey);
        }
    }

    public bool ToggleLike(TrackItem track)
    {
        bool nowLiked;
        lock (_lock)
        {
            int idx = _data.Favorites.FindIndex(t => t.UniqueKey == track.UniqueKey);
            if (idx >= 0)
            {
                _data.Favorites.RemoveAt(idx);
                nowLiked = false;
            }
            else
            {
                var copy = track.Clone();
                copy.IsLiked = true;
                _data.Favorites.Insert(0, copy);
                nowLiked = true;
            }

            foreach (var pl in _data.Playlists)
            {
                foreach (var item in pl.Tracks.Where(t => t.UniqueKey == track.UniqueKey))
                    item.IsLiked = nowLiked;
            }

            SaveUnlocked();
        }

        track.IsLiked = nowLiked;
        LibraryChanged?.Invoke();
        return nowLiked;
    }

    public List<TrackItem> GetFavorites()
    {
        lock (_lock)
        {
            return _data.Favorites.Select(t =>
            {
                var clone = t.Clone();
                clone.IsLiked = true;
                return clone;
            }).ToList();
        }
    }

    public List<UserPlaylist> GetPlaylists()
    {
        lock (_lock)
        {
            var likedKeys = _data.Favorites.Select(f => f.UniqueKey).ToHashSet();
            return _data.Playlists.Select(pl => new UserPlaylist
            {
                Id = pl.Id,
                Name = pl.Name,
                IsPinned = pl.IsPinned,
                IsExternal = pl.IsExternal,
                PlatformBrand = pl.PlatformBrand,
                ExternalId = pl.ExternalId,
                CoverUrl = pl.CoverUrl,
                Tracks = pl.Tracks.Select(t =>
                {
                    var clone = t.Clone();
                    clone.IsLiked = likedKeys.Contains(clone.UniqueKey);
                    return clone;
                }).ToList()
            }).ToList();
        }
    }

    private void SortPlaylistsUnlocked()
    {
        var pinned = _data.Playlists.Where(p => p.IsPinned).ToList();
        var unpinned = _data.Playlists.Where(p => !p.IsPinned).ToList();
        _data.Playlists.Clear();
        _data.Playlists.AddRange(pinned);
        _data.Playlists.AddRange(unpinned);
    }

    private static string ExternalMatchKey(UserPlaylist pl)
        => !string.IsNullOrWhiteSpace(pl.ExternalId) ? pl.ExternalId : pl.Name;

    public List<UserPlaylist> GetLocalPlaylists()
    {
        return GetPlaylists().Where(p => !p.IsExternal).ToList();
    }

    public void ReplaceExternalPlaylists(string platformBrand, List<UserPlaylist> playlists)
    {
        lock (_lock)
        {
            var likedKeys = _data.Favorites.Select(f => f.UniqueKey).ToHashSet();
            var existingBrand = _data.Playlists
                .Where(p => p.IsExternal && string.Equals(p.PlatformBrand, platformBrand, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var matchedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var pl in playlists)
            {
                foreach (var t in pl.Tracks)
                    t.IsLiked = likedKeys.Contains(t.UniqueKey);

                string key = ExternalMatchKey(pl);
                var existing = existingBrand.FirstOrDefault(e =>
                    !matchedIds.Contains(e.Id) &&
                    string.Equals(ExternalMatchKey(e), key, StringComparison.OrdinalIgnoreCase));

                if (existing != null)
                {
                    existing.Name = pl.Name;
                    existing.ExternalId = pl.ExternalId;
                    if (!string.IsNullOrWhiteSpace(pl.CoverUrl))
                        existing.CoverUrl = pl.CoverUrl;
                    existing.Tracks = pl.Tracks;
                    matchedIds.Add(existing.Id);
                }
                else
                {
                    pl.IsExternal = true;
                    pl.PlatformBrand = platformBrand;
                    _data.Playlists.Add(pl);
                    matchedIds.Add(pl.Id);
                }
            }

            _data.Playlists.RemoveAll(p =>
                p.IsExternal &&
                string.Equals(p.PlatformBrand, platformBrand, StringComparison.OrdinalIgnoreCase) &&
                !matchedIds.Contains(p.Id));

            SortPlaylistsUnlocked();
            SaveUnlocked();
        }

        LibraryChanged?.Invoke();
    }

    public void RemoveExternalPlaylists(string platformBrand)
    {
        bool changed;
        lock (_lock)
        {
            changed = _data.Playlists.RemoveAll(p =>
                p.IsExternal &&
                string.Equals(p.PlatformBrand, platformBrand, StringComparison.OrdinalIgnoreCase)) > 0;
            if (changed) SaveUnlocked();
        }

        if (changed)
            LibraryChanged?.Invoke();
    }

    public UserPlaylist CreatePlaylist(string name)
    {
        var clean = string.IsNullOrWhiteSpace(name) ? "Новый плейлист" : name.Trim();
        UserPlaylist created;
        lock (_lock)
        {
            created = new UserPlaylist
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = clean,
                IsExternal = false,
                PlatformBrand = "local",
                Tracks = new List<TrackItem>()
            };
            _data.Playlists.Add(created);
            SaveUnlocked();
        }

        LibraryChanged?.Invoke();
        return new UserPlaylist
        {
            Id = created.Id,
            Name = created.Name,
            IsExternal = false,
            PlatformBrand = "local",
            Tracks = new List<TrackItem>()
        };
    }

    public UserPlaylist AddImportedPlaylist(string name, List<TrackItem> tracks)
    {
        var clean = string.IsNullOrWhiteSpace(name) ? "Импорт (yandex2spotify)" : name.Trim();
        UserPlaylist created;
        lock (_lock)
        {
            var likedKeys = _data.Favorites.Select(f => f.UniqueKey).ToHashSet();
            var clonedTracks = tracks.Select(t =>
            {
                var copy = t.Clone();
                copy.IsLiked = likedKeys.Contains(copy.UniqueKey);
                return copy;
            }).ToList();

            created = new UserPlaylist
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = clean,
                IsExternal = false,
                PlatformBrand = "local",
                CoverUrl = clonedTracks.FirstOrDefault()?.CoverUrl ?? "",
                Tracks = clonedTracks
            };
            _data.Playlists.Add(created);
            SaveUnlocked();
        }

        LibraryChanged?.Invoke();
        return created;
    }

    public void DeletePlaylist(string playlistId)
    {
        bool changed;
        lock (_lock)
        {
            changed = _data.Playlists.RemoveAll(p => p.Id == playlistId) > 0;
            if (changed) SaveUnlocked();
        }

        if (changed)
            LibraryChanged?.Invoke();
    }

    public void TogglePinPlaylist(string playlistId)
    {
        bool changed = false;
        lock (_lock)
        {
            int idx = _data.Playlists.FindIndex(p => p.Id == playlistId);
            if (idx >= 0)
            {
                var pl = _data.Playlists[idx];
                pl.IsPinned = !pl.IsPinned;
                _data.Playlists.RemoveAt(idx);
                int insertAt = _data.Playlists.Count(p => p.IsPinned);
                _data.Playlists.Insert(insertAt, pl);
                SortPlaylistsUnlocked();
                SaveUnlocked();
                changed = true;
            }
        }

        if (changed)
            LibraryChanged?.Invoke();
    }

    public void MovePlaylist(string playlistId, int delta)
    {
        if (delta == 0) return;
        bool changed = false;
        lock (_lock)
        {
            int idx = _data.Playlists.FindIndex(p => p.Id == playlistId);
            if (idx >= 0)
            {
                int targetIdx = idx + (delta < 0 ? -1 : 1);
                if (targetIdx >= 0 &&
                    targetIdx < _data.Playlists.Count &&
                    _data.Playlists[targetIdx].IsPinned == _data.Playlists[idx].IsPinned)
                {
                    (_data.Playlists[idx], _data.Playlists[targetIdx]) = (_data.Playlists[targetIdx], _data.Playlists[idx]);
                    SaveUnlocked();
                    changed = true;
                }
            }
        }

        if (changed)
            LibraryChanged?.Invoke();
    }

    public void ReorderPlaylist(string sourceId, string targetId)
    {
        if (string.Equals(sourceId, targetId, StringComparison.Ordinal)) return;
        bool changed = false;
        lock (_lock)
        {
            int srcIdx = _data.Playlists.FindIndex(p => p.Id == sourceId);
            int dstIdx = _data.Playlists.FindIndex(p => p.Id == targetId);
            if (srcIdx >= 0 && dstIdx >= 0 && srcIdx != dstIdx)
            {
                var src = _data.Playlists[srcIdx];
                var dst = _data.Playlists[dstIdx];
                if (src.IsPinned || dst.IsPinned) return;
                _data.Playlists.RemoveAt(srcIdx);
                int insertIdx = Math.Clamp(dstIdx, 0, _data.Playlists.Count);
                _data.Playlists.Insert(insertIdx, src);
                SortPlaylistsUnlocked();
                SaveUnlocked();
                changed = true;
            }
        }

        if (changed)
            LibraryChanged?.Invoke();
    }

    private static bool ReorderTracksByKey(List<TrackItem> list, string sourceKey, string targetKey)
    {
        if (string.Equals(sourceKey, targetKey, StringComparison.OrdinalIgnoreCase)) return false;
        int srcIdx = list.FindIndex(t => string.Equals(t.UniqueKey, sourceKey, StringComparison.OrdinalIgnoreCase));
        int dstIdx = list.FindIndex(t => string.Equals(t.UniqueKey, targetKey, StringComparison.OrdinalIgnoreCase));
        if (srcIdx < 0 || dstIdx < 0 || srcIdx == dstIdx) return false;

        var src = list[srcIdx];
        list.RemoveAt(srcIdx);
        list.Insert(Math.Clamp(dstIdx, 0, list.Count), src);
        return true;
    }

    public void ReorderFavoriteTrack(string sourceKey, string targetKey)
    {
        bool changed;
        lock (_lock)
        {
            changed = ReorderTracksByKey(_data.Favorites, sourceKey, targetKey);
            if (changed) SaveUnlocked();
        }

        if (changed)
            LibraryChanged?.Invoke();
    }

    public void ReorderPlaylistTrack(string playlistId, string sourceKey, string targetKey)
    {
        bool changed = false;
        lock (_lock)
        {
            var pl = _data.Playlists.FirstOrDefault(p => p.Id == playlistId);
            if (pl != null)
            {
                changed = ReorderTracksByKey(pl.Tracks, sourceKey, targetKey);
                if (changed) SaveUnlocked();
            }
        }

        if (changed)
            LibraryChanged?.Invoke();
    }

    public bool AddTrackToPlaylist(string playlistId, TrackItem track)
    {
        bool added = false;
        lock (_lock)
        {
            var pl = _data.Playlists.FirstOrDefault(p => p.Id == playlistId);
            if (pl != null && pl.Tracks.All(t => t.UniqueKey != track.UniqueKey))
            {
                var copy = track.Clone();
                copy.IsLiked = _data.Favorites.Any(f => f.UniqueKey == track.UniqueKey);
                pl.Tracks.Add(copy);
                SaveUnlocked();
                added = true;
            }
        }

        if (added)
            LibraryChanged?.Invoke();
        return added;
    }

    public void RemoveTrackFromPlaylist(string playlistId, string uniqueKey)
    {
        bool removed = false;
        lock (_lock)
        {
            var pl = _data.Playlists.FirstOrDefault(p => p.Id == playlistId);
            if (pl != null)
            {
                removed = pl.Tracks.RemoveAll(t => t.UniqueKey == uniqueKey) > 0;
                if (removed) SaveUnlocked();
            }
        }

        if (removed)
            LibraryChanged?.Invoke();
    }

    public void SyncLikedFlags(IEnumerable<TrackItem> tracks)
    {
        HashSet<string> likedKeys;
        lock (_lock)
        {
            likedKeys = _data.Favorites.Select(f => f.UniqueKey).ToHashSet();
        }

        foreach (var track in tracks)
            track.IsLiked = likedKeys.Contains(track.UniqueKey);
    }
}
