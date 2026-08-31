#nullable enable

namespace Loadout.Services.OneEvent;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Text.Json;
using Godot;
using Loadout.Services.Actions;
using Loadout.Services.Compatibility;
using Loadout.Services.Networking;
using Loadout.Services.Saving;
using Loadout.Services.Targets;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

public enum OneEventMode : byte
{
    Off,
    Normal,
    All
}

public static class OneEventModeService
{
    private const int MaxSnapshotLength = 64 * 1024;
    private const int CurrentSchemaVersion = 1;
    private const string RunDirectory = "loadout/services/one_event";
    private const string RunFilePrefix = "one_event_run";

    private static OneEventRunSaveData _state = new();
    private static EventModel? _selectedEvent;
    private static readonly HashSet<LoadRunLobby> RegisteredLoadLobbies = [];
    private static readonly HashSet<INetGameService> RegisteredMessageServices = [];
    private static INetGameService? _runNetService;
    private static RunLobby? _runLobby;
    private static Delegate? _playerRejoinedHandler;
    private static string? _pendingHostSnapshotJson;

    public static event Action? Changed;
    public static event Action<OneEventSelectionChanged>? SelectionChanged;

    public static bool IsActive => _selectedEvent is not null && _state.Mode != OneEventMode.Off;
    public static OneEventMode Mode => IsActive ? _state.Mode : OneEventMode.Off;
    public static ModelId? SelectedEventId => _selectedEvent?.Id;

    public static bool RequestCycle(ModelId eventId)
    {
        if (ResolveCanonicalEvent(eventId) is null)
            return false;

        return LoadoutImmediateMutationService.Request(
            LoadoutImmediateMutationKind.CycleOneEvent,
            eventId,
            1,
            new LoadoutTargetSelection(LoadoutTargetScope.AllPlayers));
    }

    public static void ApplySynchronizedCycle(ModelId eventId)
    {
        EventModel? canonical = ResolveCanonicalEvent(eventId);
        if (canonical is null)
            return;

        ModelId? previousId = _selectedEvent?.Id;
        OneEventMode previousMode = Mode;
        OneEventMode nextMode = previousId == canonical.Id
            ? previousMode switch
            {
                OneEventMode.Normal => OneEventMode.All,
                OneEventMode.All => OneEventMode.Off,
                _ => OneEventMode.Normal
            }
            : OneEventMode.Normal;

        _selectedEvent = nextMode == OneEventMode.Off ? null : canonical;
        _state.EventId = _selectedEvent?.Id.ToString() ?? string.Empty;
        _state.Mode = nextMode;
        ApplyStateChange(save: true);
        SelectionChanged?.Invoke(new OneEventSelectionChanged(
            previousId,
            previousMode,
            _selectedEvent?.Id,
            Mode));
    }

    public static bool TryGetSelection(out EventModel eventModel, out OneEventMode mode)
    {
        if (_selectedEvent is null || _state.Mode == OneEventMode.Off)
        {
            eventModel = null!;
            mode = OneEventMode.Off;
            return false;
        }

        eventModel = _selectedEvent;
        mode = _state.Mode;
        return true;
    }

    public static bool TryGetSelectedEvent(out EventModel eventModel)
    {
        eventModel = _selectedEvent!;
        return IsActive;
    }

    public static void RegisterLoadLobby(LoadRunLobby? lobby)
    {
        if (lobby is null || !RegisteredLoadLobbies.Add(lobby))
            return;

        RegisterMessageHandlers(lobby.NetService);
        if (lobby.NetService.Type == NetGameType.Host)
            LoadRunState(lobby.Run.StartTime);
        else if (lobby.NetService.Type == NetGameType.Client)
            lobby.NetService.SendMessage(default(OneEventSnapshotRequestMessage));
    }

    public static void UnregisterLoadLobby(LoadRunLobby? lobby, bool clearState)
    {
        if (lobby is null || !RegisteredLoadLobbies.Remove(lobby))
            return;

        if (clearState && !ReferenceEquals(_runNetService, lobby.NetService))
            UnregisterMessageHandlers(lobby.NetService);
        if (clearState)
        {
            _pendingHostSnapshotJson = null;
            ClearRuntimeState(preservePendingHostSnapshot: false);
        }
    }

    public static void PrepareRunLaunch()
    {
        try
        {
            INetGameService netService = RunManager.Instance.NetService;
            ClearRuntimeState(preservePendingHostSnapshot: netService.Type == NetGameType.Client);
            if (netService.Type is NetGameType.Host or NetGameType.Singleplayer or NetGameType.Replay)
                LoadRunState();
            RegisterRunNetService(netService);
            BindRunLobby(RunManager.Instance.RunLobby);
        }
        catch (Exception exception)
        {
            GD.PushWarning($"OneEvent: failed to prepare run synchronization. {exception.Message}");
        }
    }

