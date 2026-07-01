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
    private const int CurrentSchemaVersion = 1;
    private const string FavoritesPath = "loadout/services/favorites/powers.json";
    private const string LegacyFavoritesPath = "loadout/power_giver_favorites.json";
    private const string RunDirectory = "loadout/relics/powergiver";
    private const string LegacyRunDirectory = "loadout";
    private const string RunFilePrefix = "power_giver_run";

    private static readonly object SyncRoot = new();
    private static readonly FavoritesUtility Favorites = new(FavoritesPath, [LegacyFavoritesPath]);
    private static PowerGiverRunState _run = new();
    private static bool _registered;
    private static bool _runLoaded;
    private static long? _loadedRunStartTime;

    public static PowerGiverTarget SelectedTarget
    {
        get
        {
            EnsureLoaded();
            lock (SyncRoot)
            {
                return ParseTarget(_run.SelectedTarget);
            }
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
            Favorites.Reset();
            ReloadRunState();
        };

        CombatManager.Instance.CombatSetUp += OnCombatSetUp;
        EnsureLoaded();
    }

    public static void EnsureLoaded()
    {
        Favorites.Snapshot();
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

        PowerGiverTarget target;
        EnsureLoaded();
        lock (SyncRoot)
        {
            target = ParseTarget(_run.SelectedTarget);
            Dictionary<string, int> counters = GetCounters(target);
            int next = Math.Max(0, counters.GetValueOrDefault(powerId, 0) + delta);
            if (next == 0)
                counters.Remove(powerId);
            else
                counters[powerId] = next;

            SaveRunState();
        }

        if (delta > 0)
            TaskHelper.RunSafely(ApplyCurrentCombatDeltaAsync(powerId, delta, target));
    }

    public static bool IsFavorite(string powerId)
    {
        return Favorites.Contains(powerId);
    }

    public static bool HasFavorites()
    {
        return Favorites.Any();
    }

    public static void ToggleFavorite(string powerId)
    {
        Favorites.Toggle(powerId);
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
        IReadOnlyDictionary<string, int> playerCounters = GetCountersSnapshot(PowerGiverTarget.Player);
        IReadOnlyDictionary<string, int> monsterCounters = GetCountersSnapshot(PowerGiverTarget.Monsters);
        TaskHelper.RunSafely(ApplyConfiguredPowersAsync(combatState, playerCounters, monsterCounters));
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
            if (loaded.Loaded && !loaded.LoadedFrom(primaryPath))
                SaveRunState();
        }
    }

    private static void SaveRunState()
    {
        if (_loadedRunStartTime is null)
            return;

        _run.SchemaVersion = CurrentSchemaVersion;
        _run.RunStartTime = _loadedRunStartTime.Value;
        NormalizeRunState(_run, _loadedRunStartTime.Value);
        SaveUtility.SaveProfileJson(GetRunPath(RunDirectory, _loadedRunStartTime.Value), _run);
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

    private static PowerGiverRunState NormalizeRunState(PowerGiverRunState run, long runStartTime)
    {
        run.SchemaVersion = CurrentSchemaVersion;
        run.RunStartTime = runStartTime;
        run.SelectedTarget = ParseTarget(run.SelectedTarget).ToString();
        run.PlayerCounters = NormalizeCounters(run.PlayerCounters);
        run.MonsterCounters = NormalizeCounters(run.MonsterCounters);
        return run;
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

    private static string GetRunPath(string directory, long runStartTime)
    {
        return SaveUtility.GetRunSidecarPath(directory, RunFilePrefix, runStartTime);
    }

    private static async Task ApplyConfiguredPowersAsync(
        CombatState combatState,
        IReadOnlyDictionary<string, int> playerCounters,
        IReadOnlyDictionary<string, int> monsterCounters)
    {
        Player? localPlayer = GetLocalPlayer(combatState);
        if (localPlayer is null)
            return;

        foreach ((string powerId, int amount) in playerCounters)
            await ApplyPowerToTargets(powerId, amount, [localPlayer.Creature], localPlayer.Creature);

        foreach ((string powerId, int amount) in monsterCounters)
            await ApplyPowerToTargets(powerId, amount, combatState.Enemies.ToList(), localPlayer.Creature);
    }

    private static async Task ApplyCurrentCombatDeltaAsync(string powerId, int amount, PowerGiverTarget target)
    {
        if (amount <= 0 || !CombatManager.Instance.IsInProgress)
            return;

        CombatState? combatState = CombatManager.Instance.DebugOnlyGetState();
        if (combatState is null)
            return;

        Player? localPlayer = GetLocalPlayer(combatState);
        if (localPlayer is null)
            return;

        IReadOnlyList<Creature> targets = target == PowerGiverTarget.Monsters
            ? combatState.Enemies.ToList()
            : [localPlayer.Creature];

        await ApplyPowerToTargets(powerId, amount, targets, localPlayer.Creature);
    }

    private static Player? GetLocalPlayer(CombatState combatState)
    {
        try
        {
            return LocalContext.GetMe(combatState.RunState);
        }
        catch (Exception exception)
        {
            GD.PushWarning($"PowerGiver: could not resolve local player. {exception.Message}");
            return null;
        }
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

    private struct PowerGiverRunState : ISerializable
    {
        public PowerGiverRunState()
        {
        }

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

        public void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            info.AddValue(nameof(PlayerCounters), PlayerCounters);
            info.AddValue(nameof(MonsterCounters), MonsterCounters);
        }
    }
}
