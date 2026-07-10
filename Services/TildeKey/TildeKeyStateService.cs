#nullable enable

namespace Loadout.Services.TildeKey;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using Loadout.Services.Saving;
using Loadout.Services.Targets;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Extensions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.Relics;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.RelicCollection;
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

    [JsonPropertyName("counterMember")]
    public string? CounterMember { get; set; }
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
    public const string InfiniteEnergyToggleId = "infinite_energy";
    public const string DrawTillHandLimitToggleId = "draw_till_hand_limit";
    public const string ScrollRelicCounterToggleId = "scroll_relic_counter";
    public const string DrawPerTurnStatId = "draw_per_turn";
    public const string HandSizeStatId = "hand_size";
    public const string PlayerDamageMultiplierStatId = "player_damage_multiplier";
    public const string EnemyDamageMultiplierStatId = "enemy_damage_multiplier";

    private const int CurrentSchemaVersion = 3;
    private const string RunDirectory = "loadout/services/tilde_key";
    private const string RunFilePrefix = "tilde_key_run";
    private const int DefaultDrawPerTurn = 5;
    private const int DefaultHandSize = 10;
    private const int DefaultDamageMultiplier = 100;
    private const string RelicCounterLockBadgeName = "LoadoutTildeRelicCounterLockBadge";

    private static readonly object SyncRoot = new();
    private static readonly FieldInfo? CurrentHpField = AccessTools.Field(typeof(Creature), "_currentHp");
    private static readonly FieldInfo? MaxHpField = AccessTools.Field(typeof(Creature), "_maxHp");
    private static readonly FieldInfo? BlockField = AccessTools.Field(typeof(Creature), "_block");
    private static readonly FieldInfo? CurrentHpChangedField = AccessTools.Field(typeof(Creature), "CurrentHpChanged");
    private static readonly FieldInfo? MaxHpChangedField = AccessTools.Field(typeof(Creature), "MaxHpChanged");
    private static readonly FieldInfo? BlockChangedField = AccessTools.Field(typeof(Creature), "BlockChanged");
    private static readonly FieldInfo? TurnNumberField = AccessTools.Field(typeof(PlayerCombatState), "<TurnNumber>k__BackingField");
    private static readonly MethodInfo? RelicDisplayAmountChangedMethod = AccessTools.Method(typeof(RelicModel), "InvokeDisplayAmountChanged");
    private static readonly FieldInfo? CreatureStateDisplayField = AccessTools.Field(typeof(NCreature), "_stateDisplay");
    private static readonly MethodInfo? CreatureStateDisplayRefreshValuesMethod = AccessTools.Method(typeof(NCreatureStateDisplay), "RefreshValues");
    private static readonly MethodInfo? EnergyCounterRefreshLabelMethod = AccessTools.Method(typeof(NEnergyCounter), "RefreshLabel");
    private static readonly MethodInfo? MultiplayerPlayerStateRefreshValuesMethod = AccessTools.Method(typeof(NMultiplayerPlayerState), "RefreshValues");
    private static readonly MethodInfo? MultiplayerPlayerStateRefreshCombatValuesMethod = AccessTools.Method(typeof(NMultiplayerPlayerState), "RefreshCombatValues");
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
        new(DrawPerTurnStatId, "Draw per Turn", player => GetVirtualStatValue(player, DrawPerTurnStatId, DefaultDrawPerTurn), (player, value) => SetVirtualStatValue(player, DrawPerTurnStatId, value)),
        new(HandSizeStatId, "Hand Size", player => GetVirtualStatValue(player, HandSizeStatId, DefaultHandSize), (player, value) => SetVirtualStatValue(player, HandSizeStatId, value)),
        new(PlayerDamageMultiplierStatId, "Player Damage Multiplier", player => GetVirtualStatValue(player, PlayerDamageMultiplierStatId, DefaultDamageMultiplier), (player, value) => SetVirtualStatValue(player, PlayerDamageMultiplierStatId, value)),
        new(EnemyDamageMultiplierStatId, "Enemy Damage Multiplier", player => GetVirtualStatValue(player, EnemyDamageMultiplierStatId, DefaultDamageMultiplier), (player, value) => SetVirtualStatValue(player, EnemyDamageMultiplierStatId, value)),
        new("extra_card_shop_removals", "Card Shop Removals Used", player => player.ExtraFields.CardShopRemovalsUsed, (player, value) => player.ExtraFields.CardShopRemovalsUsed = value),
        new("extra_wongo_points", "Wongo Points", player => player.ExtraFields.WongoPoints, (player, value) => player.ExtraFields.WongoPoints = value),
        new("extra_damage_dealt", "Damage Dealt", player => player.ExtraFields.DamageDealt, (player, value) => player.ExtraFields.DamageDealt = value),
        new("extra_debuffs_applied", "Debuffs Applied", player => player.ExtraFields.DebuffsApplied, (player, value) => player.ExtraFields.DebuffsApplied = value)
    ];

    private static readonly Dictionary<string, TildeKeyStatDefinition> DefinitionById =
        Definitions.ToDictionary(definition => definition.Id, StringComparer.Ordinal);

    private static RunSaveData _run = new();
    private static readonly Dictionary<string, Dictionary<string, int>> VirtualStats = new(StringComparer.Ordinal);
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
        CombatManager.Instance.TurnStarted += OnTurnStarted;
        EnsureLoaded();
    }

    public static void Unregister()
    {
        if (!_registered)
            return;

        RunManager.Instance.RunStarted -= OnRunStarted;
        SaveManager.Instance.ProfileIdChanged -= OnProfileIdChanged;
        CombatManager.Instance.CombatSetUp -= OnCombatSetUp;
        CombatManager.Instance.TurnStarted -= OnTurnStarted;
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
                VirtualStats.Clear();
            }
            return;
        }

        if (!_runLoaded)
            ReloadRunIfNeeded();

        ApplyLockedStatsForCurrentRun();
        ApplyLockedRelicCountersForCurrentRun();
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
        IReadOnlyList<Player> players = ResolveTargetPlayers(target, requester);
        bool savedValueChanged = false;
        foreach (Player player in players)
        {
            ApplyStat(definition, player, value);
            savedValueChanged |= UpdateSavedValueAfterSet(player.NetId, definition.Id, value);
        }

        if (savedValueChanged)
            SaveRunState();

        RefreshCombatPreviewsForStatChange(definition.Id, players);
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
        IReadOnlyList<Player> players = ResolveTargetPlayers(target, requester);
        foreach (Player player in players)
        {
            if (payload.Enabled)
                ApplyStat(definition, player, value);

            SetSavedLockState(player.NetId, definition.Id, value, payload.Enabled);
        }

        RefreshCombatPreviewsForStatChange(definition.Id, players);
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

        if (!IsKnownPlayerToggle(payload.ToggleId))
            return;

        IReadOnlyList<Player> players = ResolveTargetPlayers(target, requester);
        foreach (Player player in players)
        {
            SetSavedToggle(player.NetId, payload.ToggleId, payload.Enabled);
            if (string.Equals(payload.ToggleId, GodmodeToggleId, StringComparison.Ordinal))
                SetGodmodeDisplay(player, payload.Enabled);
            else if (payload.Enabled && string.Equals(payload.ToggleId, InfiniteEnergyToggleId, StringComparison.Ordinal))
                ApplyInfiniteEnergy(player);
        }

        if (payload.Enabled && string.Equals(payload.ToggleId, DrawTillHandLimitToggleId, StringComparison.Ordinal))
        {
            foreach (Player player in players)
                RequestDrawTillHandLimitForLocalPlayer(player);
        }

        SaveRunState();
        RaiseStateChanged();
    }

    public static bool TryGetDrawPerTurnOverride(Player player, out int value)
    {
        return TryGetVirtualStatOverride(player.NetId, DrawPerTurnStatId, out value);
    }

    public static bool TryGetHandSizeOverride(Player player, out int value)
    {
        return TryGetVirtualStatOverride(player.NetId, HandSizeStatId, out value);
    }

    public static int GetEffectiveHandSize(Player player)
    {
        return TryGetHandSizeOverride(player, out int value)
            ? Math.Max(0, value)
            : CardPile.MaxCardsInHand;
    }

    public static bool TryGetPlayerDamageMultiplier(Player player, out int value)
    {
        return TryGetVirtualStatOverride(player.NetId, PlayerDamageMultiplierStatId, out value);
    }

    public static bool TryGetEnemyDamageMultiplier(Player player, out int value)
    {
        return TryGetVirtualStatOverride(player.NetId, EnemyDamageMultiplierStatId, out value);
    }

    public static bool IsGodmodeProtected(Creature? creature)
    {
        Player? owner = creature?.Player ?? creature?.PetOwner;
        if (owner is null)
            return false;

        EnsureLoaded();
        lock (SyncRoot)
        {
            return GetPlayerStateLocked(owner.NetId, create: false, out TildeKeyPlayerState? state)
                   && state is not null
                   && state.Toggles.TryGetValue(GodmodeToggleId, out bool enabled)
                   && enabled;
        }
    }

    public static void RefreshRelicCounterLockBadge(NRelicInventoryHolder? holder)
    {
        if (holder is null || !GodotObject.IsInstanceValid(holder) || holder.Relic?.Model is not { } relic)
            return;

        TextureRect badge = EnsureRelicCounterLockBadge(holder);
        bool visible = TryGetRelicCounterMember(relic, out string counterMember)
                       && IsRelicCounterLocked(relic, counterMember);
        badge.Visible = visible;
    }

    public static bool IsScrollRelicCounterEnabledForLocalPlayer()
    {
        if (!RunManager.Instance.IsInProgress)
            return false;

        try
        {
            RunState? runState = RunManager.Instance.DebugOnlyGetState();
            Player? localPlayer = runState is null ? null : LocalContext.GetMe(runState);
            if (localPlayer is null)
                return false;

            lock (SyncRoot)
            {
                return GetPlayerStateLocked(localPlayer.NetId, create: false, out TildeKeyPlayerState? state)
                       && state is not null
                       && state.Toggles.TryGetValue(ScrollRelicCounterToggleId, out bool enabled)
                       && enabled;
            }
        }
        catch
        {
            return false;
        }
    }

    private static bool IsLocalPlayer(Player player)
    {
        try
        {
            return LocalContext.GetMe(player.RunState)?.NetId == player.NetId;
        }
        catch
        {
            return LocalContext.NetId == player.NetId;
        }
    }

    public static bool TryGetRelicCounterMember(RelicModel relic, out string member)
    {
        if (TryResolveRelicCounter(relic, null, out RelicCounterBinding? binding) && binding is not null)
        {
            member = binding.Key;
            return true;
        }

        member = string.Empty;
        return false;
    }

    public static bool IsRelicCounterLocked(RelicModel relic, string counterMember)
    {
        if (string.IsNullOrWhiteSpace(counterMember))
            return false;

        try
        {
            int index = FindRelicIndex(relic.Owner.Relics, relic);
            if (index < 0)
                return false;

            lock (SyncRoot)
            {
                return TryGetSavedRelicCounterLockLocked(
                    relic.Owner.NetId,
                    index,
                    RelicIdKey(relic),
                    counterMember,
                    out _);
            }
        }
        catch
        {
            return false;
        }
    }

    public static bool TryGetRelicCounterValue(RelicModel relic, string counterMember, out int value)
    {
        if (TryResolveRelicCounter(relic, counterMember, out RelicCounterBinding? binding) && binding is not null)
        {
            value = binding.GetValue(relic);
            return true;
        }

        value = 0;
        return false;
    }

    public static void ApplySynchronizedRelicCounterDelta(
        string payloadJson,
        int delta,
        LoadoutTargetSelection target,
        int ownedItemIndex,
        ModelId expectedModelId,
        Player requester)
    {
        if (!TryParsePayload(payloadJson, out TildeKeyMutationPayload? payload)
            || payload is null
            || string.IsNullOrWhiteSpace(payload.CounterMember))
        {
            return;
        }

        if (ResolveRelicTarget(target, requester, ownedItemIndex, expectedModelId) is not { } relic)
            return;

        string relicId = RelicIdKey(relic);
        lock (SyncRoot)
        {
            if (TryGetSavedRelicCounterLockLocked(relic.Owner.NetId, ownedItemIndex, relicId, payload.CounterMember, out _))
                return;
        }

        if (TryApplyRelicCounterDelta(relic, payload.CounterMember, delta, out _))
            RefreshRelicCounterLockBadgeForRelic(relic);
    }

    public static void ApplySynchronizedRelicCounterLock(
        string payloadJson,
        int value,
        LoadoutTargetSelection target,
        int ownedItemIndex,
        ModelId expectedModelId,
        Player requester)
    {
        if (!TryParsePayload(payloadJson, out TildeKeyMutationPayload? payload)
            || payload is null
            || string.IsNullOrWhiteSpace(payload.CounterMember))
        {
            return;
        }

        if (ResolveRelicTarget(target, requester, ownedItemIndex, expectedModelId) is not { } relic)
            return;

        string relicId = RelicIdKey(relic);
        if (payload.Enabled)
        {
            if (!TrySetRelicCounterValue(relic, payload.CounterMember, value))
                return;

            lock (SyncRoot)
            {
                GetPlayerStateLocked(relic.Owner.NetId, create: true, out TildeKeyPlayerState? state);
                state!.RelicCounterLocks[RelicCounterKey(ownedItemIndex, relicId, payload.CounterMember)] =
                    new TildeKeyRelicCounterLock
                    {
                        RelicIndex = ownedItemIndex,
                        RelicId = relicId,
                        CounterMember = payload.CounterMember,
                        Value = value
                    };
            }
        }
        else
        {
            lock (SyncRoot)
            {
                if (GetPlayerStateLocked(relic.Owner.NetId, create: false, out TildeKeyPlayerState? state)
                    && state is not null)
                {
                    state.RelicCounterLocks.Remove(RelicCounterKey(ownedItemIndex, relicId, payload.CounterMember));
                    RemovePlayerIfEmptyLocked(relic.Owner.NetId);
                }
            }
        }

        SaveRunState();
        RefreshRelicCounterLockBadgeForRelic(relic);
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

            if (IsPlayerToggleEnabled(netId, InfiniteEnergyToggleId)
                && (string.Equals(statId, "combat_energy", StringComparison.Ordinal)
                    || string.Equals(statId, "stars", StringComparison.Ordinal)))
            {
                continue;
            }

            ApplyStat(definition, player, value);
        }
    }

    private static void ApplyLockedRelicCountersForCurrentRun()
    {
        RunState? runState = GetCurrentRunStateOrNull();
        if (runState is null)
            return;

        List<(ulong NetId, TildeKeyRelicCounterLock Saved)> locks = [];
        lock (SyncRoot)
        {
            foreach ((string rawNetId, TildeKeyPlayerState state) in _run.Players)
            {
                if (!ulong.TryParse(rawNetId, out ulong netId))
                    continue;

                foreach (TildeKeyRelicCounterLock saved in state.RelicCounterLocks.Values)
                    locks.Add((netId, saved));
            }
        }

        foreach ((ulong netId, TildeKeyRelicCounterLock saved) in locks)
        {
            Player? player = runState.GetPlayer(netId);
            if (player is null || saved.RelicIndex < 0 || saved.RelicIndex >= player.Relics.Count)
                continue;

            RelicModel relic = player.Relics[saved.RelicIndex];
            if (!RelicIdMatches(relic, saved.RelicId))
                continue;

            TrySetRelicCounterValue(relic, saved.CounterMember, saved.Value);
        }
    }

    private static void ApplyInfiniteEnergy(Player player)
    {
        if (player.PlayerCombatState is null)
            return;

        int energyDesired = 999;
        if (TryGetSavedStat(player.NetId, "combat_energy", out TildeKeySavedStat? savedEn)
            && savedEn is not null
            && savedEn.Locked)
        {
            energyDesired = Math.Max(energyDesired, savedEn.Value);
        }

        if (player.PlayerCombatState.Energy != energyDesired)
            player.PlayerCombatState.Energy = energyDesired;

        int starsDesired = 999;
        if (TryGetSavedStat(player.NetId, "stars", out TildeKeySavedStat? savedSt)
            && savedSt is not null
            && savedSt.Locked)
        {
            starsDesired = Math.Max(starsDesired, savedSt.Value);
        }

        if (player.PlayerCombatState.Stars != starsDesired)
            player.PlayerCombatState.Stars = starsDesired;
    }

    internal static bool IsInfiniteEnergyEnabled(PlayerCombatState combatState)
    {
        Player? player = GetCurrentRunStateOrNull()?.Players
            .FirstOrDefault(candidate => ReferenceEquals(candidate.PlayerCombatState, combatState));
        return player is not null && IsPlayerToggleEnabled(player.NetId, InfiniteEnergyToggleId);
    }

    internal static void RestoreInfiniteEnergyAfterReset(PlayerCombatState combatState)
    {
        if (!IsInfiniteEnergyEnabled(combatState))
            return;

        Player? player = GetCurrentRunStateOrNull()?.Players
            .FirstOrDefault(candidate => ReferenceEquals(candidate.PlayerCombatState, combatState));
        if (player is not null)
            ApplyInfiniteEnergy(player);
    }

    public static void RequestDrawTillHandLimitForLocalPlayer(Player? player)
    {
        if (player is null || !IsLocalPlayer(player) || !IsPlayerToggleEnabled(player.NetId, DrawTillHandLimitToggleId))
            return;

        TryRequestDrawTillHandLimit(player);
    }

    internal static async Task DrawTillHandLimitAsync(PlayerChoiceContext choiceContext, Player player)
    {
        if (!IsPlayerToggleEnabled(player.NetId, DrawTillHandLimitToggleId)
            || player.PlayerCombatState?.Phase != PlayerTurnPhase.Play)
        {
            return;
        }

        CardPile hand = PileType.Hand.GetPile(player);
        CardPile drawPile = PileType.Draw.GetPile(player);
        CardPile discardPile = PileType.Discard.GetPile(player);
        int handSpace = Math.Max(0, GetEffectiveHandSize(player) - hand.Cards.Count);
        if (handSpace <= 0 || drawPile.Cards.Count + discardPile.Cards.Count <= 0)
            return;

        await CardPileCmd.Draw(choiceContext, handSpace, player);
    }

    private static void TryRequestDrawTillHandLimit(Player player)
    {
        PlayerCombatState? combatState = player.PlayerCombatState;
        if (combatState is null || combatState.Phase != PlayerTurnPhase.Play)
            return;

        CardPile hand = PileType.Hand.GetPile(player);
        CardPile drawPile = PileType.Draw.GetPile(player);
        CardPile discardPile = PileType.Discard.GetPile(player);
        CardPile playPile = PileType.Play.GetPile(player);
        int handSpace = Math.Max(0, GetEffectiveHandSize(player) - hand.Cards.Count);
        if (handSpace <= 0 || drawPile.Cards.Count + discardPile.Cards.Count + playPile.Cards.Count <= 0)
            return;

        try
        {
            RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(
                new ConsoleCmdGameAction(player, $"draw {handSpace}", CombatManager.Instance.IsInProgress));
        }
        catch (Exception exception)
        {
            GD.PushWarning($"TildeKey: failed requesting draw to hand limit for player {player.NetId}. {exception.Message}");
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

    private static RunState? GetCurrentRunStateOrNull()
    {
        try
        {
            return RunManager.Instance.IsInProgress ? RunManager.Instance.DebugOnlyGetState() : null;
        }
        catch
        {
            return null;
        }
    }

    private static int? GetVirtualStatValue(Player player, string statId, int defaultValue)
    {
        return TryGetVirtualStatOverride(player.NetId, statId, out int value) ? value : defaultValue;
    }

    private static void SetVirtualStatValue(Player player, string statId, int value)
    {
        lock (SyncRoot)
        {
            string playerKey = NetIdKey(player.NetId);
            if (!VirtualStats.TryGetValue(playerKey, out Dictionary<string, int>? stats))
            {
                stats = new Dictionary<string, int>(StringComparer.Ordinal);
                VirtualStats[playerKey] = stats;
            }

            stats[statId] = value;
        }
    }

    private static bool TryGetVirtualStatOverride(ulong netId, string statId, out int value)
    {
        lock (SyncRoot)
        {
            if (VirtualStats.TryGetValue(NetIdKey(netId), out Dictionary<string, int>? stats)
                && stats.TryGetValue(statId, out value))
            {
                return true;
            }
        }

        if (TryGetSavedStat(netId, statId, out TildeKeySavedStat? saved)
            && saved is not null)
        {
            value = saved.Value;
            return true;
        }

        value = 0;
        return false;
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

    private static RelicModel? ResolveRelicTarget(
        LoadoutTargetSelection target,
        Player requester,
        int ownedItemIndex,
        ModelId expectedModelId)
    {
        Player? player = ResolveTargetPlayers(target, requester).FirstOrDefault();
        if (player is null || ownedItemIndex < 0 || ownedItemIndex >= player.Relics.Count)
            return null;

        RelicModel relic = player.Relics[ownedItemIndex];
        return ModelIdMatches(relic, expectedModelId) ? relic : null;
    }

    private static int FindRelicIndex(IReadOnlyList<RelicModel> relics, RelicModel relic)
    {
        for (int i = 0; i < relics.Count; i++)
        {
            if (ReferenceEquals(relics[i], relic))
                return i;
        }

        return -1;
    }

    private static bool TryApplyRelicCounterDelta(RelicModel relic, string counterMember, int delta, out int newValue)
    {
        if (!TryResolveRelicCounter(relic, counterMember, out RelicCounterBinding? binding) || binding is null)
        {
            newValue = 0;
            return false;
        }

        newValue = binding.GetValue(relic) + delta;
        binding.SetValue(relic, newValue);
        NotifyRelicDisplayAmountChanged(relic);
        return true;
    }

    private static bool TrySetRelicCounterValue(RelicModel relic, string counterMember, int value)
    {
        if (!TryResolveRelicCounter(relic, counterMember, out RelicCounterBinding? binding) || binding is null)
            return false;

        if (binding.GetValue(relic) == value)
            return true;

        binding.SetValue(relic, value);
        NotifyRelicDisplayAmountChanged(relic);
        return true;
    }

    private static bool TryResolveRelicCounter(RelicModel relic, string? requestedMember, out RelicCounterBinding? binding)
    {
        binding = null;
        List<RelicCounterBinding> candidates = BuildRelicCounterCandidates(relic);
        if (!string.IsNullOrWhiteSpace(requestedMember))
        {
            binding = candidates.FirstOrDefault(candidate => string.Equals(candidate.Key, requestedMember, StringComparison.Ordinal));
            return binding is not null;
        }

        if (candidates.Count == 0)
            return false;

        int displayAmount = SafeRelicDisplayAmount(relic);
        binding = candidates
            .OrderByDescending(candidate => candidate.IsSaved)
            .ThenByDescending(candidate => candidate.GetValue(relic) == displayAmount)
            .ThenByDescending(candidate => LooksLikeCounterName(candidate.Name))
            .ThenBy(candidate => candidate.Name, StringComparer.Ordinal)
            .First();
        return true;
    }

    private static List<RelicCounterBinding> BuildRelicCounterCandidates(RelicModel relic)
    {
        Type type = relic.GetType();
        List<RelicCounterBinding> candidates = [];
        BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        foreach (PropertyInfo property in type.GetProperties(flags))
        {
            if (property.PropertyType != typeof(int)
                || property.GetIndexParameters().Length != 0
                || property.GetMethod is null
                || property.SetMethod is null
                || property.DeclaringType == typeof(RelicModel)
                || string.Equals(property.Name, nameof(RelicModel.DisplayAmount), StringComparison.Ordinal))
            {
                continue;
            }

            candidates.Add(RelicCounterBinding.ForProperty(property, HasSavedPropertyAttribute(property)));
        }

        foreach (FieldInfo field in type.GetFields(flags))
        {
            if (field.FieldType != typeof(int)
                || field.IsStatic
                || field.IsInitOnly
                || field.DeclaringType == typeof(RelicModel)
                || (field.Name.StartsWith("<", StringComparison.Ordinal) && field.Name.Contains(">k__BackingField", StringComparison.Ordinal)))
            {
                continue;
            }

            candidates.Add(RelicCounterBinding.ForField(field, HasSavedPropertyAttribute(field) || IsBackingSavedProperty(type, field)));
        }

        return candidates;
    }

    private static bool IsBackingSavedProperty(Type type, FieldInfo field)
    {
        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (!HasSavedPropertyAttribute(property))
                continue;

            if (string.Equals(field.Name, $"<{property.Name}>k__BackingField", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static bool HasSavedPropertyAttribute(MemberInfo member)
    {
        return member.GetCustomAttributes(inherit: true)
            .Any(attribute => string.Equals(attribute.GetType().Name, "SavedPropertyAttribute", StringComparison.Ordinal));
    }

    private static bool LooksLikeCounterName(string name)
    {
        string lower = name.ToLowerInvariant();
        return lower.Contains("count", StringComparison.Ordinal)
               || lower.Contains("counter", StringComparison.Ordinal)
               || lower.Contains("turn", StringComparison.Ordinal)
               || lower.Contains("played", StringComparison.Ordinal)
               || lower.Contains("used", StringComparison.Ordinal)
               || lower.Contains("seen", StringComparison.Ordinal)
               || lower.Contains("left", StringComparison.Ordinal)
               || lower.Contains("combat", StringComparison.Ordinal)
               || lower.Contains("card", StringComparison.Ordinal)
               || lower.Contains("orb", StringComparison.Ordinal)
               || lower.Contains("attack", StringComparison.Ordinal)
               || lower.Contains("elite", StringComparison.Ordinal);
    }

    private static int SafeRelicDisplayAmount(RelicModel relic)
    {
        try
        {
            return relic.DisplayAmount;
        }
        catch
        {
            return 0;
        }
    }

    private static void NotifyRelicDisplayAmountChanged(RelicModel relic)
    {
        try
        {
            RelicDisplayAmountChangedMethod?.Invoke(relic, null);
        }
        catch (Exception exception)
        {
            GD.PushWarning($"TildeKey: failed refreshing relic counter for '{relic.Id}'. {exception.Message}");
        }
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

    private static void RefreshCombatPreviewsForStatChange(string statId, IReadOnlyList<Player> players)
    {
        if (players.Count == 0)
            return;

        if (string.Equals(statId, PlayerDamageMultiplierStatId, StringComparison.Ordinal))
        {
            foreach (Player player in players)
                RefreshPlayerCombatPreview(player);
        }
        else if (string.Equals(statId, EnemyDamageMultiplierStatId, StringComparison.Ordinal))
        {
            RefreshEnemyIntentDisplays(players);
        }
        else if (string.Equals(statId, HandSizeStatId, StringComparison.Ordinal))
        {
            NPlayerHand.Instance?.ForceRefreshCardIndices();
            foreach (Player player in players)
            {
                if (IsPlayerToggleEnabled(player.NetId, DrawTillHandLimitToggleId))
                    RequestDrawTillHandLimitForLocalPlayer(player);
            }
        }
        else if (string.Equals(statId, "max_energy", StringComparison.Ordinal)
                 || string.Equals(statId, "combat_energy", StringComparison.Ordinal)
                 || string.Equals(statId, "stars", StringComparison.Ordinal))
        {
            RefreshEnergyDisplays(players);
        }
    }

    private static void RefreshEnergyDisplays(IReadOnlyList<Player> players)
    {
        try
        {
            if (NCombatRoom.Instance?.Ui?.EnergyCounterContainer is { } energyContainer)
            {
                foreach (NEnergyCounter counter in energyContainer.GetChildren().OfType<NEnergyCounter>())
                    EnergyCounterRefreshLabelMethod?.Invoke(counter, null);
            }

            if (NRun.Instance?.GlobalUi?.MultiplayerPlayerContainer is { } container)
            {
                HashSet<ulong> affected = players.Select(player => player.NetId).ToHashSet();
                foreach (NMultiplayerPlayerState state in container.GetChildren().OfType<NMultiplayerPlayerState>())
                {
                    if (affected.Contains(state.Player.NetId))
                        MultiplayerPlayerStateRefreshCombatValuesMethod?.Invoke(state, null);
                }
            }
        }
        catch (Exception exception)
        {
            GD.PushWarning($"TildeKey: failed refreshing energy display. {exception.Message}");
        }
    }

    private static void RefreshPlayerCombatPreview(Player player)
    {
        try
        {
            PlayerCombatState? combatState = player.PlayerCombatState;
            if (combatState is null)
                return;

            combatState.RecalculateCardValues();
            foreach (CardModel card in combatState.AllCards)
                RefreshLiveCardVisuals(card);
        }
        catch (Exception exception)
        {
            GD.PushWarning($"TildeKey: failed refreshing card damage preview for player {player.NetId}. {exception.Message}");
        }
    }

    private static void RefreshLiveCardVisuals(CardModel card)
    {
        try
        {
            NCard? cardNode = NCard.FindOnTable(card);
            cardNode ??= NCard.FindOnTable(card, card.Pile?.Type ?? PileType.None);
            if (cardNode is null)
                return;

            PileType pileType = card.Pile?.Type ?? PileType.None;
            cardNode.Model = null;
            cardNode.Model = card;
            cardNode.UpdateVisuals(pileType, CardPreviewMode.Normal);
        }
        catch (Exception exception)
        {
            GD.PushWarning($"TildeKey: failed refreshing live card '{card.Id}'. {exception.Message}");
        }
    }

    private static void RefreshEnemyIntentDisplays(IReadOnlyList<Player> players)
    {
        try
        {
            ICombatState? combatState = players
                .Select(player => player.Creature.CombatState)
                .FirstOrDefault(state => state is not null);

            if (combatState is null)
                return;

            Creature[] targets = combatState.Players.Select(player => player.Creature).ToArray();
            foreach (Creature enemy in combatState.Enemies)
            {
                NCreature? enemyNode = NCombatRoom.Instance?.GetCreatureNode(enemy);
                if (enemyNode is not null)
                    TaskHelper.RunSafely(enemyNode.UpdateIntent(targets));
            }
        }
        catch (Exception exception)
        {
            GD.PushWarning($"TildeKey: failed refreshing enemy intent preview. {exception.Message}");
        }
    }

    private static void SetGodmodeDisplay(Player player, bool enabled)
    {
        Creature creature = player.Creature;
        HpDisplay desired = enabled ? HpDisplay.InfiniteWithoutNumbers : HpDisplay.Normal;
        if (creature.HpDisplay != desired)
            creature.HpDisplay = desired;

        RefreshCreatureHealthBar(creature);
    }

    private static void RefreshCreatureHealthBar(Creature creature)
    {
        try
        {
            NCreature? creatureNode = NCombatRoom.Instance?.GetCreatureNode(creature);
            if (creatureNode is not null)
            {
                object? stateDisplay = CreatureStateDisplayField?.GetValue(creatureNode);
                if (stateDisplay is not null)
                    CreatureStateDisplayRefreshValuesMethod?.Invoke(stateDisplay, null);
            }

            if (creature.Player is not null
                && NRun.Instance?.GlobalUi?.MultiplayerPlayerContainer is { } container)
            {
                NMultiplayerPlayerState? multiplayerState = container.GetChildren()
                    .OfType<NMultiplayerPlayerState>()
                    .FirstOrDefault(state => state.Player.NetId == creature.Player.NetId);
                if (multiplayerState is not null)
                    MultiplayerPlayerStateRefreshValuesMethod?.Invoke(multiplayerState, null);
            }
        }
        catch (Exception exception)
        {
            GD.PushWarning($"TildeKey: failed refreshing HP display for '{creature.Name}'. {exception.Message}");
        }
    }

    private static bool IsPlayerToggleEnabled(ulong netId, string toggleId)
    {
        lock (SyncRoot)
        {
            return GetPlayerStateLocked(netId, create: false, out TildeKeyPlayerState? state)
                   && state is not null
                   && state.Toggles.TryGetValue(toggleId, out bool enabled)
                   && enabled;
        }
    }

    private static TextureRect EnsureRelicCounterLockBadge(NRelicInventoryHolder holder)
    {
        TextureRect? badge = holder.GetNodeOrNull<TextureRect>(RelicCounterLockBadgeName);
        if (badge is not null)
            return badge;

        badge = new TextureRect
        {
            Name = RelicCounterLockBadgeName,
            Texture = PreloadManager.Cache.GetTexture2D(NRelicCollectionEntry.lockedIconPath),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspect,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            FocusMode = Control.FocusModeEnum.None,
            Size = Vector2.One * 18f,
            CustomMinimumSize = Vector2.One * 18f,
            Visible = false
        };
        badge.AnchorLeft = 1f;
        badge.AnchorRight = 1f;
        badge.AnchorTop = 0f;
        badge.AnchorBottom = 0f;
        badge.OffsetLeft = -20f;
        badge.OffsetRight = -2f;
        badge.OffsetTop = 2f;
        badge.OffsetBottom = 20f;
        badge.PivotOffset = badge.Size * 0.5f;

        holder.AddChildSafely(badge);
        holder.MoveChildSafely(badge, holder.GetChildCount() - 1);
        return badge;
    }

    private static void RefreshRelicCounterLockBadgeForRelic(RelicModel relic)
    {
        try
        {
            foreach (NRelicInventoryHolder holder in NRun.Instance?.GlobalUi?.RelicInventory?.RelicNodes ?? [])
            {
                if (ReferenceEquals(holder.Relic?.Model, relic))
                    RefreshRelicCounterLockBadge(holder);
            }
        }
        catch (Exception exception)
        {
            GD.PushWarning($"TildeKey: failed refreshing relic lock badge for '{relic.Id}'. {exception.Message}");
        }
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

    private static bool UpdateSavedValueAfterSet(ulong netId, string statId, int value)
    {
        bool changed = false;
        lock (SyncRoot)
        {
            bool persistUnlocked = IsVirtualStat(statId);
            if (!GetPlayerStateLocked(netId, create: persistUnlocked, out TildeKeyPlayerState? state)
                || state is null)
            {
                return false;
            }

            if (state.Stats.TryGetValue(statId, out TildeKeySavedStat? saved) && saved is not null)
            {
                if ((saved.Locked || persistUnlocked) && saved.Value != value)
                {
                    saved.Value = value;
                    changed = true;
                }
            }
            else if (persistUnlocked)
            {
                state.Stats[statId] = new TildeKeySavedStat { Value = value, Locked = false };
                changed = true;
            }
        }

        return changed;
    }

    private static void SetSavedLockState(ulong netId, string statId, int value, bool locked)
    {
        lock (SyncRoot)
        {
            if (locked || IsVirtualStat(statId))
            {
                GetPlayerStateLocked(netId, create: true, out TildeKeyPlayerState? state);
                state!.Stats[statId] = new TildeKeySavedStat { Value = value, Locked = locked };
                return;
            }

            if (GetPlayerStateLocked(netId, create: false, out TildeKeyPlayerState? existing)
                && existing is not null)
            {
                existing.Stats.Remove(statId);
                RemovePlayerIfEmptyLocked(netId);
            }
        }
    }

    private static bool IsVirtualStat(string statId)
    {
        return string.Equals(statId, DrawPerTurnStatId, StringComparison.Ordinal)
               || string.Equals(statId, HandSizeStatId, StringComparison.Ordinal)
               || string.Equals(statId, PlayerDamageMultiplierStatId, StringComparison.Ordinal)
               || string.Equals(statId, EnemyDamageMultiplierStatId, StringComparison.Ordinal);
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
            && state.Toggles.Count == 0
            && state.RelicCounterLocks.Count == 0)
        {
            _run.Players.Remove(key);
        }
    }

    private static string NetIdKey(ulong netId)
    {
        return netId.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string RelicIdKey(RelicModel relic)
    {
        return relic.Id.ToString();
    }

    private static string RelicCounterKey(int relicIndex, string relicId, string counterMember)
    {
        return $"{relicIndex}|{relicId}|{counterMember}";
    }

    private static bool TryGetSavedRelicCounterLockLocked(
        ulong netId,
        int relicIndex,
        string relicId,
        string counterMember,
        out TildeKeyRelicCounterLock? saved)
    {
        saved = null;
        return GetPlayerStateLocked(netId, create: false, out TildeKeyPlayerState? state)
               && state is not null
               && state.RelicCounterLocks.TryGetValue(RelicCounterKey(relicIndex, relicId, counterMember), out saved)
               && saved is not null;
    }

    private static bool RelicIdMatches(RelicModel relic, string savedRelicId)
    {
        return string.Equals(relic.Id.ToString(), savedRelicId, StringComparison.Ordinal)
               || string.Equals(relic.Id.Entry, savedRelicId, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ModelIdMatches(AbstractModel model, ModelId id)
    {
        return id == ModelId.none
               || model.Id == id
               || string.Equals(model.Id.ToString(), id.ToString(), StringComparison.Ordinal)
               || string.Equals(model.Id.Entry, id.Entry, StringComparison.OrdinalIgnoreCase);
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
        lock (SyncRoot)
        {
            VirtualStats.Clear();
        }

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
            VirtualStats.Clear();
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
        foreach (Player player in combatState.Players)
        {
            if (IsPlayerToggleEnabled(player.NetId, InfiniteEnergyToggleId))
                ApplyInfiniteEnergy(player);
        }
    }

    private static void OnTurnStarted(CombatState combatState)
    {
        if (combatState?.CurrentSide != CombatSide.Player)
            return;

        foreach (Player player in combatState.Players)
            RequestDrawTillHandLimitForLocalPlayer(player);
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
            SetGodmodeDisplay(player, enabled: true);
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
                VirtualStats.Clear();
                return;
            }

            string path = GetRunPath(currentRunStartTime.Value);
            SaveUtility.LoadResult<RunSaveData> loaded =
                SaveUtility.LoadProfileJson(path, new RunSaveData { RunStartTime = currentRunStartTime.Value });
            int loadedSchemaVersion = loaded.Value.SchemaVersion;
            _run = NormalizeRun(loaded.Value, currentRunStartTime.Value);
            HydrateVirtualStatsLocked();

            if (loaded.Loaded && loadedSchemaVersion != CurrentSchemaVersion)
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

    private static void HydrateVirtualStatsLocked()
    {
        VirtualStats.Clear();
        foreach ((string playerKey, TildeKeyPlayerState state) in _run.Players)
        {
            Dictionary<string, int>? virtualStats = null;
            foreach ((string statId, TildeKeySavedStat saved) in state.Stats)
            {
                if (!IsVirtualStat(statId))
                    continue;

                virtualStats ??= new Dictionary<string, int>(StringComparer.Ordinal);
                virtualStats[statId] = saved.Value;
            }

            if (virtualStats is not null)
                VirtualStats[playerKey] = virtualStats;
        }
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
                                   && (pair.Value.Locked || IsVirtualStat(pair.Key)))
                    .ToDictionary(
                        pair => pair.Key,
                        pair => new TildeKeySavedStat
                        {
                            Value = pair.Value.Value,
                            Locked = pair.Value.Locked
                        },
                        StringComparer.Ordinal) ?? new Dictionary<string, TildeKeySavedStat>(StringComparer.Ordinal),
                Toggles = NormalizeToggles(value.Toggles),
                RelicCounterLocks = NormalizeRelicCounterLocks(value.RelicCounterLocks)
            };

            if (state.Stats.Count > 0 || state.Toggles.Count > 0 || state.RelicCounterLocks.Count > 0)
                normalized[key] = state;
        }

        return normalized;
    }

    private static Dictionary<string, TildeKeyRelicCounterLock> NormalizeRelicCounterLocks(Dictionary<string, TildeKeyRelicCounterLock>? locks)
    {
        Dictionary<string, TildeKeyRelicCounterLock> normalized = new(StringComparer.Ordinal);
        if (locks is null)
            return normalized;

        foreach (TildeKeyRelicCounterLock? value in locks.Values)
        {
            if (value is null
                || value.RelicIndex < 0
                || string.IsNullOrWhiteSpace(value.RelicId)
                || string.IsNullOrWhiteSpace(value.CounterMember))
            {
                continue;
            }

            TildeKeyRelicCounterLock saved = new()
            {
                RelicIndex = value.RelicIndex,
                RelicId = value.RelicId,
                CounterMember = value.CounterMember,
                Value = value.Value
            };
            normalized[RelicCounterKey(saved.RelicIndex, saved.RelicId, saved.CounterMember)] = saved;
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
               || string.Equals(key, GoToAnyRoomToggleId, StringComparison.Ordinal)
               || string.Equals(key, InfiniteEnergyToggleId, StringComparison.Ordinal)
               || string.Equals(key, DrawTillHandLimitToggleId, StringComparison.Ordinal)
               || string.Equals(key, ScrollRelicCounterToggleId, StringComparison.Ordinal);
    }

    private static bool IsKnownPlayerToggle(string key)
    {
        return string.Equals(key, GodmodeToggleId, StringComparison.Ordinal)
               || string.Equals(key, InfiniteEnergyToggleId, StringComparison.Ordinal)
               || string.Equals(key, DrawTillHandLimitToggleId, StringComparison.Ordinal)
               || string.Equals(key, ScrollRelicCounterToggleId, StringComparison.Ordinal);
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

    [JsonPropertyName("relicCounterLocks")]
    public Dictionary<string, TildeKeyRelicCounterLock> RelicCounterLocks { get; set; } = new(StringComparer.Ordinal);
}

public sealed class TildeKeySavedStat
{
    [JsonPropertyName("value")]
    public int Value { get; set; }

    [JsonPropertyName("locked")]
    public bool Locked { get; set; }
}

public sealed class TildeKeyRelicCounterLock
{
    [JsonPropertyName("relicIndex")]
    public int RelicIndex { get; set; }

    [JsonPropertyName("relicId")]
    public string RelicId { get; set; } = string.Empty;

    [JsonPropertyName("counterMember")]
    public string CounterMember { get; set; } = string.Empty;

    [JsonPropertyName("value")]
    public int Value { get; set; }
}

internal sealed class RelicCounterBinding
{
    private readonly Func<RelicModel, int> _getValue;
    private readonly Action<RelicModel, int> _setValue;

    private RelicCounterBinding(string key, string name, bool isSaved, Func<RelicModel, int> getValue, Action<RelicModel, int> setValue)
    {
        Key = key;
        Name = name;
        IsSaved = isSaved;
        _getValue = getValue;
        _setValue = setValue;
    }

    public string Key { get; }
    public string Name { get; }
    public bool IsSaved { get; }

    public int GetValue(RelicModel relic)
    {
        return _getValue(relic);
    }

    public void SetValue(RelicModel relic, int value)
    {
        _setValue(relic, value);
    }

    public static RelicCounterBinding ForProperty(PropertyInfo property, bool isSaved)
    {
        return new RelicCounterBinding(
            $"P:{property.Name}",
            property.Name,
            isSaved,
            relic => (int)(property.GetValue(relic) ?? 0),
            (relic, value) => property.SetValue(relic, value));
    }

    public static RelicCounterBinding ForField(FieldInfo field, bool isSaved)
    {
        return new RelicCounterBinding(
            $"F:{field.Name}",
            field.Name,
            isSaved,
            relic => (int)(field.GetValue(relic) ?? 0),
            (relic, value) => field.SetValue(relic, value));
    }
}
