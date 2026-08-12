#nullable enable

namespace Loadout.Services.CustomRuns.Networking;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using Loadout.Services.Compatibility;
using Loadout.Services.CustomRuns.Models;
using Loadout.Services.CustomRuns.Persistence;
using Loadout.Services.Networking;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;

public sealed class CustomRunRoleAssignmentSnapshot
{
    public string DefinitionId { get; set; } = string.Empty;
    public long Revision { get; set; }
    public List<CustomRunRoleAssignmentEntry> Assignments { get; set; } = [];
}

public sealed class CustomRunRoleAssignmentEntry
{
    public ulong PlayerId { get; set; }
    public string RoleId { get; set; } = string.Empty;
}

public static class CustomRunRoleAssignmentService
{
    private const int MaximumPayloadBytes = 64 * 1024;
    private static readonly HashSet<StartRunLobby> RegisteredLobbies = [];
    private static readonly Dictionary<StartRunLobby, CustomRunRoleAssignmentSnapshot> States = [];

    public static event Action<StartRunLobby>? Changed;
    public static event Action<StartRunLobby, string>? AssignmentRejected;
    public static event Action<StartRunLobby>? AssignmentAccepted;

    public static void RegisterLobby(StartRunLobby lobby)
    {
        if (!RegisteredLobbies.Add(lobby))
            return;
        lobby.NetService.RegisterMessageHandler<CustomRunRoleSelectionRequestMessage>(HandleSelectionRequest);
        lobby.NetService.RegisterMessageHandler<CustomRunRoleAssignmentSnapshotMessage>(HandleSnapshot);
        lobby.NetService.RegisterMessageHandler<CustomRunRoleAssignmentResultMessage>(HandleResult);
        States[lobby] = new CustomRunRoleAssignmentSnapshot();
    }

    public static void UnregisterLobby(StartRunLobby lobby)
    {
        if (!RegisteredLobbies.Remove(lobby))
            return;
        lobby.NetService.UnregisterMessageHandler<CustomRunRoleSelectionRequestMessage>(HandleSelectionRequest);
        lobby.NetService.UnregisterMessageHandler<CustomRunRoleAssignmentSnapshotMessage>(HandleSnapshot);
        lobby.NetService.UnregisterMessageHandler<CustomRunRoleAssignmentResultMessage>(HandleResult);
        States.Remove(lobby);
    }

    public static void OnDefinitionApplied(StartRunLobby lobby, CustomRunDefinition definition, bool sameDefinition)
    {
        CustomRunRoleAssignmentSnapshot state = GetOrCreateState(lobby);
        Dictionary<ulong, string> previous = sameDefinition
            ? state.Assignments.ToDictionary(entry => entry.PlayerId, entry => entry.RoleId)
            : [];
        state.DefinitionId = definition.Id;
        state.Assignments = Reconcile(lobby, definition, previous);
        state.Revision++;
        if (lobby.NetService.Type != NetGameType.Client)
            CustomRunLobbyService.CancelPreparation(lobby, "The loaded Custom Run changed; press Play again.");
        BroadcastSnapshot(lobby);
        Changed?.Invoke(lobby);
    }

    public static void OnDefinitionCleared(StartRunLobby lobby)
    {
        CustomRunRoleAssignmentSnapshot state = GetOrCreateState(lobby);
        state.DefinitionId = string.Empty;
        state.Assignments.Clear();
        state.Revision++;
        BroadcastSnapshot(lobby);
        Changed?.Invoke(lobby);
    }

    public static void OnPlayerConnected(StartRunLobby lobby, ulong playerId)
    {
        if (lobby.NetService.Type == NetGameType.Host)
            SendSnapshot(lobby, playerId);
        Changed?.Invoke(lobby);
    }

    public static void OnPlayerDisconnected(StartRunLobby lobby, ulong playerId)
    {
        CustomRunRoleAssignmentSnapshot state = GetOrCreateState(lobby);
        if (state.Assignments.RemoveAll(entry => entry.PlayerId == playerId) == 0)
            return;
        state.Revision++;
        BroadcastSnapshot(lobby);
        Changed?.Invoke(lobby);
    }

