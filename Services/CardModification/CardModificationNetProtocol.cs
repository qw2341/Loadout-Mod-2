#nullable enable

namespace Loadout.Services.CardModification;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using BaseLib.Abstracts;
using Godot;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Runs;
using Loadout.Services.Targets;
using Loadout.Services.Actions;
using Loadout.Services.Compatibility;
using Loadout.Services.Loadouts;
using Loadout.Services.Networking;
using Loadout.Patches.Cards.CardModification;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Cards;

public static class CardModificationNetProtocol
{
    private const int MaxStateJsonLength = 256 * 1024;
    private const int MaxFullSnapshotJsonLength = 1024 * 1024;
    private const int FullSnapshotSchemaVersion = 1;

    private static readonly HashSet<StartRunLobby> RegisteredLobbies = [];
    private static readonly Dictionary<StartRunLobby, Delegate> LobbyConnectedHandlers = new();
    private static readonly HashSet<INetGameService> RegisteredMessageServices = [];
    private static readonly object OperationSequenceGate = new();
    private static readonly SortedDictionary<int, LoadoutCardModificationOperationPayload> PendingOperationApplies = new();
    private static INetGameService? _runNetService;
    private static RunLobby? _runLobby;
    private static Delegate? _playerRejoinedHandler;
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
        foreach (INetGameService netService in RegisteredMessageServices.ToList())
            UnregisterMessageHandlers(netService);
        PermanentCardModificationStore.ClearHostOverlay();
        ClearPendingHostPermanentSnapshot();
        _registered = false;
    }

    public static void RegisterLobby(StartRunLobby? lobby)
    {
        if (!_registered || lobby is null || !RegisteredLobbies.Add(lobby))
            return;

        RegisterMessageHandlers(lobby.NetService);

        Delegate connected = Sts2Compatibility.SubscribeStartRunLobbyPlayerConnected(
            lobby,
            playerId => SendPermanentSnapshotToLobbyPlayer(lobby, playerId));
        LobbyConnectedHandlers[lobby] = connected;

        if (lobby.NetService.Type == NetGameType.Host)
        {
            foreach (ulong playerId in Sts2Compatibility.EnumerateStartRunLobbyPlayerIds(lobby))
            {
                if (playerId != lobby.NetService.NetId)
                    SendPermanentSnapshotToLobbyPlayer(lobby, playerId);
            }
        }
    }

    public static void UnregisterLobby(StartRunLobby? lobby, bool clearClientOverlay = false)
    {
        if (lobby is null || !RegisteredLobbies.Remove(lobby))
            return;

        if (LobbyConnectedHandlers.Remove(lobby, out Delegate? connected))
            Sts2Compatibility.UnsubscribeStartRunLobbyPlayerConnected(lobby, connected);

        if (clearClientOverlay && !ReferenceEquals(_runNetService, lobby.NetService))
            UnregisterMessageHandlers(lobby.NetService);

        if (clearClientOverlay && lobby.NetService.Type == NetGameType.Client)
        {
            PermanentCardModificationStore.ClearHostOverlay();
            ClearPendingHostPermanentSnapshot();
        }
    }

    public static void PrepareRunLaunch()
    {
        try
        {
            INetGameService netService = RunManager.Instance.NetService;
            if (netService is null)
                return;

            // RunManager.Launch releases buffered messages before its postfixes.
            lock (OperationSequenceGate)
            {
                _nextOperationSequence = 0;
                _lastAppliedOperationSequence = 0;
                _operationApplyQueued = false;
                PendingOperationApplies.Clear();
            }

            RegisterRunNetService(netService);
            BindRunLobby(RunManager.Instance.RunLobby);
        }
        catch (Exception exception)
        {
            GD.PushWarning($"CardModification: failed to prepare multiplayer sync for run. {exception.Message}");
        }
    }

    public static void OnRunLaunched()
    {
        try
        {
            INetGameService netService = RunManager.Instance.NetService;
            RegisterRunNetService(netService);
            BindRunLobby(RunManager.Instance.RunLobby);

            if (netService.Type == NetGameType.Host)
                BroadcastFullSnapshot();
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
        if (item.CardPileType is not null and not PileType.Deck && !item.CombatCardIndex.HasValue)
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
                    LoadoutCardPileTargets.FromPileType(item.CardPileType ?? PileType.Deck),
                    item.CombatCardIndex ?? 0,
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
                PileTarget = LoadoutCardPileTargets.FromPileType(item.CardPileType ?? PileType.Deck),
                CombatCardIndex = item.CombatCardIndex ?? 0,
                CardId = item.Model.Id.ToString(),
                StateJson = SerializeOperationDelta(operation, item, state)
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

    public static bool RequestCatalogPermanentOperation(
        CardModificationOperation operation,
        ModelId cardId,
        CardModificationSpec? state = null)
    {
        if (operation is not CardModificationOperation.ApplyPermanent
                and not CardModificationOperation.ResetPermanentToBasic
            || !LoadoutPanelAccessService.CanLocalPlayerUsePanel()
            || LoadoutModelRegistry.ResolveCard(cardId) is null)
        {
            return false;
        }

        try
        {
            INetGameService netService = RunManager.Instance.NetService;
            Player? localPlayer = GetRunPlayer(netService.NetId) ?? GetLocalRunPlayer();
            if (localPlayer is null)
                return false;

            CardModificationDelta? delta = operation == CardModificationOperation.ApplyPermanent
                ? CardModificationRuntime.CreatePermanentDelta(cardId, state)
                : null;
            string stateJson = delta is null || delta.IsEmpty
                ? string.Empty
                : CardModificationCodec.SerializeDelta(delta);
            LoadoutCardModificationOperationPayload payload = new()
            {
                Operation = operation,
                RequesterNetId = localPlayer.NetId,
                OwnerNetId = 0,
                DeckIndex = -1,
                PileTarget = LoadoutCardPileTarget.Unspecified,
                CombatCardIndex = 0,
                CardId = LoadoutModelIdSafety.ToWireString(cardId),
                StateJson = stateJson
            };
            if (!IsValidOperationPayload(payload))
                return false;

            if (netService.Type is NetGameType.Singleplayer or NetGameType.Replay)
            {
                CardModificationRuntime.ApplyCatalogPermanentDelta(cardId, delta, authoritativeRemote: false);
                return true;
            }

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
            GD.PushWarning($"CardModification: failed to request catalog {operation}. {exception.Message}");
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
            || !TryDeserializeDelta(message.StateJson, out CardModificationDelta? delta))
        {
            GD.PushWarning("CardModification: ignored malformed permanent delta from host.");
            return;
        }

        if (!LoadoutModelRegistry.TryResolveWireId(message.CardId, out ModelId cardId))
            return;

        CardModificationSpec previous = PermanentCardModificationStore.Get(cardId);
        if (PermanentCardModificationStore.ApplyHostDelta(cardId, delta))
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
                StateJson = SerializePermanentDelta(cardId, state)
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

        if (!TryDeserializeDelta(payload.StateJson, out CardModificationDelta? delta))
        {
            GD.PushWarning($"CardModification: ignored invalid state JSON for {payload.Operation}.");
            return;
        }

        if (IsCatalogOperationPayload(payload))
        {
            CardModificationRuntime.ApplyCatalogPermanentDelta(
                cardId,
                payload.Operation == CardModificationOperation.ResetPermanentToBasic ? null : delta,
                authoritativeRemote);
            return;
        }

        CardModificationRuntime.ApplySynchronizedDeltaOperation(
            payload.Operation,
            cardId,
            LoadoutTargetSelection.ForPlayer(payload.OwnerNetId),
            payload.DeckIndex,
            payload.PileTarget.NormalizeForOwnedCard(),
            payload.CombatCardIndex,
            cardId,
            delta,
            actionPlayer,
            authoritativeRemote);
    }

    private static bool IsValidOperationPayload(LoadoutCardModificationOperationPayload payload)
    {
        if (payload.RequesterNetId == 0
            || string.IsNullOrWhiteSpace(payload.CardId)
            || (payload.StateJson?.Length ?? 0) > MaxStateJsonLength)
        {
            return false;
        }

        if (IsCatalogOperationPayload(payload))
        {
            return LoadoutModelRegistry.TryResolveWireId(payload.CardId, out ModelId cardId)
                   && LoadoutModelRegistry.ResolveCard(cardId) is not null;
        }

        return (payload.Operation is CardModificationOperation.SaveTemporary
                    or CardModificationOperation.ResetTemporary
                    or CardModificationOperation.ResetTemporaryToBasic
                    or CardModificationOperation.ApplyPermanent
                    or CardModificationOperation.ResetPermanentToBasic)
               && payload.OwnerNetId != 0
               && LoadoutCardPileTargets.IsSupportedOwnedTarget(payload.PileTarget.NormalizeForOwnedCard())
               && (payload.PileTarget.NormalizeForOwnedCard().IsCombatPile() || payload.DeckIndex >= 0)
               && LoadoutModelRegistry.TryResolveWireId(payload.CardId, out ModelId ownedCardId)
               && LoadoutModelRegistry.ResolveCard(ownedCardId) is not null;
    }

    private static bool IsCatalogOperationPayload(LoadoutCardModificationOperationPayload payload)
    {
        return (payload.Operation is CardModificationOperation.ApplyPermanent
                   or CardModificationOperation.ResetPermanentToBasic)
               && payload.OwnerNetId == 0
               && payload.DeckIndex == -1
               && payload.PileTarget == LoadoutCardPileTarget.Unspecified
               && payload.CombatCardIndex == 0;
    }

    private static bool TryDeserializeDelta(string? stateJson, out CardModificationDelta? delta)
    {
        delta = null;
        if (string.IsNullOrWhiteSpace(stateJson))
            return true;

        if (stateJson.Length > MaxStateJsonLength)
            return false;

        try
        {
            if (!CardModificationCodec.TryDeserializeDelta(stateJson, out CardModificationDelta parsed))
                return false;
            delta = parsed.IsEmpty ? null : parsed;
            return true;
        }
        catch (Exception exception)
        {
            GD.PushWarning($"CardModification: failed to deserialize state delta. {exception.Message}");
            return false;
        }
    }

    private static string SerializeOperationDelta(
        CardModificationOperation operation,
        LoadoutOwnedItem<CardModel> item,
        CardModificationSpec? state)
    {
        if (state is null || state.IsEmpty)
            return string.Empty;

        CardModificationDelta delta = operation switch
        {
            CardModificationOperation.SaveTemporary => CardModificationRuntime.CreateTemporaryDelta(item.Model, state),
            CardModificationOperation.ApplyPermanent => CardModificationRuntime.CreatePermanentDelta(item.Model.Id, state),
            _ => new CardModificationDelta()
        };
        return delta.IsEmpty ? string.Empty : CardModificationCodec.SerializeDelta(delta);
    }

    private static string SerializePermanentDelta(ModelId cardId, CardModificationSpec? state)
    {
        if (state is null || state.IsEmpty)
            return string.Empty;
        CardModificationDelta delta = CardModificationRuntime.CreatePermanentDelta(cardId, state);
        return delta.IsEmpty ? string.Empty : CardModificationCodec.SerializeDelta(delta);
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

            foreach (ulong playerId in Sts2Compatibility.EnumerateStartRunLobbyPlayerIds(lobby))
            {
                if (playerId != lobby.NetService.NetId)
                    SendPermanentSnapshotToLobbyPlayer(lobby, playerId);
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

    public static void BroadcastFullSnapshot()
    {
        try
        {
            if (!RunManager.Instance.IsInProgress || RunManager.Instance.NetService.Type != NetGameType.Host)
                return;

            INetGameService netService = RunManager.Instance.NetService;
            LoadoutCardModificationFullSyncMessage message = CreateFullSnapshotMessage();
            LoadoutNetworkBroadcast.SendToRunClients(
                netService,
                recipient => netService.SendMessage(message, recipient),
                "card modification full snapshot");
        }
        catch (Exception exception)
        {
            GD.PushWarning($"CardModification: failed to broadcast full snapshot. {exception.Message}");
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
                pileTarget = LoadoutCardPileTargets.FromPileType(item.CardPileType ?? PileType.Deck),
                combatCardIndex = item.CombatCardIndex ?? 0,
                cardId = item.Model.Id.ToString(),
                stateJson = CardModificationFields.TryGet(item.Model, out CardModificationCardData data)
                    ? data.Serialized
                    : string.Empty
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
        RegisterMessageHandlers(_runNetService);
    }

    private static void UnregisterRunNetService(bool clearClientOverlay)
    {
        if (_runNetService is null)
            return;

        NetGameType type = _runNetService.Type;
        UnbindRunLobby();
        UnregisterMessageHandlers(_runNetService);
        _runNetService = null;

        if (clearClientOverlay && type == NetGameType.Client)
        {
            PermanentCardModificationStore.ClearHostOverlay();
            ClearPendingHostPermanentSnapshot();
        }
    }

    private static void RegisterMessageHandlers(INetGameService netService)
    {
        if (!RegisteredMessageServices.Add(netService))
            return;

        netService.RegisterMessageHandler<LoadoutCardModificationPermanentSyncMessage>(HandlePermanentSync);
        netService.RegisterMessageHandler<LoadoutCardModificationTemporarySyncMessage>(HandleTemporarySync);
        netService.RegisterMessageHandler<LoadoutCardModificationFullSyncMessage>(HandleFullSync);
    }

    private static void UnregisterMessageHandlers(INetGameService netService)
    {
        if (!RegisteredMessageServices.Remove(netService))
            return;

        netService.UnregisterMessageHandler<LoadoutCardModificationPermanentSyncMessage>(HandlePermanentSync);
        netService.UnregisterMessageHandler<LoadoutCardModificationTemporarySyncMessage>(HandleTemporarySync);
        netService.UnregisterMessageHandler<LoadoutCardModificationFullSyncMessage>(HandleFullSync);
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
                SendFullSnapshotToRunPlayer);
        }
    }

    private static void UnbindRunLobby()
    {
        if (_runLobby is not null && _playerRejoinedHandler is not null)
            Sts2Compatibility.UnsubscribeRunLobbyPlayerRejoined(_runLobby, _playerRejoinedHandler);

        _runLobby = null;
        _playerRejoinedHandler = null;
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

    private static LoadoutCardModificationFullSyncMessage CreateFullSnapshotMessage()
    {
        int operationSequence;
        lock (OperationSequenceGate)
            operationSequence = _nextOperationSequence;

        LoadoutCardModificationFullSnapshot snapshot = new()
        {
            SchemaVersion = FullSnapshotSchemaVersion,
            PermanentJson = PermanentCardModificationStore.ExportEffectiveSnapshotJson()
        };
        RunState? runState = RunManager.Instance.DebugOnlyGetState();
        if (runState is not null)
        {
            foreach (Player owner in runState.Players.OrderBy(player => player.NetId))
            {
                IReadOnlyList<CardModel> deck = owner.Deck.Cards;
                for (int index = 0; index < deck.Count; index++)
                {
                    CardModel card = deck[index];
                    if (!CardModificationFields.TryGet(card, out CardModificationCardData data))
                        continue;

                    snapshot.TemporaryDeltas.Add(new LoadoutCardModificationDeckDelta
                    {
                        OwnerNetId = owner.NetId,
                        DeckIndex = index,
                        CardId = card.Id.ToString(),
                        StateJson = data.Serialized
                    });
                }
            }
        }

        return new LoadoutCardModificationFullSyncMessage
        {
            Payload = JsonSerializer.Serialize(snapshot),
            OperationSequence = operationSequence
        };
    }

    private static void SendFullSnapshotToRunPlayer(ulong playerId)
    {
        LoadoutMutationSerialExecutor.Enqueue(() =>
        {
            try
            {
                INetGameService? netService = _runNetService;
                if (netService?.Type != NetGameType.Host || playerId == netService.NetId)
                    return Task.CompletedTask;

                netService.SendMessage(CreateFullSnapshotMessage(), playerId);
            }
            catch (Exception exception)
            {
                GD.PushWarning($"CardModification: failed to send rejoin snapshot to {playerId}. {exception.Message}");
            }
            return Task.CompletedTask;
        }, $"card modification rejoin snapshot for {playerId}");
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

    private static void HandleFullSync(LoadoutCardModificationFullSyncMessage message, ulong senderId)
    {
        if (IsHostSession() || !IsExpectedHostSender(senderId))
            return;
        if (string.IsNullOrWhiteSpace(message.Payload)
            || message.Payload.Length > MaxFullSnapshotJsonLength)
        {
            GD.PushWarning("CardModification: ignored malformed full multiplayer snapshot.");
            return;
        }

        LoadoutCardModificationFullSnapshot? snapshot;
        try
        {
            snapshot = JsonSerializer.Deserialize<LoadoutCardModificationFullSnapshot>(message.Payload);
        }
        catch (Exception exception)
        {
            GD.PushWarning($"CardModification: ignored malformed full multiplayer snapshot. {exception.Message}");
            return;
        }

        if (snapshot is null
            || snapshot.SchemaVersion != FullSnapshotSchemaVersion
            || snapshot.PermanentJson is null
            || snapshot.TemporaryDeltas is null
            || snapshot.PermanentJson.Length > MaxStateJsonLength)
        {
            GD.PushWarning("CardModification: ignored unsupported full multiplayer snapshot.");
            return;
        }

        Dictionary<LoadoutDeckCardIdentity, CardModificationDelta> temporaryDeltas = new();
        foreach (LoadoutCardModificationDeckDelta entry in snapshot.TemporaryDeltas)
        {
            if (entry.OwnerNetId == 0
                || entry.DeckIndex < 0
                || string.IsNullOrWhiteSpace(entry.CardId)
                || !TryDeserializeDelta(entry.StateJson, out CardModificationDelta? delta)
                || delta is null)
            {
                GD.PushWarning("CardModification: ignored malformed deck delta in full multiplayer snapshot.");
                return;
            }

            LoadoutDeckCardIdentity identity = new(entry.OwnerNetId, entry.DeckIndex, entry.CardId);
            if (!temporaryDeltas.TryAdd(identity, delta))
            {
                GD.PushWarning("CardModification: ignored full multiplayer snapshot with duplicate deck identities.");
                return;
            }
        }

        EstablishClientOperationSequenceBaseline(message.OperationSequence);
        StorePendingHostPermanentSnapshot(snapshot.PermanentJson);

        RunState? runState = RunManager.Instance.DebugOnlyGetState();
        if (runState is null)
        {
            PermanentCardModificationStore.ApplyHostSnapshot(snapshot.PermanentJson);
            return;
        }

        Dictionary<ModelId, CardModificationSpec> previousPermanent = CaptureCurrentPermanentSpecs();
        IReadOnlyList<ModelId> changedPermanent =
            PermanentCardModificationStore.ApplyHostSnapshot(snapshot.PermanentJson);
        CardModificationRuntime.RetrofitChangedPermanentCards(changedPermanent, previousPermanent);
        CardModificationRuntime.ReconcileAuthoritativeDeckDeltas(temporaryDeltas);
    }

    private static void HandleTemporarySync(LoadoutCardModificationTemporarySyncMessage message, ulong senderId)
    {
        if (IsHostSession() || !IsExpectedHostSender(senderId))
            return;

        if ((message.stateJson?.Length ?? 0) > MaxStateJsonLength
            || !TryDeserializeDelta(message.stateJson, out CardModificationDelta? delta))
        {
            GD.PushWarning("CardModification: ignored malformed temporary multiplayer state.");
            return;
        }

        CardModificationRuntime.ApplyRemoteTemporaryDelta(
            message.ownerNetId,
            message.deckIndex,
            message.pileTarget.NormalizeForOwnedCard(),
            message.combatCardIndex,
            message.cardId,
            delta);
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
    public LoadoutCardPileTarget PileTarget;
    public uint CombatCardIndex;
    public string CardId;
    public string StateJson;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteInt(Sequence);
        writer.WriteInt((int)Operation, 8);
        writer.WriteULong(RequesterNetId);
        writer.WriteULong(OwnerNetId);
        writer.WriteInt(DeckIndex);
        writer.WriteInt((int)PileTarget, 4);
        writer.WriteUInt(CombatCardIndex);
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
        PileTarget = (LoadoutCardPileTarget)reader.ReadInt(4);
        CombatCardIndex = reader.ReadUInt();
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

public struct LoadoutCardModificationFullSyncMessage : INetMessage, IPacketSerializable
{
    public string Payload;
    public int OperationSequence;

    public bool ShouldBroadcast => false;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.VeryDebug;
    public bool ShouldBuffer => true;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteString(Payload ?? string.Empty);
        writer.WriteInt(OperationSequence);
    }

    public void Deserialize(PacketReader reader)
    {
        Payload = reader.ReadString();
        OperationSequence = reader.ReadInt();
    }
}

public readonly record struct LoadoutDeckCardIdentity(
    ulong OwnerNetId,
    int DeckIndex,
    string CardId);

public sealed class LoadoutCardModificationFullSnapshot
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; }

    [JsonPropertyName("permanent")]
    public string PermanentJson { get; set; } = string.Empty;

    [JsonPropertyName("temporary")]
    public List<LoadoutCardModificationDeckDelta> TemporaryDeltas { get; set; } = [];
}

public sealed class LoadoutCardModificationDeckDelta
{
    [JsonPropertyName("ownerNetId")]
    public ulong OwnerNetId { get; set; }

    [JsonPropertyName("deckIndex")]
    public int DeckIndex { get; set; }

    [JsonPropertyName("cardId")]
    public string CardId { get; set; } = string.Empty;

    [JsonPropertyName("state")]
    public string StateJson { get; set; } = string.Empty;
}

public struct LoadoutCardModificationTemporarySyncMessage : INetMessage, IPacketSerializable
{
    public ulong ownerNetId;
    public int deckIndex;
    public LoadoutCardPileTarget pileTarget;
    public uint combatCardIndex;
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
        writer.WriteInt((int)pileTarget, 4);
        writer.WriteUInt(combatCardIndex);
        writer.WriteString(cardId ?? string.Empty);
        writer.WriteString(stateJson ?? string.Empty);
    }

    public void Deserialize(PacketReader reader)
    {
        ownerNetId = reader.ReadULong();
        deckIndex = reader.ReadInt();
        pileTarget = (LoadoutCardPileTarget)reader.ReadInt(4);
        combatCardIndex = reader.ReadUInt();
        cardId = reader.ReadString();
        stateJson = reader.ReadString();
    }
}
