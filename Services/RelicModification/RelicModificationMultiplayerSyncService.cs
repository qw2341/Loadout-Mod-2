#nullable enable

using Loadout.Patches.Cards;

namespace Loadout.Services.RelicModification;

using System;
using System.Collections.Generic;
using System.Linq;
using BaseLib.Abstracts;
using Godot;
using Loadout.Services.Actions;
using Loadout.Services.CardModification;
using Loadout.Services.Networking;
using Loadout.Services.Targets;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
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
    private static readonly Dictionary<StartRunLobby, Action<LobbyPlayer>> ConnectedHandlers = new();
    private static INetGameService? _runNetService;
    private static bool _registered;
    private static string? _pendingHostSnapshot;

    public static bool HasPendingHostPermanentSnapshot => !string.IsNullOrWhiteSpace(_pendingHostSnapshot);

    public static void Register() => _registered = true;

    public static void Unregister()
    {
        foreach (StartRunLobby lobby in Lobbies.ToList()) UnregisterLobby(lobby, clearOverlay: false);
        UnregisterRunNetService(clearOverlay: true);
        Lobbies.Clear(); ConnectedHandlers.Clear(); _pendingHostSnapshot = null; _registered = false;
    }

    public static bool RequestOperation(RelicModificationOperation operation, LoadoutOwnedItem<RelicModel> item, RelicModificationState? state = null)
        => _registered && LoadoutImmediateMutationService.RequestRelicModification(item, operation, state);

    public static bool RequestAddCopies(LoadoutOwnedItem<RelicModel> item, int amount)
        => _registered && LoadoutImmediateMutationService.RequestOwnedRelicCopies(item, amount);

    public static void RegisterLobby(StartRunLobby lobby)
    {
        if (!_registered || !Lobbies.Add(lobby)) return;
        lobby.NetService.RegisterMessageHandler<LoadoutRelicModificationPermanentSyncMessage>(HandleSnapshot);
        Action<LobbyPlayer> connected = player => SendSnapshot(lobby.NetService, player.id);
        ConnectedHandlers[lobby] = connected;
        lobby.PlayerConnected += connected;
        if (lobby.NetService.Type == NetGameType.Host)
            foreach (LobbyPlayer player in lobby.Players.Where(player => player.id != lobby.NetService.NetId)) SendSnapshot(lobby.NetService, player.id);
    }

    public static void UnregisterLobby(StartRunLobby lobby, bool clearOverlay)
    {
        if (!Lobbies.Remove(lobby)) return;
        lobby.NetService.UnregisterMessageHandler<LoadoutRelicModificationPermanentSyncMessage>(HandleSnapshot);
        if (ConnectedHandlers.Remove(lobby, out Action<LobbyPlayer>? handler)) lobby.PlayerConnected -= handler;
        if (clearOverlay && lobby.NetService.Type == NetGameType.Client) RelicModificationStateService.ClearHostPermanentOverlay();
    }

    public static void OnRunLaunched()
    {
        try
        {
            RegisterRunNetService(RunManager.Instance.NetService);
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
            foreach (LobbyPlayer player in lobby.Players.Where(player => player.id != lobby.NetService.NetId)) lobby.NetService.SendMessage(message, player.id);
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
        net.RegisterMessageHandler<LoadoutRelicModificationPermanentSyncMessage>(HandleSnapshot);
    }

    private static void UnregisterRunNetService(bool clearOverlay)
    {
        if (_runNetService is null) return;
        NetGameType type = _runNetService.Type;
        _runNetService.UnregisterMessageHandler<LoadoutRelicModificationPermanentSyncMessage>(HandleSnapshot);
        _runNetService = null;
        if (clearOverlay && type == NetGameType.Client) RelicModificationStateService.ClearHostPermanentOverlay();
    }

    private static void SendSnapshot(INetGameService net, ulong playerId)
    {
        if (net.Type != NetGameType.Host || playerId == net.NetId) return;
        net.SendMessage(new LoadoutRelicModificationPermanentSyncMessage { Payload = RelicModificationStateService.ExportPermanentSnapshot() }, playerId);
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
