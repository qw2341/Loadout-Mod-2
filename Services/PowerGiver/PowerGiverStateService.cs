#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Godot;
using Loadout.Services.Favorites;
using Loadout.Services.Saving;
using Loadout.Services.Targets;
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

namespace Loadout.Services.PowerGiver;

public enum PowerGiverTarget
{
    Player,
    Monsters
}

public static class PowerGiverStateService
{
    private const int CurrentSchemaVersion = 2;
    private const string FavoritesPath = "loadout/services/favorites/powers.json";
    private const string LegacyFavoritesPath = "loadout/power_giver_favorites.json";
    private const string RunDirectory = "loadout/relics/powergiver";
    private const string LegacyRunDirectory = "loadout";
    private const string RunFilePrefix = "power_giver_run";
    private const ulong FallbackSingleplayerNetId = 1;

    public const string TargetKey = "power_giver";

    private static readonly object SyncRoot = new();
    private static readonly FavoritesUtility Favorites = new(FavoritesPath, [LegacyFavoritesPath]);
    private static PowerGiverRunState _run = new();
    private static bool _registered;
    private static bool _runLoaded;
    private static long? _loadedRunStartTime;

    public static LoadoutTargetSelection SelectedTarget
    {
        get
        {
            EnsureLoaded();
            return LoadoutTargetService.GetSelected(TargetKey, LoadoutTargetMode.PowerGiver);
        }
    }

    public static void Register()
    {
        if (_registered)
            return;

        _registered = true;
        RunManager.Instance.RunStarted += OnRunStarted;
        SaveManager.Instance.ProfileIdChanged += OnProfileIdChanged;
        CombatManager.Instance.CombatSetUp += OnCombatSetUp;
        EnsureLoaded();
    }

    public static void Unregister()
    {
        if (!_registered)
            return;

        RunManager.Instance.RunStarted -= OnRunStarted;
        SaveManager.Instance.ProfileIdChanged -= OnProfileIdChanged;
        CombatManager.Instance.CombatSetUp -= OnCombatSetUp;
        _registered = false;
    }

    public static void EnsureLoaded()
    {
        Favorites.Snapshot(FavoriteCategory.Power);
        ReloadRunStateIfNeeded();
    }

    public static int GetCounter(string powerId)
    {
        return GetCounter(powerId, SelectedTarget);
    }

    public static int GetCounter(string powerId, LoadoutTargetSelection target)
    {
        EnsureLoaded();
        lock (SyncRoot)
        {
            Dictionary<string, int>? counters = GetCounters(target, createPlayerBucket: false);
            return counters?.GetValueOrDefault(powerId, 0) ?? 0;
        }
    }

    public static bool AdjustCounterFromAction(
        string powerId,
        int delta,
        LoadoutTargetSelection target,
        Player actionPlayer)
    {
        if (string.IsNullOrWhiteSpace(powerId) || delta == 0)
            return false;

        EnsureLoaded();
        bool adjusted;
        lock (SyncRoot)
        {
            adjusted = AdjustCounterLocked(powerId, delta, target);
        }

        if (adjusted)
            TaskHelper.RunSafely(ApplyCurrentCombatDeltaAsync(powerId, delta, target, actionPlayer));

        return adjusted;
    }

    public static bool IsFavorite(string powerId)
    {
        return Favorites.Contains(FavoriteCategory.Power, powerId);
    }

    public static bool HasFavorites()
    {
        return Favorites.Any(FavoriteCategory.Power);
    }

    public static void ToggleFavorite(string powerId)
    {
        Favorites.Toggle(FavoriteCategory.Power, powerId);
    }

    public static IReadOnlyDictionary<string, int> GetCountersSnapshot(LoadoutTargetSelection target)
    {
        EnsureLoaded();
        lock (SyncRoot)
        {
            Dictionary<string, int>? counters = GetCounters(target, createPlayerBucket: false);
            return counters is null
                ? new Dictionary<string, int>(StringComparer.Ordinal)
                : new Dictionary<string, int>(counters, StringComparer.Ordinal);
        }
    }

    private static bool AdjustCounterLocked(string powerId, int delta, LoadoutTargetSelection target)
    {
        Dictionary<string, int>? counters = GetCounters(target, createPlayerBucket: true);
        if (counters is null)
            return false;

        int next = counters.GetValueOrDefault(powerId, 0) + delta;
        if (next == 0)
            counters.Remove(powerId);
        else
            counters[powerId] = next;

        SaveRunState();
        return true;
    }

