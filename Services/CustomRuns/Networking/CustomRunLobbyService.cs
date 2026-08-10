#nullable enable

namespace Loadout.Services.CustomRuns.Networking;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Loadout.Services.Compatibility;
using Loadout.Services.CustomRuns.Compilation;
using Loadout.Services.CustomRuns.Models;
using Loadout.Services.CustomRuns.Persistence;
using Loadout.Services.CustomRuns.Runtime;
using Loadout.Services.Networking;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;

public sealed record CustomRunPreparationResult(bool Succeeded, string Error)
{
    public static CustomRunPreparationResult Success { get; } = new(true, string.Empty);
}

public static class CustomRunLobbyService
{
    private static readonly HashSet<StartRunLobby> RegisteredLobbies = [];
    private static readonly Dictionary<StartRunLobby, Delegate> ConnectedHandlers = new();
    private static readonly Dictionary<StartRunLobby, Delegate> DisconnectedHandlers = new();
    private static readonly Dictionary<StartRunLobby, CustomRunDefinition> HostDefinitions = new();
    private static readonly Dictionary<StartRunLobby, HostPreparationState> HostPreparations = new();
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

    public static ResolvedCustomRunSnapshot? GetPendingSnapshot(StartRunLobby lobby)
    {
        return HostPreparations.TryGetValue(lobby, out HostPreparationState? state)
            ? state.Snapshot
            : CustomRunRuntimeSnapshotService.PendingSnapshot;
    }

    public static void RegisterLobby(StartRunLobby? lobby)
    {
        if (lobby is null || !RegisteredLobbies.Add(lobby))
            return;

        lobby.NetService.RegisterMessageHandler<CustomRunDefinitionMessage>(HandleDefinitionMessage);
        lobby.NetService.RegisterMessageHandler<CustomRunSnapshotMessage>(HandleSnapshotMessage);
        lobby.NetService.RegisterMessageHandler<CustomRunSnapshotAckMessage>(HandleSnapshotAckMessage);

        ConnectedHandlers[lobby] = Sts2Compatibility.SubscribeStartRunLobbyPlayerConnected(
            lobby,
            playerId => OnPlayerConnected(lobby, playerId));
        DisconnectedHandlers[lobby] = Sts2Compatibility.SubscribeStartRunLobbyPlayerDisconnected(
            lobby,
            playerId => OnPlayerDisconnected(lobby, playerId));
    }

