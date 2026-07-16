#nullable enable

namespace Loadout.Services.CardModification;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using BaseLib.Abstracts;
using Godot;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Runs;
using Loadout.Services.Targets;
using Loadout.Services.Actions;
using Loadout.Services.Loadouts;
using Loadout.Services.Networking;
using Loadout.Patches.Cards.CardModification;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;

public static class CardModificationNetProtocol
{
    private const int MaxStateJsonLength = 256 * 1024;

    private static readonly HashSet<StartRunLobby> RegisteredLobbies = [];
    private static readonly Dictionary<StartRunLobby, Action<LobbyPlayer>> LobbyConnectedHandlers = new();
    private static readonly object OperationSequenceGate = new();
    private static readonly SortedDictionary<int, LoadoutCardModificationOperationPayload> PendingOperationApplies = new();
    private static INetGameService? _runNetService;
    private static bool _registered;
    private static string? _pendingHostPermanentSnapshotJson;
    private static int _nextOperationSequence;
    private static int _lastAppliedOperationSequence;
    private static bool _operationApplyQueued;

    public static event Action? HostPermanentSnapshotAvailable;

    public static bool HasPendingHostPermanentSnapshot => !string.IsNullOrWhiteSpace(_pendingHostPermanentSnapshotJson);

    public static void Register()
    {
        _registered = true;
    }

    public static void Unregister()
    {
        foreach (StartRunLobby lobby in new List<StartRunLobby>(RegisteredLobbies))
            UnregisterLobby(lobby, clearClientOverlay: false);

        RegisteredLobbies.Clear();
        LobbyConnectedHandlers.Clear();
        UnregisterRunNetService(clearClientOverlay: true);
        PermanentCardModificationStore.ClearHostOverlay();
        ClearPendingHostPermanentSnapshot();
        _registered = false;
    }

    public static void RegisterLobby(StartRunLobby? lobby)
    {
        if (!_registered || lobby is null || !RegisteredLobbies.Add(lobby))
            return;

        lobby.NetService.RegisterMessageHandler<LoadoutCardModificationPermanentSyncMessage>(HandlePermanentSync);
        lobby.NetService.RegisterMessageHandler<LoadoutCardModificationTemporarySyncMessage>(HandleTemporarySync);

        Action<LobbyPlayer> connected = player => SendPermanentSnapshotToLobbyPlayer(lobby, player.id);
        LobbyConnectedHandlers[lobby] = connected;
        lobby.PlayerConnected += connected;

        if (lobby.NetService.Type == NetGameType.Host)
        {
            foreach (LobbyPlayer player in lobby.Players)
            {
                if (player.id != lobby.NetService.NetId)
                    SendPermanentSnapshotToLobbyPlayer(lobby, player.id);
            }
        }
    }

    public static void UnregisterLobby(StartRunLobby? lobby, bool clearClientOverlay = false)
    {
        if (lobby is null || !RegisteredLobbies.Remove(lobby))
            return;

        lobby.NetService.UnregisterMessageHandler<LoadoutCardModificationPermanentSyncMessage>(HandlePermanentSync);
        lobby.NetService.UnregisterMessageHandler<LoadoutCardModificationTemporarySyncMessage>(HandleTemporarySync);

        if (LobbyConnectedHandlers.Remove(lobby, out Action<LobbyPlayer>? connected))
            lobby.PlayerConnected -= connected;

        if (clearClientOverlay && lobby.NetService.Type == NetGameType.Client)
        {
            PermanentCardModificationStore.ClearHostOverlay();
            ClearPendingHostPermanentSnapshot();
        }
    }

    public static void OnRunLaunched()
    {
        try
        {
            INetGameService netService = RunManager.Instance.NetService;
            if (netService is null)
                return;

            // Reset before registering the run handler. Buffered snapshot messages can be
            // delivered as part of registration; resetting afterward would erase the
            // sequence baseline supplied by the host.
            lock (OperationSequenceGate)
            {
                _nextOperationSequence = 0;
                _lastAppliedOperationSequence = 0;
                _operationApplyQueued = false;
                PendingOperationApplies.Clear();
            }

            RegisterRunNetService(netService);

            if (netService.Type == NetGameType.Host)
                BroadcastPermanentSnapshot();
            else if (netService.Type is NetGameType.Singleplayer or NetGameType.Replay)
                PermanentCardModificationStore.ClearHostOverlay();
        }
        catch (Exception exception)
        {
            GD.PushWarning($"CardModification: failed to initialize multiplayer sync for run. {exception.Message}");
        }
    }

