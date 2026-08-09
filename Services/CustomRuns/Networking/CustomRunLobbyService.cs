#nullable enable

namespace Loadout.Services.CustomRuns.Networking;

using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Loadout.Services.Compatibility;
using Loadout.Services.CustomRuns.Models;
using Loadout.Services.CustomRuns.Persistence;
using Loadout.Services.Networking;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;

public static class CustomRunLobbyService
{
    private static readonly HashSet<StartRunLobby> RegisteredLobbies = [];
    private static readonly Dictionary<StartRunLobby, Delegate> ConnectedHandlers = new();
    private static readonly Dictionary<StartRunLobby, CustomRunDefinition> HostDefinitions = new();
    private static CustomRunDefinition? _remoteDefinition;

    public static event Action? RemoteDefinitionChanged;

    public static CustomRunDefinition? GetRemoteDefinition()
    {
        return _remoteDefinition is null
            ? null
            : CustomRunNormalizationService.Clone(_remoteDefinition);
    }

    public static CustomRunDefinition? GetHostDefinition(StartRunLobby lobby)
    {
        return HostDefinitions.TryGetValue(lobby, out CustomRunDefinition? definition)
            ? CustomRunNormalizationService.Clone(definition)
            : null;
    }

    public static void RegisterLobby(StartRunLobby? lobby)
    {
        if (lobby is null || !RegisteredLobbies.Add(lobby))
            return;

        lobby.NetService.RegisterMessageHandler<CustomRunDefinitionMessage>(HandleDefinitionMessage);
        Delegate connected = Sts2Compatibility.SubscribeStartRunLobbyPlayerConnected(
            lobby,
            playerId => SendDefinitionToPlayer(lobby, playerId));
        ConnectedHandlers[lobby] = connected;
    }

    public static void UnregisterLobby(StartRunLobby? lobby)
    {
        if (lobby is null || !RegisteredLobbies.Remove(lobby))
            return;

        lobby.NetService.UnregisterMessageHandler<CustomRunDefinitionMessage>(HandleDefinitionMessage);
        if (ConnectedHandlers.Remove(lobby, out Delegate? connected))
            Sts2Compatibility.UnsubscribeStartRunLobbyPlayerConnected(lobby, connected);
        HostDefinitions.Remove(lobby);

        if (lobby.NetService.Type == NetGameType.Client)
        {
            _remoteDefinition = null;
            RemoteDefinitionChanged?.Invoke();
        }
    }

    public static bool ApplyHostDefinition(StartRunLobby lobby, CustomRunDefinition definition, out string error)
    {
        error = string.Empty;
        if (lobby.NetService.Type == NetGameType.Client)
        {
            error = "Only the host can apply a Custom Run definition.";
            return false;
        }

        CustomRunDefinition normalized = CustomRunNormalizationService.Normalize(
            CustomRunNormalizationService.Clone(definition));
        HostDefinitions[lobby] = normalized;
        if (lobby.NetService.Type == NetGameType.Host)
            BroadcastDefinition(lobby);
        return true;
    }

    private static void BroadcastDefinition(StartRunLobby lobby)
    {
        foreach (ulong playerId in Sts2Compatibility.EnumerateStartRunLobbyPlayerIds(lobby))
        {
            if (playerId != lobby.NetService.NetId)
                SendDefinitionToPlayer(lobby, playerId);
        }
    }

    private static void SendDefinitionToPlayer(StartRunLobby lobby, ulong playerId)
    {
        if (lobby.NetService.Type != NetGameType.Host || playerId == lobby.NetService.NetId)
            return;

        HostDefinitions.TryGetValue(lobby, out CustomRunDefinition? definition);
        lobby.NetService.SendMessage(new CustomRunDefinitionMessage
        {
            payload = definition is null ? string.Empty : CustomRunSerializationService.Serialize(definition)
        }, playerId);
    }

    private static void HandleDefinitionMessage(CustomRunDefinitionMessage message, ulong senderId)
    {
        if (!IsExpectedHostSender(senderId))
            return;

        if (string.IsNullOrWhiteSpace(message.payload))
        {
            _remoteDefinition = null;
            RemoteDefinitionChanged?.Invoke();
            return;
        }

        if (!CustomRunSerializationService.TryDeserialize(message.payload, out CustomRunDefinition definition, out string error))
        {
            GD.PushWarning($"Loadout Custom Run: rejected host definition. {error}");
            return;
        }

        _remoteDefinition = definition;
        RemoteDefinitionChanged?.Invoke();
    }

    private static bool IsExpectedHostSender(ulong senderId)
    {
        return LoadoutNetworkBroadcast.IsExpectedHostSender(
            senderId,
            null,
            RegisteredLobbies.Select(lobby => lobby.NetService));
    }
}

public struct CustomRunDefinitionMessage : INetMessage, IPacketSerializable
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
