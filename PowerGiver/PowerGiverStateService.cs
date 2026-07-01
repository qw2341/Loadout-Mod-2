#nullable enable

namespace Loadout.PowerGiver;

using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

public enum PowerGiverTarget
{
    Player,
    Monsters
}

public static class PowerGiverStateService
{
    private const int CurrentSchemaVersion = 1;
    private const string SaveDirectory = "loadout";
    private const string FavoritesFileName = "power_giver_favorites.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    private static readonly object SyncRoot = new();
    private static PowerGiverFavoritesSave _favorites = new();
    private static PowerGiverRunSave _run = new();
    private static bool _registered;
    private static bool _favoritesLoaded;
    private static long? _loadedRunStartTime;

    public static PowerGiverTarget SelectedTarget
    {
        get
        {
            EnsureLoaded();
            return ParseTarget(_run.SelectedTarget);
        }
    }

    public static void Register()
    {
        if (_registered)
            return;

        _registered = true;
        RunManager.Instance.RunStarted += _ => ReloadRunState();
        SaveManager.Instance.ProfileIdChanged += _ =>
        {
            lock (SyncRoot)
            {
                _favoritesLoaded = false;
                _loadedRunStartTime = null;
            }

            EnsureLoaded();
        };

        CombatManager.Instance.CombatSetUp += OnCombatSetUp;
        EnsureLoaded();
    }

    public static void EnsureLoaded()
    {
        lock (SyncRoot)
        {
            if (!_favoritesLoaded)
            {
                _favorites = LoadJson(GetFavoritesPath(), new PowerGiverFavoritesSave());
                _favorites.FavoritePowerIds = NormalizeIds(_favorites.FavoritePowerIds).ToList();
                _favoritesLoaded = true;
            }
        }

        ReloadRunStateIfNeeded();
    }

    public static void SetSelectedTarget(PowerGiverTarget target)
    {
        EnsureLoaded();
        lock (SyncRoot)
        {
            _run.SelectedTarget = target.ToString();
            SaveRunState();
        }
    }

    public static int GetCounter(string powerId)
    {
        EnsureLoaded();
        lock (SyncRoot)
        {
            return GetCounters(ParseTarget(_run.SelectedTarget)).GetValueOrDefault(powerId, 0);
        }
    }

    public static void AdjustCounter(string powerId, int delta)
    {
        if (string.IsNullOrWhiteSpace(powerId) || delta == 0)
            return;

        EnsureLoaded();
        lock (SyncRoot)
        {
            Dictionary<string, int> counters = GetCounters(ParseTarget(_run.SelectedTarget));
            int next = Math.Max(0, counters.GetValueOrDefault(powerId, 0) + delta);
            if (next == 0)
                counters.Remove(powerId);
            else
                counters[powerId] = next;

            SaveRunState();
        }
    }

    public static bool IsFavorite(string powerId)
    {
        EnsureLoaded();
        lock (SyncRoot)
        {
            return _favorites.FavoritePowerIds.Contains(powerId, StringComparer.Ordinal);
        }
    }

    public static bool HasFavorites()
    {
        EnsureLoaded();
        lock (SyncRoot)
        {
            return _favorites.FavoritePowerIds.Count > 0;
        }
    }

    public static void ToggleFavorite(string powerId)
    {
        if (string.IsNullOrWhiteSpace(powerId))
            return;

        EnsureLoaded();
        lock (SyncRoot)
        {
            if (_favorites.FavoritePowerIds.Contains(powerId, StringComparer.Ordinal))
                _favorites.FavoritePowerIds = _favorites.FavoritePowerIds.Where(id => !string.Equals(id, powerId, StringComparison.Ordinal)).ToList();
            else
                _favorites.FavoritePowerIds.Add(powerId);

            _favorites.FavoritePowerIds = NormalizeIds(_favorites.FavoritePowerIds).ToList();
            SaveFavorites();
        }
    }

    public static IReadOnlyDictionary<string, int> GetCountersSnapshot(PowerGiverTarget target)
    {
        EnsureLoaded();
        lock (SyncRoot)
        {
            return new Dictionary<string, int>(GetCounters(target), StringComparer.Ordinal);
        }
    }