    public static void OnRunCleaningUp()
    {
        PermanentCardModificationStore.FlushPendingSave();
        UnregisterRunNetService(clearClientOverlay: true);
        lock (OperationSequenceGate)
        {
            _nextOperationSequence = 0;
            _lastAppliedOperationSequence = 0;
            _operationApplyQueued = false;
            PendingOperationApplies.Clear();
        }
    }

    public static bool RequestOperation(
        CardModificationOperation operation,
        LoadoutOwnedItem<CardModel> item,
        CardModificationSpec? state = null)
    {
        if (!LoadoutPanelAccessService.CanLocalPlayerUsePanel())
            return false;

        try
        {
            INetGameService netService = RunManager.Instance.NetService;
            Player? localPlayer = GetRunPlayer(netService.NetId) ?? GetLocalRunPlayer();
            if (localPlayer is null)
                return false;

            if (netService.Type is NetGameType.Singleplayer or NetGameType.Replay)
            {
                CardModificationRuntime.ApplySynchronizedOperation(
                    operation,
                    item.Model.Id,
                    LoadoutTargetSelection.ForPlayer(item.OwnerNetId),
                    item.Index,
                    item.Model.Id,
                    state,
                    localPlayer,
                    authoritativeRemote: false);
                return true;
            }

            LoadoutCardModificationOperationPayload payload = new()
            {
                Operation = operation,
                RequesterNetId = localPlayer.NetId,
                OwnerNetId = item.OwnerNetId,
                DeckIndex = item.Index,
                CardId = item.Model.Id.ToString(),
                StateJson = state is null || state.IsEmpty
                    ? string.Empty
                    : CardModificationCodec.Serialize(state)
            };
            if (!IsValidOperationPayload(payload))
                return false;

            if (netService.Type == NetGameType.Client)
            {
                CustomMessageWrapper.Send(new LoadoutCardModificationOperationRequestMessage
                {
                    Payload = payload
                }, netService);
                return true;
            }

            PublishOperation(payload, netService);
            return true;
        }
        catch (Exception exception)
        {
            GD.PushWarning($"CardModification: failed to request {operation}. {exception.Message}");
            return false;
        }
    }

    internal static void HandleOperationRequest(
        LoadoutCardModificationOperationRequestMessage message,
        ulong senderId)
    {
        try
        {
            INetGameService? netService = _runNetService ?? RunManager.Instance.NetService;
            if (netService?.Type != NetGameType.Host
                || !LoadoutPanelAccessService.CanRequesterUsePanel(senderId))
            {
                return;
            }

            LoadoutCardModificationOperationPayload payload = message.Payload;
            payload.RequesterNetId = senderId;
            if (!IsValidOperationPayload(payload))
            {
                GD.PushWarning($"CardModification: rejected malformed {payload.Operation} request from peer {senderId}.");
                return;
            }

            PublishOperation(payload, netService);
        }
        catch (Exception exception)
        {
            GD.PushWarning($"CardModification: failed to handle operation request. {exception.Message}");
        }
    }

    internal static void HandleOperationApply(
        LoadoutCardModificationOperationApplyMessage message,
        ulong senderId)
    {
        if (IsHostSession() || !IsExpectedHostSender(senderId))
            return;

        lock (OperationSequenceGate)
        {
            if (message.Sequence <= _lastAppliedOperationSequence)
                return;

            PendingOperationApplies[message.Sequence] = message.Payload;
        }

        TryScheduleNextOperationApply();
    }

    internal static void HandlePermanentDelta(
        LoadoutCardModificationPermanentDeltaMessage message,
        ulong senderId)
    {
        if (IsHostSession() || !IsExpectedHostSender(senderId))
            return;

        if (string.IsNullOrWhiteSpace(message.CardId)
            || message.StateJson?.Length > MaxStateJsonLength
            || !TryDeserializeState(message.StateJson, out CardModificationSpec? state))
        {
            GD.PushWarning("CardModification: ignored malformed permanent delta from host.");
            return;
        }

        if (!LoadoutModelRegistry.TryResolveWireId(message.CardId, out ModelId cardId))
            return;

        CardModificationSpec previous = PermanentCardModificationStore.Get(cardId);
        if (PermanentCardModificationStore.ApplyHostDelta(cardId, state))
            CardModificationRuntime.RetrofitLiveDeckCopies(cardId, previous);
    }

