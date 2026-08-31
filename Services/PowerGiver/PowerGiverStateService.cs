#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using Loadout.Services.Compatibility;
using Loadout.Services.Favorites;
using Loadout.Services.Networking;
using Loadout.Services.Saving;
using Loadout.Services.Targets;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace Loadout.Services.PowerGiver;

public enum PowerGiverTarget
{
    Player,
    Monsters
}

public sealed class PowerGiverCombatStartHook : AbstractModel
{
    public override bool ShouldReceiveCombatHooks => true;

    public override Task BeforeCombatStart()
        => PowerGiverStateService.AwaitCombatStartApplicationAsync();
}

public static class PowerGiverStateService
{
    private const int CurrentSchemaVersion = 2;
    private const string CombatStartHookId = "Loadout.PowerGiver.StartingPowers";
    private const string FavoritesPath = "loadout/services/favorites/powers.json";
    private const string LegacyFavoritesPath = "loadout/power_giver_favorites.json";
    private const string RunDirectory = "loadout/relics/powergiver";
    private const string LegacyRunDirectory = "loadout";
    private const string RunFilePrefix = "power_giver_run";
    private const ulong FallbackSingleplayerNetId = 1;

    public const string TargetKey = "power_giver";

    private static readonly object SyncRoot = new();
    private static readonly FavoritesUtility Favorites = new(FavoritesPath, [LegacyFavoritesPath]);
    private static readonly HashSet<LoadRunLobby> RegisteredLoadLobbies = [];
    private static readonly HashSet<INetGameService> RegisteredMessageServices = [];
    private static PowerGiverRunState _run = new();
    private static INetGameService? _runNetService;
    private static RunLobby? _runLobby;
    private static Delegate? _playerRejoinedHandler;
    private static PowerGiverCounterSnapshot? _pendingHostSnapshot;
    private static TaskCompletionSource<PowerGiverCounterSnapshot>? _combatStartSnapshotSource;
    private static PowerGiverCounterSnapshot? _pendingCombatStartSnapshot;
    private static Task? _combatStartTask;
    private static bool _registered;
    private static bool _combatStartHookRegistered;
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
        RegisterCombatStartHook();
        EnsureLoaded();
    }

    public static void Unregister()
    {
        if (!_registered)
            return;

        foreach (INetGameService netService in RegisteredMessageServices.ToList())
            UnregisterMessageHandlers(netService);
        RegisteredLoadLobbies.Clear();
        UnbindRunLobby();
        _runNetService = null;
        ResetHostSnapshot();
        ResetCombatStart();
        RunManager.Instance.RunStarted -= OnRunStarted;
        SaveManager.Instance.ProfileIdChanged -= OnProfileIdChanged;
        CombatManager.Instance.CombatSetUp -= OnCombatSetUp;
        _registered = false;
    }

    public static void RegisterLoadLobby(LoadRunLobby? lobby)
    {
        if (!_registered || lobby is null || !RegisteredLoadLobbies.Add(lobby))
            return;

        RegisterMessageHandlers(lobby.NetService);
        if (lobby.NetService.Type == NetGameType.Host)
        {
            ReloadRunState(lobby.Run.StartTime);
            return;
        }
        if (lobby.NetService.Type != NetGameType.Client)
            return;

        ResetHostSnapshot();
        lobby.NetService.SendMessage(default(PowerGiverSnapshotRequestMessage));
    }

    public static void UnregisterLoadLobby(LoadRunLobby? lobby, bool clearClientState)
    {
        if (lobby is null || !RegisteredLoadLobbies.Remove(lobby))
            return;

        if (!clearClientState)
            return;

        UnregisterMessageHandlers(lobby.NetService);
        ResetHostSnapshot();
    }

    public static void PrepareRunLaunch()
    {
        INetGameService netService = RunManager.Instance.NetService;
        RegisterRunNetService(netService);
        BindRunLobby(RunManager.Instance.RunLobby);

    }

    public static void OnRunLaunched()
    {
        INetGameService netService = RunManager.Instance.NetService;
        RegisterRunNetService(netService);
        BindRunLobby(RunManager.Instance.RunLobby);

        if (netService.Type == NetGameType.Host)
        {
            BroadcastSnapshot();
            return;
        }

        bool hasPendingHostSnapshot;
        lock (SyncRoot)
            hasPendingHostSnapshot = _pendingHostSnapshot is not null;
        if (netService.Type == NetGameType.Client && !hasPendingHostSnapshot)
            netService.SendMessage(default(PowerGiverSnapshotRequestMessage));
    }

    public static void OnRunCleaningUp()
    {
        UnbindRunLobby();
        foreach (INetGameService netService in RegisteredMessageServices.ToList())
            UnregisterMessageHandlers(netService);
        RegisteredLoadLobbies.Clear();
        _runNetService = null;
        ResetHostSnapshot();
        ResetCombatStart();
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
        int appliedDelta;
        lock (SyncRoot)
        {
            adjusted = AdjustCounterLocked(
                powerId,
                delta,
                target,
                out appliedDelta);
        }

        if (adjusted)
        {
            TaskHelper.RunSafely(
                ApplyCurrentCombatDeltaAsync(
                    powerId,
                    appliedDelta,
                    target,
                    actionPlayer));
        }

        return adjusted;
    }

    public static async Task<bool> AdjustCounterFromCardAsync(
        string powerId,
        int delta,
        CardModel source,
        PlayerChoiceContext choiceContext)
    {
        if (string.IsNullOrWhiteSpace(powerId) || delta == 0)
            return false;

        Player owner = source.Owner;
        LoadoutTargetSelection target =
            LoadoutTargetSelection.ForPlayer(owner.NetId);
        EnsureLoaded();
        bool adjusted;
        int appliedDelta;
        lock (SyncRoot)
        {
            adjusted = AdjustCounterLocked(
                powerId,
                delta,
                target,
                out appliedDelta);
        }

        if (!adjusted)
            return false;

        await ApplyCurrentCombatDeltaAsync(
            powerId,
            appliedDelta,
            target,
            owner,
            choiceContext,
            source);
        return true;
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

    public static void ReplaceCustomRunPlayerCounters(
        ulong playerNetId,
        IReadOnlyDictionary<string, int> counters)
    {
        EnsureLoaded();
        lock (SyncRoot)
        {
            Dictionary<string, int> normalized = NormalizeCounters(
                counters.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
            if (normalized.Count == 0)
                _run.PlayerCountersByNetId.Remove(playerNetId);
            else
                _run.PlayerCountersByNetId[playerNetId] = normalized;
            SaveRunState();
        }
    }

    private static bool AdjustCounterLocked(
        string powerId,
        int delta,
        LoadoutTargetSelection target,
        out int appliedDelta)
    {
        appliedDelta = 0;
        Dictionary<string, int>? counters = GetCounters(target, createPlayerBucket: true);
        if (counters is null)
            return false;

        int current = counters.GetValueOrDefault(powerId, 0);
        long requested = (long)current + delta;
        int next = requested > int.MaxValue
            ? int.MaxValue
            : requested < int.MinValue
                ? int.MinValue
                : (int)requested;
        appliedDelta = (int)((long)next - current);
        if (appliedDelta == 0)
            return false;

        if (next == 0)
            counters.Remove(powerId);
        else
            counters[powerId] = next;

        SaveRunState();
        return true;
    }

    public static async Task AwaitCombatStartApplicationAsync()
    {
        Task? task = _combatStartTask;
        if (task is not null)
            await task;

        if (ReferenceEquals(task, _combatStartTask))
            ResetCombatStart();
    }

    private static void OnCombatSetUp(CombatState combatState)
    {
        if (!_registered)
            return;

        INetGameService netService = RunManager.Instance.NetService;
        if (netService.Type == NetGameType.Client)
        {
            TaskCompletionSource<PowerGiverCounterSnapshot> source;
            PowerGiverCounterSnapshot? pendingSnapshot;
            lock (SyncRoot)
            {
                _combatStartSnapshotSource?.TrySetCanceled();
                source = new TaskCompletionSource<PowerGiverCounterSnapshot>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _combatStartSnapshotSource = source;
                pendingSnapshot = _pendingCombatStartSnapshot;
                _pendingCombatStartSnapshot = null;
            }

            if (pendingSnapshot is not null)
                source.TrySetResult(pendingSnapshot);
            _combatStartTask = ApplyCombatStartSnapshotAsync(source.Task, combatState);
            return;
        }

        EnsureLoaded();
        PowerGiverCounterSnapshot snapshot;
        lock (SyncRoot)
            snapshot = CreateCounterSnapshotLocked();

        ResetCombatStart();
        if (netService.Type == NetGameType.Host)
        {
            PowerGiverCombatStartMessage message = new()
            {
                SnapshotJson = JsonSerializer.Serialize(snapshot)
            };
            LoadoutNetworkBroadcast.SendToRunClients(
                netService,
                recipient => netService.SendMessage(message, recipient),
                "Power Giver combat start");
        }

        _combatStartTask = ApplyCombatStartSnapshotAsync(
            Task.FromResult(snapshot),
            combatState);
    }

    private static async Task ApplyCombatStartSnapshotAsync(
        Task<PowerGiverCounterSnapshot> snapshotTask,
        CombatState combatState)
    {
        PowerGiverCounterSnapshot snapshot = NormalizeCounterSnapshot(await snapshotTask);
        ApplyHostSnapshot(snapshot);
        await ApplyConfiguredPowersAsync(
            combatState,
            snapshot.AllPlayerCounters,
            snapshot.PlayerCountersByNetId,
            snapshot.MonsterCounters);
    }

    private static void RegisterCombatStartHook()
    {
        if (_combatStartHookRegistered)
            return;

        PowerGiverCombatStartHook hook = ModelDb.GetById<PowerGiverCombatStartHook>(
            ModelDb.GetId<PowerGiverCombatStartHook>());
        ModHelper.SubscribeForRunStateHooks(CombatStartHookId, _ => [hook]);
        _combatStartHookRegistered = true;
    }

    private static void OnRunStarted(RunState _)
    {
        if (_runNetService?.Type == NetGameType.Client)
        {
            PowerGiverCounterSnapshot? hostSnapshot;
            lock (SyncRoot)
                hostSnapshot = _pendingHostSnapshot;
            if (hostSnapshot is not null)
                ApplyHostSnapshot(hostSnapshot);
            return;
        }

        ReloadRunState();
    }

    private static void OnProfileIdChanged(int _)
    {
        Favorites.Reset();
        ResetHostSnapshot();
        ReloadRunState();
    }

    private static void RegisterMessageHandlers(INetGameService netService)
    {
        if (!RegisteredMessageServices.Add(netService))
            return;

        netService.RegisterMessageHandler<PowerGiverSnapshotRequestMessage>(HandleSnapshotRequest);
        netService.RegisterMessageHandler<PowerGiverSnapshotMessage>(HandleSnapshot);
        netService.RegisterMessageHandler<PowerGiverCombatStartMessage>(HandleCombatStartSnapshot);
    }

    private static void RegisterRunNetService(INetGameService netService)
    {
        if (ReferenceEquals(_runNetService, netService))
            return;

        if (_runNetService is not null)
            UnregisterMessageHandlers(_runNetService);
        _runNetService = netService;
        RegisterMessageHandlers(netService);
    }

    private static void BindRunLobby(RunLobby? runLobby)
    {
        if (ReferenceEquals(_runLobby, runLobby))
            return;

        UnbindRunLobby();
        _runLobby = runLobby;
        if (_runLobby is not null)
        {
            _playerRejoinedHandler = Sts2Compatibility.SubscribeRunLobbyPlayerRejoined(
                _runLobby,
                OnPlayerRejoined);
        }
    }

    private static void UnbindRunLobby()
    {
        if (_runLobby is null)
            return;

        if (_playerRejoinedHandler is not null)
        {
            Sts2Compatibility.UnsubscribeRunLobbyPlayerRejoined(
                _runLobby,
                _playerRejoinedHandler);
        }

        _playerRejoinedHandler = null;
        _runLobby = null;
    }

    private static void OnPlayerRejoined(ulong playerId)
    {
        if (_runNetService?.Type == NetGameType.Host && playerId != _runNetService.NetId)
            SendSnapshot(_runNetService, playerId);
    }

    private static void BroadcastSnapshot()
    {
        if (_runNetService?.Type != NetGameType.Host)
            return;

        EnsureLoaded();
        PowerGiverCounterSnapshot snapshot;
        lock (SyncRoot)
            snapshot = CreateCounterSnapshotLocked();
        PowerGiverSnapshotMessage message = new()
        {
            SnapshotJson = JsonSerializer.Serialize(snapshot)
        };

        LoadoutNetworkBroadcast.SendToRunClients(
            _runNetService,
            recipient => _runNetService.SendMessage(message, recipient),
            "Power Giver snapshot");
    }

    private static void SendSnapshot(INetGameService netService, ulong recipient)
    {
        if (netService.Type != NetGameType.Host || recipient == netService.NetId)
            return;

        EnsureLoaded();
        PowerGiverCounterSnapshot snapshot;
        lock (SyncRoot)
            snapshot = CreateCounterSnapshotLocked();

        netService.SendMessage(new PowerGiverSnapshotMessage
        {
            SnapshotJson = JsonSerializer.Serialize(snapshot)
        }, recipient);
    }

    private static void UnregisterMessageHandlers(INetGameService netService)
    {
        if (!RegisteredMessageServices.Remove(netService))
            return;

        netService.UnregisterMessageHandler<PowerGiverSnapshotRequestMessage>(HandleSnapshotRequest);
        netService.UnregisterMessageHandler<PowerGiverSnapshotMessage>(HandleSnapshot);
        netService.UnregisterMessageHandler<PowerGiverCombatStartMessage>(HandleCombatStartSnapshot);
    }

    private static void HandleSnapshotRequest(PowerGiverSnapshotRequestMessage _, ulong senderId)
    {
        LoadRunLobby? lobby = RegisteredLoadLobbies.FirstOrDefault(candidate =>
            candidate.NetService.Type == NetGameType.Host
            && Sts2Compatibility.EnumerateLoadRunLobbyPlayerIds(candidate).Contains(senderId));
        if (lobby is not null)
        {
            SendSnapshot(lobby.NetService, senderId);
            return;
        }

        if (_runNetService?.Type != NetGameType.Host || !IsCurrentRunPlayer(senderId))
            return;
        SendSnapshot(_runNetService, senderId);
    }

    private static void HandleSnapshot(PowerGiverSnapshotMessage message, ulong senderId)
    {
        if (!LoadoutNetworkBroadcast.IsExpectedHostSender(
                senderId,
                _runNetService,
                RegisteredLoadLobbies.Select(lobby => lobby.NetService)))
            return;

        PowerGiverCounterSnapshot? snapshot;
        try
        {
            snapshot = JsonSerializer.Deserialize<PowerGiverCounterSnapshot>(message.SnapshotJson);
        }
        catch (Exception exception)
        {
            GD.PushWarning($"PowerGiver: failed to read host snapshot. {exception.Message}");
            return;
        }

        if (snapshot is null)
            return;

        snapshot = NormalizeCounterSnapshot(snapshot);
        lock (SyncRoot)
            _pendingHostSnapshot = snapshot;
        ApplyHostSnapshot(snapshot);
    }

    private static void HandleCombatStartSnapshot(PowerGiverCombatStartMessage message, ulong senderId)
    {
        if (!LoadoutNetworkBroadcast.IsExpectedHostSender(
                senderId,
                _runNetService,
                RegisteredLoadLobbies.Select(lobby => lobby.NetService)))
        {
            return;
        }

        PowerGiverCounterSnapshot? snapshot;
        try
        {
            snapshot = JsonSerializer.Deserialize<PowerGiverCounterSnapshot>(message.SnapshotJson);
        }
        catch (Exception exception)
        {
            GD.PushWarning($"PowerGiver: failed to read combat-start snapshot. {exception.Message}");
            return;
        }

        if (snapshot is null)
            return;

        snapshot = NormalizeCounterSnapshot(snapshot);
        TaskCompletionSource<PowerGiverCounterSnapshot>? source;
        lock (SyncRoot)
        {
            source = _combatStartSnapshotSource;
            if (source is null)
                _pendingCombatStartSnapshot = snapshot;
        }

        source?.TrySetResult(snapshot);
    }

    private static bool IsCurrentRunPlayer(ulong playerId)
    {
        try
        {
            RunState? runState = RunManager.Instance.IsInProgress
                ? RunManager.Instance.DebugOnlyGetState()
                : null;
            return runState?.Players.Any(player => player.NetId == playerId) == true;
        }
        catch
        {
            return false;
        }
    }

    private static void ApplyHostSnapshot(PowerGiverCounterSnapshot snapshot)
    {
        EnsureLoaded();
        snapshot = NormalizeCounterSnapshot(snapshot);
        lock (SyncRoot)
        {
            _run.AllPlayerCounters = snapshot.AllPlayerCounters;
            _run.PlayerCountersByNetId = snapshot.PlayerCountersByNetId;
            _run.MonsterCounters = snapshot.MonsterCounters;
        }
    }

    private static PowerGiverCounterSnapshot CreateCounterSnapshotLocked()
    {
        return new PowerGiverCounterSnapshot
        {
            AllPlayerCounters = new Dictionary<string, int>(_run.AllPlayerCounters, StringComparer.Ordinal),
            PlayerCountersByNetId = _run.PlayerCountersByNetId.ToDictionary(
                pair => pair.Key,
                pair => new Dictionary<string, int>(pair.Value, StringComparer.Ordinal)),
            MonsterCounters = new Dictionary<string, int>(_run.MonsterCounters, StringComparer.Ordinal)
        };
    }

    private static PowerGiverCounterSnapshot NormalizeCounterSnapshot(PowerGiverCounterSnapshot snapshot)
    {
        snapshot.AllPlayerCounters = NormalizeCounters(snapshot.AllPlayerCounters);
        snapshot.MonsterCounters = NormalizeCounters(snapshot.MonsterCounters);
        snapshot.PlayerCountersByNetId ??= new Dictionary<ulong, Dictionary<string, int>>();
        snapshot.PlayerCountersByNetId = snapshot.PlayerCountersByNetId
            .Select(pair => new KeyValuePair<ulong, Dictionary<string, int>>(
                pair.Key,
                NormalizeCounters(pair.Value)))
            .Where(pair => pair.Value.Count > 0)
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        return snapshot;
    }

    private static void ResetHostSnapshot()
    {
        lock (SyncRoot)
            _pendingHostSnapshot = null;
    }

    private static void ResetCombatStart()
    {
        TaskCompletionSource<PowerGiverCounterSnapshot>? source;
        lock (SyncRoot)
        {
            source = _combatStartSnapshotSource;
            _combatStartSnapshotSource = null;
            _pendingCombatStartSnapshot = null;
            _combatStartTask = null;
        }

        source?.TrySetCanceled();
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

    private static void ReloadRunState(long runStartTime)
    {
        lock (SyncRoot)
            LoadRunStateLocked(runStartTime);
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

            LoadRunStateLocked(currentRunStartTime.Value);
        }
    }

    private static void LoadRunStateLocked(long runStartTime)
    {
        _runLoaded = true;
        _loadedRunStartTime = runStartTime;
        string primaryPath = GetRunPath(RunDirectory, runStartTime);
        string legacyPath = GetRunPath(LegacyRunDirectory, runStartTime);
        SaveUtility.LoadResult<PowerGiverRunState> loaded = SaveUtility.LoadProfileJson(
            primaryPath,
            new PowerGiverRunState { RunStartTime = runStartTime },
            [legacyPath]);

        _run = NormalizeRunState(loaded.Value, runStartTime);
        if (loaded.Loaded && (!loaded.LoadedFrom(primaryPath) || loaded.Value.SchemaVersion != CurrentSchemaVersion))
            SaveRunState();
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
        IReadOnlyDictionary<ulong, Dictionary<string, int>> playerCountersByNetId,
        IReadOnlyDictionary<string, int> monsterCounters)
    {
        foreach (Player player in combatState.Players.OrderBy(player => player.NetId))
        {
            Dictionary<string, int> mergedCounters = new(allPlayerCounters, StringComparer.Ordinal);
            if (playerCountersByNetId.TryGetValue(player.NetId, out Dictionary<string, int>? playerCounters))
            {
                foreach ((string powerId, int amount) in playerCounters)
                    mergedCounters[powerId] = mergedCounters.GetValueOrDefault(powerId, 0) + amount;
            }

            foreach ((string powerId, int amount) in mergedCounters
                         .Where(pair => pair.Value != 0)
                         .OrderBy(pair => pair.Key, StringComparer.Ordinal))
                await ApplyPowerToTargets(powerId, amount, [player.Creature], player.Creature);
        }

        Creature? applier = combatState.Players.FirstOrDefault()?.Creature;
        IReadOnlyList<Creature> enemies = combatState.Enemies.ToList();
        foreach ((string powerId, int amount) in monsterCounters.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            await ApplyPowerToTargets(powerId, amount, enemies, applier);
    }

    private static async Task ApplyCurrentCombatDeltaAsync(
        string powerId,
        int amount,
        LoadoutTargetSelection target,
        Player actionPlayer,
        PlayerChoiceContext? choiceContext = null,
        CardModel? source = null)
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
        await ApplyPowerToTargets(
            powerId,
            amount,
            targets,
            applier,
            choiceContext,
            source);
    }

    private static async Task ApplyPowerToTargets(
        string powerId,
        int amount,
        IEnumerable<Creature> targets,
        Creature? applier,
        PlayerChoiceContext? choiceContext = null,
        CardModel? source = null)
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
                await PowerCmd.Apply(
                    choiceContext ?? new ThrowingPlayerChoiceContext(),
                    power.ToMutable(),
                    target,
                    amount,
                    applier,
                    source);
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

public sealed class PowerGiverCounterSnapshot
{
    [JsonPropertyName("allPlayerCounters")]
    public Dictionary<string, int> AllPlayerCounters { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("playerCountersByNetId")]
    public Dictionary<ulong, Dictionary<string, int>> PlayerCountersByNetId { get; set; } = new();

    [JsonPropertyName("monsterCounters")]
    public Dictionary<string, int> MonsterCounters { get; set; } = new(StringComparer.Ordinal);
}

public struct PowerGiverSnapshotRequestMessage : INetMessage, IPacketSerializable
{
    public bool ShouldBroadcast => false;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.VeryDebug;
    public bool ShouldBuffer => true;

    public readonly void Serialize(PacketWriter writer)
    {
    }

    public void Deserialize(PacketReader reader)
    {
    }
}

public struct PowerGiverSnapshotMessage : INetMessage, IPacketSerializable
{
    public string SnapshotJson;

    public bool ShouldBroadcast => false;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.VeryDebug;
    public bool ShouldBuffer => true;

    public readonly void Serialize(PacketWriter writer)
    {
        writer.WriteString(SnapshotJson ?? string.Empty);
    }

    public void Deserialize(PacketReader reader)
    {
        SnapshotJson = reader.ReadString();
    }
}

public struct PowerGiverCombatStartMessage : INetMessage, IPacketSerializable
{
    public string SnapshotJson;

    public bool ShouldBroadcast => false;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.Debug;
    public bool ShouldBuffer => false;

    public readonly void Serialize(PacketWriter writer)
    {
        writer.WriteString(SnapshotJson ?? string.Empty);
    }

    public void Deserialize(PacketReader reader)
    {
        SnapshotJson = reader.ReadString();
    }
}

[HarmonyPatch(typeof(LoadRunLobby))]
public static class LoadRunLobbyPowerGiverConstructorPatch
{
    public static IEnumerable<MethodBase> TargetMethods()
        => AccessTools.GetDeclaredConstructors(typeof(LoadRunLobby));

    [HarmonyPostfix]
    public static void Postfix(LoadRunLobby __instance)
        => PowerGiverStateService.RegisterLoadLobby(__instance);
}

[HarmonyPatch(typeof(LoadRunLobby), nameof(LoadRunLobby.CleanUp))]
public static class LoadRunLobbyPowerGiverCleanUpPatch
{
    [HarmonyPrefix]
    public static void Prefix(LoadRunLobby __instance, bool disconnectSession)
        => PowerGiverStateService.UnregisterLoadLobby(__instance, disconnectSession);
}

[HarmonyPatch(typeof(RunManager), nameof(RunManager.Launch))]
public static class RunManagerPowerGiverLaunchPatch
{
    [HarmonyPrefix]
    public static void Prefix()
        => PowerGiverStateService.PrepareRunLaunch();

    [HarmonyPostfix]
    public static void Postfix()
        => PowerGiverStateService.OnRunLaunched();
}

[HarmonyPatch(typeof(RunManager), nameof(RunManager.CleanUp))]
public static class RunManagerPowerGiverCleanUpPatch
{
    [HarmonyPrefix]
    public static void Prefix()
        => PowerGiverStateService.OnRunCleaningUp();
}