    public static IReadOnlyDictionary<ulong, string?> GetAssignments(StartRunLobby lobby)
    {
        if (!States.TryGetValue(lobby, out CustomRunRoleAssignmentSnapshot? state))
            return new Dictionary<ulong, string?>();
        return state.Assignments.ToDictionary(
            entry => entry.PlayerId,
            entry => string.IsNullOrWhiteSpace(entry.RoleId) ? null : (string?)entry.RoleId);
    }

    public static string? GetRoleId(StartRunLobby lobby, ulong playerId)
    {
        string? roleId = States.TryGetValue(lobby, out CustomRunRoleAssignmentSnapshot? state)
            ? state.Assignments.FirstOrDefault(entry => entry.PlayerId == playerId)?.RoleId
            : null;
        return string.IsNullOrWhiteSpace(roleId) ? null : roleId;
    }

    public static bool HasLockedSelection(StartRunLobby lobby, ulong playerId)
    {
        return States.TryGetValue(lobby, out CustomRunRoleAssignmentSnapshot? state)
               && state.Assignments.Any(entry => entry.PlayerId == playerId);
    }

    public static bool AreMinimumsSatisfied(
        StartRunLobby lobby,
        CustomRunDefinition definition,
        ulong? proposedPlayerId = null,
        string? proposedRoleId = null)
    {
        IReadOnlyDictionary<ulong, string?> current = GetAssignments(lobby);
        return definition.Roles.All(role =>
        {
            int count = current.Count(pair =>
                pair.Key != proposedPlayerId && string.Equals(pair.Value, role.Id, StringComparison.Ordinal));
            if (proposedPlayerId.HasValue && string.Equals(proposedRoleId, role.Id, StringComparison.Ordinal))
                count++;
            return count >= role.MinimumPlayers;
        });
    }

    public static bool IsRoleAtCapacity(
        StartRunLobby lobby,
        CustomRunDefinition definition,
        ulong playerId,
        string? roleId)
    {
        if (string.IsNullOrWhiteSpace(roleId))
            return false;
        RoleDefinition? role = definition.Roles.FirstOrDefault(candidate => candidate.Id == roleId);
        if (role is null || role.MaximumPlayers == 0)
            return false;
        int occupiedByOthers = GetAssignments(lobby).Count(pair =>
            pair.Key != playerId && string.Equals(pair.Value, roleId, StringComparison.Ordinal));
        return occupiedByOthers >= role.MaximumPlayers;
    }

    public static void NotifyLobbyStateChanged(StartRunLobby lobby)
    {
        Changed?.Invoke(lobby);
    }

    public static long GetRevision(StartRunLobby lobby)
    {
        return States.TryGetValue(lobby, out CustomRunRoleAssignmentSnapshot? state) ? state.Revision : 0;
    }

    public static bool RequestLocalRoleLock(StartRunLobby lobby, string? roleId, out string error)
    {
        error = string.Empty;
        CustomRunDefinition? definition = CustomRunLobbyService.GetLoadedDefinition(lobby);
        if (definition is null)
        {
            error = "No Custom Run is loaded.";
            return false;
        }
        if (definition.RoleAssignmentMode != RoleAssignmentMode.PlayersChoose)
        {
            error = "Players cannot choose roles in this Custom Run.";
            return false;
        }
        if (!ValidateRoleId(definition, roleId, out error))
            return false;
        if (GetPlayer(lobby, lobby.NetService.NetId)?.IsReady == true)
        {
            error = "Unready before changing roles.";
            return false;
        }

        if (lobby.NetService.Type == NetGameType.Client)
        {
            lobby.NetService.SendMessage(new CustomRunRoleSelectionRequestMessage
            {
                definitionId = definition.Id,
                roleId = roleId ?? string.Empty,
                revision = GetRevision(lobby),
                locked = true
            });
            return true;
        }

        return TrySetAssignment(lobby, definition, lobby.NetService.NetId, roleId, out error);
    }