    public static void BroadcastPermanentDelta(ModelId cardId, CardModificationSpec? state)
    {
        try
        {
            if (!RunManager.Instance.IsInProgress || RunManager.Instance.NetService.Type != NetGameType.Host)
                return;

            LoadoutCardModificationPermanentDeltaMessage message = new()
            {
                CardId = cardId.ToString(),
                StateJson = state is null || state.IsEmpty
                    ? string.Empty
                    : CardModificationCodec.Serialize(state)
            };
            INetGameService netService = RunManager.Instance.NetService;
            LoadoutNetworkBroadcast.SendToRunClients(
                netService,
                recipient => netService.SendMessage(new CustomMessageWrapper { Message = message }, recipient),
                $"card modification permanent delta {cardId}");
        }
        catch (Exception exception)
        {
            GD.PushWarning($"CardModification: failed to broadcast permanent delta. {exception.Message}");
        }
    }

    private static void PublishOperation(
        LoadoutCardModificationOperationPayload payload,
        INetGameService netService)
    {
        if (!IsValidOperationPayload(payload))
            return;

        lock (OperationSequenceGate)
            payload.Sequence = ++_nextOperationSequence;

        LoadoutMutationSerialExecutor.Enqueue(() =>
        {
            ApplyOperation(payload, authoritativeRemote: false);

            if (netService.Type == NetGameType.Host)
            {
                LoadoutCardModificationOperationApplyMessage message = new()
                {
                    Sequence = payload.Sequence,
                    Payload = payload
                };
                LoadoutNetworkBroadcast.SendToRunClients(
                    netService,
                    recipient => netService.SendMessage(new CustomMessageWrapper { Message = message }, recipient),
                    $"card modification {payload.Operation} #{payload.Sequence}");
            }

            return Task.CompletedTask;
        }, $"card modification {payload.Operation} #{payload.Sequence}");
    }

    private static void TryScheduleNextOperationApply()
    {
        int sequence;
        LoadoutCardModificationOperationPayload payload;
        lock (OperationSequenceGate)
        {
            if (_operationApplyQueued)
                return;

            int expectedSequence = _lastAppliedOperationSequence + 1;
            if (!PendingOperationApplies.TryGetValue(expectedSequence, out payload))
                return;

            sequence = expectedSequence;
            PendingOperationApplies.Remove(sequence);
            _operationApplyQueued = true;
        }

        LoadoutMutationSerialExecutor.Enqueue(() =>
        {
            bool shouldApply;
            lock (OperationSequenceGate)
                shouldApply = sequence > _lastAppliedOperationSequence;

            // A buffered snapshot can establish a newer baseline while this item is
            // waiting in the shared executor. Never replay an operation already
            // represented by that snapshot.
            if (shouldApply)
                ApplyOperation(payload, authoritativeRemote: true);

            lock (OperationSequenceGate)
            {
                _lastAppliedOperationSequence = Math.Max(_lastAppliedOperationSequence, sequence);
                _operationApplyQueued = false;
            }

            TryScheduleNextOperationApply();
            return Task.CompletedTask;
        }, $"remote card modification {payload.Operation} #{sequence}");
    }

    private static void ApplyOperation(
        LoadoutCardModificationOperationPayload payload,
        bool authoritativeRemote)
    {
        if (!IsValidOperationPayload(payload))
        {
            GD.PushWarning($"CardModification: ignored malformed {payload.Operation} payload.");
            return;
        }

        if (!LoadoutModelRegistry.TryResolveWireId(payload.CardId, out ModelId cardId))
        {
            GD.PushWarning($"CardModification: ignored unknown card id '{payload.CardId}'.");
            return;
        }

        Player? actionPlayer = GetRunPlayer(payload.RequesterNetId) ?? GetLocalRunPlayer();
        if (actionPlayer is null)
            return;

        if (!TryDeserializeState(payload.StateJson, out CardModificationSpec? state))
        {
            GD.PushWarning($"CardModification: ignored invalid state JSON for {payload.Operation}.");
            return;
        }

        CardModificationRuntime.ApplySynchronizedOperation(
            payload.Operation,
            cardId,
            LoadoutTargetSelection.ForPlayer(payload.OwnerNetId),
            payload.DeckIndex,
            cardId,
            state,
            actionPlayer,
            authoritativeRemote);
    }

