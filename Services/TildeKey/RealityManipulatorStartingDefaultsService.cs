#nullable enable

using System.Runtime.Serialization;

namespace Loadout.Services.TildeKey;

using Loadout.Services.Saving;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

public sealed class RealityManipulatorStartingDefaultsSnapshot
{
    public RealityManipulatorStartingDefaultsSnapshot(
        IReadOnlyDictionary<string, int> stats,
        IReadOnlySet<string> toggles)
    {
        Stats = stats;
        Toggles = toggles;
    }

    public IReadOnlyDictionary<string, int> Stats { get; }

    public IReadOnlySet<string> Toggles { get; }
}

public static class RealityManipulatorStartingDefaultsService
{
    private const int CurrentSchemaVersion = 1;
    private const string SavePath = "loadout/services/tilde_key/reality_manipulator_starting_defaults.json";

    private static readonly object SyncRoot = new();
    private static SaveData _save = new();
    private static bool _loaded;

    public static event Action? StateChanged;

    public static bool TryGetStatValue(string statId, out int value)
    {
        EnsureLoaded();
        lock (SyncRoot)
        {
            if (_save.Stats.TryGetValue(statId, out StartingDefaultStat? saved) && saved is not null)
            {
                value = saved.Value;
                return true;
            }
        }

        value = 0;
        return false;
    }

    public static bool IsStatEnabled(string statId)
    {
        EnsureLoaded();
        lock (SyncRoot)
        {
            return _save.Stats.TryGetValue(statId, out StartingDefaultStat? saved)
                   && saved is { Enabled: true };
        }
    }

    public static void SetStatValue(string statId, int value)
    {
        if (!TildeKeyStateService.IsStartingDefaultStatId(statId))
            return;

        bool changed;
        lock (SyncRoot)
        {
            EnsureLoadedLocked();
            if (_save.Stats.TryGetValue(statId, out StartingDefaultStat? saved) && saved is not null)
            {
                changed = saved.Value != value;
                saved.Value = value;
            }
            else
            {
                _save.Stats[statId] = new StartingDefaultStat { Value = value };
                changed = true;
            }

            if (changed)
                SaveLocked();
        }

        if (changed)
            StateChanged?.Invoke();
    }

    public static bool SetStatEnabled(string statId, bool enabled)
    {
        if (!TildeKeyStateService.IsStartingDefaultStatId(statId))
            return false;

        bool changed;
        lock (SyncRoot)
        {
            EnsureLoadedLocked();
            if (!_save.Stats.TryGetValue(statId, out StartingDefaultStat? saved) || saved is null)
                return false;

            changed = saved.Enabled != enabled;
            saved.Enabled = enabled;
            if (changed)
                SaveLocked();
        }

        if (changed)
            StateChanged?.Invoke();
        return true;
    }

    public static bool IsToggleEnabled(string toggleId)
    {
        EnsureLoaded();
        lock (SyncRoot)
        {
            return _save.Toggles.TryGetValue(toggleId, out bool enabled) && enabled;
        }
    }

    public static void SetToggleEnabled(string toggleId, bool enabled)
    {
        if (!TildeKeyStateService.IsStartingDefaultToggleId(toggleId))
            return;

        bool changed;
        lock (SyncRoot)
        {
            EnsureLoadedLocked();
            bool current = _save.Toggles.TryGetValue(toggleId, out bool saved) && saved;
            changed = current != enabled;
            if (!changed)
                return;

            if (enabled)
                _save.Toggles[toggleId] = true;
            else
                _save.Toggles.Remove(toggleId);
            SaveLocked();
        }

        StateChanged?.Invoke();
    }

    public static RealityManipulatorStartingDefaultsSnapshot GetEnabledSnapshot()
    {
        EnsureLoaded();
        lock (SyncRoot)
        {
            Dictionary<string, int> stats = _save.Stats
                .Where(pair => pair.Value is { Enabled: true })
                .ToDictionary(pair => pair.Key, pair => pair.Value.Value, StringComparer.Ordinal);
            HashSet<string> toggles = _save.Toggles
                .Where(pair => pair.Value)
                .Select(pair => pair.Key)
                .ToHashSet(StringComparer.Ordinal);
            return new RealityManipulatorStartingDefaultsSnapshot(stats, toggles);
        }
    }

    private static void EnsureLoaded()
    {
        lock (SyncRoot)
            EnsureLoadedLocked();
    }

    private static void EnsureLoadedLocked()
    {
        if (_loaded)
            return;

        SaveUtility.LoadResult<SaveData> loaded = SaveUtility.LoadGlobalJson(SavePath, new SaveData());
        int loadedSchemaVersion = loaded.Value.SchemaVersion;
        _save = Normalize(loaded.Value);
        _loaded = true;

        if (loaded.Loaded && loadedSchemaVersion != CurrentSchemaVersion)
            SaveLocked();
    }

    private static void SaveLocked()
    {
        _save.SchemaVersion = CurrentSchemaVersion;
        SaveUtility.SaveGlobalJson(SavePath, _save);
    }

    private static SaveData Normalize(SaveData save)
    {
        save.SchemaVersion = CurrentSchemaVersion;
        save.Stats = save.Stats?
            .Where(pair => TildeKeyStateService.IsStartingDefaultStatId(pair.Key) && pair.Value is not null)
            .ToDictionary(
                pair => pair.Key,
                pair => new StartingDefaultStat
                {
                    Value = pair.Value.Value,
                    Enabled = pair.Value.Enabled
                },
                StringComparer.Ordinal)
            ?? new Dictionary<string, StartingDefaultStat>(StringComparer.Ordinal);
        save.Toggles = save.Toggles?
            .Where(pair => pair.Value && TildeKeyStateService.IsStartingDefaultToggleId(pair.Key))
            .ToDictionary(pair => pair.Key, _ => true, StringComparer.Ordinal)
            ?? new Dictionary<string, bool>(StringComparer.Ordinal);
        return save;
    }

    private struct SaveData : ISerializable
    {
        public SaveData()
        {
        }

        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        [JsonPropertyName("stats")]
        public Dictionary<string, StartingDefaultStat> Stats { get; set; } = new(StringComparer.Ordinal);

        [JsonPropertyName("toggles")]
        public Dictionary<string, bool> Toggles { get; set; } = new(StringComparer.Ordinal);

        public void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            info.AddValue(nameof(SchemaVersion), SchemaVersion);
            info.AddValue(nameof(Stats), Stats);
            info.AddValue(nameof(Toggles), Toggles);
        }
    }

    private sealed class StartingDefaultStat
    {
        public StartingDefaultStat()
        {
        }

        [JsonPropertyName("value")]
        public int Value { get; set; }

        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; }
    }
}