    private static void OnCombatSetUp(CombatState combatState)
    {
        EnsureLoaded();

        IReadOnlyDictionary<string, int> allPlayerCounters;
        IReadOnlyDictionary<ulong, IReadOnlyDictionary<string, int>> playerCountersByNetId;
        IReadOnlyDictionary<string, int> monsterCounters;
        lock (SyncRoot)
        {
            allPlayerCounters = new Dictionary<string, int>(_run.AllPlayerCounters, StringComparer.Ordinal);
            playerCountersByNetId = _run.PlayerCountersByNetId.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyDictionary<string, int>)new Dictionary<string, int>(pair.Value, StringComparer.Ordinal));
            monsterCounters = new Dictionary<string, int>(_run.MonsterCounters, StringComparer.Ordinal);
        }

        TaskHelper.RunSafely(ApplyConfiguredPowersAsync(
            combatState,
            allPlayerCounters,
            playerCountersByNetId,
            monsterCounters));
    }

    private static void OnRunStarted(RunState _)
    {
        ReloadRunState();
    }

    private static void OnProfileIdChanged(int _)
    {
        Favorites.Reset();
        ReloadRunState();
    }

    private static void ReloadRunState()
    {
        lock (SyncRoot)
        {
            _runLoaded = false;
            _loadedRunStartTime = null;
        }

        ReloadRunStateIfNeeded();
    }

    private static void ReloadRunStateIfNeeded()
    {
        long? currentRunStartTime = SaveUtility.GetCurrentRunStartTime();
        lock (SyncRoot)
        {
            if (_runLoaded && _loadedRunStartTime == currentRunStartTime)
                return;

            _runLoaded = true;
            _loadedRunStartTime = currentRunStartTime;
            if (currentRunStartTime is null)
            {
                _run = NormalizeRunState(new PowerGiverRunState(), 0);
                return;
            }

            string primaryPath = GetRunPath(RunDirectory, currentRunStartTime.Value);
            string legacyPath = GetRunPath(LegacyRunDirectory, currentRunStartTime.Value);
            SaveUtility.LoadResult<PowerGiverRunState> loaded = SaveUtility.LoadProfileJson(
                primaryPath,
                new PowerGiverRunState { RunStartTime = currentRunStartTime.Value },
                [legacyPath]);

            _run = NormalizeRunState(loaded.Value, currentRunStartTime.Value);
            if (loaded.Loaded && (!loaded.LoadedFrom(primaryPath) || loaded.Value.SchemaVersion != CurrentSchemaVersion))
                SaveRunState();
        }
    }

    private static void SaveRunState()
    {
        if (_loadedRunStartTime is null)
            return;

        _run.SchemaVersion = CurrentSchemaVersion;
        _run.RunStartTime = _loadedRunStartTime.Value;
        _run = NormalizeRunState(_run, _loadedRunStartTime.Value);
        _run.LegacyPlayerCounters = null;
        SaveUtility.SaveProfileJson(GetRunPath(RunDirectory, _loadedRunStartTime.Value), _run);
    }

    private static Dictionary<string, int>? GetCounters(LoadoutTargetSelection target, bool createPlayerBucket)
    {
        if (target.Scope == LoadoutTargetScope.AllMonsters)
            return _run.MonsterCounters;

        if (target.Scope == LoadoutTargetScope.AllPlayers)
            return _run.AllPlayerCounters;

        ulong? netId = target.PlayerNetId ?? GetCurrentPlayerNetId();
        if (netId is null)
            return null;

        if (_run.PlayerCountersByNetId.TryGetValue(netId.Value, out Dictionary<string, int>? counters))
            return counters;

        if (!createPlayerBucket)
            return null;

        counters = new Dictionary<string, int>(StringComparer.Ordinal);
        _run.PlayerCountersByNetId[netId.Value] = counters;
        return counters;
    }

    private static ulong? GetCurrentPlayerNetId()
    {
        try
        {
            RunState? runState = RunManager.Instance.IsInProgress
                ? RunManager.Instance.DebugOnlyGetState()
                : null;

            if (runState is not null)
                return LocalContext.GetMe(runState)?.NetId;
        }
        catch (Exception exception)
        {
            GD.PushWarning($"PowerGiver: could not resolve current run player net ID. {exception.Message}");
        }

        return LocalContext.NetId;
    }

    private static PowerGiverRunState NormalizeRunState(PowerGiverRunState run, long runStartTime)
    {
        run.SchemaVersion = CurrentSchemaVersion;
        run.RunStartTime = runStartTime;
        run.AllPlayerCounters = NormalizeCounters(run.AllPlayerCounters);
        run.PlayerCountersByNetId ??= new Dictionary<ulong, Dictionary<string, int>>();
        run.MonsterCounters = NormalizeCounters(run.MonsterCounters);

        if (run.LegacyPlayerCounters is not null)
        {
            ulong legacyNetId = GetCurrentPlayerNetId() ?? FallbackSingleplayerNetId;
            run.PlayerCountersByNetId[legacyNetId] = MergeCounters(
                run.PlayerCountersByNetId.GetValueOrDefault(legacyNetId),
                run.LegacyPlayerCounters);
        }

        Dictionary<ulong, Dictionary<string, int>> normalizedPlayers = new();
        foreach ((ulong netId, Dictionary<string, int>? counters) in run.PlayerCountersByNetId)
        {
            Dictionary<string, int> normalizedCounters = NormalizeCounters(counters);
            if (normalizedCounters.Count > 0)
                normalizedPlayers[netId] = normalizedCounters;
        }

        run.PlayerCountersByNetId = normalizedPlayers;
        return run;
    }

    private static Dictionary<string, int> MergeCounters(
        Dictionary<string, int>? existing,
        Dictionary<string, int> legacy)
    {
        Dictionary<string, int> merged = NormalizeCounters(existing);
        foreach ((string powerId, int amount) in NormalizeCounters(legacy))
            merged[powerId] = amount;

        return merged;
    }

    private static Dictionary<string, int> NormalizeCounters(Dictionary<string, int>? counters)
    {
        Dictionary<string, int> normalized = new(StringComparer.Ordinal);
        if (counters is null)
            return normalized;

        foreach ((string key, int value) in counters)
        {
            if (!string.IsNullOrWhiteSpace(key) && value != 0)
                normalized[key] = value;
        }

        return normalized;
    }

    private static string GetRunPath(string directory, long runStartTime)
    {
        return SaveUtility.GetRunSidecarPath(directory, RunFilePrefix, runStartTime);
    }

    private static async Task ApplyConfiguredPowersAsync(
        CombatState combatState,
        IReadOnlyDictionary<string, int> allPlayerCounters,
        IReadOnlyDictionary<ulong, IReadOnlyDictionary<string, int>> playerCountersByNetId,
        IReadOnlyDictionary<string, int> monsterCounters)
    {
        foreach (Player player in combatState.Players)
        {
            Dictionary<string, int> mergedCounters = new(allPlayerCounters, StringComparer.Ordinal);
            if (playerCountersByNetId.TryGetValue(player.NetId, out IReadOnlyDictionary<string, int>? playerCounters))
            {
                foreach ((string powerId, int amount) in playerCounters)
                    mergedCounters[powerId] = mergedCounters.GetValueOrDefault(powerId, 0) + amount;
            }

            foreach ((string powerId, int amount) in mergedCounters.Where(pair => pair.Value != 0))
                await ApplyPowerToTargets(powerId, amount, [player.Creature], player.Creature);
        }

        Creature? applier = combatState.Players.FirstOrDefault()?.Creature;
        foreach ((string powerId, int amount) in monsterCounters)
            await ApplyPowerToTargets(powerId, amount, combatState.Enemies.ToList(), applier);
    }

    private static async Task ApplyCurrentCombatDeltaAsync(
        string powerId,
        int amount,
        LoadoutTargetSelection target,
        Player actionPlayer)
    {
        if (amount == 0 || !CombatManager.Instance.IsInProgress)
            return;

        CombatState? combatState = CombatManager.Instance.DebugOnlyGetState();
        if (combatState is null)
            return;

        IReadOnlyList<Creature> targets = target.Scope switch
        {
            LoadoutTargetScope.AllMonsters => combatState.Enemies.ToList(),
            LoadoutTargetScope.AllPlayers => combatState.Players.Select(player => player.Creature).ToList(),
            LoadoutTargetScope.Player when target.PlayerNetId.HasValue && combatState.GetPlayer(target.PlayerNetId.Value) is { } player => [player.Creature],
            _ => []
        };

        if (targets.Count == 0)
            return;

        Creature? applier = combatState.GetPlayer(actionPlayer.NetId)?.Creature
                            ?? combatState.Players.FirstOrDefault()?.Creature;
        await ApplyPowerToTargets(powerId, amount, targets, applier);
    }

    private static async Task ApplyPowerToTargets(string powerId, int amount, IEnumerable<Creature> targets, Creature? applier)
    {
        if (amount == 0)
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

    private struct PowerGiverRunState : ISerializable
    {
        public PowerGiverRunState()
        {
        }

        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        [JsonPropertyName("runStartTime")]
        public long RunStartTime { get; set; }

        [JsonPropertyName("allPlayerCounters")]
        public Dictionary<string, int> AllPlayerCounters { get; set; } = new(StringComparer.Ordinal);

        [JsonPropertyName("playerCountersByNetId")]
        public Dictionary<ulong, Dictionary<string, int>> PlayerCountersByNetId { get; set; } = new();

        [JsonPropertyName("monsterCounters")]
        public Dictionary<string, int> MonsterCounters { get; set; } = new(StringComparer.Ordinal);

        [JsonPropertyName("playerCounters")]
        public Dictionary<string, int>? LegacyPlayerCounters { get; set; }

        public void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            info.AddValue(nameof(SchemaVersion), SchemaVersion);
            info.AddValue(nameof(RunStartTime), RunStartTime);
            info.AddValue(nameof(AllPlayerCounters), AllPlayerCounters);
            info.AddValue(nameof(PlayerCountersByNetId), PlayerCountersByNetId);
            info.AddValue(nameof(MonsterCounters), MonsterCounters);
        }
    }
}
