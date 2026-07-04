#nullable enable

namespace Loadout.Services.CardModification;

using System;
using System.Collections.Generic;
using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Runs;
using Loadout.Services.Targets;
using MegaCrit.Sts2.Core.Models;

public static class CardModificationMultiplayerSyncService
{
    private static readonly HashSet<StartRunLobby> RegisteredLobbies = [];
    private static readonly Dictionary<StartRunLobby, Action<LobbyPlayer>> LobbyConnectedHandlers = new();
    private static INetGameService? _runNetService;
    private static bool _registered;
    private static string? _pendingHostPermanentSnapshotJson;

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
            CardModificationStateService.ClearHostPermanentOverlay();
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

            RegisterRunNetService(netService);

            if (netService.Type == NetGameType.Host)
                BroadcastPermanentSnapshot();
            else if (netService.Type is NetGameType.Singleplayer or NetGameType.Replay)
                CardModificationStateService.ClearHostPermanentOverlay();
        }
        catch (Exception exception)
        {
            GD.PushWarning($"CardModification: failed to initialize multiplayer sync for run. {exception.Message}");
        }
    }

    public static void OnRunCleaningUp()
    {
        UnregisterRunNetService(clearClientOverlay: true);
    }

    public static void BroadcastPermanentSnapshot()
    {
        string payload = CardModificationStateService.ExportPermanentSnapshotJson();
        LoadoutCardModificationPermanentSyncMessage message = new()
        {
            payload = payload
        };

        foreach (StartRunLobby lobby in RegisteredLobbies)
        {
            if (lobby.NetService.Type == NetGameType.Host)
                lobby.NetService.SendMessage(message);
        }

        try
        {
            if (RunManager.Instance.IsInProgress && RunManager.Instance.NetService.Type == NetGameType.Host)
                RunManager.Instance.NetService.SendMessage(message);
        }
        catch
        {
            // There may be no active run service while still in the lobby.
        }
    }

    public static void BroadcastTemporary(LoadoutOwnedItem<CardModel> item, CardModificationState state)
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
                stateJson = state.IsEmpty ? string.Empty : JsonSerializer.Serialize(state)
            };
            RunManager.Instance.NetService.SendMessage(message);
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
            CardModificationStateService.ClearHostPermanentOverlay();
            ClearPendingHostPermanentSnapshot();
        }
    }

    private static void SendPermanentSnapshotToLobbyPlayer(StartRunLobby lobby, ulong playerId)
    {
        if (lobby.NetService.Type != NetGameType.Host || playerId == lobby.NetService.NetId)
            return;

        lobby.NetService.SendMessage(new LoadoutCardModificationPermanentSyncMessage
        {
            payload = CardModificationStateService.ExportPermanentSnapshotJson()
        }, playerId);
    }

    private static void HandlePermanentSync(LoadoutCardModificationPermanentSyncMessage message, ulong senderId)
    {
        if (IsHostSession())
            return;

        StorePendingHostPermanentSnapshot(message.payload);
    }

    private static void HandleTemporarySync(LoadoutCardModificationTemporarySyncMessage message, ulong senderId)
    {
        if (IsHostSession())
            return;

        CardModificationState? state = null;
        if (!string.IsNullOrWhiteSpace(message.stateJson))
        {
            try
            {
                state = JsonSerializer.Deserialize<CardModificationState>(message.stateJson);
            }
            catch (Exception exception)
            {
                GD.PushWarning($"CardModification: failed to deserialize temporary multiplayer state. {exception.Message}");
            }
        }

        CardModificationStateService.ApplyRemoteTemporaryState(
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

    public static IReadOnlyList<ModelId> ApplyPendingHostPermanentSnapshot(CardModificationPermanentImportMode mode)
    {
        string? snapshot = _pendingHostPermanentSnapshotJson;
        ClearPendingHostPermanentSnapshot();
        return CardModificationStateService.ApplyPermanentSnapshotToProfile(snapshot, mode);
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

public struct LoadoutCardModificationPermanentSyncMessage : INetMessage, IPacketSerializable
{
    public string payload;

    public bool ShouldBroadcast => false;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.VeryDebug;
    public bool ShouldBuffer => true;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteString(payload ?? string.Empty);
    }

    public void Deserialize(PacketReader reader)
    {
        payload = reader.ReadString();
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
