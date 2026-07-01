#nullable enable

using System.Runtime.Serialization;

namespace Loadout.Services.Favorites;

using Loadout.Services.Saving;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

public enum FavoriteCategory
{
    Power,
    Card,
    Relic,
    Potion,
    Event
}

public sealed class FavoritesUtility
{
    private const int CurrentSchemaVersion = 1;
    private readonly string _relativePath;
    private readonly IReadOnlyList<string> _fallbackRelativePaths;
    private readonly object _syncRoot = new();
    private SaveData _save = new();
    private bool _loaded;

    public FavoritesUtility(
        string relativePath,
        IEnumerable<string>? fallbackRelativePaths = null)
    {
        _relativePath = relativePath;
        _fallbackRelativePaths = fallbackRelativePaths?.ToList() ?? [];
    }

    public void Reset()
    {
        lock (_syncRoot)
        {
            _loaded = false;
            _save = new SaveData();
        }
    }

    public bool Contains(FavoriteCategory category, string id)
    {
        EnsureLoaded();
        lock (_syncRoot)
        {
            return GetSet(_save, category).Contains(id);
        }
    }

    public bool Any(FavoriteCategory category)
    {
        EnsureLoaded();
        lock (_syncRoot)
        {
            return GetSet(_save, category).Count > 0;
        }
    }

    public void Toggle(FavoriteCategory category, string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return;

        EnsureLoaded();
        lock (_syncRoot)
        {
            HashSet<string> set = GetSet(_save, category);
            if (!set.Remove(id))
                set.Add(id);
            
            Save();
        }
    }

    public IReadOnlySet<string> Snapshot(FavoriteCategory category)
    {
        EnsureLoaded();
        lock (_syncRoot)
        {
            return new HashSet<string>(GetSet(_save, category), StringComparer.Ordinal);
        }
    }

    private void EnsureLoaded()
    {
        lock (_syncRoot)
        {
            if (_loaded)
                return;

            SaveUtility.LoadResult<SaveData> loaded = SaveUtility.LoadProfileJson(
                _relativePath,
                new SaveData(),
                _fallbackRelativePaths);

            bool shouldMigrate = loaded.Loaded && ShouldSaveLoadedData(loaded.Value, loaded.SourceRelativePath);
            _save = NormalizeSave(loaded.Value);
            _loaded = true;

            if (shouldMigrate)
                Save();
        }
    }

    private void Save()
    {
        _save.SchemaVersion = CurrentSchemaVersion;
        SaveUtility.SaveProfileJson(_relativePath, _save);
    }

    private bool ShouldSaveLoadedData(SaveData save, string? sourceRelativePath)
    {
        return !string.Equals(sourceRelativePath, _relativePath, StringComparison.Ordinal)
            || save.SchemaVersion != CurrentSchemaVersion
            || HasAny(save.LegacyFavoriteIds)
            || HasAny(save.LegacyFavoriteIdsPascal)
            || HasAny(save.LegacyFavoritePowerIdsPascal);
    }

    private static bool HasAny(IEnumerable<string>? ids)
    {
        return ids is not null && ids.Any(id => !string.IsNullOrWhiteSpace(id));
    }

    private static SaveData NormalizeSave(SaveData save)
    {
        save.SchemaVersion = CurrentSchemaVersion;
        save.FavoritePowerIds = NormalizeSet(save.FavoritePowerIds);
        save.FavoriteCardIds = NormalizeSet(save.FavoriteCardIds);
        save.FavoriteRelicIds = NormalizeSet(save.FavoriteRelicIds);
        save.FavoritePotionIds = NormalizeSet(save.FavoritePotionIds);
        save.FavoriteEventIds = NormalizeSet(save.FavoriteEventIds);

        MergeSet(save.FavoritePowerIds, save.LegacyFavoriteIds);
        MergeSet(save.FavoritePowerIds, save.LegacyFavoriteIdsPascal);
        MergeSet(save.FavoritePowerIds, save.LegacyFavoritePowerIdsPascal);

        save.LegacyFavoriteIds = null;
        save.LegacyFavoriteIdsPascal = null;
        save.LegacyFavoritePowerIdsPascal = null;
        return save;
    }

    private static HashSet<string> NormalizeSet(HashSet<string>? ids)
    {
        HashSet<string> normalized = new(StringComparer.Ordinal);
        if (ids is null)
            return normalized;

        foreach (string id in ids)
        {
            if (!string.IsNullOrWhiteSpace(id))
                normalized.Add(id);
        }

        return normalized;
    }

    private static void MergeSet(HashSet<string> destination, IEnumerable<string>? source)
    {
        if (source is null)
            return;

        foreach (string id in source)
        {
            if (!string.IsNullOrWhiteSpace(id))
                destination.Add(id);
        }
    }

    private static HashSet<string> GetSet(SaveData save, FavoriteCategory category)
    {
        return category switch
        {
            FavoriteCategory.Card => save.FavoriteCardIds,
            FavoriteCategory.Relic => save.FavoriteRelicIds,
            FavoriteCategory.Potion => save.FavoritePotionIds,
            FavoriteCategory.Event => save.FavoriteEventIds,
            _ => save.FavoritePowerIds
        };
    }

    private struct SaveData : ISerializable
    {
        public SaveData()
        {
        }

        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        [JsonPropertyName("favoritePowerIds")]
        public HashSet<string> FavoritePowerIds { get; set; } = new(StringComparer.Ordinal);

        [JsonPropertyName("favoriteCardIds")]
        public HashSet<string> FavoriteCardIds { get; set; } = new(StringComparer.Ordinal);

        [JsonPropertyName("favoriteRelicIds")]
        public HashSet<string> FavoriteRelicIds { get; set; } = new(StringComparer.Ordinal);

        [JsonPropertyName("favoritePotionIds")]
        public HashSet<string> FavoritePotionIds { get; set; } = new(StringComparer.Ordinal);

        [JsonPropertyName("favoriteEventIds")]
        public HashSet<string> FavoriteEventIds { get; set; } = new(StringComparer.Ordinal);

        [JsonPropertyName("favoriteIds")]
        public HashSet<string>? LegacyFavoriteIds { get; set; }

        [JsonPropertyName("FavoriteIds")]
        public HashSet<string>? LegacyFavoriteIdsPascal { get; set; }

        [JsonPropertyName("FavoritePowerIds")]
        public HashSet<string>? LegacyFavoritePowerIdsPascal { get; set; }

        public void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            info.AddValue(nameof(SchemaVersion), SchemaVersion);
            info.AddValue(nameof(FavoritePowerIds), FavoritePowerIds);
            info.AddValue(nameof(FavoriteCardIds), FavoriteCardIds);
            info.AddValue(nameof(FavoriteRelicIds), FavoriteRelicIds);
            info.AddValue(nameof(FavoritePotionIds), FavoritePotionIds);
            info.AddValue(nameof(FavoriteEventIds), FavoriteEventIds);
        }
    }
}