    private static bool IsValidOperationPayload(LoadoutCardModificationOperationPayload payload)
    {
        return (payload.Operation is CardModificationOperation.SaveTemporary
                    or CardModificationOperation.ResetTemporary
                    or CardModificationOperation.ResetTemporaryToBasic
                    or CardModificationOperation.ApplyPermanent
                    or CardModificationOperation.ResetPermanentToBasic)
               && payload.RequesterNetId != 0
               && payload.OwnerNetId != 0
               && payload.DeckIndex >= 0
               && !string.IsNullOrWhiteSpace(payload.CardId)
               && (payload.StateJson?.Length ?? 0) <= MaxStateJsonLength;
    }

    private static bool TryDeserializeState(string? stateJson, out CardModificationSpec? state)
    {
        state = null;
        if (string.IsNullOrWhiteSpace(stateJson))
            return true;

        if (stateJson.Length > MaxStateJsonLength)
            return false;

        try
        {
            return CardModificationCodec.TryDeserialize(stateJson, out state);
        }
        catch (Exception exception)
        {
            GD.PushWarning($"CardModification: failed to deserialize state delta. {exception.Message}");
            return false;
        }
    }

    private static Player? GetLocalRunPlayer()
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
            return RunManager.Instance.IsInProgress
                ? RunManager.Instance.DebugOnlyGetState()?.GetPlayer(netId)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static Dictionary<ModelId, CardModificationSpec> CaptureCurrentPermanentSpecs()
    {
        Dictionary<ModelId, CardModificationSpec> result = new();
        foreach (CardModel card in ModelDb.AllCards)
        {
            if (PermanentCardModificationStore.TryGet(card.Id, out CardModificationSpec? spec))
                result[card.Id] = spec.Clone();
        }
        return result;
    }

    public static void BroadcastPermanentSnapshot()
    {
        string payload = PermanentCardModificationStore.ExportEffectiveSnapshotJson();
        LoadoutCardModificationPermanentSyncMessage message = CreatePermanentSnapshotMessage(payload);

        foreach (StartRunLobby lobby in RegisteredLobbies)
        {
            if (lobby.NetService.Type != NetGameType.Host)
                continue;

            foreach (LobbyPlayer player in lobby.Players)
            {
                if (player.id != lobby.NetService.NetId)
                    SendPermanentSnapshotToLobbyPlayer(lobby, player.id);
            }
        }

        try
        {
            INetGameService netService = RunManager.Instance.NetService;
            if (RunManager.Instance.IsInProgress && netService.Type == NetGameType.Host)
            {
                LoadoutNetworkBroadcast.SendToRunClients(
                    netService,
                    recipient => netService.SendMessage(message, recipient),
                    "card modification permanent snapshot");
            }
        }
        catch
        {
            // There may be no active run service while still in the lobby.
        }
    }

    public static void BroadcastTemporary(LoadoutOwnedItem<CardModel> item, CardModificationSpec? next)
    {
        try
        {
            if (!RunManager.Instance.IsInProgress || RunManager.Instance.NetService.Type != NetGameType.Host)
                return;

            LoadoutCardModificationTemporarySyncMessage message = new()
            {
                ownerNetId = item.OwnerNetId,
                deckIndex = item.Index,
                cardId = item.Model.Id.ToString(),
                stateJson = next is null || next.IsEmpty
                    ? string.Empty
                    : CardModificationCodec.Serialize(next)
            };
            INetGameService netService = RunManager.Instance.NetService;
            LoadoutNetworkBroadcast.SendToRunClients(
                netService,
                recipient => netService.SendMessage(message, recipient),
                "card modification temporary snapshot");
        }
        catch (Exception exception)
        {
            GD.PushWarning($"CardModification: failed to broadcast temporary card modification. {exception.Message}");
        }
    }

    private static void RegisterRunNetService(INetGameService netService)
    {
        if (_runNetService == netService)
            return;

        UnregisterRunNetService(clearClientOverlay: false);
        _runNetService = netService;
        _runNetService.RegisterMessageHandler<LoadoutCardModificationPermanentSyncMessage>(HandlePermanentSync);
        _runNetService.RegisterMessageHandler<LoadoutCardModificationTemporarySyncMessage>(HandleTemporarySync);
    }

    private static void UnregisterRunNetService(bool clearClientOverlay)
    {
        if (_runNetService is null)
            return;

        NetGameType type = _runNetService.Type;
        _runNetService.UnregisterMessageHandler<LoadoutCardModificationPermanentSyncMessage>(HandlePermanentSync);
        _runNetService.UnregisterMessageHandler<LoadoutCardModificationTemporarySyncMessage>(HandleTemporarySync);
        _runNetService = null;

        if (clearClientOverlay && type == NetGameType.Client)
        {
            PermanentCardModificationStore.ClearHostOverlay();
            ClearPendingHostPermanentSnapshot();
        }
    }