    public static void OnRunLaunched()
    {
        try
        {
            INetGameService netService = RunManager.Instance.NetService;
            RegisterRunNetService(netService);
            BindRunLobby(RunManager.Instance.RunLobby);
            if (netService.Type == NetGameType.Client)
            {
                if (!ApplyPendingHostSnapshot())
                    netService.SendMessage(default(OneEventSnapshotRequestMessage));
            }
            else
            {
                Changed?.Invoke();
                NotifyCurrentSelectionApplied();
            }

            if (netService.Type == NetGameType.Host)
                BroadcastSnapshot();
        }
        catch (Exception exception)
        {
            GD.PushWarning($"OneEvent: failed to initialize run state. {exception.Message}");
        }
    }

    public static void OnRunCleaningUp()
    {
        UnbindRunLobby();
        UnregisterRunNetService();
        ClearRuntimeState(preservePendingHostSnapshot: false);
    }

    internal static bool TryResolveOrdinaryEvent(EventModel current, out EventModel selected)
    {
        selected = null!;
        return TryGetSelection(out EventModel eventModel, out OneEventMode mode)
               && mode is OneEventMode.Normal or OneEventMode.All
               && (selected = eventModel) is not null;
    }

    internal static bool TryResolveAncientEvent(out EventModel selected)
    {
        selected = null!;
        return TryGetSelection(out EventModel eventModel, out OneEventMode mode)
               && mode == OneEventMode.All
               && (selected = eventModel) is not null;
    }

    internal static bool TryPrepareRoomEntry(
        EventRoom room,
        bool isRestoringRoomStackBase,
        bool isOrdinaryEventRoll,
        bool wasRestoredFromSave,
        out EventModel selected,
        out long occurrence,
        out bool replaceRoom)
    {
        selected = null!;
        occurrence = 0;
        replaceRoom = false;
        if (isRestoringRoomStackBase
            || !TryGetSelection(out EventModel eventModel, out OneEventMode mode)
            || TryGetRunState() is not { } runState)
        {
            return false;
        }

        bool applies = mode == OneEventMode.All;
        if (!applies && mode == OneEventMode.Normal)
        {
            bool unknownMapEvent = runState.CurrentMapPointHistoryEntry?.MapPointType == MapPointType.Unknown;
            applies = unknownMapEvent
                      && (isOrdinaryEventRoll
                          || wasRestoredFromSave);
        }
        if (!applies)
            return false;

        selected = eventModel;
        replaceRoom = room.CanonicalEvent.Id != eventModel.Id;
        _state.EntrySequence++;
        occurrence = _state.EntrySequence;
        SaveRunStateIfAuthoritative();
        return true;
    }

    internal static void RecordRoomReplacement(EventModel previous, EventModel selected)
    {
        if (TryGetRunState() is not { } runState
            || runState.CurrentRoom is EventRoom active && active.CanonicalEvent.Id == previous.Id
            || runState.CurrentMapPointHistoryEntry?.Rooms is not { Count: > 0 } rooms)
            return;

        var latest = rooms[^1];
        if (latest.RoomType == RoomType.Event && latest.ModelId == previous.Id)
            latest.ModelId = selected.Id;
    }

    private static void ApplyStateChange(bool save)
    {
        if (save)
            SaveRunStateIfAuthoritative();
        Changed?.Invoke();
    }

    private static void LoadRunState()
    {
        long? runStartTime = SaveUtility.GetCurrentRunStartTime();
        if (!runStartTime.HasValue)
        {
            _state = new OneEventRunSaveData();
            _selectedEvent = null;
            return;
        }
        LoadRunState(runStartTime.Value);
    }

    private static void LoadRunState(long runStartTime)
    {
        string path = SaveUtility.GetRunSidecarPath(RunDirectory, RunFilePrefix, runStartTime);
        _state = SaveUtility.LoadProfileJson(path, new OneEventRunSaveData()).Value;
        _state.SchemaVersion = CurrentSchemaVersion;
        _state.RunStartTime = runStartTime;
        RebuildSelection();
    }

    private static void RebuildSelection()
    {
        if (_state.Mode is not (OneEventMode.Normal or OneEventMode.All)
            || ResolveCanonicalEvent(_state.EventId) is not { } eventModel)
        {
            _state.EventId = string.Empty;
            _state.Mode = OneEventMode.Off;
            _selectedEvent = null;
            return;
        }
        _selectedEvent = eventModel;
    }

