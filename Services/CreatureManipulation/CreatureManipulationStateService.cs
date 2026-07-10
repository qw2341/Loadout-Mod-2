#nullable enable

using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;

namespace Loadout.Services.CreatureManipulation;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using Loadout.Services.Actions;
using Loadout.Services.Networking;
using MegaCrit.Sts2.Core.Animation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Runs;

public enum CreatureManipulationOperation
{
    SetPosition,
    AdjustPower,
    ClearPowers,
    Morph,
    Kill,
    SetStat,
    SetStatLock,
    Duplicate
}

public sealed class CreatureManipulationMutation
{
    [JsonPropertyName("operation")]
    public CreatureManipulationOperation Operation { get; set; }

    [JsonPropertyName("combatId")]
    public ulong CombatId { get; set; }

    [JsonPropertyName("x")]
    public float X { get; set; }

    [JsonPropertyName("y")]
    public float Y { get; set; }

    [JsonPropertyName("statId")]
    public string? StatId { get; set; }

    [JsonPropertyName("value")]
    public int Value { get; set; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("duplicate")]
    public CreatureDuplicateSnapshot? Duplicate { get; set; }
}

public sealed class CreatureDuplicateSnapshot
{
    [JsonPropertyName("currentHp")]
    public int CurrentHp { get; set; }

    [JsonPropertyName("maxHp")]
    public int MaxHp { get; set; }

    [JsonPropertyName("block")]
    public int Block { get; set; }

    [JsonPropertyName("side")]
    public int Side { get; set; }

    [JsonPropertyName("x")]
    public float X { get; set; }

    [JsonPropertyName("y")]
    public float Y { get; set; }

    [JsonPropertyName("powers")]
    public List<CreaturePowerSnapshot> Powers { get; set; } = [];
}

public sealed class CreaturePowerSnapshot
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("amount")]
    public int Amount { get; set; }
}

public sealed class CreatureManipulationSnapshot
{
    [JsonPropertyName("visualStates")]
    public List<CreatureVisualSnapshot> VisualStates { get; set; } = [];

    [JsonPropertyName("locks")]
    public List<CreatureStatLockSnapshot> Locks { get; set; } = [];
}

public sealed class CreatureVisualSnapshot
{
    [JsonPropertyName("combatId")]
    public ulong CombatId { get; set; }

    [JsonPropertyName("hasPosition")]
    public bool HasPosition { get; set; }

    [JsonPropertyName("x")]
    public float X { get; set; }

    [JsonPropertyName("y")]
    public float Y { get; set; }

    [JsonPropertyName("morphModelId")]
    public string MorphModelId { get; set; } = string.Empty;
}

public sealed class CreatureStatLockSnapshot
{
    [JsonPropertyName("combatId")]
    public ulong CombatId { get; set; }

    [JsonPropertyName("statId")]
    public string StatId { get; set; } = string.Empty;

    [JsonPropertyName("value")]
    public int Value { get; set; }
}

internal sealed class CreatureVisualState
{
    public bool HasPosition { get; set; }
    public Vector2 Position { get; set; }
    public ModelId MorphModelId { get; set; } = ModelId.none;
}

public static class CreatureManipulationStateService
{
    public const string CurrentHpStatId = "current_hp";
    public const string MaxHpStatId = "max_hp";
    public const string BlockStatId = "block";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly object StateLock = new();
    private static readonly SemaphoreSlim MutationGate = new(1, 1);
    private static readonly Dictionary<ulong, CreatureVisualState> VisualStates = [];
    private static readonly Dictionary<(ulong CombatId, string StatId), int> StatLocks = [];

    private static readonly FieldInfo? CurrentHpField = AccessTools.Field(typeof(Creature), "_currentHp");
    private static readonly FieldInfo? MaxHpField = AccessTools.Field(typeof(Creature), "_maxHp");
    private static readonly FieldInfo? BlockField = AccessTools.Field(typeof(Creature), "_block");
    private static readonly FieldInfo? CurrentHpChangedField = AccessTools.Field(typeof(Creature), "CurrentHpChanged");
    private static readonly FieldInfo? MaxHpChangedField = AccessTools.Field(typeof(Creature), "MaxHpChanged");
    private static readonly FieldInfo? BlockChangedField = AccessTools.Field(typeof(Creature), "BlockChanged");
    private static readonly FieldInfo? VisualsField = AccessTools.Field(typeof(NCreature), "<Visuals>k__BackingField");
    private static readonly FieldInfo? AnimatorField = AccessTools.Field(typeof(NCreature), "_spineAnimator");
    private static readonly FieldInfo? CreatureStateDisplayField = AccessTools.Field(typeof(NCreature), "_stateDisplay");
    private static readonly MethodInfo? ConnectAnimatorSignalsMethod = AccessTools.Method(typeof(NCreature), "ConnectSpineAnimatorSignals");
    private static readonly MethodInfo? UpdateBoundsMethod = AccessTools.Method(typeof(NCreature), "UpdateBounds", [typeof(Node)]);
    private static readonly MethodInfo? UpdatePhobiaModeMethod = AccessTools.Method(typeof(NCreature), "UpdatePhobiaMode");
    private static readonly MethodInfo? UpdateNavigationMethod = AccessTools.Method(typeof(NCreature), "UpdateNavigation");
    private static readonly MethodInfo? SetOrbManagerPositionMethod = AccessTools.Method(typeof(NCreature), "SetOrbManagerPosition");
    private static readonly MethodInfo? ImmediatelySetIdleMethod = AccessTools.Method(typeof(NCreature), "ImmediatelySetIdle");
    private static readonly MethodInfo? CreatureStateDisplayRefreshValuesMethod = AccessTools.Method(typeof(NCreatureStateDisplay), "RefreshValues");
    private static readonly MethodInfo? MultiplayerPlayerStateRefreshValuesMethod = AccessTools.Method(typeof(NMultiplayerPlayerState), "RefreshValues");