    private static LoadoutCardModificationPermanentSyncMessage CreatePermanentSnapshotMessage(string payload)
    {
        int operationSequence;
        lock (OperationSequenceGate)
            operationSequence = _nextOperationSequence;

        return new LoadoutCardModificationPermanentSyncMessage
        {
            payload = payload,
            operationSequence = operationSequence
        };
    }

    private static void EstablishClientOperationSequenceBaseline(int sequence)
    {
        if (sequence < 0)
            return;

        lock (OperationSequenceGate)
        {
            if (sequence <= _lastAppliedOperationSequence)
                return;

            _lastAppliedOperationSequence = sequence;
            foreach (int staleSequence in PendingOperationApplies.Keys
                         .Where(pendingSequence => pendingSequence <= sequence)
                         .ToList())
            {
                PendingOperationApplies.Remove(staleSequence);
            }
        }

        // An apply packet may have arrived before the reliable buffered snapshot.
        // The snapshot supplies the exact baseline, allowing the next queued delta
        // to proceed without waiting for operations that predate this client.
        TryScheduleNextOperationApply();
    }

    private static void SendPermanentSnapshotToLobbyPlayer(StartRunLobby lobby, ulong playerId)
    {
        if (lobby.NetService.Type != NetGameType.Host || playerId == lobby.NetService.NetId)
            return;

        lobby.NetService.SendMessage(
            CreatePermanentSnapshotMessage(PermanentCardModificationStore.ExportEffectiveSnapshotJson()),
            playerId);
    }

    private static void HandlePermanentSync(LoadoutCardModificationPermanentSyncMessage message, ulong senderId)
    {
        if (IsHostSession() || !IsExpectedHostSender(senderId))
            return;

        EstablishClientOperationSequenceBaseline(message.operationSequence);
        StorePendingHostPermanentSnapshot(message.payload);
        HostPermanentSnapshotApplyMode applyMode = RunManager.Instance.IsInProgress
            ? HostPermanentSnapshotApplyMode.LiveDecks
            : HostPermanentSnapshotApplyMode.CatalogOnly;
        Dictionary<ModelId, CardModificationSpec> previous = CaptureCurrentPermanentSpecs();
        IReadOnlyList<ModelId> changed = PermanentCardModificationStore.ApplyHostSnapshot(message.payload);
        if (applyMode == HostPermanentSnapshotApplyMode.LiveDecks)
            CardModificationRuntime.RetrofitChangedPermanentCards(changed, previous);
    }

    private static void HandleTemporarySync(LoadoutCardModificationTemporarySyncMessage message, ulong senderId)
    {
        if (IsHostSession() || !IsExpectedHostSender(senderId))
            return;

        if ((message.stateJson?.Length ?? 0) > MaxStateJsonLength
            || !TryDeserializeState(message.stateJson, out CardModificationSpec? state))
        {
            GD.PushWarning("CardModification: ignored malformed temporary multiplayer state.");
            return;
        }

        CardModificationRuntime.ApplyRemoteTemporaryState(
            message.ownerNetId,
            message.deckIndex,
            message.cardId,
            state);
    }

