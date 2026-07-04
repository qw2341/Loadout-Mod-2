#nullable enable

namespace Loadout.Services.Loadouts;

using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Loadout.Services.Saving;
using MegaCrit.Sts2.Core.Saves;

public static class LoadoutStorageService
{
    public const int CurrentSchemaVersion = 1;

    private const string ProfilePath = "loadout/services/loadouts/profile_loadouts.json";

    private static readonly object SyncRoot = new();
    private static LoadoutProfileSaveData _profile = new();
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

    public static IReadOnlyList<SavedLoadout> GetLoadouts()
    {
        EnsureLoaded();
        lock (SyncRoot)
        {
            return _profile.Loadouts
                .Select(loadout => loadout.Clone())
                .OrderBy(loadout => loadout.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public static SavedLoadout Upsert(SavedLoadout loadout)
    {
        EnsureLoaded();
        SavedLoadout normalized = LoadoutSerializationService.Normalize(loadout.Clone());
        normalized.IsRemote = false;
        normalized.RemoteOwnerLabel = null;
        normalized.UpdatedAtUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        lock (SyncRoot)
        {
            int existingIndex = _profile.Loadouts.FindIndex(existing => string.Equals(existing.Id, normalized.Id, StringComparison.Ordinal));
            if (existingIndex >= 0)
            {
                normalized.CreatedAtUnixSeconds = _profile.Loadouts[existingIndex].CreatedAtUnixSeconds;
                _profile.Loadouts[existingIndex] = normalized;
            }
            else
            {
                _profile.Loadouts.Add(normalized);
            }

            SaveLocked();
        }

        RaiseChanged();
        return normalized.Clone();
    }

    public static SavedLoadout Import(SavedLoadout loadout)
    {
        SavedLoadout imported = LoadoutSerializationService.Normalize(loadout.Clone());
        imported.Id = Guid.NewGuid().ToString("N");
        imported.IsRemote = false;
        imported.RemoteOwnerLabel = null;
        return Upsert(imported);
    }

    public static bool Rename(string id, string name)
    {
        EnsureLoaded();
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
            return false;

        bool changed = false;
        lock (SyncRoot)
        {
            SavedLoadout? loadout = _profile.Loadouts.FirstOrDefault(candidate => string.Equals(candidate.Id, id, StringComparison.Ordinal));
            if (loadout is not null)
            {
                loadout.Name = name.Trim();
                loadout.UpdatedAtUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                SaveLocked();
                changed = true;
            }
        }

        if (changed)
            RaiseChanged();

        return changed;
    }

    public static bool Delete(string id)
    {
        EnsureLoaded();
        bool changed;
        lock (SyncRoot)
        {
            changed = _profile.Loadouts.RemoveAll(candidate => string.Equals(candidate.Id, id, StringComparison.Ordinal)) > 0;
            if (changed)
                SaveLocked();
        }

        if (changed)
            RaiseChanged();

        return changed;
    }

    private static void EnsureLoaded()
    {
        lock (SyncRoot)
        {
            if (_loaded)
                return;

            SaveUtility.LoadResult<LoadoutProfileSaveData> loaded =
                SaveUtility.LoadProfileJson(ProfilePath, new LoadoutProfileSaveData());
            _profile = NormalizeProfile(loaded.Value);
            _loaded = true;

            if (loaded.Loaded && loaded.Value.SchemaVersion != CurrentSchemaVersion)
                SaveLocked();
        }
    }

    private static LoadoutProfileSaveData NormalizeProfile(LoadoutProfileSaveData profile)
    {
        profile.SchemaVersion = CurrentSchemaVersion;
        profile.Loadouts = profile.Loadouts
            .Where(loadout => loadout is not null)
            .Select(LoadoutSerializationService.Normalize)
            .GroupBy(loadout => loadout.Id, StringComparer.Ordinal)
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
            _loaded = false;
            _profile = new LoadoutProfileSaveData();
        }

        EnsureLoaded();
        RaiseChanged();
    }

    private static void RaiseChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception exception)
        {
            GD.PushWarning($"Loadout: loadout storage change handler failed. {exception.Message}");
        }
    }
}