    private static INetGameService? _runNetService;
    private static RunLobby? _runLobby;
    private static CombatState? _activeCombatState;
    private static CreatureManipulationSnapshot? _pendingSnapshot;

    public static event Action<ulong>? CreatureChanged;

    public static void OnRunLaunched()
    {
        ClearCombatState();
        try
        {
            INetGameService? netService = RunManager.Instance.NetService;
            if (netService is null)
                return;

            RegisterRunNetService(netService);
        }
        catch (Exception exception)
        {
            GD.PushWarning($"CreatureManipulation: failed to initialize run synchronization. {exception.Message}");
        }
    }

    public static void OnRunCleaningUp()
    {
        UnbindRunLobby();
        UnregisterRunNetService();
        ClearCombatState();
    }

    public static void Process(float delta)
    {
        _ = delta;
        CombatState? combatState = GetCombatState();
        EnsureCombatState(combatState);
        if (combatState is null)
            return;

        ApplyPendingSnapshot(combatState);

        List<((ulong CombatId, string StatId) Key, int Value)> locks;
        lock (StateLock)
            locks = StatLocks.Select(pair => (pair.Key, pair.Value)).ToList();

        foreach (((ulong combatId, string statId), int value) in locks)
        {
            Creature? creature = ResolveCreature(combatState, combatId);
            if (creature is null)
                continue;

            if (GetStat(creature, statId) != value)
                SetStatDirect(creature, statId, value);
        }
    }

    public static bool RequestSetPosition(Creature creature, Vector2 position)
    {
        return Request(creature, CreatureManipulationOperation.SetPosition, ModelId.none, mutation =>
        {
            mutation.X = position.X;
            mutation.Y = position.Y;
        });
    }

    public static bool RequestAdjustPower(Creature creature, PowerModel power, int delta)
    {
        return delta != 0 && Request(creature, CreatureManipulationOperation.AdjustPower, power.Id, mutation => mutation.Value = delta);
    }

    public static bool RequestClearPowers(Creature creature, PowerType type)
    {
        return type is PowerType.Buff or PowerType.Debuff
               && Request(creature, CreatureManipulationOperation.ClearPowers, ModelId.none, mutation => mutation.Value = (int)type);
    }

    public static bool RequestMorph(Creature creature, ModelId modelId)
    {
        return Request(creature, CreatureManipulationOperation.Morph, modelId);
    }

    public static bool RequestRestoreOriginalAppearance(Creature creature)
    {
        return Request(creature, CreatureManipulationOperation.Morph, ModelId.none);
    }

    public static bool RequestKill(Creature creature)
    {
        return Request(creature, CreatureManipulationOperation.Kill, ModelId.none);
    }

    public static bool RequestSetStat(Creature creature, string statId, int value)
    {
        return IsSupportedStat(statId)
               && Request(creature, CreatureManipulationOperation.SetStat, ModelId.none, mutation =>
               {
                   mutation.StatId = statId;
                   mutation.Value = value;
               });
    }

    public static bool RequestSetStatLock(Creature creature, string statId, int value, bool locked)
    {
        return IsSupportedStat(statId)
               && Request(creature, CreatureManipulationOperation.SetStatLock, ModelId.none, mutation =>
               {
                   mutation.StatId = statId;
                   mutation.Value = value;
                   mutation.Enabled = locked;
               });
    }

    public static bool RequestDuplicate(Creature creature)
    {
        return creature.IsMonster && Request(creature, CreatureManipulationOperation.Duplicate, creature.ModelId);
    }

    public static int GetPowerAmount(Creature creature, ModelId powerId)
    {
        return creature.Powers
            .Where(power => LoadoutModelIdSafety.Matches(power, powerId))
            .Sum(power => power.Amount);
    }

    public static int GetStat(Creature creature, string statId)
    {
        return statId switch
        {
            CurrentHpStatId => creature.CurrentHp,
            MaxHpStatId => creature.MaxHp,
            BlockStatId => creature.Block,
            _ => 0
        };
    }

    public static bool IsStatLocked(Creature creature, string statId)
    {
        if (!TryGetCombatId(creature, out ulong combatId))
            return false;

        lock (StateLock)
            return StatLocks.ContainsKey((combatId, statId));
    }