    public static bool RequestLocalRoleUnlock(StartRunLobby lobby, out string error)
    {
        error = string.Empty;
        CustomRunDefinition? definition = CustomRunLobbyService.GetLoadedDefinition(lobby);
        if (definition is null)
        {
            error = "No Custom Run is loaded.";
            return false;
        }
        if (definition.RoleAssignmentMode != RoleAssignmentMode.PlayersChoose)
        {
            error = "Players cannot choose roles in this Custom Run.";
            return false;
        }
        if (GetPlayer(lobby, lobby.NetService.NetId)?.IsReady == true)
        {
            error = "Unready before changing roles.";
            return false;
        }

        if (lobby.NetService.Type == NetGameType.Client)
        {
            lobby.NetService.SendMessage(new CustomRunRoleSelectionRequestMessage
            {
                definitionId = definition.Id,
                roleId = string.Empty,
                revision = GetRevision(lobby),
                locked = false
            });
            return true;
        }

        return TryClearAssignment(lobby, lobby.NetService.NetId, out error);
    }

    public static bool AssignAsHost(StartRunLobby lobby, ulong playerId, string? roleId, out string error)
    {
        error = string.Empty;
        if (lobby.NetService.Type == NetGameType.Client)
        {
            error = "Only the host can assign another player.";
            return false;
        }
        CustomRunDefinition? definition = CustomRunLobbyService.GetLoadedDefinition(lobby);
        if (definition is null)
        {
            error = "No Custom Run is loaded.";
            return false;
        }
        if (definition.RoleAssignmentMode != RoleAssignmentMode.HostAssigns)
        {
            error = "This Custom Run does not use host-assigned roles.";
            return false;
        }
        return TrySetAssignment(lobby, definition, playerId, roleId, out error);
    }

    public static bool UnlockAsHost(StartRunLobby lobby, ulong playerId, out string error)
    {
        error = string.Empty;
        if (lobby.NetService.Type == NetGameType.Client)
        {
            error = "Only the host can unlock another player's role.";
            return false;
        }
        CustomRunDefinition? definition = CustomRunLobbyService.GetLoadedDefinition(lobby);
        if (definition is null)
        {
            error = "No Custom Run is loaded.";
            return false;
        }
        if (definition.RoleAssignmentMode != RoleAssignmentMode.HostAssigns)
        {
            error = "This Custom Run does not use host-assigned roles.";
            return false;
        }
        return TryClearAssignment(lobby, playerId, out error);
    }

    private static bool TrySetAssignment(
        StartRunLobby lobby,
        CustomRunDefinition definition,
        ulong playerId,
        string? roleId,
        out string error)
    {
        error = string.Empty;
        StartRunLobbyPlayerInfo? player = GetPlayer(lobby, playerId);
        if (player is null)
        {
            error = "The selected player is no longer in the lobby.";
            return false;
        }
        if (player.IsReady)
        {
            error = "That player must unready before their role can change.";
            return false;
        }
        if (IsHostReady(lobby))
        {
            error = "The host must unready before roles can change.";
            return false;
        }
        if (!ValidateRoleId(definition, roleId, out error))
            return false;

        CustomRunRoleAssignmentSnapshot state = GetOrCreateState(lobby);
        CustomRunRoleAssignmentEntry? currentEntry = state.Assignments
            .FirstOrDefault(entry => entry.PlayerId == playerId);
        string normalizedRoleId = roleId ?? string.Empty;
        if (currentEntry is not null
            && string.Equals(currentEntry.RoleId, normalizedRoleId, StringComparison.Ordinal))
            return true;

        if (roleId is not null)
        {
            RoleDefinition role = definition.Roles.First(candidate => candidate.Id == roleId);
            int occupied = state.Assignments.Count(entry =>
                entry.PlayerId != playerId && string.Equals(entry.RoleId, roleId, StringComparison.Ordinal));
            if (role.MaximumPlayers > 0 && occupied >= role.MaximumPlayers)
            {
                error = $"Role '{role.Name}' is full.";
                return false;
            }
        }

        state.DefinitionId = definition.Id;
        state.Assignments.RemoveAll(entry => entry.PlayerId == playerId);
        state.Assignments.Add(new CustomRunRoleAssignmentEntry
        {
            PlayerId = playerId,
            RoleId = normalizedRoleId
        });
        state.Assignments = OrderAssignments(lobby, state.Assignments);
        state.Revision++;
        CustomRunLobbyService.CancelPreparation(lobby, "A role assignment changed; press Play again.");
        BroadcastSnapshot(lobby);
        Changed?.Invoke(lobby);
        return true;
    }

