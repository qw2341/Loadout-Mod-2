#nullable enable

namespace Loadout.Services.RelicModification;

using System;
using System.Collections.Generic;
using System.Linq;
using BaseLib.Abstracts;
using Godot;
using Loadout.Services.Actions;
using Loadout.Services.CardModification;
using Loadout.Services.Compatibility;
using Loadout.Services.Networking;
using Loadout.Services.Targets;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Runs;

public static class RelicModificationMultiplayerSyncService
{
    private const int MaxSnapshotLength = 256 * 1024;
    private static readonly HashSet<StartRunLobby> Lobbies = [];
    private static readonly Dictionary<StartRunLobby, Delegate> ConnectedHandlers = new();
    private static readonly HashSet<INetGameService> RegisteredMessageServices = [];
    private static INetGameService? _runNetService;
    private static RunLobby? _runLobby;
    private static Delegate? _playerRejoinedHandler;
    private static bool _registered;
    private static string? _pendingHostSnapshot;

    public static bool HasPendingHostPermanentSnapshot => !string.IsNullOrWhiteSpace(_pendingHostSnapshot);

    public static void Register() => _registered = true;

    public static void Unregister()
    {
        foreach (StartRunLobby lobby in Lobbies.ToList()) UnregisterLobby(lobby, clearOverlay: false);
        UnregisterRunNetService(clearOverlay: true);
        foreach (INetGameService netService in RegisteredMessageServices.ToList())
            UnregisterMessageHandler(netService);
        Lobbies.Clear(); ConnectedHandlers.Clear(); _pendingHostSnapshot = null; _registered = false;
    }

    public static bool RequestOperation(RelicModificationOperation operation, LoadoutOwnedItem<RelicModel> item, RelicModificationState? state = null)
        => _registered && LoadoutImmediateMutationService.RequestRelicModification(item, operation, state);

    public static bool RequestAddCopies(LoadoutOwnedItem<RelicModel> item, int amount)
        => _registered && LoadoutImmediateMutationService.RequestOwnedRelicCopies(item, amount);

    public static void RegisterLobby(StartRunLobby lobby)
    {
        if (!_registered || !Lobbies.Add(lobby)) return;
        RegisterMessageHandler(lobby.NetService);
        Delegate connected = Sts2Compatibility.SubscribeStartRunLobbyPlayerConnected(
            lobby,
            playerId => SendSnapshot(lobby.NetService, playerId));
        ConnectedHandlers[lobby] = connected;
        if (lobby.NetService.Type == NetGameType.Host)
            foreach (ulong playerId in Sts2Compatibility.EnumerateStartRunLobbyPlayerIds(lobby).Where(playerId => playerId != lobby.NetService.NetId)) SendSnapshot(lobby.NetService, playerId);
    }

    public static void UnregisterLobby(StartRunLobby lobby, bool clearOverlay)
    {
        if (!Lobbies.Remove(lobby)) return;
        if (ConnectedHandlers.Remove(lobby, out Delegate? handler)) Sts2Compatibility.UnsubscribeStartRunLobbyPlayerConnected(lobby, handler);
        if (clearOverlay && !ReferenceEquals(_runNetService, lobby.NetService))
            UnregisterMessageHandler(lobby.NetService);
        if (clearOverlay && lobby.NetService.Type == NetGameType.Client) RelicModificationStateService.ClearHostPermanentOverlay();
    }

    public static void PrepareRunLaunch()
    {
        try
        {
            RegisterRunNetService(RunManager.Instance.NetService);
            BindRunLobby(RunManager.Instance.RunLobby);
        }
        catch (Exception exception)
        {
            GD.PushWarning($"RelicModifier: failed to prepare multiplayer sync. {exception.Message}");
        }
    }

    public static void OnRunLaunched()
    {
        try
        {
            RegisterRunNetService(RunManager.Instance.NetService);
            BindRunLobby(RunManager.Instance.RunLobby);
            if (RunManager.Instance.NetService.Type == NetGameType.Host) BroadcastSnapshot();
            else if (RunManager.Instance.NetService.Type is NetGameType.Singleplayer or NetGameType.Replay) RelicModificationStateService.ClearHostPermanentOverlay();
        }
        catch (Exception exception) { GD.PushWarning($"RelicModifier: failed to initialize multiplayer sync. {exception.Message}"); }
    }

    public static void OnRunCleaningUp()
    {
        UnregisterRunNetService(clearOverlay: true);
        _pendingHostSnapshot = null;
    }