    private static void SaveRunStateIfAuthoritative()
    {
        try
        {
            if ((_runNetService ?? RunManager.Instance.NetService).Type == NetGameType.Client)
                return;
            long? runStartTime = SaveUtility.GetCurrentRunStartTime();
            if (!runStartTime.HasValue)
                return;
            _state.SchemaVersion = CurrentSchemaVersion;
            _state.RunStartTime = runStartTime.Value;
            string path = SaveUtility.GetRunSidecarPath(RunDirectory, RunFilePrefix, runStartTime.Value);
            SaveUtility.SaveProfileJson(path, _state);
        }
        catch (Exception exception)
        {
            GD.PushWarning($"OneEvent: failed to save run state. {exception.Message}");
        }
    }

    private static EventModel? ResolveCanonicalEvent(ModelId eventId)
    {
        return ModelDb.AllEvents.Cast<EventModel>()
            .Concat(ModelDb.AllAncients)
            .FirstOrDefault(eventModel => eventModel.Id == eventId);
    }

    private static EventModel? ResolveCanonicalEvent(string eventId)
    {
        return ModelDb.AllEvents.Cast<EventModel>()
            .Concat(ModelDb.AllAncients)
            .FirstOrDefault(eventModel =>
                string.Equals(eventModel.Id.ToString(), eventId, StringComparison.Ordinal)
                || string.Equals(eventModel.Id.Entry, eventId, StringComparison.OrdinalIgnoreCase));
    }

    private static RunState? TryGetRunState()
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

    private static void RegisterRunNetService(INetGameService netService)
    {
        if (ReferenceEquals(_runNetService, netService))
            return;
        UnregisterRunNetService();
        _runNetService = netService;
        RegisterMessageHandlers(netService);
    }

    private static void UnregisterRunNetService()
    {
        if (_runNetService is null)
            return;
        UnregisterMessageHandlers(_runNetService);
        _runNetService = null;
    }

    private static void RegisterMessageHandlers(INetGameService netService)
    {
        if (!RegisteredMessageServices.Add(netService))
            return;
        netService.RegisterMessageHandler<OneEventSnapshotMessage>(HandleSnapshot);
        netService.RegisterMessageHandler<OneEventSnapshotRequestMessage>(HandleSnapshotRequest);
    }

    private static void UnregisterMessageHandlers(INetGameService netService)
    {
        if (!RegisteredMessageServices.Remove(netService))
            return;
        netService.UnregisterMessageHandler<OneEventSnapshotMessage>(HandleSnapshot);
        netService.UnregisterMessageHandler<OneEventSnapshotRequestMessage>(HandleSnapshotRequest);
    }

    private static void BindRunLobby(RunLobby? runLobby)
    {
        if (ReferenceEquals(_runLobby, runLobby))
            return;
        UnbindRunLobby();
        _runLobby = runLobby;
        if (_runLobby is not null)
            _playerRejoinedHandler = Sts2Compatibility.SubscribeRunLobbyPlayerRejoined(_runLobby, OnPlayerRejoined);
    }

    private static void UnbindRunLobby()
    {
        if (_runLobby is not null && _playerRejoinedHandler is not null)
            Sts2Compatibility.UnsubscribeRunLobbyPlayerRejoined(_runLobby, _playerRejoinedHandler);
        _runLobby = null;
        _playerRejoinedHandler = null;
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
        LoadoutNetworkBroadcast.SendToRunClients(
            _runNetService,
            recipient => SendSnapshot(_runNetService, recipient),
            "One Event snapshot");
    }

    private static void SendSnapshot(INetGameService netService, ulong recipient)
    {
        if (netService.Type != NetGameType.Host || recipient == netService.NetId)
            return;
        netService.SendMessage(new OneEventSnapshotMessage
        {
            SnapshotJson = JsonSerializer.Serialize(_state)
        }, recipient);
    }

    private static void HandleSnapshotRequest(OneEventSnapshotRequestMessage _, ulong senderId)
    {
        LoadRunLobby? loadLobby = RegisteredLoadLobbies.FirstOrDefault(candidate =>
            candidate.NetService.Type == NetGameType.Host
            && Sts2Compatibility.EnumerateLoadRunLobbyPlayerIds(candidate).Contains(senderId));
        if (loadLobby is not null)
        {
            SendSnapshot(loadLobby.NetService, senderId);
            return;
        }

        if (_runNetService?.Type == NetGameType.Host)
            SendSnapshot(_runNetService, senderId);
    }

