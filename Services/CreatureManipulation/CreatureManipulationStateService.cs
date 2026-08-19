#nullable enable

namespace Loadout.Services.CreatureManipulation;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Loadout.Patches.TildeKey;
using Loadout.Services.Actions;
using Loadout.Services.Compatibility;
using Loadout.Services.Configuration;
using Loadout.Services.Loadouts;
using Loadout.Services.Networking;
using Loadout.Services.TildeKey;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Rooms;
using Loadout.UI.CreatureManipulation;

public static class CreatureManipulationStateService
{
    private const int MaxRequestJsonLength = 64 * 1024;
    private const int MaxPowerDeltaMagnitude = 999_999_999;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly Dictionary<uint, SerializableVector2> Positions = [];
    private static readonly Dictionary<uint, CreatureStatLockSnapshot> Locks = [];
    private static readonly Dictionary<uint, HostDragLease> HostDragLeases = [];
    private static readonly Dictionary<uint, int> LastDragSequence = [];

    private static INetGameService? _netService;
    private static RunLobby? _runLobby;
    private static Delegate? _playerRejoinedHandler;
    private static int _combatEpoch;
    private static int _dragSequence;
    private static long _nextDragSession;
    private static uint _localDragCombatId;
    private static ulong _localDragSessionId;
    private static int _lastCompletedEpoch;
    private static CreatureManipulationCombatSnapshot? _pendingSnapshot;
    private static bool _clientCombatSetUpSeen;

    [ThreadStatic]
    private static int _lockReapplyDepth;

    public static event Action? StateChanged;
    public static event Action<CreatureDragMessage>? DragAuthoritative;

    public static int CombatEpoch => _combatEpoch;
    public static ulong LocalNetId => GetLocalPlayer()?.NetId ?? 0;
    public static bool HasCreatureLocks => Locks.Values.Any(entry => !entry.IsEmpty);

    public static void OnRunLaunched()
    {
        ClearCombatState(resetEpoch: true);
        CombatManager.Instance.CombatSetUp -= OnCombatSetUp;
        CombatManager.Instance.CombatSetUp += OnCombatSetUp;
        CombatManager.Instance.CombatEnded -= OnCombatEnded;
        CombatManager.Instance.CombatEnded += OnCombatEnded;
        LoadoutPanelAccessService.AccessChanged -= OnAccessChanged;
        LoadoutPanelAccessService.AccessChanged += OnAccessChanged;

        try
        {
            RegisterNetService(RunManager.Instance.NetService);
        }
        catch (Exception exception)
        {
            GD.PushWarning($"CreatureManipulation: failed to register run networking. {exception.Message}");
        }
    }

    public static void OnRunCleaningUp()
    {
        CombatManager.Instance.CombatSetUp -= OnCombatSetUp;
        CombatManager.Instance.CombatEnded -= OnCombatEnded;
        LoadoutPanelAccessService.AccessChanged -= OnAccessChanged;
        UnbindRunLobby();
        UnregisterNetService();
        ClearCombatState(resetEpoch: true);
    }

    public static void OnCreatureReady(NCreature node)
    {
        if (node.Entity?.CombatId is not uint combatId)
            return;

        if (Positions.TryGetValue(combatId, out SerializableVector2 position))
            node.Position = position.ToVector2();
    }

    public static bool RequestAdjustPower(Creature target, ModelId powerId, int delta)
    {
        if (delta == 0)
            return false;

        return Request(target, new CreatureManipulationPayload
        {
            Operation = CreatureManipulationOperation.AdjustPower,
            ModelId = powerId.ToString(),
            Amount = delta
        });
    }

    public static bool RequestClearPowers(Creature target, PowerType powerType)
    {
        if (powerType is not (PowerType.Buff or PowerType.Debuff))
            return false;

        return Request(target, new CreatureManipulationPayload
        {
            Operation = CreatureManipulationOperation.ClearPowersByType,
            PowerType = powerType
        });
    }

    public static bool RequestSetStat(Creature target, CreatureManipulationStat stat, int value) =>
        Request(target, new CreatureManipulationPayload
        {
            Operation = CreatureManipulationOperation.SetStat,
            Stat = stat,
            Value = Math.Max(0, value)
        });

    public static bool RequestSetLock(
        Creature target,
        CreatureManipulationStat stat,
        bool locked,
        int value) =>
        Request(target, new CreatureManipulationPayload
        {
            Operation = CreatureManipulationOperation.SetLock,
            Stat = stat,
            Locked = locked,
            Value = Math.Max(0, value)
        });

    public static bool RequestKill(Creature target) =>
        Request(target, new CreatureManipulationPayload
        {
            Operation = CreatureManipulationOperation.Kill
        });

