#nullable enable

namespace Loadout.Services.TildeKey;

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
using Loadout.Services.Saving;
using Loadout.Services.Targets;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

public sealed class TildeKeyMutationPayload
{
    [JsonPropertyName("statId")]
    public string? StatId { get; set; }

    [JsonPropertyName("toggleId")]
    public string? ToggleId { get; set; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }
}

public sealed class TildeKeyStatDefinition
{
    internal TildeKeyStatDefinition(
        string id,
        string label,
        Func<Player, int?> getValue,
        Action<Player, int> setValue,
        bool supportsLock = true)
    {
        Id = id;
        Label = label;
        SupportsLock = supportsLock;
        GetValue = getValue;
        SetValue = setValue;
    }

    public string Id { get; }
    public string Label { get; }
    public bool SupportsLock { get; }

    internal Func<Player, int?> GetValue { get; }
    internal Action<Player, int> SetValue { get; }
}

public static class TildeKeyStateService
{
    public const string TargetKey = "tilde_key";
    public const string GodmodeToggleId = "godmode";
    public const string GoToAnyRoomToggleId = "go_to_any_room";

    private const int CurrentSchemaVersion = 1;
    private const string RunDirectory = "loadout/services/tilde_key";
    private const string RunFilePrefix = "tilde_key_run";
    private const int GodmodeAmount = 999999999;

    private static readonly object SyncRoot = new();
    private static readonly FieldInfo? CurrentHpField = AccessTools.Field(typeof(Creature), "_currentHp");
    private static readonly FieldInfo? MaxHpField = AccessTools.Field(typeof(Creature), "_maxHp");
    private static readonly FieldInfo? BlockField = AccessTools.Field(typeof(Creature), "_block");
    private static readonly FieldInfo? CurrentHpChangedField = AccessTools.Field(typeof(Creature), "CurrentHpChanged");
    private static readonly FieldInfo? MaxHpChangedField = AccessTools.Field(typeof(Creature), "MaxHpChanged");
    private static readonly FieldInfo? BlockChangedField = AccessTools.Field(typeof(Creature), "BlockChanged");
    private static readonly FieldInfo? TurnNumberField = AccessTools.Field(typeof(PlayerCombatState), "<TurnNumber>k__BackingField");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly IReadOnlyList<TildeKeyStatDefinition> Definitions =
    [
        new("current_hp", "Current HP", player => player.Creature.CurrentHp, SetCurrentHpDirect),
        new("max_hp", "Max HP", player => player.Creature.MaxHp, SetMaxHpDirect),
        new("block", "Block", player => player.Creature.Block, SetBlockDirect),
        new("gold", "Gold", player => player.Gold, (player, value) => player.Gold = value),
        new("max_energy", "Max Energy", player => player.MaxEnergy, (player, value) => player.MaxEnergy = value),
        new("combat_energy", "Combat Energy", player => player.PlayerCombatState?.Energy, (player, value) =>
        {
            if (player.PlayerCombatState is not null)
                player.PlayerCombatState.Energy = value;
        }),
        new("stars", "Stars", player => player.PlayerCombatState?.Stars, (player, value) =>
        {
            if (player.PlayerCombatState is not null)
                player.PlayerCombatState.Stars = value;
        }),
        new("base_orb_slots", "Base Orb Slots", player => player.BaseOrbSlotCount, SetBaseOrbSlots),
        new("max_potion_slots", "Max Potion Slots", player => player.MaxPotionCount, SetMaxPotionSlots, supportsLock: false),
        new("turn_number", "Turn Number", player => player.PlayerCombatState?.TurnNumber, SetTurnNumber),
        new("extra_card_shop_removals", "Card Shop Removals Used", player => player.ExtraFields.CardShopRemovalsUsed, (player, value) => player.ExtraFields.CardShopRemovalsUsed = value),
        new("extra_wongo_points", "Wongo Points", player => player.ExtraFields.WongoPoints, (player, value) => player.ExtraFields.WongoPoints = value),
        new("extra_damage_dealt", "Damage Dealt", player => player.ExtraFields.DamageDealt, (player, value) => player.ExtraFields.DamageDealt = value),
        new("extra_debuffs_applied", "Debuffs Applied", player => player.ExtraFields.DebuffsApplied, (player, value) => player.ExtraFields.DebuffsApplied = value)
    ];