    public static void BroadcastSnapshot()
    {
        LoadoutRelicModificationPermanentSyncMessage message = new() { Payload = RelicModificationStateService.ExportPermanentSnapshot() };
        foreach (StartRunLobby lobby in Lobbies.Where(lobby => lobby.NetService.Type == NetGameType.Host))
            foreach (ulong playerId in Sts2Compatibility.EnumerateStartRunLobbyPlayerIds(lobby).Where(playerId => playerId != lobby.NetService.NetId)) lobby.NetService.SendMessage(message, playerId);
        try
        {
            INetGameService net = RunManager.Instance.NetService;
            if (RunManager.Instance.IsInProgress && net.Type == NetGameType.Host)
                LoadoutNetworkBroadcast.SendToRunClients(net, recipient => net.SendMessage(message, recipient), "relic modification permanent snapshot");
        }
        catch { }
    }

    public static void ApplyPendingHostPermanentSnapshot(CardModificationPermanentImportMode mode)
    {
        string? snapshot = _pendingHostSnapshot;
        _pendingHostSnapshot = null;
        if (string.IsNullOrWhiteSpace(snapshot) || mode == CardModificationPermanentImportMode.KeepMine) return;
        RelicModificationStateService.ImportHostPermanentSnapshot(snapshot, merge: mode == CardModificationPermanentImportMode.MergeNonConflicting);
    }

    private static void RegisterRunNetService(INetGameService net)
    {
        if (_runNetService == net) return;
        UnregisterRunNetService(clearOverlay: false);
        _runNetService = net;
        RegisterMessageHandler(net);
    }

    private static void UnregisterRunNetService(bool clearOverlay)
    {
        if (_runNetService is null) return;
        NetGameType type = _runNetService.Type;
        UnbindRunLobby();
        UnregisterMessageHandler(_runNetService);
        _runNetService = null;
        if (clearOverlay && type == NetGameType.Client) RelicModificationStateService.ClearHostPermanentOverlay();
    }

    private static void RegisterMessageHandler(INetGameService netService)
    {
        if (!RegisteredMessageServices.Add(netService)) return;
        netService.RegisterMessageHandler<LoadoutRelicModificationPermanentSyncMessage>(HandleSnapshot);
    }

    private static void UnregisterMessageHandler(INetGameService netService)
    {
        if (!RegisteredMessageServices.Remove(netService)) return;
        netService.UnregisterMessageHandler<LoadoutRelicModificationPermanentSyncMessage>(HandleSnapshot);
    }

    private static void BindRunLobby(RunLobby? runLobby)
    {
        if (ReferenceEquals(_runLobby, runLobby)) return;
        UnbindRunLobby();
        _runLobby = runLobby;
        if (_runLobby is not null)
        {
            _playerRejoinedHandler = Sts2Compatibility.SubscribeRunLobbyPlayerRejoined(
                _runLobby,
                SendSnapshotToRunPlayer);
        }
    }

    private static void UnbindRunLobby()
    {
        if (_runLobby is not null && _playerRejoinedHandler is not null)
            Sts2Compatibility.UnsubscribeRunLobbyPlayerRejoined(_runLobby, _playerRejoinedHandler);
        _runLobby = null;
        _playerRejoinedHandler = null;
    }

    private static void SendSnapshot(INetGameService net, ulong playerId)
    {
        if (net.Type != NetGameType.Host || playerId == net.NetId) return;
        net.SendMessage(new LoadoutRelicModificationPermanentSyncMessage { Payload = RelicModificationStateService.ExportPermanentSnapshot() }, playerId);
    }

    public static void SendSnapshotToRunPlayer(ulong playerId)
    {
        if (_runNetService is not null)
            SendSnapshot(_runNetService, playerId);
    }

    private static void HandleSnapshot(LoadoutRelicModificationPermanentSyncMessage message, ulong senderId)
    {
        if (!LoadoutNetworkBroadcast.IsExpectedHostSender(senderId, _runNetService, Lobbies.Select(lobby => lobby.NetService))) return;
        if (string.IsNullOrWhiteSpace(message.Payload) || message.Payload.Length > MaxSnapshotLength) return;
        try
        {
            RelicModificationStateService.SetHostPermanentOverlay(message.Payload);
            _pendingHostSnapshot = message.Payload;
        }
        catch (Exception exception)
        {
            GD.PushWarning($"RelicModifier: ignored malformed host permanent snapshot. {exception.Message}");
        }
    }
}

public struct LoadoutRelicModificationPermanentSyncMessage : INetMessage, IPacketSerializable
{
    public string Payload;
    public bool ShouldBroadcast => false;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.VeryDebug;
    public bool ShouldBuffer => true;
    public void Serialize(PacketWriter writer) => writer.WriteString(Payload ?? string.Empty);
    public void Deserialize(PacketReader reader) => Payload = reader.ReadString();
}