    public static ModelId GetMorphModelId(Creature creature)
    {
        if (!TryGetCombatId(creature, out ulong combatId))
            return ModelId.none;

        lock (StateLock)
            return VisualStates.TryGetValue(combatId, out CreatureVisualState? state) ? state.MorphModelId : ModelId.none;
    }

    public static bool TryPrepareHostPayload(ref LoadoutImmediateMutationPayload payload)
    {
        if (!TryDeserialize(payload.TildePayloadJson, out CreatureManipulationMutation? mutation))
            return false;

        CombatState? combatState = GetCombatState();
        EnsureCombatState(combatState);
        Creature? target = combatState is null ? null : ResolveCreature(combatState, mutation.CombatId);
        if (target is null)
            return false;

        switch (mutation.Operation)
        {
            case CreatureManipulationOperation.SetPosition:
                if (!float.IsFinite(mutation.X) || !float.IsFinite(mutation.Y))
                    return false;
                mutation.X = Mathf.Clamp(mutation.X, -300f, 1300f);
                mutation.Y = Mathf.Clamp(mutation.Y, -200f, 800f);
                break;
            case CreatureManipulationOperation.AdjustPower:
                if (mutation.Value == 0 || ResolvePower(payload.ModelId) is null || !target.CanReceivePowers)
                    return false;
                mutation.Value = Math.Clamp(mutation.Value, -999, 999);
                if (mutation.Value < 0)
                {
                    int currentAmount = GetPowerAmount(target, payload.ModelId);
                    if (currentAmount <= 0)
                        return false;
                    mutation.Value = Math.Max(mutation.Value, -currentAmount);
                }
                break;
            case CreatureManipulationOperation.ClearPowers:
                if ((PowerType)mutation.Value is not (PowerType.Buff or PowerType.Debuff))
                    return false;
                break;
            case CreatureManipulationOperation.Morph:
                if (!LoadoutModelIdSafety.IsNoneOrEmpty(payload.ModelId) && ResolveMorphModel(payload.ModelId) is null)
                    return false;
                break;
            case CreatureManipulationOperation.Kill:
                if (target.IsDead)
                    return false;
                break;
            case CreatureManipulationOperation.SetStat:
                if (!IsSupportedStat(mutation.StatId))
                    return false;
                break;
            case CreatureManipulationOperation.SetStatLock:
                if (!IsSupportedStat(mutation.StatId))
                    return false;
                break;
            case CreatureManipulationOperation.Duplicate:
                if (!target.IsMonster || target.Monster is null)
                    return false;
                payload.ModelId = target.Monster.Id;
                mutation.Duplicate = CreateDuplicateSnapshot(target);
                break;
            default:
                return false;
        }

        payload.TildePayloadJson = JsonSerializer.Serialize(mutation, JsonOptions);
        return true;
    }

    public static void ApplySynchronizedMutation(LoadoutImmediateMutationPayload payload)
    {
        TaskHelper.RunSafely(ApplySerializedAsync(payload));
    }

    public static void BindCreatureNode(NCreature creatureNode)
    {
        if (!GodotObject.IsInstanceValid(creatureNode) || !TryGetCombatId(creatureNode.Entity, out ulong combatId))
            return;

        EnsureCombatState(GetCombatState());

        CreatureVisualState? state;
        lock (StateLock)
            VisualStates.TryGetValue(combatId, out state);

        if (state is null)
            return;

        if (state.HasPosition)
        {
            creatureNode.Position = state.Position;
            UpdateNavigationMethod?.Invoke(creatureNode, null);
        }

        if (!LoadoutModelIdSafety.IsNoneOrEmpty(state.MorphModelId))
            ApplyMorphVisuals(creatureNode, state.MorphModelId);
    }

    public static bool TryMapMorphAnimation(NCreature creatureNode, ref string trigger)
    {
        EnsureCombatState(GetCombatState());
        if (!TryGetCombatId(creatureNode.Entity, out ulong combatId))
            return true;

        lock (StateLock)
        {
            if (!VisualStates.TryGetValue(combatId, out CreatureVisualState? state)
                || LoadoutModelIdSafety.IsNoneOrEmpty(state.MorphModelId))
            {
                return true;
            }
        }

        if (AnimatorField?.GetValue(creatureNode) is not CreatureAnimator animator)
            return false;

        foreach (string candidate in GetAnimationCandidates(trigger))
        {
            if (!animator.HasTrigger(candidate))
                continue;

            trigger = candidate;
            return true;
        }

        return false;
    }

    private static bool Request(
        Creature creature,
        CreatureManipulationOperation operation,
        ModelId modelId,
        Action<CreatureManipulationMutation>? configure = null)
    {
        if (!CombatManager.Instance.IsInProgress || !TryGetCombatId(creature, out ulong combatId))
            return false;

        CreatureManipulationMutation mutation = new()
        {
            Operation = operation,
            CombatId = combatId
        };
        configure?.Invoke(mutation);
        return LoadoutImmediateMutationService.RequestCreatureManipulation(
            modelId,
            JsonSerializer.Serialize(mutation, JsonOptions));
    }