    public static bool RequestDuplicate(Creature target) =>
        Request(target, new CreatureManipulationPayload
        {
            Operation = CreatureManipulationOperation.Duplicate
        });

    public static ulong BeginDrag(Creature target, Vector2 position)
    {
        if (!CanRequest(target) || target.CombatId is not uint combatId)
            return 0;

        ulong sessionId = unchecked((ulong)Interlocked.Increment(ref _nextDragSession));
        _localDragCombatId = combatId;
        _localDragSessionId = sessionId;
        SendDrag(new CreatureDragMessage
        {
            combatEpoch = _combatEpoch,
            combatId = combatId,
            sessionId = sessionId,
            ownerNetId = LocalNetId,
            phase = CreatureDragPhase.Begin,
            x = position.X,
            y = position.Y
        });
        return sessionId;
    }

    public static void UpdateDrag(uint combatId, ulong sessionId, Vector2 position) =>
        SendDrag(new CreatureDragMessage
        {
            combatEpoch = _combatEpoch,
            combatId = combatId,
            sessionId = sessionId,
            ownerNetId = LocalNetId,
            phase = CreatureDragPhase.Update,
            x = position.X,
            y = position.Y
        });

    public static void EndDrag(uint combatId, ulong sessionId, Vector2 position) =>
        SendDrag(new CreatureDragMessage
        {
            combatEpoch = _combatEpoch,
            combatId = combatId,
            sessionId = sessionId,
            ownerNetId = LocalNetId,
            phase = CreatureDragPhase.End,
            x = position.X,
            y = position.Y
        });

    public static bool TryGetLock(
        uint combatId,
        CreatureManipulationStat stat,
        out int value)
    {
        value = 0;
        if (!Locks.TryGetValue(combatId, out CreatureStatLockSnapshot? entry))
            return false;

        int? result = stat switch
        {
            CreatureManipulationStat.CurrentHp => entry.CurrentHp,
            CreatureManipulationStat.MaxHp => entry.MaxHp,
            CreatureManipulationStat.Block => entry.Block,
            _ => null
        };
        if (!result.HasValue)
            return false;

        value = result.Value;
        return true;
    }

    public static void ClearLocks(Creature creature)
    {
        if (creature.CombatId is not uint combatId || !Locks.Remove(combatId))
            return;

        TildeKeyStateService.RefreshDynamicLockPatches();
        StateChanged?.Invoke();
    }

    internal static void ReassertCreatureLocks(Creature creature)
    {
        if (_lockReapplyDepth > 0
            || creature.CombatId is not uint combatId
            || !Locks.TryGetValue(combatId, out CreatureStatLockSnapshot? entry))
        {
            return;
        }

        _lockReapplyDepth++;
        try
        {
            if (entry.MaxHp.HasValue && creature.MaxHp != entry.MaxHp.Value)
                creature.SetMaxHpInternal(entry.MaxHp.Value);
            if (entry.CurrentHp.HasValue && creature.CurrentHp != Math.Min(entry.CurrentHp.Value, creature.MaxHp))
                creature.SetCurrentHpInternal(entry.CurrentHp.Value);
            if (entry.Block.HasValue)
                SetBlock(creature, entry.Block.Value);
        }
        finally
        {
            _lockReapplyDepth--;
        }
    }

    internal static async Task ApplySynchronizedActionAsync(string payloadJson)
    {
        CreatureManipulationPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<CreatureManipulationPayload>(payloadJson, JsonOptions);
        }
        catch (Exception exception)
        {
            GD.PushWarning($"CreatureManipulation: invalid synchronized payload. {exception.Message}");
            return;
        }

        if (payload is null
            || payload.CombatEpoch != _combatEpoch
            || !CombatManager.Instance.IsInProgress
            || CombatManager.Instance.DebugOnlyGetState() is not { } combatState)
        {
            return;
        }

        Creature? target = combatState.GetCreature(payload.TargetCombatId);
        if (target is null)
            return;

