#nullable enable

using System.Runtime.Serialization;

namespace Loadout.Services.Favorites;

using Loadout.Services.Saving;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

public sealed class FavoritesUtility
{
    private const int CurrentSchemaVersion = 1;
    private readonly string _relativePath;
    private readonly IReadOnlyList<string> _fallbackRelativePaths;
    private readonly string _legacyIdsPropertyName;
    private readonly object _syncRoot = new();
    private SaveData _save = new();
    private bool _loaded;

    public FavoritesUtility(
        string relativePath,
        IEnumerable<string>? fallbackRelativePaths = null,
        string legacyIdsPropertyName = "favoritePowerIds")
    {
        _relativePath = relativePath;
        _fallbackRelativePaths = fallbackRelativePaths?.ToList() ?? [];
        _legacyIdsPropertyName = legacyIdsPropertyName;
    }

    public void Reset()
    {
        lock (_syncRoot)
        {
            _loaded = false;
            _save = new SaveData();
        }
    }

    public bool Contains(string id)
    {
        EnsureLoaded();
        lock (_syncRoot)
        {
            return _save.FavoriteIds.Contains(id, StringComparer.Ordinal);
        }
    }

    public bool Any()
    {
        EnsureLoaded();
        lock (_syncRoot)
        {
            return _save.FavoriteIds.Count > 0;
        }
    }

    public void Toggle(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return;

        EnsureLoaded();
        lock (_syncRoot)
        {
            if (_save.FavoriteIds.Contains(id, StringComparer.Ordinal))
                _save.FavoriteIds = _save.FavoriteIds.Where(savedId => !string.Equals(savedId, id, StringComparison.Ordinal)).ToList();
            else
                _save.FavoriteIds.Add(id);

            _save = NormalizeSave(_save);
            Save();
        }
    }

    public IReadOnlyList<string> Snapshot()
    {
        EnsureLoaded();
        lock (_syncRoot)
        {
            return _save.FavoriteIds.ToList();
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

            _save = loaded.Value;
            _save = NormalizeSave(_save);
            _loaded = true;

            if (loaded.Loaded && !loaded.LoadedFrom(_relativePath))
                Save();
        }
    }

    private void Save()
    {
        _save.SchemaVersion = CurrentSchemaVersion;
        _save.LegacyFavoritePowerIds = null;
        SaveUtility.SaveProfileJson(_relativePath, _save);
    }

    private SaveData NormalizeSave(SaveData save)
    {
        IReadOnlyList<string> legacyIds = _legacyIdsPropertyName == "favoritePowerIds"
            ? save.LegacyFavoritePowerIds ?? []
            : [];

        save.FavoriteIds = (save.FavoriteIds ?? [])
            .Concat(legacyIds)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        return save;
    }

    private struct SaveData : ISerializable
    {
        public SaveData()
        {
        }

        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        [JsonPropertyName("favoriteIds")]
        public List<string> FavoriteIds { get; set; } = [];

        [JsonPropertyName("favoritePowerIds")]
        public List<string>? LegacyFavoritePowerIds { get; set; }


        public void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            info.AddValue(nameof(SchemaVersion), SchemaVersion);
            info.AddValue(nameof(FavoriteIds), FavoriteIds);
            info.AddValue(nameof(LegacyFavoritePowerIds), LegacyFavoritePowerIds);
        }
    }
}