    private static async Task ApplySerializedAsync(LoadoutImmediateMutationPayload payload)
    {
        await MutationGate.WaitAsync();
        try
        {
            await ApplyAsync(payload);
        }
        catch (Exception exception)
        {
            GD.PushWarning($"CreatureManipulation: failed applying synchronized mutation. {exception.Message}");
        }
        finally
        {
            MutationGate.Release();
        }
    }

    private static async Task ApplyAsync(LoadoutImmediateMutationPayload payload)
    {
        if (!TryDeserialize(payload.TildePayloadJson, out CreatureManipulationMutation? mutation))
            return;

        CombatState? combatState = GetCombatState();
        EnsureCombatState(combatState);
        Creature? target = combatState is null ? null : ResolveCreature(combatState, mutation.CombatId);
        if (target is null)
            return;

        switch (mutation.Operation)
        {
            case CreatureManipulationOperation.SetPosition:
                ApplyPosition(target, new Vector2(mutation.X, mutation.Y));
                break;
            case CreatureManipulationOperation.AdjustPower:
                await AdjustPowerAsync(target, payload.ModelId, mutation.Value);
                break;
            case CreatureManipulationOperation.ClearPowers:
                await ClearPowersAsync(target, (PowerType)mutation.Value);
                break;
            case CreatureManipulationOperation.Morph:
                ApplyMorph(target, payload.ModelId);
                break;
            case CreatureManipulationOperation.Kill:
                await CreatureCmd.Kill(target);
                break;
            case CreatureManipulationOperation.SetStat:
                if (mutation.StatId is not null)
                    SetStatDirect(target, mutation.StatId, mutation.Value);
                break;
            case CreatureManipulationOperation.SetStatLock:
                if (mutation.StatId is not null)
                    ApplyStatLock(target, mutation.StatId, mutation.Value, mutation.Enabled);
                break;
            case CreatureManipulationOperation.Duplicate:
                if (mutation.Duplicate is not null)
                    await DuplicateAsync(combatState!, payload.ModelId, mutation.Duplicate);
                break;
        }

        NotifyCreatureChanged(mutation.CombatId);
    }

    private static void ApplyPosition(Creature target, Vector2 position)
    {
        if (!TryGetCombatId(target, out ulong combatId))
            return;

        lock (StateLock)
        {
            CreatureVisualState state = GetOrCreateVisualState(combatId);
            state.HasPosition = true;
            state.Position = position;
        }

        NCreature? node = NCombatRoom.Instance?.GetCreatureNode(target);
        if (node is not null)
        {
            node.Position = position;
            UpdateNavigationMethod?.Invoke(node, null);
        }
    }

    private static async Task AdjustPowerAsync(Creature target, ModelId powerId, int delta)
    {
        if (delta == 0 || ResolvePower(powerId) is not { } canonical)
            return;

        await PowerCmd.Apply(
            new ThrowingPlayerChoiceContext(),
            canonical.ToMutable(),
            target,
            delta,
            target,
            null);
    }

    private static async Task ClearPowersAsync(Creature target, PowerType type)
    {
        if (type is not (PowerType.Buff or PowerType.Debuff))
            return;

        foreach (PowerModel power in target.Powers.Where(power => power.Type == type).ToList())
            await PowerCmd.Remove(power);
    }

    private static void ApplyMorph(Creature target, ModelId morphModelId)
    {
        if (!TryGetCombatId(target, out ulong combatId))
            return;

        lock (StateLock)
        {
            CreatureVisualState state = GetOrCreateVisualState(combatId);
            state.MorphModelId = LoadoutModelIdSafety.OrNone(morphModelId);
        }

        NCreature? node = NCombatRoom.Instance?.GetCreatureNode(target);
        if (node is null)
            return;

        if (LoadoutModelIdSafety.IsNoneOrEmpty(morphModelId))
            RestoreOriginalVisuals(node);
        else
            ApplyMorphVisuals(node, morphModelId);
    }

    private static void ApplyStatLock(Creature target, string statId, int value, bool locked)
    {
        if (!TryGetCombatId(target, out ulong combatId))
            return;

        lock (StateLock)
        {
            if (locked)
                StatLocks[(combatId, statId)] = value;
            else
                StatLocks.Remove((combatId, statId));
        }

        if (locked)
            SetStatDirect(target, statId, value);
    }

    private static async Task DuplicateAsync(CombatState combatState, ModelId monsterId, CreatureDuplicateSnapshot snapshot)
    {
        MonsterModel? canonical = ModelDb.Monsters.FirstOrDefault(monster => LoadoutModelIdSafety.Matches(monster, monsterId));
        if (canonical is null)
            return;

        MonsterModel monster = canonical.ToMutable();
        CombatSide side = Enum.IsDefined(typeof(CombatSide), snapshot.Side)
            ? (CombatSide)snapshot.Side
            : CombatSide.Enemy;
        string? slotName = side == CombatSide.Enemy ? TryGetNextSlot(combatState) : null;
        Creature duplicate = await CreatureCmd.Add(monster, combatState, side, slotName);

        foreach (PowerModel power in duplicate.Powers
                     .Where(power => power.Type is PowerType.Buff or PowerType.Debuff)
                     .ToList())
        {
            await PowerCmd.Remove(power);
        }

        foreach (CreaturePowerSnapshot powerSnapshot in snapshot.Powers)
        {
            PowerModel? power = ResolvePower(powerSnapshot.Id);
            if (power is null || powerSnapshot.Amount == 0)
                continue;

            await PowerCmd.Apply(
                new ThrowingPlayerChoiceContext(),
                power.ToMutable(),
                duplicate,
                powerSnapshot.Amount,
                duplicate,
                null);
        }

        SetStatDirect(duplicate, MaxHpStatId, snapshot.MaxHp);
        SetStatDirect(duplicate, CurrentHpStatId, snapshot.CurrentHp);
        SetStatDirect(duplicate, BlockStatId, snapshot.Block);
        ApplyPosition(duplicate, new Vector2(snapshot.X, snapshot.Y));
    }