    private static void OnCombatSetUp(CombatState combatState)
    {
        EnsureLoaded();
        TaskHelper.RunSafely(ApplyConfiguredPowersAsync(combatState));
    }

    private static async Task ApplyConfiguredPowersAsync(CombatState combatState)
    {
        Player? localPlayer;
        try
        {
            localPlayer = LocalContext.GetMe(combatState.RunState);
        }
        catch (Exception exception)
        {
            GD.PushWarning($"PowerGiver: could not resolve local player for combat setup. {exception.Message}");
            return;
        }

        if (localPlayer is null)
            return;

        IReadOnlyDictionary<string, int> playerCounters = GetCountersSnapshot(PowerGiverTarget.Player);
        IReadOnlyDictionary<string, int> monsterCounters = GetCountersSnapshot(PowerGiverTarget.Monsters);

        foreach ((string powerId, int amount) in playerCounters)
            await ApplyPowerToTargets(powerId, amount, [localPlayer.Creature], localPlayer.Creature);

        foreach ((string powerId, int amount) in monsterCounters)
            await ApplyPowerToTargets(powerId, amount, combatState.Enemies, localPlayer.Creature);
    }

    private static async Task ApplyPowerToTargets(string powerId, int amount, IEnumerable<Creature> targets, Creature applier)
    {
        if (amount <= 0)
            return;

        PowerModel? power = ResolvePower(powerId);
        if (power is null)
        {
            GD.PushWarning($"PowerGiver: skipping unknown power id '{powerId}'.");
            return;
        }

        foreach (Creature target in targets)
        {
            try
            {
                await PowerCmd.Apply(new ThrowingPlayerChoiceContext(), power.ToMutable(), target, amount, applier, null);
            }
            catch (Exception exception)
            {
                GD.PushWarning($"PowerGiver: failed to apply '{powerId}' to '{target.Name}'. {exception.Message}");
            }
        }
    }