    private static bool TryClearAssignment(StartRunLobby lobby, ulong playerId, out string error)
    {
        error = string.Empty;
        StartRunLobbyPlayerInfo? player = GetPlayer(lobby, playerId);
        if (player is null)
        {
            error = "The selected player is no longer in the lobby.";
            return false;
        }
        if (player.IsReady)
        {
            error = "That player must unready before their role can change.";
            return false;
        }
        if (IsHostReady(lobby))
        {
            error = "The host must unready before roles can change.";
            return false;
        }

        CustomRunRoleAssignmentSnapshot state = GetOrCreateState(lobby);
        if (state.Assignments.RemoveAll(entry => entry.PlayerId == playerId) == 0)
            return true;
        state.Revision++;
        CustomRunLobbyService.CancelPreparation(lobby, "A role assignment changed; press Play again.");
        BroadcastSnapshot(lobby);
        Changed?.Invoke(lobby);
        return true;
    }

    private static bool ValidateRoleId(CustomRunDefinition definition, string? roleId, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(roleId))
            return true;
        if (definition.Roles.Any(role => string.Equals(role.Id, roleId, StringComparison.Ordinal)))
            return true;
        error = "The selected role no longer exists.";
        return false;
    }

    private static List<CustomRunRoleAssignmentEntry> Reconcile(
        StartRunLobby lobby,
        CustomRunDefinition definition,
        IReadOnlyDictionary<ulong, string> previous)
    {
        if (definition.RoleAssignmentMode == RoleAssignmentMode.Random)
            return [];

        List<StartRunLobbyPlayerInfo> players = Sts2Compatibility.EnumerateStartRunLobbyPlayers(lobby)
            .OrderBy(player => player.SlotId)
            .ThenBy(player => player.PlayerId)
            .ToList();
        HashSet<string> validRoleIds = definition.Roles.Select(role => role.Id).ToHashSet(StringComparer.Ordinal);
        List<CustomRunRoleAssignmentEntry> reconciled = players
            .Where(player => previous.TryGetValue(player.PlayerId, out string? roleId)
                             && (string.IsNullOrWhiteSpace(roleId) || validRoleIds.Contains(roleId)))
            .Select(player => new CustomRunRoleAssignmentEntry
            {
                PlayerId = player.PlayerId,
                RoleId = previous[player.PlayerId]
            })
            .ToList();
        foreach (RoleDefinition role in definition.Roles)
        {
            if (role.MaximumPlayers == 0)
                continue;
            CustomRunRoleAssignmentEntry[] overflow = reconciled
                .Where(entry => entry.RoleId == role.Id)
                .Skip(role.MaximumPlayers)
                .ToArray();
            foreach (CustomRunRoleAssignmentEntry entry in overflow)
                reconciled.Remove(entry);
        }
        return reconciled;
    }

    private static List<CustomRunRoleAssignmentEntry> OrderAssignments(
        StartRunLobby lobby,
        IEnumerable<CustomRunRoleAssignmentEntry> assignments)
    {
        Dictionary<ulong, int> slots = Sts2Compatibility.EnumerateStartRunLobbyPlayers(lobby)
            .ToDictionary(player => player.PlayerId, player => player.SlotId);
        return assignments
            .OrderBy(entry => slots.GetValueOrDefault(entry.PlayerId, int.MaxValue))
            .ThenBy(entry => entry.PlayerId)
            .ToList();
    }

    private static StartRunLobbyPlayerInfo? GetPlayer(StartRunLobby lobby, ulong playerId)
    {
        return Sts2Compatibility.EnumerateStartRunLobbyPlayers(lobby)
            .FirstOrDefault(player => player.PlayerId == playerId);
    }

    public static bool IsHostReady(StartRunLobby lobby)
    {
        ulong hostId = lobby.NetService.Type == NetGameType.Client
            && lobby.NetService is INetClientGameService clientService
            ? clientService.NetClient?.HostNetId ?? 0
            : lobby.NetService.NetId;
        return hostId != 0 && GetPlayer(lobby, hostId)?.IsReady == true;
    }

    private static CustomRunRoleAssignmentSnapshot GetOrCreateState(StartRunLobby lobby)
    {
        if (!States.TryGetValue(lobby, out CustomRunRoleAssignmentSnapshot? state))
        {
            state = new CustomRunRoleAssignmentSnapshot();
            States[lobby] = state;
        }
        return state;
    }

    private static void BroadcastSnapshot(StartRunLobby lobby)
    {
        if (lobby.NetService.Type != NetGameType.Host)
            return;
        foreach (ulong playerId in Sts2Compatibility.EnumerateStartRunLobbyPlayerIds(lobby))
        {
            if (playerId != lobby.NetService.NetId)
                SendSnapshot(lobby, playerId);
        }
    }

    private static void SendSnapshot(StartRunLobby lobby, ulong playerId)
    {
        if (lobby.NetService.Type != NetGameType.Host || playerId == lobby.NetService.NetId)
            return;
        string payload = JsonSerializer.Serialize(GetOrCreateState(lobby), CustomRunSerializationService.SharedJsonOptions);
        lobby.NetService.SendMessage(new CustomRunRoleAssignmentSnapshotMessage { payload = payload }, playerId);
    }

    private static void HandleSelectionRequest(CustomRunRoleSelectionRequestMessage message, ulong senderId)
    {
        StartRunLobby? lobby = RegisteredLobbies.FirstOrDefault(candidate =>
            candidate.NetService.Type == NetGameType.Host
            && Sts2Compatibility.EnumerateStartRunLobbyPlayerIds(candidate).Contains(senderId));
        if (lobby is null)
            return;
        CustomRunDefinition? definition = CustomRunLobbyService.GetHostDefinition(lobby);
        string? roleId = string.IsNullOrWhiteSpace(message.roleId) ? null : message.roleId;
        string error = string.Empty;
        bool accepted = definition is not null
                        && string.Equals(definition.Id, message.definitionId, StringComparison.Ordinal)
                        && definition.RoleAssignmentMode == RoleAssignmentMode.PlayersChoose
                        && (message.locked
                            ? TrySetAssignment(lobby, definition, senderId, roleId, out error)
                            : TryClearAssignment(lobby, senderId, out error));
        string failure = accepted
            ? string.Empty
            : definition is null || !string.Equals(definition.Id, message.definitionId, StringComparison.Ordinal)
                ? "The loaded Custom Run changed; choose again."
                : definition.RoleAssignmentMode != RoleAssignmentMode.PlayersChoose
                    ? "Players cannot choose roles in this Custom Run."
                    : error;
        if (!accepted)
            SendSnapshot(lobby, senderId);
        lobby.NetService.SendMessage(new CustomRunRoleAssignmentResultMessage
        {
            accepted = accepted,
            error = failure,
            revision = GetRevision(lobby)
        }, senderId);
    }

    private static void HandleSnapshot(CustomRunRoleAssignmentSnapshotMessage message, ulong senderId)
    {
        StartRunLobby? lobby = RegisteredLobbies.FirstOrDefault(candidate =>
            candidate.NetService.Type == NetGameType.Client
            && LoadoutNetworkBroadcast.IsExpectedHostSender(senderId, candidate.NetService));
        if (lobby is null || string.IsNullOrWhiteSpace(message.payload)
            || Encoding.UTF8.GetByteCount(message.payload) > MaximumPayloadBytes)
        {
            return;
        }
        try
        {
            CustomRunRoleAssignmentSnapshot? incoming = JsonSerializer.Deserialize<CustomRunRoleAssignmentSnapshot>(
                message.payload,
                CustomRunSerializationService.SharedJsonOptions);
            CustomRunDefinition? definition = CustomRunLobbyService.GetRemoteDefinition();
            if (incoming is null
                || definition is null
                || incoming.Assignments is null
                || !string.Equals(incoming.DefinitionId, definition.Id, StringComparison.Ordinal)
                || incoming.Revision < 0
                || incoming.Assignments.Count > 4
                || incoming.Assignments.Select(entry => entry.PlayerId).Distinct().Count() != incoming.Assignments.Count
                || incoming.Assignments.Any(entry => (entry.RoleId?.Length ?? 0) > 64)
                || incoming.Assignments.Any(entry => GetPlayer(lobby, entry.PlayerId) is null)
                || incoming.Assignments.Any(entry =>
                    !string.IsNullOrWhiteSpace(entry.RoleId)
                    && !definition.Roles.Any(role => string.Equals(role.Id, entry.RoleId, StringComparison.Ordinal)))
                || definition.Roles.Any(role => incoming.Assignments.Count(entry => entry.RoleId == role.Id)
                                                   > role.MaximumPlayers && role.MaximumPlayers > 0))
            {
                return;
            }
            CustomRunRoleAssignmentSnapshot current = GetOrCreateState(lobby);
            if (string.Equals(current.DefinitionId, incoming.DefinitionId, StringComparison.Ordinal)
                && incoming.Revision < current.Revision)
            {
                return;
            }
            States[lobby] = incoming;
            Changed?.Invoke(lobby);
        }
        catch (JsonException)
        {
        }
    }

    private static void HandleResult(CustomRunRoleAssignmentResultMessage message, ulong senderId)
    {
        StartRunLobby? lobby = RegisteredLobbies.FirstOrDefault(candidate =>
            candidate.NetService.Type == NetGameType.Client
            && LoadoutNetworkBroadcast.IsExpectedHostSender(senderId, candidate.NetService));
        if (lobby is null)
            return;
        if (message.accepted)
            AssignmentAccepted?.Invoke(lobby);
        else
            AssignmentRejected?.Invoke(lobby, message.error);
    }
}