        switch (payload.Operation)
        {
            case CreatureManipulationOperation.AdjustPower:
                await ApplyPowerDeltaAsync(target, payload);
                break;
            case CreatureManipulationOperation.ClearPowersByType:
                if (target.IsAlive)
                    await ApplyClearPowersByTypeAsync(target, payload.PowerType);
                break;
            case CreatureManipulationOperation.SetStat:
                ApplyStat(target, payload.Stat, payload.Value, updateExistingLock: true);
                break;
            case CreatureManipulationOperation.SetLock:
                ApplyLock(target, payload.Stat, payload.Locked, payload.Value);
                break;
            case CreatureManipulationOperation.Kill:
                ClearLocks(target);
                if (target.IsAlive)
                    await CreatureCmd.Kill(target, force: true);
                break;
            case CreatureManipulationOperation.Duplicate:
                await ApplyDuplicateAsync(combatState, payload.Duplicate);
                break;
        }
    }

    private static bool Request(Creature target, CreatureManipulationPayload payload)
    {
        if (!CanRequest(target) || target.CombatId is not uint combatId)
            return false;

        Player? requester = GetLocalPlayer();
        if (requester is null)
            return false;

        payload.CombatEpoch = _combatEpoch;
        payload.RequesterNetId = requester.NetId;
        payload.TargetCombatId = combatId;
        string json = JsonSerializer.Serialize(payload, JsonOptions);

        try
        {
            INetGameService net = RunManager.Instance.NetService;
            if (net.Type == NetGameType.Client)
            {
                net.SendMessage(new CreatureManipulationRequestMessage { payloadJson = json });
                return true;
            }

            return PublishHostAction(payload, net.NetId);
        }
        catch (Exception exception)
        {
            GD.PushWarning($"CreatureManipulation: failed to request {payload.Operation}. {exception.Message}");
            return false;
        }
    }

    private static bool CanRequest(Creature target) =>
        target is not null
        && target.CombatId.HasValue
        && _combatEpoch > 0
        && CombatManager.Instance.IsInProgress
        && LoadoutConfigService.EnableCreatureManipulationPanel
        && LoadoutPanelAccessService.CanLocalPlayerUsePanel();

    private static void HandleRequest(CreatureManipulationRequestMessage message, ulong senderId)
    {
        if (_netService?.Type != NetGameType.Host
            || !LoadoutPanelAccessService.CanRequesterUsePanel(senderId)
            || string.IsNullOrEmpty(message.payloadJson)
            || message.payloadJson.Length > MaxRequestJsonLength)
        {
            return;
        }

        try
        {
            CreatureManipulationPayload? payload =
                JsonSerializer.Deserialize<CreatureManipulationPayload>(message.payloadJson, JsonOptions);
            if (payload is null)
                return;

            payload.RequesterNetId = senderId;
            PublishHostAction(payload, senderId);
        }
        catch (Exception exception)
        {
            GD.PushWarning($"CreatureManipulation: rejected malformed request from {senderId}. {exception.Message}");
        }
    }

    private static bool PublishHostAction(CreatureManipulationPayload payload, ulong senderId)
    {
        if (payload.CombatEpoch != _combatEpoch
            || !CombatManager.Instance.IsInProgress
            || !LoadoutPanelAccessService.CanRequesterUsePanel(senderId)
            || CombatManager.Instance.DebugOnlyGetState() is not { } combatState
            || combatState.GetCreature(payload.TargetCombatId) is not { } target)
        {
            return false;
        }

        if (!ValidateOperation(payload, target))
            return false;

        if (payload.Operation == CreatureManipulationOperation.Duplicate)
        {
            payload.Duplicate = CaptureDuplicate(target, combatState);
            if (payload.Duplicate is null)
                return false;
        }

        Player? owner = GetLocalPlayer();
        if (owner is null)
            return false;

        string json = JsonSerializer.Serialize(payload, JsonOptions);
        RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(
            new LoadoutCreatureManipulationAction(owner, json));
        return true;
    }

    private static bool ValidateOperation(CreatureManipulationPayload payload, Creature target)
    {
        return payload.Operation switch
        {
            CreatureManipulationOperation.AdjustPower =>
                payload.Amount is >= -MaxPowerDeltaMagnitude and <= MaxPowerDeltaMagnitude
                && payload.Amount != 0
                && ResolvePower(payload.ModelId) is not null,
            CreatureManipulationOperation.ClearPowersByType =>
                target.IsAlive
                && payload.PowerType is PowerType.Buff or PowerType.Debuff,
            CreatureManipulationOperation.SetStat or CreatureManipulationOperation.SetLock =>
                Enum.IsDefined(payload.Stat) && payload.Value >= 0,
            CreatureManipulationOperation.Kill => true,
            CreatureManipulationOperation.Duplicate => target.Monster is not null,
            _ => false
        };
    }

    private static async Task ApplyPowerDeltaAsync(Creature target, CreatureManipulationPayload payload)
    {
        PowerModel? canonical = ResolvePower(payload.ModelId);
        if (canonical is null || payload.Amount == 0)
            return;

        Creature? applier = GetRunPlayer(payload.RequesterNetId)?.Creature;
        ThrowingPlayerChoiceContext choiceContext = new();
        List<PowerModel> existing = target.Powers
            .Where(power => SameModelId(power.Id, payload.ModelId))
            .ToList();

        if (payload.Amount > 0)
        {
            await PowerCmd.Apply(
                choiceContext,
                canonical.ToMutable(),
                target,
                payload.Amount,
                applier,
                null);
            return;
        }

        if (existing.Count == 0)
            return;

        int remaining = -payload.Amount;
        for (int index = existing.Count - 1; index >= 0 && remaining > 0; index--)
        {
            PowerModel power = existing[index];
            int available = Math.Max(1, Math.Abs(power.Amount));
            int step = Math.Min(remaining, available);
            await PowerCmd.ModifyAmount(choiceContext, power, -step, applier, null);
            remaining -= step;
        }
    }

    private static async Task ApplyClearPowersByTypeAsync(
        Creature target,
        PowerType powerType)
    {
        if (powerType is not (PowerType.Buff or PowerType.Debuff))
            return;

        List<PowerModel> powers = target.Powers
            .Where(power => power.Type == powerType)
            .ToList();

        foreach (PowerModel power in powers)
        {
            try
            {
                await PowerCmd.Remove(power);
            }
            catch (Exception exception)
            {
                GD.PushWarning(
                    $"CreatureManipulation: failed clearing {powerType} power " +
                    $"'{power.Id}' from '{target.Name}'. {exception.Message}");
            }
        }
    }

    private static void ApplyStat(
        Creature target,
        CreatureManipulationStat stat,
        int rawValue,
        bool updateExistingLock)
    {
        int value = Math.Max(0, rawValue);
        if (updateExistingLock
            && target.CombatId is uint combatId
            && Locks.TryGetValue(combatId, out CreatureStatLockSnapshot? entry))
        {
            SetLockValue(entry, stat, value);
        }

        _lockReapplyDepth++;
        try
        {
            switch (stat)
            {
                case CreatureManipulationStat.MaxHp:
                    target.SetMaxHpInternal(value);
                    break;
                case CreatureManipulationStat.CurrentHp:
                    target.SetCurrentHpInternal(value);
                    break;
                case CreatureManipulationStat.Block:
                    SetBlock(target, value);
                    break;
            }
        }
        finally
        {
            _lockReapplyDepth--;
        }

        StateChanged?.Invoke();
    }

    private static void ApplyLock(
        Creature target,
        CreatureManipulationStat stat,
        bool locked,
        int value)
    {
        if (target.CombatId is not uint combatId)
            return;

        if (!Locks.TryGetValue(combatId, out CreatureStatLockSnapshot? entry))
        {
            if (!locked)
                return;
            entry = new CreatureStatLockSnapshot();
            Locks[combatId] = entry;
        }

        SetLockValue(entry, stat, locked ? Math.Max(0, value) : null);
        if (entry.IsEmpty)
            Locks.Remove(combatId);
        else if (locked)
            ApplyStat(target, stat, value, updateExistingLock: false);

        TildeKeyStateService.RefreshDynamicLockPatches();
        StateChanged?.Invoke();
    }

    private static void SetLockValue(
        CreatureStatLockSnapshot entry,
        CreatureManipulationStat stat,
        int? value)
    {
        switch (stat)
        {
            case CreatureManipulationStat.CurrentHp:
                entry.CurrentHp = value;
                break;
            case CreatureManipulationStat.MaxHp:
                entry.MaxHp = value;
                break;
            case CreatureManipulationStat.Block:
                entry.Block = value;
                break;
        }
    }

    private static void SetBlock(Creature creature, int value)
    {
        value = Math.Max(0, value);
        if (value > creature.Block)
            creature.GainBlockInternal(value - creature.Block);
        else if (value < creature.Block)
            creature.LoseBlockInternal(creature.Block - value);
    }

    private static CreatureDuplicateSnapshot? CaptureDuplicate(Creature target, CombatState combatState)
    {
        if (target.Monster is not { } monster)
            return null;

        foreach (PowerModel power in target.Powers)
        {
            if (ResolvePower(power.Id.ToString()) is null)
                return null;
        }

        string slotName = string.Empty;
        try
        {
            slotName = combatState.Encounter?.GetNextSlot(combatState) ?? string.Empty;
        }
        catch
        {
            slotName = string.Empty;
        }

        Vector2 position = ChooseDuplicatePosition(target);
        return new CreatureDuplicateSnapshot
        {
            MonsterId = monster.Id.ToString(),
            CurrentHp = target.CurrentHp,
            MaxHp = target.MaxHp,
            Block = target.Block,
            SlotName = slotName,
            PositionX = position.X,
            PositionY = position.Y,
            Powers = target.Powers
                .Where(power => LoadoutMonsterSpawnRules.CopySourcePowerWhenDuplicating(monster, power))
                .Select(power => new CreaturePowerSnapshot
                {
                    ModelId = power.Id.ToString(),
                    Amount = power.Amount
                }).ToList()
        };
    }

    private static Vector2 ChooseDuplicatePosition(Creature target)
    {
        NCreature? source = NCombatRoom.Instance?.GetCreatureNode(target);
        if (source is null)
            return new Vector2(520f, 200f);

        float x = source.Position.X;
        float y = source.Position.Y;
        float sourceHalfWidth = MathF.Max(45f, source.Visuals.Bounds.Size.X * 0.5f);
        IReadOnlyList<NCreature> nodes = NCombatRoom.Instance?.CreatureNodes
            .Where(node =>
                GodotObject.IsInstanceValid(node)
                && node.Entity.IsMonster
                && node.Entity.Side == CombatSide.Enemy)
            .ToList() ?? [];
        float sourceHalfHeight = MathF.Max(45f, source.Visuals.Bounds.Size.Y * 0.5f);
        bool foundPosition = false;

        for (int attempt = 0; attempt < Math.Max(1, nodes.Count + 1); attempt++)
        {
            x += sourceHalfWidth * 2f + 70f;
            if (x > 900f)
                break;

            bool overlaps = nodes.Any(node =>
                MathF.Abs(node.Position.X - x)
                < sourceHalfWidth + MathF.Max(45f, node.Visuals.Bounds.Size.X * 0.5f) + 20f
                && MathF.Abs(node.Position.Y - y)
                < sourceHalfHeight + MathF.Max(45f, node.Visuals.Bounds.Size.Y * 0.5f) + 20f);
            if (!overlaps)
            {
                foundPosition = true;
                break;
            }
        }

        if (!foundPosition)
        {
            int index = nodes.Count;
            x = 160f + index % 4 * 205f;
            y = 200f + index / 4 % 3 * 74f;
        }

        return new Vector2(Mathf.Clamp(x, 120f, 900f), Mathf.Clamp(y, 120f, 380f));
    }

    private static async Task ApplyDuplicateAsync(
        CombatState combatState,
        CreatureDuplicateSnapshot? snapshot)
    {
        if (snapshot is null)
            return;

        MonsterModel? canonical = ModelDb.Monsters.FirstOrDefault(
            monster => SameModelId(monster.Id, snapshot.MonsterId));
        if (canonical is null
            || snapshot.Powers.Any(power => ResolvePower(power.ModelId) is null))
        {
            return;
        }

        Creature duplicate = await LoadoutSummonMonsterService.AddMonsterWithIntentFallbackAsync(
            canonical.ToMutable(),
            combatState,
            CombatSide.Enemy,
            string.IsNullOrWhiteSpace(snapshot.SlotName) ? null : snapshot.SlotName);

        duplicate.SetMaxHpInternal(snapshot.MaxHp);
        duplicate.SetCurrentHpInternal(snapshot.CurrentHp);
        SetBlock(duplicate, snapshot.Block);

        foreach (PowerModel power in duplicate.Powers.ToList())
        {
            if (!LoadoutMonsterSpawnRules.PreserveSpawnedPowerWhenDuplicating(canonical, power))
                power.RemoveInternal();
        }

        foreach (CreaturePowerSnapshot powerSnapshot in snapshot.Powers)
        {
            PowerModel power = ResolvePower(powerSnapshot.ModelId)!.ToMutable();
            power.Applier = duplicate;
            power.ApplyInternal(duplicate, powerSnapshot.Amount, silent: true);
        }

        if (string.IsNullOrWhiteSpace(snapshot.SlotName)
            && NCombatRoom.Instance?.GetCreatureNode(duplicate) is { } node)
        {
            node.Position = new Vector2(snapshot.PositionX, snapshot.PositionY);
            if (duplicate.CombatId is uint combatId)
                Positions[combatId] = SerializableVector2.From(node.Position);
        }
    }

    private static void SendDrag(CreatureDragMessage message)
    {
        try
        {
            INetGameService net = RunManager.Instance.NetService;
            if (net.Type == NetGameType.Client)
                net.SendMessage(message);
            else
                HandleHostDrag(message, net.NetId);
        }
        catch (Exception exception)
        {
            GD.PushWarning($"CreatureManipulation: failed to send drag update. {exception.Message}");
        }
    }

    private static void HandleDrag(CreatureDragMessage message, ulong senderId)
    {
        if (_netService?.Type == NetGameType.Host)
        {
            HandleHostDrag(message, senderId);
            return;
        }

        if (_netService?.Type != NetGameType.Client
            || !LoadoutNetworkBroadcast.IsExpectedHostSender(senderId, _netService)
            || message.combatEpoch != _combatEpoch)
        {
            return;
        }

        ApplyAuthoritativeDrag(message);
    }

    private static void HandleHostDrag(CreatureDragMessage message, ulong senderId)
    {
        if (message.combatEpoch != _combatEpoch
            || message.sessionId == 0
            || !float.IsFinite(message.x)
            || !float.IsFinite(message.y)
            || !LoadoutPanelAccessService.CanRequesterUsePanel(senderId)
            || CombatManager.Instance.DebugOnlyGetState()?.GetCreature(message.combatId) is not { } target)
        {
            RejectDrag(message);
            return;
        }

        bool accepted = message.phase switch
        {
            CreatureDragPhase.Begin => TryAcquireDrag(message.combatId, senderId, message.sessionId),
            CreatureDragPhase.Update or CreatureDragPhase.End =>
                HostDragLeases.TryGetValue(message.combatId, out HostDragLease lease)
                && lease.OwnerNetId == senderId
                && lease.SessionId == message.sessionId,
            _ => false
        };

        if (!accepted)
        {
            RejectDrag(message);
            return;
        }

        message.sequence = ++_dragSequence;
        message.ownerNetId = senderId;
        Vector2 position = new(message.x, message.y);
        Positions[message.combatId] = SerializableVector2.From(position);
        ApplyCreaturePosition(target, position);
        if (message.phase == CreatureDragPhase.End)
        {
            HostDragLeases.Remove(message.combatId);
            if (message.ownerNetId == LocalNetId
                && message.combatId == _localDragCombatId
                && message.sessionId == _localDragSessionId)
            {
                _localDragCombatId = 0;
                _localDragSessionId = 0;
            }
        }

        BroadcastDrag(message);
        DragAuthoritative?.Invoke(message);
    }

    private static bool TryAcquireDrag(uint combatId, ulong ownerNetId, ulong sessionId)
    {
        if (HostDragLeases.ContainsKey(combatId))
            return false;

        HostDragLeases[combatId] = new HostDragLease(ownerNetId, sessionId);
        return true;
    }

    private static void RejectDrag(CreatureDragMessage request)
    {
        if (Positions.TryGetValue(request.combatId, out SerializableVector2 saved))
        {
            Vector2 position = saved.ToVector2();
            request.x = position.X;
            request.y = position.Y;
        }

        request.phase = CreatureDragPhase.Cancel;
        request.ownerNetId = request.ownerNetId == 0 ? LocalNetId : request.ownerNetId;
        request.sequence = ++_dragSequence;
        BroadcastDrag(request);
        DragAuthoritative?.Invoke(request);
    }

    private static void BroadcastDrag(CreatureDragMessage message)
    {
        if (_netService?.Type != NetGameType.Host)
            return;

        LoadoutNetworkBroadcast.SendToRunClients(
            _netService,
            recipient => _netService.SendMessage(message, recipient),
            $"creature drag {message.phase} #{message.sequence}");
    }

    private static void ApplyAuthoritativeDrag(CreatureDragMessage message)
    {
        if (LastDragSequence.TryGetValue(message.combatId, out int last)
            && message.sequence <= last)
        {
            return;
        }

        LastDragSequence[message.combatId] = message.sequence;
        Vector2 position = new(message.x, message.y);
        Positions[message.combatId] = SerializableVector2.From(position);
        bool isLocalActiveDrag = message.combatId == _localDragCombatId
                                 && message.sessionId == _localDragSessionId
                                 && message.ownerNetId == LocalNetId;
        if ((!isLocalActiveDrag || message.phase is CreatureDragPhase.End or CreatureDragPhase.Cancel)
            && CombatManager.Instance.DebugOnlyGetState()?.GetCreature(message.combatId) is { } target)
        {
            ApplyCreaturePosition(target, position);
        }
        if (isLocalActiveDrag && message.phase is CreatureDragPhase.End or CreatureDragPhase.Cancel)
        {
            _localDragCombatId = 0;
            _localDragSessionId = 0;
        }
        DragAuthoritative?.Invoke(message);
    }

    private static void ApplyCreaturePosition(Creature target, Vector2 position)
    {
        if (NCombatRoom.Instance?.GetCreatureNode(target) is { } node)
            node.Position = position;
    }

    private static void OnCombatSetUp(CombatState _)
    {
        ClearCombatState(resetEpoch: false);
        if (_netService?.Type == NetGameType.Client)
        {
            _clientCombatSetUpSeen = true;
            _combatEpoch = 0;
            if (_pendingSnapshot is { } pending
                && pending.CombatEpoch > _lastCompletedEpoch)
            {
                ApplySnapshot(pending);
                _pendingSnapshot = null;
            }
        }
        else
        {
            _combatEpoch = Math.Max(_combatEpoch, _lastCompletedEpoch) + 1;
        }

        if (_netService?.Type == NetGameType.Host)
            BroadcastSnapshot();
        StateChanged?.Invoke();
    }

    private static void OnCombatEnded(CombatRoom _)
    {
        CreatureManipulationUiService.Clear();
        _lastCompletedEpoch = Math.Max(_lastCompletedEpoch, _combatEpoch);
        _clientCombatSetUpSeen = false;
        ClearCombatState(resetEpoch: false);
        if (_netService?.Type == NetGameType.Client)
            _combatEpoch = 0;
        StateChanged?.Invoke();
    }

    private static void OnAccessChanged()
    {
        if (_netService?.Type == NetGameType.Host && !LoadoutPanelAccessService.HostAllowsGuests)
        {
            foreach ((uint combatId, HostDragLease lease) in HostDragLeases
                         .Where(pair => pair.Value.OwnerNetId != _netService.NetId)
                         .ToList())
            {
                ReleaseDragLease(combatId, lease);
            }
        }

        if (!LoadoutPanelAccessService.CanLocalPlayerUsePanel())
            DragAuthoritative?.Invoke(new CreatureDragMessage { phase = CreatureDragPhase.Cancel });
    }

    private static void RegisterNetService(INetGameService netService)
    {
        if (ReferenceEquals(_netService, netService))
            return;

        UnregisterNetService();
        _netService = netService;
        _netService.RegisterMessageHandler<CreatureManipulationRequestMessage>(HandleRequest);
        _netService.RegisterMessageHandler<CreatureDragMessage>(HandleDrag);
        _netService.RegisterMessageHandler<CreatureManipulationSnapshotMessage>(HandleSnapshot);
        BindRunLobby(RunManager.Instance.RunLobby);
    }

    private static void UnregisterNetService()
    {
        if (_netService is null)
            return;

        _netService.UnregisterMessageHandler<CreatureManipulationRequestMessage>(HandleRequest);
        _netService.UnregisterMessageHandler<CreatureDragMessage>(HandleDrag);
        _netService.UnregisterMessageHandler<CreatureManipulationSnapshotMessage>(HandleSnapshot);
        _netService = null;
    }

    private static void BindRunLobby(RunLobby? lobby)
    {
        if (ReferenceEquals(_runLobby, lobby))
            return;
        UnbindRunLobby();
        _runLobby = lobby;
        if (_runLobby is not null)
        {
            _playerRejoinedHandler = Sts2Compatibility.SubscribeRunLobbyPlayerRejoined(
                _runLobby,
                OnPlayerRejoined);
            _runLobby.RemotePlayerDisconnected += OnRemotePlayerDisconnected;
            _runLobby.LocalPlayerDisconnected += OnLocalPlayerDisconnected;
        }
    }

    private static void UnbindRunLobby()
    {
        if (_runLobby is null)
            return;
        if (_playerRejoinedHandler is not null)
            Sts2Compatibility.UnsubscribeRunLobbyPlayerRejoined(_runLobby, _playerRejoinedHandler);

        _playerRejoinedHandler = null;
        _runLobby.RemotePlayerDisconnected -= OnRemotePlayerDisconnected;
        _runLobby.LocalPlayerDisconnected -= OnLocalPlayerDisconnected;
        _runLobby = null;
    }

    private static void OnPlayerRejoined(ulong playerId)
    {
        if (_netService?.Type == NetGameType.Host && playerId != _netService.NetId)
            SendSnapshot(playerId);
    }

    private static void OnRemotePlayerDisconnected(ulong playerId)
    {
        foreach ((uint combatId, HostDragLease lease) in HostDragLeases
                     .Where(pair => pair.Value.OwnerNetId == playerId)
                     .ToList())
        {
            ReleaseDragLease(combatId, lease);
        }
    }

    private static void OnLocalPlayerDisconnected()
    {
        HostDragLeases.Clear();
        DragAuthoritative?.Invoke(new CreatureDragMessage { phase = CreatureDragPhase.Cancel });
    }

    private static void ReleaseDragLease(uint combatId, HostDragLease lease)
    {
        HostDragLeases.Remove(combatId);
        Vector2 position = Positions.TryGetValue(combatId, out SerializableVector2 saved)
            ? saved.ToVector2()
            : Vector2.Zero;
        CreatureDragMessage cancel = new()
        {
            combatEpoch = _combatEpoch,
            combatId = combatId,
            sessionId = lease.SessionId,
            ownerNetId = lease.OwnerNetId,
            phase = CreatureDragPhase.Cancel,
            sequence = ++_dragSequence,
            x = position.X,
            y = position.Y
        };
        BroadcastDrag(cancel);
        DragAuthoritative?.Invoke(cancel);
    }

    private static void BroadcastSnapshot()
    {
        if (_netService?.Type != NetGameType.Host)
            return;
        LoadoutNetworkBroadcast.SendToRunClients(
            _netService,
            SendSnapshot,
            "creature manipulation combat snapshot");
    }

    private static void SendSnapshot(ulong recipient)
    {
        if (_netService?.Type != NetGameType.Host)
            return;

        CreatureManipulationCombatSnapshot snapshot = new()
        {
            CombatEpoch = _combatEpoch,
            Positions = new Dictionary<uint, SerializableVector2>(Positions),
            Locks = Locks.ToDictionary(
                pair => pair.Key,
                pair => new CreatureStatLockSnapshot
                {
                    CurrentHp = pair.Value.CurrentHp,
                    MaxHp = pair.Value.MaxHp,
                    Block = pair.Value.Block
                })
        };
        _netService.SendMessage(new CreatureManipulationSnapshotMessage
        {
            snapshotJson = JsonSerializer.Serialize(snapshot, JsonOptions)
        }, recipient);
    }

    private static void HandleSnapshot(CreatureManipulationSnapshotMessage message, ulong senderId)
    {
        if (_netService?.Type != NetGameType.Client
            || !LoadoutNetworkBroadcast.IsExpectedHostSender(senderId, _netService))
        {
            return;
        }

        try
        {
            CreatureManipulationCombatSnapshot? snapshot =
                JsonSerializer.Deserialize<CreatureManipulationCombatSnapshot>(
                    message.snapshotJson,
                    JsonOptions);
            if (snapshot is null
                || snapshot.CombatEpoch <= _lastCompletedEpoch
                || snapshot.CombatEpoch < _combatEpoch)
                return;

            if (!_clientCombatSetUpSeen)
            {
                _pendingSnapshot = snapshot;
                return;
            }

            ApplySnapshot(snapshot);
        }
        catch (Exception exception)
        {
            GD.PushWarning($"CreatureManipulation: invalid combat snapshot. {exception.Message}");
        }
    }

    private static void ClearCombatState(bool resetEpoch)
    {
        Positions.Clear();
        Locks.Clear();
        HostDragLeases.Clear();
        LastDragSequence.Clear();
        _dragSequence = 0;
        _localDragCombatId = 0;
        _localDragSessionId = 0;
        if (resetEpoch)
        {
            _combatEpoch = 0;
            _lastCompletedEpoch = 0;
            _pendingSnapshot = null;
            _clientCombatSetUpSeen = false;
        }
        TildeKeyStateService.RefreshDynamicLockPatches();
    }

    private static void ApplySnapshot(CreatureManipulationCombatSnapshot snapshot)
    {
        _combatEpoch = snapshot.CombatEpoch;
        Positions.Clear();
        foreach ((uint id, SerializableVector2 position) in snapshot.Positions)
            Positions[id] = position;
        Locks.Clear();
        foreach ((uint id, CreatureStatLockSnapshot entry) in snapshot.Locks)
            Locks[id] = entry;

        if (CombatManager.Instance.DebugOnlyGetState() is { } combatState)
        {
            foreach ((uint id, SerializableVector2 position) in Positions)
            {
                if (combatState.GetCreature(id) is { } creature)
                    ApplyCreaturePosition(creature, position.ToVector2());
            }
        }

        TildeKeyStateService.RefreshDynamicLockPatches();
        StateChanged?.Invoke();
    }

    private static Player? GetLocalPlayer()
    {
        try
        {
            return RunManager.Instance.IsInProgress
                ? LocalContext.GetMe(RunManager.Instance.DebugOnlyGetState())
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static Player? GetRunPlayer(ulong netId)
    {
        try
        {
            return RunManager.Instance.DebugOnlyGetState()?.GetPlayer(netId);
        }
        catch
        {
            return null;
        }
    }

    private static PowerModel? ResolvePower(string modelId) =>
        ModelDb.AllPowers.FirstOrDefault(power => SameModelId(power.Id, modelId));

    private static bool SameModelId(ModelId modelId, string raw) =>
        string.Equals(modelId.ToString(), raw, StringComparison.Ordinal)
        || string.Equals(modelId.Entry, raw, StringComparison.OrdinalIgnoreCase);

    private readonly record struct HostDragLease(ulong OwnerNetId, ulong SessionId);
}