    private static bool IsHostSession()
    {
        if (_runNetService?.Type == NetGameType.Host)
            return true;

        foreach (StartRunLobby lobby in RegisteredLobbies)
        {
            if (lobby.NetService.Type == NetGameType.Host)
                return true;
        }

        try
        {
            return RunManager.Instance.NetService.Type == NetGameType.Host;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsExpectedHostSender(ulong senderId)
    {
        return LoadoutNetworkBroadcast.IsExpectedHostSender(
            senderId,
            _runNetService,
            RegisteredLobbies.Select(lobby => lobby.NetService));
    }

    public static IReadOnlyList<ModelId> ApplyPendingHostPermanentSnapshot(CardModificationPermanentImportMode mode)
    {
        string? snapshot = _pendingHostPermanentSnapshotJson;
        ClearPendingHostPermanentSnapshot();
        Dictionary<ModelId, CardModificationSpec> previous = CaptureCurrentPermanentSpecs();
        IReadOnlyList<ModelId> changed = PermanentCardModificationStore.ImportSnapshotToProfile(snapshot, mode);
        if (RunManager.Instance.IsInProgress)
            CardModificationRuntime.RetrofitChangedPermanentCards(changed, previous);
        return changed;
    }

    public static void ClearPendingHostPermanentSnapshot()
    {
        _pendingHostPermanentSnapshotJson = null;
    }

    private static void StorePendingHostPermanentSnapshot(string? payload)
    {
        _pendingHostPermanentSnapshotJson = payload;
        HostPermanentSnapshotAvailable?.Invoke();
    }
}

public struct LoadoutCardModificationOperationPayload
{
    public int Sequence;
    public CardModificationOperation Operation;
    public ulong RequesterNetId;
    public ulong OwnerNetId;
    public int DeckIndex;
    public string CardId;
    public string StateJson;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteInt(Sequence);
        writer.WriteInt((int)Operation, 8);
        writer.WriteULong(RequesterNetId);
        writer.WriteULong(OwnerNetId);
        writer.WriteInt(DeckIndex);
        writer.WriteString(CardId ?? string.Empty);
        writer.WriteString(StateJson ?? string.Empty);
    }

    public void Deserialize(PacketReader reader)
    {
        Sequence = reader.ReadInt();
        Operation = (CardModificationOperation)reader.ReadInt(8);
        RequesterNetId = reader.ReadULong();
        OwnerNetId = reader.ReadULong();
        DeckIndex = reader.ReadInt();
        CardId = reader.ReadString();
        StateJson = reader.ReadString();
    }
}

public struct LoadoutCardModificationOperationRequestMessage : ICustomMessage
{
    public LoadoutCardModificationOperationPayload Payload;

    public bool ShouldBroadcast => false;

    public void HandleMessage(ulong senderId)
    {
        CardModificationNetProtocol.HandleOperationRequest(this, senderId);
    }

    public void Serialize(PacketWriter writer)
    {
        Payload.Serialize(writer);
    }

    public void Deserialize(PacketReader reader)
    {
        Payload = new LoadoutCardModificationOperationPayload();
        Payload.Deserialize(reader);
    }
}

public struct LoadoutCardModificationOperationApplyMessage : ICustomMessage
{
    public int Sequence;
    public LoadoutCardModificationOperationPayload Payload;

    public bool ShouldBroadcast => false;

    public void HandleMessage(ulong senderId)
    {
        CardModificationNetProtocol.HandleOperationApply(this, senderId);
    }

    public void Serialize(PacketWriter writer)
    {
        writer.WriteInt(Sequence);
        Payload.Serialize(writer);
    }

    public void Deserialize(PacketReader reader)
    {
        Sequence = reader.ReadInt();
        Payload = new LoadoutCardModificationOperationPayload();
        Payload.Deserialize(reader);
        Payload.Sequence = Sequence;
    }
}

public struct LoadoutCardModificationPermanentDeltaMessage : ICustomMessage
{
    public string CardId;
    public string StateJson;

    public bool ShouldBroadcast => false;

    public void HandleMessage(ulong senderId)
    {
        CardModificationNetProtocol.HandlePermanentDelta(this, senderId);
    }

    public void Serialize(PacketWriter writer)
    {
        writer.WriteString(CardId ?? string.Empty);
        writer.WriteString(StateJson ?? string.Empty);
    }

    public void Deserialize(PacketReader reader)
    {
        CardId = reader.ReadString();
        StateJson = reader.ReadString();
    }
}

public struct LoadoutCardModificationPermanentSyncMessage : INetMessage, IPacketSerializable
{
    public string payload;
    public int operationSequence;

    public bool ShouldBroadcast => false;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.VeryDebug;
    public bool ShouldBuffer => true;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteString(payload ?? string.Empty);
        writer.WriteInt(operationSequence);
    }

    public void Deserialize(PacketReader reader)
    {
        payload = reader.ReadString();
        operationSequence = reader.ReadInt();
    }
}

public struct LoadoutCardModificationTemporarySyncMessage : INetMessage, IPacketSerializable
{
    public ulong ownerNetId;
    public int deckIndex;
    public string cardId;
    public string stateJson;

    public bool ShouldBroadcast => false;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.VeryDebug;
    public bool ShouldBuffer => true;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteULong(ownerNetId);
        writer.WriteInt(deckIndex);
        writer.WriteString(cardId ?? string.Empty);
        writer.WriteString(stateJson ?? string.Empty);
    }

    public void Deserialize(PacketReader reader)
    {
        ownerNetId = reader.ReadULong();
        deckIndex = reader.ReadInt();
        cardId = reader.ReadString();
        stateJson = reader.ReadString();
    }
}
