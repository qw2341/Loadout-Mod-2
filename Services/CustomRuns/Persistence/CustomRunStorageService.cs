#nullable enable

namespace Loadout.Services.CustomRuns.Persistence;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Text.Json.Serialization;
using Loadout.Services.CustomRuns.Models;
using Loadout.Services.Saving;
using MegaCrit.Sts2.Core.Saves;

public static class CustomRunStorageService
{
    public const int CurrentSchemaVersion = 1;

    private const string ProfilePath = "loadout/services/custom_runs/profile_custom_runs.json";
    private static readonly object SyncRoot = new();
    private static CustomRunProfileSaveData _profile = new();
    private static bool _loaded;
    private static bool _registered;

    public static event Action? Changed;

    public static void Register()
    {
        if (_registered)
            return;
        _registered = true;
        SaveManager.Instance.ProfileIdChanged += OnProfileIdChanged;
        EnsureLoaded();
    }

    public static void Unregister()
    {
        if (!_registered)
            return;
        SaveManager.Instance.ProfileIdChanged -= OnProfileIdChanged;
        _registered = false;
    }

    public static IReadOnlyList<CustomRunDefinition> GetDefinitions()
    {
        EnsureLoaded();
        lock (SyncRoot)
        {
            return _profile.Definitions
                .Select(CustomRunNormalizationService.Clone)
                .ToList();
        }
    }

    public static CustomRunDefinition CreateNew()
    {
        return Upsert(new CustomRunDefinition());
    }

    public static CustomRunDefinition Upsert(CustomRunDefinition definition)
    {
        EnsureLoaded();
        CustomRunDefinition normalized = CustomRunNormalizationService.Normalize(
            CustomRunNormalizationService.Clone(definition));
        normalized.UpdatedAtUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        lock (SyncRoot)
        {
            int index = _profile.Definitions.FindIndex(existing =>
                string.Equals(existing.Id, normalized.Id, StringComparison.Ordinal));
            if (index >= 0)
            {
                normalized.CreatedAtUnixSeconds = _profile.Definitions[index].CreatedAtUnixSeconds;
                _profile.Definitions[index] = normalized;
            }
            else
            {
                _profile.Definitions.Add(normalized);
            }
            SaveLocked();
        }

        Changed?.Invoke();
        return CustomRunNormalizationService.Clone(normalized);
    }

    public static CustomRunDefinition Import(CustomRunDefinition definition)
    {
        CustomRunDefinition imported = CustomRunNormalizationService.Clone(definition);
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        imported.Id = Guid.NewGuid().ToString("N");
        imported.CreatedAtUnixSeconds = now;
        imported.UpdatedAtUnixSeconds = now;
        return Upsert(imported);
    }

    public static CustomRunDefinition Duplicate(CustomRunDefinition definition)
    {
        CustomRunDefinition duplicate = CustomRunNormalizationService.Clone(definition);
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        duplicate.Id = Guid.NewGuid().ToString("N");
        duplicate.Name = $"{definition.Name} Copy";
        duplicate.CreatedAtUnixSeconds = now;
        duplicate.UpdatedAtUnixSeconds = now;
        return Upsert(duplicate);
    }

    public static bool Delete(string id)
    {
        EnsureLoaded();
        bool changed;
        lock (SyncRoot)
        {
            changed = _profile.Definitions.RemoveAll(definition =>
                string.Equals(definition.Id, id, StringComparison.Ordinal)) > 0;
            if (changed)
                SaveLocked();
        }
        if (changed)
            Changed?.Invoke();
        return changed;
    }

    public static bool Move(string sourceId, string? targetId, bool placeAfter)
    {
        EnsureLoaded();
        bool changed = false;
        lock (SyncRoot)
        {
            int sourceIndex = _profile.Definitions.FindIndex(definition =>
                string.Equals(definition.Id, sourceId, StringComparison.Ordinal));
            if (sourceIndex < 0)
                return false;

            CustomRunDefinition source = _profile.Definitions[sourceIndex];
            _profile.Definitions.RemoveAt(sourceIndex);
            int targetIndex = string.IsNullOrEmpty(targetId)
                ? _profile.Definitions.Count
                : _profile.Definitions.FindIndex(definition =>
                    string.Equals(definition.Id, targetId, StringComparison.Ordinal));
            if (targetIndex < 0)
                targetIndex = _profile.Definitions.Count;
            else if (placeAfter)
                targetIndex++;
            targetIndex = Math.Clamp(targetIndex, 0, _profile.Definitions.Count);
            _profile.Definitions.Insert(targetIndex, source);
            changed = targetIndex != sourceIndex;
            if (changed)
                SaveLocked();
        }
        if (changed)
            Changed?.Invoke();
        return changed;
    }

    private static void EnsureLoaded()
    {
        lock (SyncRoot)
        {
            if (_loaded)
                return;
            SaveUtility.LoadResult<CustomRunProfileSaveData> loaded =
                SaveUtility.LoadProfileJson(ProfilePath, new CustomRunProfileSaveData());
            _profile = NormalizeProfile(loaded.Value);
            _loaded = true;
            if (loaded.Loaded && loaded.Value.SchemaVersion != CurrentSchemaVersion)
                SaveLocked();
        }
    }

    private static CustomRunProfileSaveData NormalizeProfile(CustomRunProfileSaveData profile)
    {
        profile.SchemaVersion = CurrentSchemaVersion;
        profile.Definitions = (profile.Definitions ?? [])
            .Where(definition => definition is not null)
            .Select(CustomRunNormalizationService.Normalize)
            .GroupBy(definition => definition.Id, StringComparer.Ordinal)
            .Select(group => group.Last())
            .ToList();
        return profile;
    }

    private static void SaveLocked()
    {
        _profile = NormalizeProfile(_profile);
        SaveUtility.SaveProfileJson(ProfilePath, _profile);
    }

    private static void OnProfileIdChanged(int _)
    {
        lock (SyncRoot)
        {
            _profile = new CustomRunProfileSaveData();
            _loaded = false;
        }
        EnsureLoaded();
        Changed?.Invoke();
    }
}

public sealed class CustomRunProfileSaveData : ISerializable
{
    public CustomRunProfileSaveData()
    {
    }

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = CustomRunStorageService.CurrentSchemaVersion;

    [JsonPropertyName("definitions")]
    public List<CustomRunDefinition> Definitions { get; set; } = [];

    public void GetObjectData(SerializationInfo info, StreamingContext context)
    {
        info.AddValue(nameof(SchemaVersion), SchemaVersion);
        info.AddValue(nameof(Definitions), Definitions);
    }
}