    private static CreatureDuplicateSnapshot CreateDuplicateSnapshot(Creature target)
    {
        Vector2 position = NCombatRoom.Instance?.GetCreatureNode(target)?.Position ?? new Vector2(520f, 200f);
        Vector2 duplicatePosition = FindDuplicatePosition(position);
        return new CreatureDuplicateSnapshot
        {
            CurrentHp = target.CurrentHp,
            MaxHp = target.MaxHp,
            Block = target.Block,
            Side = (int)target.Side,
            X = duplicatePosition.X,
            Y = duplicatePosition.Y,
            Powers = target.Powers
                .Where(power => power.Type is PowerType.Buff or PowerType.Debuff)
                .Select(power => new CreaturePowerSnapshot
                {
                    Id = power.Id.ToString(),
                    Amount = power.Amount
                })
                .ToList()
        };
    }

    private static Vector2 FindDuplicatePosition(Vector2 sourcePosition)
    {
        List<Vector2> occupied = NCombatRoom.Instance?.CreatureNodes
            .Where(GodotObject.IsInstanceValid)
            .Select(node => node.Position)
            .ToList() ?? [];

        Vector2[] candidates =
        [
            sourcePosition + new Vector2(190f, 0f),
            sourcePosition + new Vector2(-190f, 0f),
            sourcePosition + new Vector2(100f, 90f),
            sourcePosition + new Vector2(-100f, 90f),
            sourcePosition + new Vector2(100f, -90f),
            sourcePosition + new Vector2(-100f, -90f)
        ];

        foreach (Vector2 candidate in candidates)
        {
            Vector2 clamped = new(Mathf.Clamp(candidate.X, 100f, 930f), Mathf.Clamp(candidate.Y, 100f, 410f));
            if (occupied.All(position => position.DistanceTo(clamped) >= 125f))
                return clamped;
        }

        int index = occupied.Count;
        return new Vector2(150f + index % 5 * 185f, 150f + index / 5 % 3 * 90f);
    }

    private static string? TryGetNextSlot(CombatState combatState)
    {
        try
        {
            string? slotName = combatState.Encounter?.GetNextSlot(combatState);
            return string.IsNullOrWhiteSpace(slotName) ? null : slotName;
        }
        catch
        {
            return null;
        }
    }

    private static void SetStatDirect(Creature creature, string statId, int value)
    {
        switch (statId)
        {
            case CurrentHpStatId:
            {
                bool wasDead = creature.IsDead;
                SetCreatureValue(creature, CurrentHpField, CurrentHpChangedField, creature.CurrentHp, value, next => creature.SetCurrentHpInternal(next));
                if (creature.Player is { } player)
                {
                    if (wasDead && creature.IsAlive)
                        player.ActivateHooks();
                    else if (!wasDead && creature.IsDead)
                        player.DeactivateHooks();
                }
                break;
            }
            case MaxHpStatId:
                SetCreatureValue(creature, MaxHpField, MaxHpChangedField, creature.MaxHp, value, next => creature.SetMaxHpInternal(next));
                break;
            case BlockStatId:
                if (BlockField is not null)
                {
                    int oldBlock = creature.Block;
                    BlockField.SetValue(creature, value);
                    InvokeCreatureIntChanged(BlockChangedField, creature, oldBlock, value);
                }
                else if (value > creature.Block)
                    creature.GainBlockInternal(value - creature.Block);
                else if (value < creature.Block)
                    creature.LoseBlockInternal(creature.Block - value);
                break;
        }

        RefreshCreatureStateDisplay(creature);
    }