    private static PowerModel? ResolvePower(string powerId)
    {
        try
        {
            return ModelDb.AllPowers.FirstOrDefault(power =>
                string.Equals(power.Id.ToString(), powerId, StringComparison.Ordinal)
                || string.Equals(power.Id.Entry, powerId, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception exception)
        {
            GD.PushWarning($"PowerGiver: could not resolve power '{powerId}'. {exception.Message}");
            return null;
        }
    }

    private static void ReloadRunStateIfNeeded()
    {
        long? currentRunStartTime = GetCurrentRunStartTime();
        lock (SyncRoot)
        {
            if (_loadedRunStartTime == currentRunStartTime)
                return;

            _loadedRunStartTime = currentRunStartTime;
            _run = currentRunStartTime is null
                ? new PowerGiverRunSave()
                : LoadJson(GetRunPath(currentRunStartTime.Value), new PowerGiverRunSave { RunStartTime = currentRunStartTime.Value });

            _run.RunStartTime = currentRunStartTime ?? 0;
            NormalizeRunState(_run);
        }
    }

    private static void ReloadRunState()
    {
        lock (SyncRoot)
            _loadedRunStartTime = null;

        ReloadRunStateIfNeeded();
    }

    private static long? GetCurrentRunStartTime()
    {
        try
        {
            if (!RunManager.Instance.IsInProgress || RunManager.Instance.DebugOnlyGetState() is null)
                return null;

            return RunManager.Instance.ToSave(null).StartTime;
        }
        catch (Exception exception)
        {
            GD.PushWarning($"PowerGiver: could not determine current run start time. {exception.Message}");
            return null;
        }
    }

    private static Dictionary<string, int> GetCounters(PowerGiverTarget target)
    {
        return target == PowerGiverTarget.Monsters
            ? _run.MonsterCounters
            : _run.PlayerCounters;
    }

    private static PowerGiverTarget ParseTarget(string? value)
    {
        return Enum.TryParse(value, ignoreCase: true, out PowerGiverTarget target)
            ? target
            : PowerGiverTarget.Player;
    }

    private static void NormalizeRunState(PowerGiverRunSave run)
    {
        run.SelectedTarget = ParseTarget(run.SelectedTarget).ToString();
        run.PlayerCounters = NormalizeCounters(run.PlayerCounters);
        run.MonsterCounters = NormalizeCounters(run.MonsterCounters);
    }

    private static Dictionary<string, int> NormalizeCounters(Dictionary<string, int>? counters)
    {
        Dictionary<string, int> normalized = new(StringComparer.Ordinal);
        if (counters is null)
            return normalized;

        foreach ((string key, int value) in counters)
        {
            if (!string.IsNullOrWhiteSpace(key) && value > 0)
                normalized[key] = value;
        }

        return normalized;
    }

    private static IEnumerable<string> NormalizeIds(IEnumerable<string>? ids)
    {
        return (ids?
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal))
            ?? Enumerable.Empty<string>();
    }

    private static void SaveFavorites()
    {
        _favorites.SchemaVersion = CurrentSchemaVersion;
        SaveJson(GetFavoritesPath(), _favorites);
    }

    private static void SaveRunState()
    {
        if (_loadedRunStartTime is null)
            return;

        _run.SchemaVersion = CurrentSchemaVersion;
        _run.RunStartTime = _loadedRunStartTime.Value;
        NormalizeRunState(_run);
        SaveJson(GetRunPath(_loadedRunStartTime.Value), _run);
    }

    private static string GetFavoritesPath()
    {
        return GetProfileScopedPath(FavoritesFileName);
    }

    private static string GetRunPath(long runStartTime)
    {
        return GetProfileScopedPath($"power_giver_run_{runStartTime}.json");
    }

    private static string GetProfileScopedPath(string fileName)
    {
        string relative = $"{SaveDirectory}/{fileName}";
        try
        {
            if (SaveManager.Instance.IsProfileInitialized)
                return SaveManager.Instance.GetProfileScopedPath(relative);
        }
        catch (Exception exception)
        {
            GD.PushWarning($"PowerGiver: falling back to user data save path. {exception.Message}");
        }

        return $"user://{relative}";
    }

    private static T LoadJson<T>(string path, T fallback)
    {
        try
        {
            string globalPath = ProjectSettings.GlobalizePath(path);
            if (!System.IO.File.Exists(globalPath))
                return fallback;

            string json = System.IO.File.ReadAllText(globalPath);
            return JsonSerializer.Deserialize<T>(json, JsonOptions) ?? fallback;
        }
        catch (Exception exception)
        {
            GD.PushWarning($"PowerGiver: failed to load save '{path}'. {exception.Message}");
            return fallback;
        }
    }

    private static void SaveJson<T>(string path, T data)
    {
        try
        {
            string globalPath = ProjectSettings.GlobalizePath(path);
            string? directory = System.IO.Path.GetDirectoryName(globalPath);
            if (!string.IsNullOrWhiteSpace(directory))
                System.IO.Directory.CreateDirectory(directory);

            string json = JsonSerializer.Serialize(data, JsonOptions);
            string tempPath = $"{globalPath}.tmp";
            System.IO.File.WriteAllText(tempPath, json);
            if (System.IO.File.Exists(globalPath))
                System.IO.File.Delete(globalPath);

            System.IO.File.Move(tempPath, globalPath);
        }
        catch (Exception exception)
        {
            GD.PushWarning($"PowerGiver: failed to save '{path}'. {exception.Message}");
        }
    }

    private sealed class PowerGiverFavoritesSave
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        [JsonPropertyName("favoritePowerIds")]
        public List<string> FavoritePowerIds { get; set; } = [];
    }

    private sealed class PowerGiverRunSave
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        [JsonPropertyName("runStartTime")]
        public long RunStartTime { get; set; }

        [JsonPropertyName("selectedTarget")]
        public string SelectedTarget { get; set; } = PowerGiverTarget.Player.ToString();

        [JsonPropertyName("playerCounters")]
        public Dictionary<string, int> PlayerCounters { get; set; } = new(StringComparer.Ordinal);

        [JsonPropertyName("monsterCounters")]
        public Dictionary<string, int> MonsterCounters { get; set; } = new(StringComparer.Ordinal);
    }
}