    private static void HandleSnapshot(OneEventSnapshotMessage message, ulong senderId)
    {
        if (!LoadoutNetworkBroadcast.IsExpectedHostSender(
                senderId,
                _runNetService,
                RegisteredLoadLobbies.Select(lobby => lobby.NetService))
            || string.IsNullOrWhiteSpace(message.SnapshotJson)
            || message.SnapshotJson.Length > MaxSnapshotLength)
        {
            return;
        }

        try
        {
            OneEventRunSaveData? incoming = JsonSerializer.Deserialize<OneEventRunSaveData>(message.SnapshotJson);
            if (incoming is null)
                return;
            long? expected = GetExpectedRunStartTime();
            if (expected.HasValue && incoming.RunStartTime != 0 && incoming.RunStartTime != expected.Value)
                return;
            if (!RunManager.Instance.IsInProgress)
            {
                _pendingHostSnapshotJson = message.SnapshotJson;
                return;
            }
            ApplyHostSnapshot(incoming);
        }
        catch (Exception exception)
        {
            GD.PushWarning($"OneEvent: failed to apply host snapshot. {exception.Message}");
        }
    }

    private static bool ApplyPendingHostSnapshot()
    {
        string? pending = _pendingHostSnapshotJson;
        _pendingHostSnapshotJson = null;
        if (string.IsNullOrWhiteSpace(pending))
            return false;
        try
        {
            OneEventRunSaveData? incoming = JsonSerializer.Deserialize<OneEventRunSaveData>(pending);
            if (incoming is null)
                return false;
            ApplyHostSnapshot(incoming);
            return true;
        }
        catch (Exception exception)
        {
            GD.PushWarning($"OneEvent: failed to apply pending host snapshot. {exception.Message}");
            return false;
        }
    }

    private static void ApplyHostSnapshot(OneEventRunSaveData incoming)
    {
        ModelId? previousId = _selectedEvent?.Id;
        OneEventMode previousMode = Mode;
        _state = incoming;
        RebuildSelection();
        Changed?.Invoke();
        if (previousId != _selectedEvent?.Id || previousMode != Mode)
        {
            SelectionChanged?.Invoke(new OneEventSelectionChanged(
                previousId,
                previousMode,
                _selectedEvent?.Id,
                Mode));
        }
    }

    private static long? GetExpectedRunStartTime()
    {
        long? current = SaveUtility.GetCurrentRunStartTime();
        if (current.HasValue)
            return current;
        return RegisteredLoadLobbies
            .FirstOrDefault(lobby => lobby.NetService.Type == NetGameType.Client)
            ?.Run.StartTime;
    }

    private static void ClearRuntimeState(bool preservePendingHostSnapshot)
    {
        ModelId? previousId = _selectedEvent?.Id;
        OneEventMode previousMode = Mode;
        _state = new OneEventRunSaveData();
        _selectedEvent = null;
        if (!preservePendingHostSnapshot)
            _pendingHostSnapshotJson = null;
        Changed?.Invoke();
        if (previousId is not null || previousMode != OneEventMode.Off)
            SelectionChanged?.Invoke(new OneEventSelectionChanged(previousId, previousMode, null, OneEventMode.Off));
    }

    private static void NotifyCurrentSelectionApplied()
    {
        if (_selectedEvent is not null)
            SelectionChanged?.Invoke(new OneEventSelectionChanged(null, OneEventMode.Off, _selectedEvent.Id, Mode));
    }

    public sealed class OneEventRunSaveData : ISerializable
    {
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;
        public long RunStartTime { get; set; }
        public string EventId { get; set; } = string.Empty;
        public OneEventMode Mode { get; set; }
        public long EntrySequence { get; set; }

        public void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            info.AddValue(nameof(SchemaVersion), SchemaVersion);
            info.AddValue(nameof(RunStartTime), RunStartTime);
            info.AddValue(nameof(EventId), EventId);
            info.AddValue(nameof(Mode), Mode);
            info.AddValue(nameof(EntrySequence), EntrySequence);
        }
    }
}

public readonly record struct OneEventSelectionChanged(
    ModelId? PreviousEventId,
    OneEventMode PreviousMode,
    ModelId? SelectedEventId,
    OneEventMode Mode);

public struct OneEventSnapshotMessage : INetMessage, IPacketSerializable
{
    public string SnapshotJson;
    public bool ShouldBroadcast => false;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.VeryDebug;
    public bool ShouldBuffer => true;

    public void Serialize(PacketWriter writer) => writer.WriteString(SnapshotJson ?? string.Empty);
    public void Deserialize(PacketReader reader) => SnapshotJson = reader.ReadString();
}

public struct OneEventSnapshotRequestMessage : INetMessage, IPacketSerializable
{
    public bool ShouldBroadcast => false;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.VeryDebug;
    public bool ShouldBuffer => true;
    public readonly void Serialize(PacketWriter writer) { }
    public void Deserialize(PacketReader reader) { }
}