    private static void SetCreatureValue(
        Creature creature,
        FieldInfo? valueField,
        FieldInfo? eventField,
        int oldValue,
        int newValue,
        Action<int> fallback)
    {
        if (valueField is not null)
        {
            valueField.SetValue(creature, newValue);
            InvokeCreatureIntChanged(eventField, creature, oldValue, newValue);
        }
        else
            fallback(newValue);
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
            GD.PushWarning($"CreatureManipulation: failed invoking creature change event. {exception.Message}");
        }
    }

    private static void RefreshCreatureStateDisplay(Creature creature)
    {
        try
        {
            NCreature? node = NCombatRoom.Instance?.GetCreatureNode(creature);
            object? stateDisplay = node is null ? null : CreatureStateDisplayField?.GetValue(node);
            if (stateDisplay is not null)
                CreatureStateDisplayRefreshValuesMethod?.Invoke(stateDisplay, null);

            if (creature.Player is not null
                && MegaCrit.Sts2.Core.Nodes.NRun.Instance?.GlobalUi?.MultiplayerPlayerContainer is { } container)
            {
                NMultiplayerPlayerState? playerState = container.GetChildren()
                    .OfType<NMultiplayerPlayerState>()
                    .FirstOrDefault(state => state.Player.NetId == creature.Player.NetId);
                if (playerState is not null)
                    MultiplayerPlayerStateRefreshValuesMethod?.Invoke(playerState, null);
            }
        }
        catch (Exception exception)
        {
            GD.PushWarning($"CreatureManipulation: failed refreshing creature stats. {exception.Message}");
        }
    }

    private static void ApplyMorphVisuals(NCreature node, ModelId modelId)
    {
        AbstractModel? model = ResolveMorphModel(modelId);
        if (model is null)
            return;

        try
        {
            NCreatureVisuals visuals;
            CreatureAnimator? animator;
            if (model is MonsterModel canonicalMonster)
            {
                MonsterModel monster = canonicalMonster.ToMutable();
                monster.Creature = node.Entity;
                visuals = monster.CreateVisuals();
                animator = visuals.HasSpineAnimation ? monster.GenerateAnimator(visuals.SpineBody) : null;
                if (visuals.HasSpineAnimation)
                    visuals.SetUpSkin(monster);
            }
            else if (model is CharacterModel character)
            {
                visuals = character.CreateVisuals();
                animator = visuals.HasSpineAnimation ? character.GenerateAnimator(visuals.SpineBody) : null;
            }
            else
                return;

            ReplaceVisuals(node, visuals, animator);
        }
        catch (Exception exception)
        {
            GD.PushWarning($"CreatureManipulation: failed morphing '{node.Entity.Name}' to '{modelId}'. {exception.Message}");
        }
    }

    private static void RestoreOriginalVisuals(NCreature node)
    {
        try
        {
            NCreatureVisuals visuals = node.Entity.CreateVisuals();
            CreatureAnimator? animator = null;
            if (visuals.HasSpineAnimation)
            {
                if (node.Entity.Player?.Character is { } character)
                    animator = character.GenerateAnimator(visuals.SpineBody);
                else if (node.Entity.Monster is { } monster)
                {
                    animator = monster.GenerateAnimator(visuals.SpineBody);
                    visuals.SetUpSkin(monster);
                }
            }

            ReplaceVisuals(node, visuals, animator);
        }
        catch (Exception exception)
        {
            GD.PushWarning($"CreatureManipulation: failed restoring '{node.Entity.Name}' visuals. {exception.Message}");
        }
    }

    private static void ReplaceVisuals(NCreature node, NCreatureVisuals visuals, CreatureAnimator? animator)
    {
        if (VisualsField is null || AnimatorField is null)
        {
            visuals.QueueFree();
            GD.PushWarning("CreatureManipulation: required NCreature visual fields were not found.");
            return;
        }

        NCreatureVisuals? oldVisuals = node.Visuals;
        if (oldVisuals is not null && GodotObject.IsInstanceValid(oldVisuals))
        {
            oldVisuals.GetParent()?.RemoveChild(oldVisuals);
            oldVisuals.QueueFree();
        }

        VisualsField.SetValue(node, visuals);
        AnimatorField.SetValue(node, animator);
        node.AddChild(visuals);
        node.MoveChild(visuals, 0);
        visuals.Position = Vector2.Zero;

        if (animator is not null)
            ConnectAnimatorSignalsMethod?.Invoke(node, null);

        UpdateBoundsMethod?.Invoke(node, [visuals]);
        UpdatePhobiaModeMethod?.Invoke(node, null);
        SetOrbManagerPositionMethod?.Invoke(node, null);
        ImmediatelySetIdleMethod?.Invoke(node, null);
    }

    private static IEnumerable<string> GetAnimationCandidates(string trigger)
    {
        string safeTrigger = trigger ?? string.Empty;
        string normalized = safeTrigger.Replace("_", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        List<string> candidates = [safeTrigger];

        void Add(params string[] names)
        {
            foreach (string name in names)
            {
                if (!candidates.Contains(name, StringComparer.Ordinal))
                    candidates.Add(name);
            }
        }

        if (normalized.Contains("attack", StringComparison.Ordinal)
            || normalized.Contains("strike", StringComparison.Ordinal)
            || normalized.Contains("slash", StringComparison.Ordinal)
            || normalized.Contains("stab", StringComparison.Ordinal)
            || normalized.Contains("bite", StringComparison.Ordinal)
            || normalized.Contains("slam", StringComparison.Ordinal)
            || normalized.Contains("shoot", StringComparison.Ordinal))
        {
            Add("Attack", "attack", "Cast", "cast", "PowerUp");
        }
        else if (normalized.Contains("cast", StringComparison.Ordinal)
                 || normalized.Contains("spell", StringComparison.Ordinal)
                 || normalized.Contains("magic", StringComparison.Ordinal))
        {
            Add("Cast", "cast", "PowerUp", "Attack", "attack");
        }
        else if (normalized.Contains("power", StringComparison.Ordinal)
                 || normalized.Contains("buff", StringComparison.Ordinal)
                 || normalized.Contains("debuff", StringComparison.Ordinal)
                 || normalized.Contains("charge", StringComparison.Ordinal)
                 || normalized.Contains("roar", StringComparison.Ordinal)
                 || normalized.Contains("summon", StringComparison.Ordinal))
        {
            Add("PowerUp", "Cast", "cast", "Attack", "attack");
        }
        else if (normalized.Contains("hit", StringComparison.Ordinal)
                 || normalized.Contains("hurt", StringComparison.Ordinal)
                 || normalized.Contains("damage", StringComparison.Ordinal))
        {
            Add("Hit", "hit");
        }
        else if (normalized.Contains("death", StringComparison.Ordinal)
                 || normalized.Contains("die", StringComparison.Ordinal))
        {
            Add("Death", "death");
        }
        else if (normalized.Contains("revive", StringComparison.Ordinal)
                 || normalized.Contains("resurrect", StringComparison.Ordinal))
        {
            Add("Revive", "revive");
        }

        Add("Idle", "idle");
        return candidates;
    }

    private static AbstractModel? ResolveMorphModel(ModelId id)
    {
        if (LoadoutModelIdSafety.IsNoneOrEmpty(id))
            return null;

        return ModelDb.Monsters.Cast<AbstractModel>()
                   .Concat(ModelDb.AllCharacters)
                   .FirstOrDefault(model => LoadoutModelIdSafety.Matches(model, id));
    }

    private static PowerModel? ResolvePower(ModelId id)
    {
        return LoadoutModelIdSafety.IsNoneOrEmpty(id)
            ? null
            : ModelDb.AllPowers.FirstOrDefault(power => LoadoutModelIdSafety.Matches(power, id));
    }

    private static PowerModel? ResolvePower(string rawId)
    {
        return ModelDb.AllPowers.FirstOrDefault(power =>
            string.Equals(power.Id.ToString(), rawId, StringComparison.Ordinal)
            || string.Equals(power.Id.Entry, rawId, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsSupportedStat(string? statId)
    {
        return statId is CurrentHpStatId or MaxHpStatId or BlockStatId;
    }

    private static bool TryGetCombatId(Creature creature, out ulong combatId)
    {
        combatId = creature.CombatId ?? 0;
        return combatId != 0;
    }

    private static CombatState? GetCombatState()
    {
        try
        {
            return CombatManager.Instance.IsInProgress ? CombatManager.Instance.DebugOnlyGetState() : null;
        }
        catch
        {
            return null;
        }
    }

    private static Creature? ResolveCreature(CombatState combatState, ulong combatId)
    {
        return combatId == 0
            ? null
            : combatState.Creatures.FirstOrDefault(creature => creature.CombatId == combatId);
    }

    private static CreatureVisualState GetOrCreateVisualState(ulong combatId)
    {
        if (!VisualStates.TryGetValue(combatId, out CreatureVisualState? state))
        {
            state = new CreatureVisualState();
            VisualStates[combatId] = state;
        }

        return state;
    }

    private static bool TryDeserialize(string json, out CreatureManipulationMutation? mutation)
    {
        mutation = null;
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            mutation = JsonSerializer.Deserialize<CreatureManipulationMutation>(json, JsonOptions);
            return mutation is not null && mutation.CombatId != 0;
        }
        catch (Exception exception)
        {
            GD.PushWarning($"CreatureManipulation: invalid mutation payload. {exception.Message}");
            return false;
        }
    }

    private static void NotifyCreatureChanged(ulong combatId)
    {
        try
        {
            CreatureChanged?.Invoke(combatId);
        }
        catch (Exception exception)
        {
            GD.PushWarning($"CreatureManipulation: state changed handler failed. {exception.Message}");
        }
    }

    private static void EnsureCombatState(CombatState? combatState)
    {
        if (ReferenceEquals(_activeCombatState, combatState))
            return;

        lock (StateLock)
        {
            VisualStates.Clear();
            StatLocks.Clear();
        }
        _activeCombatState = combatState;
    }

    private static void ClearCombatState()
    {
        lock (StateLock)
        {
            VisualStates.Clear();
            StatLocks.Clear();
            _pendingSnapshot = null;
        }

        _activeCombatState = null;
    }

    private static void RegisterRunNetService(INetGameService netService)
    {
        if (ReferenceEquals(_runNetService, netService))
            return;

        UnregisterRunNetService();
        _runNetService = netService;
        _runNetService.RegisterMessageHandler<CreatureManipulationSnapshotMessage>(HandleSnapshotMessage);
        BindRunLobby(RunManager.Instance.RunLobby);
    }

    private static void UnregisterRunNetService()
    {
        if (_runNetService is null)
            return;

        _runNetService.UnregisterMessageHandler<CreatureManipulationSnapshotMessage>(HandleSnapshotMessage);
        _runNetService = null;
    }

    private static void BindRunLobby(RunLobby? runLobby)
    {
        if (ReferenceEquals(_runLobby, runLobby))
            return;

        UnbindRunLobby();
        _runLobby = runLobby;
        if (_runLobby is not null)
            _runLobby.PlayerRejoined += OnPlayerRejoined;
    }

    private static void UnbindRunLobby()
    {
        if (_runLobby is null)
            return;

        _runLobby.PlayerRejoined -= OnPlayerRejoined;
        _runLobby = null;
    }

    private static void OnPlayerRejoined(ulong playerId)
    {
        if (_runNetService?.Type != NetGameType.Host || playerId == _runNetService.NetId)
            return;

        _runNetService.SendMessage(new CreatureManipulationSnapshotMessage
        {
            json = BuildSnapshotJson()
        }, playerId);
    }

    private static string BuildSnapshotJson()
    {
        CreatureManipulationSnapshot snapshot = new();

        lock (StateLock)
        {
            snapshot.VisualStates = VisualStates.Select(pair => new CreatureVisualSnapshot
            {
                CombatId = pair.Key,
                HasPosition = pair.Value.HasPosition,
                X = pair.Value.Position.X,
                Y = pair.Value.Position.Y,
                MorphModelId = LoadoutModelIdSafety.ToWireString(pair.Value.MorphModelId)
            }).ToList();

            snapshot.Locks = StatLocks.Select(pair => new CreatureStatLockSnapshot
            {
                CombatId = pair.Key.CombatId,
                StatId = pair.Key.StatId,
                Value = pair.Value
            }).ToList();
        }

        return JsonSerializer.Serialize(snapshot, JsonOptions);
    }

    private static void HandleSnapshotMessage(CreatureManipulationSnapshotMessage message, ulong senderId)
    {
        if (_runNetService?.Type == NetGameType.Host
            || !LoadoutNetworkBroadcast.IsExpectedHostSender(senderId, _runNetService ?? RunManager.Instance.NetService))
        {
            return;
        }

        try
        {
            CreatureManipulationSnapshot? snapshot = JsonSerializer.Deserialize<CreatureManipulationSnapshot>(message.json, JsonOptions);
            if (snapshot is null)
                return;

            CombatState? combatState = GetCombatState();
            if (combatState is null)
            {
                lock (StateLock)
                    _pendingSnapshot = snapshot;
                return;
            }

            EnsureCombatState(combatState);
            ApplySnapshot(combatState, snapshot);
        }
        catch (Exception exception)
        {
            GD.PushWarning($"CreatureManipulation: failed applying rejoin snapshot. {exception.Message}");
        }
    }


    private static void ApplyPendingSnapshot(CombatState combatState)
    {
        CreatureManipulationSnapshot? snapshot;
        lock (StateLock)
        {
            snapshot = _pendingSnapshot;
            _pendingSnapshot = null;
        }

        if (snapshot is not null)
            ApplySnapshot(combatState, snapshot);
    }

    private static void ApplySnapshot(CombatState combatState, CreatureManipulationSnapshot snapshot)
    {
        HashSet<ulong> currentCombatIds = combatState.Creatures
            .Where(creature => creature.CombatId.HasValue && creature.CombatId.Value != 0)
            .Select(creature => creature.CombatId!.Value)
            .ToHashSet();

        lock (StateLock)
        {
            VisualStates.Clear();
            StatLocks.Clear();

            foreach (CreatureVisualSnapshot saved in snapshot.VisualStates
                         .Where(saved => currentCombatIds.Contains(saved.CombatId)))
            {
                VisualStates[saved.CombatId] = new CreatureVisualState
                {
                    HasPosition = saved.HasPosition,
                    Position = new Vector2(saved.X, saved.Y),
                    MorphModelId = ResolveModelId(saved.MorphModelId)
                };
            }

            foreach (CreatureStatLockSnapshot saved in snapshot.Locks
                         .Where(saved => currentCombatIds.Contains(saved.CombatId) && IsSupportedStat(saved.StatId)))
            {
                StatLocks[(saved.CombatId, saved.StatId)] = saved.Value;
            }
        }

        foreach (NCreature node in NCombatRoom.Instance?.CreatureNodes ?? [])
            BindCreatureNode(node);
    }

    private static ModelId ResolveModelId(string rawId)
    {
        if (string.IsNullOrWhiteSpace(rawId))
            return ModelId.none;

        return ResolveMorphModelByString(rawId)?.Id ?? ModelId.none;
    }

    private static AbstractModel? ResolveMorphModelByString(string rawId)
    {
        return ModelDb.Monsters.Cast<AbstractModel>()
            .Concat(ModelDb.AllCharacters)
            .FirstOrDefault(model =>
                string.Equals(model.Id.ToString(), rawId, StringComparison.Ordinal)
                || string.Equals(model.Id.Entry, rawId, StringComparison.OrdinalIgnoreCase));
    }
}

public struct CreatureManipulationSnapshotMessage : INetMessage, IPacketSerializable
{
    public string json;

    public bool ShouldBroadcast => false;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.VeryDebug;
    public bool ShouldBuffer => false;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteString(json ?? string.Empty);
    }

    public void Deserialize(PacketReader reader)
    {
        json = reader.ReadString();
    }
}