    public static void UnregisterLobby(StartRunLobby? lobby)
    {
        if (lobby is null || !RegisteredLobbies.Remove(lobby))
            return;

        CancelPreparation(lobby, "The Custom Run lobby was closed.");
        lobby.NetService.UnregisterMessageHandler<CustomRunDefinitionMessage>(HandleDefinitionMessage);
        lobby.NetService.UnregisterMessageHandler<CustomRunSnapshotMessage>(HandleSnapshotMessage);
        lobby.NetService.UnregisterMessageHandler<CustomRunSnapshotAckMessage>(HandleSnapshotAckMessage);
        if (ConnectedHandlers.Remove(lobby, out Delegate? connected))
            Sts2Compatibility.UnsubscribeStartRunLobbyPlayerConnected(lobby, connected);
        if (DisconnectedHandlers.Remove(lobby, out Delegate? disconnected))
            Sts2Compatibility.UnsubscribeStartRunLobbyPlayerDisconnected(lobby, disconnected);
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

    public static async Task<CustomRunPreparationResult> PrepareHostRunAsync(
        StartRunLobby lobby,
        ResolvedCustomRunSnapshot snapshot)
    {
        if (lobby.NetService.Type == NetGameType.Client)
            return new CustomRunPreparationResult(false, "Only the host can Play a local Custom Run.");
        if (!string.Equals(CustomRunHashService.Compute(snapshot), snapshot.SnapshotHash, StringComparison.Ordinal))
            return new CustomRunPreparationResult(false, "The compiled Custom Run snapshot hash was invalid.");

        CancelPreparation(lobby, "A new Custom Run launch replaced the previous attempt.");
        ResolvedPlayerSetup? localSetup = snapshot.Players
            .FirstOrDefault(player => player.PlayerId == lobby.NetService.NetId);
        if (localSetup is null)
            return new CustomRunPreparationResult(false, "The local player is missing from the compiled setup.");

        CharacterModel? localCharacter = CustomRunCompiler.ResolveCharacter(localSetup.CharacterModelId);
        if (localCharacter is null)
            return new CustomRunPreparationResult(false, $"Unknown local character '{localSetup.CharacterModelId}'.");

        MainFile.Logger.Info($"[Loadout] Preparing Custom Run snapshot {snapshot.SnapshotHash}.");
        StartRunLobbyPlayerInfo? currentLocalPlayer = Sts2Compatibility.EnumerateStartRunLobbyPlayers(lobby)
            .FirstOrDefault(player => player.PlayerId == lobby.NetService.NetId);
        if (currentLocalPlayer?.Character?.Id != localCharacter.Id)
            lobby.SetLocalCharacter(localCharacter);
        if (!string.Equals(lobby.Seed, snapshot.RunSeed, StringComparison.Ordinal))
            lobby.SetSeed(snapshot.RunSeed);
        CustomRunRuntimeSnapshotService.SetPending(snapshot);
        MainFile.Logger.Info($"[Loadout] Prepared Custom Run snapshot {snapshot.SnapshotHash}.");

        if (lobby.NetService.Type != NetGameType.Host)
            return CustomRunPreparationResult.Success;

        HashSet<ulong> expected = Sts2Compatibility.EnumerateStartRunLobbyPlayerIds(lobby)
            .Where(playerId => playerId != lobby.NetService.NetId)
            .ToHashSet();
        if (expected.Count == 0)
            return CustomRunPreparationResult.Success;

        HostPreparationState state = new(snapshot, expected);
        HostPreparations[lobby] = state;
        string payload = CustomRunSnapshotSerializationService.Serialize(snapshot);
        foreach (ulong playerId in expected)
            lobby.NetService.SendMessage(new CustomRunSnapshotMessage { payload = payload }, playerId);

        _ = TimeoutPreparationAsync(lobby, state);
        CustomRunPreparationResult result = await state.Completion.Task;
        if (result.Succeeded)
        {
            HashSet<ulong> currentRoster = Sts2Compatibility.EnumerateStartRunLobbyPlayerIds(lobby).ToHashSet();
            if (!currentRoster.SetEquals(state.InitialPlayerIds))
                result = new CustomRunPreparationResult(false, "The lobby roster changed; press Play again.");
        }
        if (HostPreparations.TryGetValue(lobby, out HostPreparationState? current)
            && ReferenceEquals(current, state))
        {
            HostPreparations.Remove(lobby);
        }
        state.Timeout.Cancel();
        state.Timeout.Dispose();
        if (!result.Succeeded)
            CustomRunRuntimeSnapshotService.ClearPending();
        return result;
    }

    public static void CancelPreparation(StartRunLobby lobby, string reason)
    {
        if (!HostPreparations.Remove(lobby, out HostPreparationState? state))
            return;

        state.Completion.TrySetResult(new CustomRunPreparationResult(false, reason));
        CustomRunRuntimeSnapshotService.ClearPending();
    }

    private static async Task TimeoutPreparationAsync(StartRunLobby lobby, HostPreparationState state)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), state.Timeout.Token);
            if (HostPreparations.TryGetValue(lobby, out HostPreparationState? current)
                && ReferenceEquals(current, state))
            {
                string pending = string.Join(", ", state.PendingPlayerIds.OrderBy(id => id));
                state.Completion.TrySetResult(new CustomRunPreparationResult(
                    false,
                    $"Timed out waiting for Custom Run confirmation from player {pending}."));
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static void OnPlayerConnected(StartRunLobby lobby, ulong playerId)
    {
        SendDefinitionToPlayer(lobby, playerId);
        CancelPreparation(lobby, "The lobby roster changed; press Play again.");
    }

    private static void OnPlayerDisconnected(StartRunLobby lobby, ulong playerId)
    {
        CancelPreparation(lobby, $"Player {playerId} disconnected; press Play again.");
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

    private static void HandleSnapshotMessage(CustomRunSnapshotMessage message, ulong senderId)
    {
        StartRunLobby? lobby = RegisteredLobbies.FirstOrDefault(candidate =>
            candidate.NetService.Type == NetGameType.Client
            && LoadoutNetworkBroadcast.IsExpectedHostSender(senderId, candidate.NetService));
        if (lobby is null)
            return;

        if (!CustomRunSnapshotSerializationService.TryDeserialize(message.payload, out ResolvedCustomRunSnapshot snapshot, out string error))
        {
            SendAck(lobby, senderId, string.Empty, false, error);
            return;
        }

        IReadOnlyList<string> missingMods = CustomRunCompiler.GetMissingRequiredMods(snapshot.RequiredModIds);
        if (missingMods.Count > 0)
        {
            SendAck(lobby, senderId, snapshot.SnapshotHash, false, $"Missing required mod: {missingMods[0]}.");
            return;
        }

        ResolvedPlayerSetup? localSetup = snapshot.Players
            .FirstOrDefault(player => player.PlayerId == lobby.NetService.NetId);
        CharacterModel? localCharacter = CustomRunCompiler.ResolveCharacter(localSetup?.CharacterModelId);
        if (localSetup is null || localCharacter is null)
        {
            SendAck(lobby, senderId, snapshot.SnapshotHash, false, "The local player setup was missing or invalid.");
            return;
        }

        lobby.SetLocalCharacter(localCharacter);
        CustomRunRuntimeSnapshotService.SetPending(snapshot);
        MainFile.Logger.Info($"[Loadout] Accepted Custom Run snapshot {snapshot.SnapshotHash}.");
        SendAck(lobby, senderId, snapshot.SnapshotHash, true, string.Empty);
    }

    private static void HandleSnapshotAckMessage(CustomRunSnapshotAckMessage message, ulong senderId)
    {
        StartRunLobby? lobby = RegisteredLobbies.FirstOrDefault(candidate =>
            candidate.NetService.Type == NetGameType.Host
            && HostPreparations.TryGetValue(candidate, out HostPreparationState? state)
            && state.PendingPlayerIds.Contains(senderId));
        if (lobby is null || !HostPreparations.TryGetValue(lobby, out HostPreparationState? preparation))
            return;
        if (!string.Equals(message.snapshotHash, preparation.Snapshot.SnapshotHash, StringComparison.Ordinal))
        {
            preparation.Completion.TrySetResult(new CustomRunPreparationResult(
                false,
                $"Player {senderId} acknowledged a different Custom Run snapshot."));
            return;
        }
        if (!message.accepted)
        {
            preparation.Completion.TrySetResult(new CustomRunPreparationResult(
                false,
                $"Player {senderId} rejected the Custom Run: {message.error}"));
            return;
        }

        preparation.PendingPlayerIds.Remove(senderId);
        if (preparation.PendingPlayerIds.Count == 0)
            preparation.Completion.TrySetResult(CustomRunPreparationResult.Success);
    }

    private static void SendAck(
        StartRunLobby lobby,
        ulong hostId,
        string hash,
        bool accepted,
        string error)
    {
        lobby.NetService.SendMessage(new CustomRunSnapshotAckMessage
        {
            snapshotHash = hash,
            accepted = accepted,
            error = error.Length > 500 ? error[..500] : error
        }, hostId);
    }

    private static bool IsExpectedHostSender(ulong senderId)
    {
        return LoadoutNetworkBroadcast.IsExpectedHostSender(
            senderId,
            null,
            RegisteredLobbies.Select(lobby => lobby.NetService));
    }

    private sealed class HostPreparationState(
        ResolvedCustomRunSnapshot snapshot,
        HashSet<ulong> pendingPlayerIds)
    {
        public ResolvedCustomRunSnapshot Snapshot { get; } = snapshot;
        public HashSet<ulong> PendingPlayerIds { get; } = pendingPlayerIds;
        public HashSet<ulong> InitialPlayerIds { get; } =
            snapshot.Players.Select(player => player.PlayerId).ToHashSet();
        public TaskCompletionSource<CustomRunPreparationResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenSource Timeout { get; } = new();
    }
}

public struct CustomRunDefinitionMessage : INetMessage, IPacketSerializable
{
    public string payload;

    public bool ShouldBroadcast => false;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.VeryDebug;
    public bool ShouldBuffer => true;

    public void Serialize(PacketWriter writer) => writer.WriteString(payload ?? string.Empty);
    public void Deserialize(PacketReader reader) => payload = reader.ReadString();
}

public struct CustomRunSnapshotMessage : INetMessage, IPacketSerializable
{
    public string payload;

    public bool ShouldBroadcast => false;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.VeryDebug;
    public bool ShouldBuffer => false;

    public void Serialize(PacketWriter writer) => writer.WriteString(payload ?? string.Empty);
    public void Deserialize(PacketReader reader) => payload = reader.ReadString();
}

public struct CustomRunSnapshotAckMessage : INetMessage, IPacketSerializable
{
    public string snapshotHash;
    public bool accepted;
    public string error;

    public bool ShouldBroadcast => false;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.VeryDebug;
    public bool ShouldBuffer => false;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteString(snapshotHash ?? string.Empty);
        writer.WriteBool(accepted);
        writer.WriteString(error ?? string.Empty);
    }

    public void Deserialize(PacketReader reader)
    {
        snapshotHash = reader.ReadString();
        accepted = reader.ReadBool();
        error = reader.ReadString();
    }
}