public struct CustomRunRoleSelectionRequestMessage : INetMessage, IPacketSerializable
{
    public string definitionId;
    public string roleId;
    public long revision;
    public bool locked;

    public bool ShouldBroadcast => false;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.VeryDebug;
    public bool ShouldBuffer => false;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteString(definitionId ?? string.Empty);
        writer.WriteString(roleId ?? string.Empty);
        writer.WriteLong(revision);
        writer.WriteBool(locked);
    }

    public void Deserialize(PacketReader reader)
    {
        definitionId = reader.ReadString();
        roleId = reader.ReadString();
        revision = reader.ReadLong();
        locked = reader.ReadBool();
    }
}

public struct CustomRunRoleAssignmentSnapshotMessage : INetMessage, IPacketSerializable
{
    public string payload;

    public bool ShouldBroadcast => false;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.VeryDebug;
    public bool ShouldBuffer => true;

    public void Serialize(PacketWriter writer) => writer.WriteString(payload ?? string.Empty);
    public void Deserialize(PacketReader reader) => payload = reader.ReadString();
}

public struct CustomRunRoleAssignmentResultMessage : INetMessage, IPacketSerializable
{
    public bool accepted;
    public string error;
    public long revision;

    public bool ShouldBroadcast => false;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.VeryDebug;
    public bool ShouldBuffer => false;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteBool(accepted);
        writer.WriteString(error ?? string.Empty);
        writer.WriteLong(revision);
    }

    public void Deserialize(PacketReader reader)
    {
        accepted = reader.ReadBool();
        error = reader.ReadString();
        revision = reader.ReadLong();
    }
}