    private static readonly Dictionary<string, TildeKeyStatDefinition> DefinitionById =
        Definitions.ToDictionary(definition => definition.Id, StringComparer.Ordinal);

    private static RunSaveData _run = new();
    private static bool _registered;
    private static bool _runLoaded;
    private static long? _loadedRunStartTime;
    private static NMapScreen? _lastMapScreen;
    private static bool _lastDesiredDebugTravel;

    public static event Action? StateChanged;

    public static IReadOnlyList<TildeKeyStatDefinition> StatDefinitions => Definitions;

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
        ReloadRunIfNeeded();
    }

    public static void Process(float _)
    {
        if (!RunManager.Instance.IsInProgress)
        {
            SyncMapDebugTravel(force: false);
            lock (SyncRoot)
            {
                _runLoaded = false;
                _loadedRunStartTime = null;
                _run = new RunSaveData();
            }
            return;
        }

        if (!_runLoaded)
            ReloadRunIfNeeded();

        ApplyLockedStatsForCurrentRun();
        SyncMapDebugTravel(force: false);
    }

    public static int GetDisplayValue(TildeKeyStatDefinition definition, LoadoutTargetSelection target)
    {
        Player? player = ResolveTargetPlayers(target).FirstOrDefault();
        if (player is not null && definition.GetValue(player) is { } currentValue)
            return currentValue;

        if (player is not null
            && TryGetSavedStat(player.NetId, definition.Id, out TildeKeySavedStat? saved)
            && saved is not null)
        {
            return saved.Value;
        }

        return 0;
    }

    public static bool IsLocked(TildeKeyStatDefinition definition, LoadoutTargetSelection target)
    {
        if (!definition.SupportsLock)
            return false;

        IReadOnlyList<Player> players = ResolveTargetPlayers(target);
        if (players.Count == 0)
            return false;

        foreach (Player player in players)
        {
            if (!TryGetSavedStat(player.NetId, definition.Id, out TildeKeySavedStat? saved)
                || saved is null
                || !saved.Locked)
            {
                return false;
            }
        }

        return true;
    }

    public static bool GetToggle(string toggleId, LoadoutTargetSelection target)
    {
        EnsureLoaded();
        lock (SyncRoot)
        {
            if (string.Equals(toggleId, GoToAnyRoomToggleId, StringComparison.Ordinal))
                return _run.GlobalToggles.TryGetValue(toggleId, out bool enabled) && enabled;
        }

        IReadOnlyList<Player> players = ResolveTargetPlayers(target);
        if (players.Count == 0)
            return false;

        lock (SyncRoot)
        {
            foreach (Player player in players)
            {
                if (!GetPlayerStateLocked(player.NetId, create: false, out TildeKeyPlayerState? state)
                    || state is null
                    || !state.Toggles.TryGetValue(toggleId, out bool enabled)
                    || !enabled)
                {
                    return false;
                }
            }
        }

        return true;
    }

    public static void ApplySynchronizedStatSet(
        string payloadJson,
        int value,
        LoadoutTargetSelection target,
        Player requester)
    {
        if (!TryParsePayload(payloadJson, out TildeKeyMutationPayload? payload)
            || payload is null
            || string.IsNullOrWhiteSpace(payload.StatId)
            || !DefinitionById.TryGetValue(payload.StatId, out TildeKeyStatDefinition? definition))
        {
            return;
        }

        EnsureLoaded();
        foreach (Player player in ResolveTargetPlayers(target, requester))
        {
            ApplyStat(definition, player, value);
            UpdateSavedValueIfLocked(player.NetId, definition.Id, value);
        }

        RaiseStateChanged();
    }

    public static void ApplySynchronizedStatLock(
        string payloadJson,
        int value,
        LoadoutTargetSelection target,
        Player requester)
    {
        if (!TryParsePayload(payloadJson, out TildeKeyMutationPayload? payload)
            || payload is null
            || string.IsNullOrWhiteSpace(payload.StatId)
            || !DefinitionById.TryGetValue(payload.StatId, out TildeKeyStatDefinition? definition)
            || !definition.SupportsLock)
        {
            return;
        }

        EnsureLoaded();
        foreach (Player player in ResolveTargetPlayers(target, requester))
        {
            if (payload.Enabled)
            {
                ApplyStat(definition, player, value);
                SetSavedLock(player.NetId, definition.Id, value);
            }
            else
            {
                ClearSavedLock(player.NetId, definition.Id);
            }
        }

        SaveRunState();
        RaiseStateChanged();
    }

    public static void ApplySynchronizedToggle(
        string payloadJson,
        LoadoutTargetSelection target,
        Player requester)
    {
        if (!TryParsePayload(payloadJson, out TildeKeyMutationPayload? payload)
            || payload is null
            || string.IsNullOrWhiteSpace(payload.ToggleId))
        {
            return;
        }

        EnsureLoaded();
        if (string.Equals(payload.ToggleId, GoToAnyRoomToggleId, StringComparison.Ordinal))
        {
            lock (SyncRoot)
            {
                if (payload.Enabled)
                    _run.GlobalToggles[GoToAnyRoomToggleId] = true;
                else
                    _run.GlobalToggles.Remove(GoToAnyRoomToggleId);
            }

            SaveRunState();
            SyncMapDebugTravel(force: true);
            RaiseStateChanged();
            return;
        }

        if (!string.Equals(payload.ToggleId, GodmodeToggleId, StringComparison.Ordinal))
            return;

        foreach (Player player in ResolveTargetPlayers(target, requester))
        {
            SetSavedToggle(player.NetId, GodmodeToggleId, payload.Enabled);
            TaskHelper.RunSafely(payload.Enabled ? EnableGodmode(player) : DisableGodmode(player));
        }

        SaveRunState();
        RaiseStateChanged();
    }

    public static async Task KillCurrentEnemiesAsync()
    {
        if (!CombatManager.Instance.IsInProgress)
            return;

        CombatState? combatState = CombatManager.Instance.DebugOnlyGetState();
        if (combatState is null)
            return;

        List<Creature> enemies = combatState.Enemies.ToList();
        foreach (Creature enemy in enemies)
            await CreatureCmd.Kill(enemy);

        await CombatManager.Instance.CheckWinCondition();
    }

    public static async Task SpareCurrentEnemiesAsync()
    {
        if (!CombatManager.Instance.IsInProgress)
            return;

        CombatState? combatState = CombatManager.Instance.DebugOnlyGetState();
        if (combatState is null)
            return;

        List<Creature> enemies = combatState.Enemies.ToList();
        foreach (Creature enemy in enemies)
            await CreatureCmd.Escape(enemy);

        await CombatManager.Instance.CheckWinCondition();
    }

    private static void ApplyLockedStatsForCurrentRun()
    {
        RunState? runState;
        try
        {
            runState = RunManager.Instance.DebugOnlyGetState();
        }
        catch
        {
            return;
        }

        if (runState is null)
            return;

        List<(ulong NetId, string StatId, int Value)> locks = new();
        lock (SyncRoot)
        {
            foreach ((string rawNetId, TildeKeyPlayerState state) in _run.Players)
            {
                if (!ulong.TryParse(rawNetId, out ulong netId))
                    continue;

                foreach ((string statId, TildeKeySavedStat saved) in state.Stats)
                {
                    if (saved.Locked)
                        locks.Add((netId, statId, saved.Value));
                }
            }
        }

        foreach ((ulong netId, string statId, int value) in locks)
        {
            Player? player = runState.GetPlayer(netId);
            if (player is null || !DefinitionById.TryGetValue(statId, out TildeKeyStatDefinition? definition))
                continue;

            ApplyStat(definition, player, value);
        }
    }

    private static void ApplyStat(TildeKeyStatDefinition definition, Player player, int value)
    {
        try
        {
            definition.SetValue(player, value);
        }
        catch (Exception exception)
        {
            GD.PushWarning($"TildeKey: failed setting '{definition.Id}' for player {player.NetId}. {exception.Message}");
        }
    }

    private static void SetCurrentHpDirect(Player player, int value)
    {
        Creature creature = player.Creature;
        int oldValue = creature.CurrentHp;
        bool wasDead = creature.IsDead;
        if (CurrentHpField is not null)
        {
            CurrentHpField.SetValue(creature, value);
            InvokeCreatureIntChanged(CurrentHpChangedField, creature, oldValue, value);
        }
        else
        {
            creature.SetCurrentHpInternal(value);
        }

        if (wasDead && creature.IsAlive)
            player.ActivateHooks();
        else if (!wasDead && creature.IsDead)
            player.DeactivateHooks();
    }

    private static void SetMaxHpDirect(Player player, int value)
    {
        Creature creature = player.Creature;
        int oldValue = creature.MaxHp;
        if (MaxHpField is not null)
        {
            MaxHpField.SetValue(creature, value);
            InvokeCreatureIntChanged(MaxHpChangedField, creature, oldValue, value);
            return;
        }

        creature.SetMaxHpInternal(value);
    }

    private static void SetBlockDirect(Player player, int value)
    {
        Creature creature = player.Creature;
        int oldValue = creature.Block;
        if (BlockField is not null)
        {
            BlockField.SetValue(creature, value);
            InvokeCreatureIntChanged(BlockChangedField, creature, oldValue, value);
            return;
        }

        if (value > creature.Block)
            creature.GainBlockInternal(value - creature.Block);
        else
            creature.LoseBlockInternal(creature.Block - value);
    }

    private static void SetBaseOrbSlots(Player player, int value)
    {
        player.BaseOrbSlotCount = value;
        if (player.PlayerCombatState is null)
            return;

        int delta = value - player.PlayerCombatState.OrbQueue.Capacity;
        if (delta > 0)
            player.PlayerCombatState.OrbQueue.AddCapacity(delta);
        else if (delta < 0)
            player.PlayerCombatState.OrbQueue.RemoveCapacity(-delta);
    }

    private static void SetMaxPotionSlots(Player player, int value)
    {
        int target = Math.Max(0, value);
        int delta = target - player.MaxPotionCount;
        if (delta > 0)
            player.AddToMaxPotionCount(delta);
        else if (delta < 0)
            player.SubtractFromMaxPotionCount(-delta);
    }

    private static void SetTurnNumber(Player player, int value)
    {
        if (player.PlayerCombatState is null)
            return;

        TurnNumberField?.SetValue(player.PlayerCombatState, value);
    }

    private static void InvokeCreatureIntChanged(FieldInfo? eventField, Creature creature, int oldValue, int newValue)
    {
        if (oldValue == newValue)
            return;

        try
        {
            if (eventField?.GetValue(creature) is Action<int, int> changed)
                changed.Invoke(oldValue, newValue);
        }
        catch (Exception exception)
        {
            GD.PushWarning($"TildeKey: failed to invoke creature change event. {exception.Message}");
        }
    }

    private static async Task EnableGodmode(Player player)
    {
        Creature creature = player.Creature;
        await PowerCmd.Apply<StrengthPower>(new ThrowingPlayerChoiceContext(), creature, GodmodeAmount, creature, null);
        await PowerCmd.Apply<BufferPower>(new ThrowingPlayerChoiceContext(), creature, GodmodeAmount, creature, null);
        await PowerCmd.Apply<RegenPower>(new ThrowingPlayerChoiceContext(), creature, GodmodeAmount, creature, null);
    }

    private static async Task DisableGodmode(Player player)
    {
        Creature creature = player.Creature;
        await PowerCmd.Remove<StrengthPower>(creature);
        await PowerCmd.Remove<BufferPower>(creature);
        await PowerCmd.Remove<RegenPower>(creature);
    }

    private static void SyncMapDebugTravel(bool force)
    {
        bool desired = IsGoToAnyRoomEnabled();
        NMapScreen? screen = NMapScreen.Instance;
        if (screen is null)
        {
            _lastMapScreen = null;
            _lastDesiredDebugTravel = desired;
            return;
        }

        bool shouldTouchScreen = force
                                 || desired
                                 || _lastDesiredDebugTravel
                                 || !ReferenceEquals(screen, _lastMapScreen);
        if (!shouldTouchScreen)
            return;

        if (force
            || !ReferenceEquals(screen, _lastMapScreen)
            || screen.IsDebugTravelEnabled != desired)
        {
            screen.SetDebugTravelEnabled(desired);
        }

        _lastMapScreen = screen;
        _lastDesiredDebugTravel = desired;
    }

    private static bool IsGoToAnyRoomEnabled()
    {
        EnsureLoaded();
        lock (SyncRoot)
            return _run.GlobalToggles.TryGetValue(GoToAnyRoomToggleId, out bool enabled) && enabled;
    }

    private static IReadOnlyList<Player> ResolveTargetPlayers(LoadoutTargetSelection target, Player? fallback = null)
    {
        try
        {
            if (RunManager.Instance.IsInProgress)
            {
                RunState? runState = RunManager.Instance.DebugOnlyGetState();
                if (runState is null)
                    return fallback is null ? [] : [fallback];

                IReadOnlyList<Player> players = LoadoutTargetService.ResolvePlayers(target, runState);
                if (players.Count > 0)
                    return players;
            }
        }
        catch
        {
            // Fall through to fallback.
        }

        return fallback is null ? [] : [fallback];
    }

    private static bool TryGetSavedStat(ulong netId, string statId, out TildeKeySavedStat? saved)
    {
        EnsureLoaded();
        lock (SyncRoot)
        {
            if (GetPlayerStateLocked(netId, create: false, out TildeKeyPlayerState? state)
                && state is not null
                && state.Stats.TryGetValue(statId, out TildeKeySavedStat? found))
            {
                saved = found;
                return true;
            }
        }

        saved = null;
        return false;
    }

    private static void UpdateSavedValueIfLocked(ulong netId, string statId, int value)
    {
        bool changed = false;
        lock (SyncRoot)
        {
            if (GetPlayerStateLocked(netId, create: false, out TildeKeyPlayerState? state)
                && state is not null
                && state.Stats.TryGetValue(statId, out TildeKeySavedStat? saved)
                && saved is not null
                && saved.Locked
                && saved.Value != value)
            {
                saved.Value = value;
                changed = true;
            }
        }

        if (changed)
            SaveRunState();
    }

    private static void SetSavedLock(ulong netId, string statId, int value)
    {
        lock (SyncRoot)
        {
            GetPlayerStateLocked(netId, create: true, out TildeKeyPlayerState? state);
            state!.Stats[statId] = new TildeKeySavedStat { Value = value, Locked = true };
        }
    }

    private static void ClearSavedLock(ulong netId, string statId)
    {
        lock (SyncRoot)
        {
            if (!GetPlayerStateLocked(netId, create: false, out TildeKeyPlayerState? state)
                || state is null)
                return;

            state.Stats.Remove(statId);
            RemovePlayerIfEmptyLocked(netId);
        }
    }

    private static void SetSavedToggle(ulong netId, string toggleId, bool enabled)
    {
        lock (SyncRoot)
        {
            GetPlayerStateLocked(netId, create: true, out TildeKeyPlayerState? state);
            if (enabled)
                state!.Toggles[toggleId] = true;
            else
                state!.Toggles.Remove(toggleId);

            RemovePlayerIfEmptyLocked(netId);
        }
    }

    private static bool GetPlayerStateLocked(ulong netId, bool create, out TildeKeyPlayerState? state)
    {
        string key = NetIdKey(netId);
        if (_run.Players.TryGetValue(key, out state))
            return true;

        if (!create)
            return false;

        state = new TildeKeyPlayerState();
        _run.Players[key] = state;
        return true;
    }

    private static void RemovePlayerIfEmptyLocked(ulong netId)
    {
        string key = NetIdKey(netId);
        if (_run.Players.TryGetValue(key, out TildeKeyPlayerState? state)
            && state is not null
            && state.Stats.Count == 0
            && state.Toggles.Count == 0)
        {
            _run.Players.Remove(key);
        }
    }

    private static string NetIdKey(ulong netId)
    {
        return netId.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static bool TryParsePayload(string payloadJson, out TildeKeyMutationPayload? payload)
    {
        try
        {
            payload = JsonSerializer.Deserialize<TildeKeyMutationPayload>(payloadJson, JsonOptions);
            return payload is not null;
        }
        catch (Exception exception)
        {
            GD.PushWarning($"TildeKey: failed to deserialize payload. {exception.Message}");
            payload = null;
            return false;
        }
    }

    private static void OnRunStarted(RunState _)
    {
        ReloadRun();
        ApplySavedGodmodeToPlayers();
        SyncMapDebugTravel(force: true);
    }

    private static void OnProfileIdChanged(int _)
    {
        lock (SyncRoot)
        {
            _runLoaded = false;
            _loadedRunStartTime = null;
            _run = new RunSaveData();
        }

        EnsureLoaded();
        SyncMapDebugTravel(force: true);
    }

    private static void OnCombatSetUp(CombatState combatState)
    {
        if (combatState is null)
            return;

        ApplySavedGodmodeToPlayers(combatState.Players);
        ApplyLockedStatsForCurrentRun();
    }

    private static void ApplySavedGodmodeToPlayers(IEnumerable<Player>? players = null)
    {
        EnsureLoaded();
        IReadOnlyList<Player> playerSnapshot = players?.ToList() ?? ResolveTargetPlayers(new LoadoutTargetSelection(LoadoutTargetScope.AllPlayers));
        if (playerSnapshot.Count == 0)
            return;

        List<Player> godmodePlayers = new();
        lock (SyncRoot)
        {
            foreach (Player player in playerSnapshot)
            {
                if (GetPlayerStateLocked(player.NetId, create: false, out TildeKeyPlayerState? state)
                    && state is not null
                    && state.Toggles.TryGetValue(GodmodeToggleId, out bool enabled)
                    && enabled)
                {
                    godmodePlayers.Add(player);
                }
            }
        }

        foreach (Player player in godmodePlayers)
            TaskHelper.RunSafely(EnableGodmode(player));
    }

    private static void ReloadRun()
    {
        lock (SyncRoot)
        {
            _runLoaded = false;
            _loadedRunStartTime = null;
            _run = new RunSaveData();
        }

        ReloadRunIfNeeded();
    }

    private static void ReloadRunIfNeeded()
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
                _run = NormalizeRun(new RunSaveData(), 0);
                return;
            }

            string path = GetRunPath(currentRunStartTime.Value);
            SaveUtility.LoadResult<RunSaveData> loaded =
                SaveUtility.LoadProfileJson(path, new RunSaveData { RunStartTime = currentRunStartTime.Value });
            _run = NormalizeRun(loaded.Value, currentRunStartTime.Value);

            if (loaded.Loaded && loaded.Value.SchemaVersion != CurrentSchemaVersion)
                SaveRunState();
        }
    }

    private static void SaveRunState()
    {
        long? runStartTime;
        RunSaveData snapshot;
        lock (SyncRoot)
        {
            if (_loadedRunStartTime is null)
                return;

            _run.SchemaVersion = CurrentSchemaVersion;
            _run.RunStartTime = _loadedRunStartTime.Value;
            _run = NormalizeRun(_run, _loadedRunStartTime.Value);
            runStartTime = _loadedRunStartTime.Value;
            snapshot = _run;
        }

        SaveUtility.SaveProfileJson(GetRunPath(runStartTime.Value), snapshot);
    }

    private static RunSaveData NormalizeRun(RunSaveData save, long runStartTime)
    {
        save.SchemaVersion = CurrentSchemaVersion;
        save.RunStartTime = runStartTime;
        save.Players = NormalizePlayers(save.Players);
        save.GlobalToggles = NormalizeToggles(save.GlobalToggles);
        return save;
    }

    private static Dictionary<string, TildeKeyPlayerState> NormalizePlayers(Dictionary<string, TildeKeyPlayerState>? players)
    {
        Dictionary<string, TildeKeyPlayerState> normalized = new(StringComparer.Ordinal);
        if (players is null)
            return normalized;

        foreach ((string key, TildeKeyPlayerState? value) in players)
        {
            if (string.IsNullOrWhiteSpace(key) || value is null)
                continue;

            TildeKeyPlayerState state = new()
            {
                Stats = value.Stats?
                    .Where(pair => !string.IsNullOrWhiteSpace(pair.Key)
                                   && DefinitionById.ContainsKey(pair.Key)
                                   && pair.Value is not null
                                   && pair.Value.Locked)
                    .ToDictionary(
                        pair => pair.Key,
                        pair => new TildeKeySavedStat { Value = pair.Value.Value, Locked = true },
                        StringComparer.Ordinal) ?? new Dictionary<string, TildeKeySavedStat>(StringComparer.Ordinal),
                Toggles = NormalizeToggles(value.Toggles)
            };

            if (state.Stats.Count > 0 || state.Toggles.Count > 0)
                normalized[key] = state;
        }

        return normalized;
    }

    private static Dictionary<string, bool> NormalizeToggles(Dictionary<string, bool>? toggles)
    {
        Dictionary<string, bool> normalized = new(StringComparer.Ordinal);
        if (toggles is null)
            return normalized;

        foreach ((string key, bool value) in toggles)
        {
            if (value && IsKnownToggle(key))
                normalized[key] = true;
        }

        return normalized;
    }

    private static bool IsKnownToggle(string key)
    {
        return string.Equals(key, GodmodeToggleId, StringComparison.Ordinal)
               || string.Equals(key, GoToAnyRoomToggleId, StringComparison.Ordinal);
    }

    private static string GetRunPath(long runStartTime)
    {
        return SaveUtility.GetRunSidecarPath(RunDirectory, RunFilePrefix, runStartTime);
    }

    private static void RaiseStateChanged()
    {
        StateChanged?.Invoke();
    }

    private struct RunSaveData : ISerializable
    {
        public RunSaveData()
        {
        }

        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        [JsonPropertyName("runStartTime")]
        public long RunStartTime { get; set; }

        [JsonPropertyName("players")]
        public Dictionary<string, TildeKeyPlayerState> Players { get; set; } = new(StringComparer.Ordinal);

        [JsonPropertyName("globalToggles")]
        public Dictionary<string, bool> GlobalToggles { get; set; } = new(StringComparer.Ordinal);

        public void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            info.AddValue(nameof(SchemaVersion), SchemaVersion);
            info.AddValue(nameof(RunStartTime), RunStartTime);
            info.AddValue(nameof(Players), Players);
            info.AddValue(nameof(GlobalToggles), GlobalToggles);
        }
    }
}

public sealed class TildeKeyPlayerState
{
    [JsonPropertyName("stats")]
    public Dictionary<string, TildeKeySavedStat> Stats { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("toggles")]
    public Dictionary<string, bool> Toggles { get; set; } = new(StringComparer.Ordinal);
}

public sealed class TildeKeySavedStat
{
    [JsonPropertyName("value")]
    public int Value { get; set; }

    [JsonPropertyName("locked")]
    public bool Locked { get; set; }
}
