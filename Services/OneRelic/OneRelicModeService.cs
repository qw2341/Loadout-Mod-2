#nullable enable

namespace Loadout.Services.OneRelic;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Loadout.Patches.OneRelic;
using Loadout.Services.Actions;
using Loadout.Services.Compatibility;
using Loadout.Services.ContentBans;
using Loadout.Services.Networking;
using Loadout.Services.RelicReplacement;
using Loadout.Services.Saving;
using Loadout.Services.Targets;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Runs;

public static class OneRelicModeService
{
    private const int MaxSnapshotLength = 256 * 1024;
    private const int CurrentSchemaVersion = 1;
    private const string RunDirectory = "loadout/services/one_relic";
    private const string RunFilePrefix = "one_relic_run";

    private static readonly AsyncLocal<int> ExactGrantDepth = new();
    private static readonly AsyncLocal<ObtainChain?> CurrentObtainChain = new();
    private static OneRelicRunSaveData _state = new();
    private static readonly Dictionary<ulong, RelicModel> SelectedRelics = new();
    private static readonly HashSet<LoadRunLobby> RegisteredLoadLobbies = [];
    private static readonly HashSet<INetGameService> RegisteredMessageServices = [];
    private static INetGameService? _runNetService;
    private static RunLobby? _runLobby;
    private static Delegate? _playerRejoinedHandler;
    private static string? _pendingHostSnapshotJson;

    public static event Action? Changed;

    public static bool IsActive => SelectedRelics.Count > 0;

    public static bool RequestToggle(ModelId relicId, LoadoutTargetSelection target)
    {
        if (ResolveCanonicalRelic(relicId) is null)
            return false;

        return LoadoutImmediateMutationService.Request(
            LoadoutImmediateMutationKind.ToggleOneRelic,
            relicId,
            1,
            target);
    }

    public static void ApplySynchronizedToggle(ModelId relicId, LoadoutTargetSelection target)
    {
        RelicModel? canonical = ResolveCanonicalRelic(relicId);
        RunState? runState = TryGetRunState();
        if (canonical is null || runState is null)
            return;

        IReadOnlyList<Player> targets = LoadoutTargetService.ResolvePlayers(target, runState);
        if (targets.Count == 0)
            return;

        bool clear = targets.All(player =>
            SelectedRelics.TryGetValue(player.NetId, out RelicModel? current)
            && current.Id == canonical.Id);
        bool changed = false;
        foreach (Player player in targets)
        {
            string playerKey = player.NetId.ToString();
            if (clear)
            {
                changed |= SelectedRelics.Remove(player.NetId);
                changed |= _state.Players.Remove(playerKey);
                continue;
            }

            if (SelectedRelics.TryGetValue(player.NetId, out RelicModel? current)
                && current.Id == canonical.Id)
            {
                continue;
            }

            SelectedRelics[player.NetId] = canonical;
            _state.Players[playerKey] = canonical.Id.ToString();
            changed = true;
        }

        if (!changed)
            return;

        ApplyStateChange(save: true);
    }

    public static bool TryGetSelectedRelic(Player player, out RelicModel relic)
        => SelectedRelics.TryGetValue(player.NetId, out relic!);

    public static IReadOnlySet<ModelId> GetSelectedRelicIds(LoadoutTargetSelection target)
    {
        RunState? runState = TryGetRunState();
        if (runState is null)
            return new HashSet<ModelId>();

        return LoadoutTargetService.ResolvePlayers(target, runState)
            .Select(player => SelectedRelics.GetValueOrDefault(player.NetId)?.Id)
            .OfType<ModelId>()
            .ToHashSet();
    }

    public static IDisposable BeginExactRelicGrant()
    {
        ExactGrantDepth.Value++;
        return new ExactGrantScope();
    }

    public static void RegisterLoadLobby(LoadRunLobby? lobby)
    {
        if (lobby is null || !RegisteredLoadLobbies.Add(lobby))
            return;

        RegisterMessageHandlers(lobby.NetService);
        if (lobby.NetService.Type == NetGameType.Host)
        {
            LoadRunState(
                lobby.Run.StartTime,
                lobby.Run.Players.Select(player => player.NetId).ToHashSet());
        }
        else if (lobby.NetService.Type == NetGameType.Client)
        {
            lobby.NetService.SendMessage(default(OneRelicSnapshotRequestMessage));
        }
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
            RegisterRunNetService(netService);
            BindRunLobby(RunManager.Instance.RunLobby);
        }
        catch (Exception exception)
        {
            GD.PushWarning($"OneRelic: failed to prepare run synchronization. {exception.Message}");
        }
    }

    public static void OnRunLaunched()
    {
        try
        {
            INetGameService netService = RunManager.Instance.NetService;
            RegisterRunNetService(netService);
            BindRunLobby(RunManager.Instance.RunLobby);

            if (netService.Type is NetGameType.Host or NetGameType.Singleplayer or NetGameType.Replay)
            {
                LoadRunState();
                ApplyStateChange(save: false);
            }
            else if (netService.Type == NetGameType.Client)
            {
                if (!ApplyPendingHostSnapshot())
                    netService.SendMessage(default(OneRelicSnapshotRequestMessage));
            }

            if (netService.Type == NetGameType.Host)
                BroadcastSnapshot();
        }
        catch (Exception exception)
        {
            GD.PushWarning($"OneRelic: failed to initialize run state. {exception.Message}");
        }
    }

    public static void OnRunCleaningUp()
    {
        UnbindRunLobby();
        UnregisterRunNetService();
        ClearRuntimeState(preservePendingHostSnapshot: false);
    }

    internal static bool TryPrepareObtain(
        Player player,
        ref RelicModel relic,
        out IDisposable? chainScope)
    {
        chainScope = null;
        if (!IsActive
            || ExactGrantDepth.Value > 0
            || IsInsideOneRelicObtainChain(player)
            || !TryGetSelectedRelic(player, out RelicModel selected))
        {
            return false;
        }

        if (!RelicReplacementProvenance.IsForced(RelicReplacementSource.OneRelic, relic)
            || relic.CanonicalInstance.Id != selected.Id)
        {
            relic = RelicReplacementProvenance.CreateOccurrence(
                RelicReplacementSource.OneRelic,
                selected,
                relic);
        }

        chainScope = BeginObtainChain(player);
        return true;
    }

    internal static bool ShouldReplaceTypedObtain(Player player)
        => IsActive
           && ExactGrantDepth.Value == 0
           && !IsInsideOneRelicObtainChain(player)
           && TryGetSelectedRelic(player, out _);

    internal static async Task<T> ObtainTypedReplacementAsync<T>(Player player)
        where T : RelicModel
    {
        RelicModel original = ModelDb.Relic<T>().ToMutable();
        RelicModel selected = SelectedRelics[player.NetId];
        RelicModel occurrence = RelicReplacementProvenance.CreateOccurrence(
            RelicReplacementSource.OneRelic,
            selected,
            original);

        Task<RelicModel> obtainTask;
        using (BeginObtainChain(player))
            obtainTask = RelicCmd.Obtain(occurrence, player);
        RelicModel obtained = await obtainTask;
        return obtained as T ?? null!;
    }

    internal static void OnRewardsSetTracked(MegaCrit.Sts2.Core.Rewards.RewardsSet set)
    {
        if (IsActive)
            OneRelicLiveOfferService.ReconcileRewardsSet(set);
    }

    private static bool IsInsideOneRelicObtainChain(Player player)
    {
        for (ObtainChain? chain = CurrentObtainChain.Value; chain is not null; chain = chain.Previous)
        {
            if (chain.PlayerNetId == player.NetId)
                return true;
        }
        return false;
    }

    private static IDisposable BeginObtainChain(Player player)
    {
        ObtainChain chain = new(player.NetId, CurrentObtainChain.Value);
        CurrentObtainChain.Value = chain;
        return new ObtainChainScope(chain);
    }

    private static void ApplyStateChange(bool save)
    {
        OneRelicRuntimePatchManager.Reconcile(IsActive);
        OneRelicLiveOfferService.ReconcileCurrentOffers();
        Callable.From(ContentBanService.ReconcileRelicOffersAfterExternalChange).CallDeferred();
        if (save)
            SaveRunStateIfAuthoritative();
        Changed?.Invoke();
    }

    private static void LoadRunState()
    {
        long? runStartTime = SaveUtility.GetCurrentRunStartTime();
        if (!runStartTime.HasValue)
        {
            _state = new OneRelicRunSaveData();
            SelectedRelics.Clear();
            return;
        }

        RunState? runState = TryGetRunState();
        LoadRunState(
            runStartTime.Value,
            runState?.Players.Select(player => player.NetId).ToHashSet());
    }

    private static void LoadRunState(long runStartTime, IReadOnlySet<ulong>? validPlayers)
    {
        string path = SaveUtility.GetRunSidecarPath(RunDirectory, RunFilePrefix, runStartTime);
        _state = SaveUtility.LoadProfileJson(path, new OneRelicRunSaveData()).Value;
        _state.SchemaVersion = CurrentSchemaVersion;
        _state.RunStartTime = runStartTime;
        _state.Players ??= new Dictionary<string, string>(StringComparer.Ordinal);
        RebuildSelectedRelics(validPlayers);
    }

    private static void RebuildSelectedRelics(IReadOnlySet<ulong>? validPlayers = null)
    {
        SelectedRelics.Clear();
        validPlayers ??= TryGetRunState()?.Players.Select(player => player.NetId).ToHashSet();
        foreach (string playerKey in _state.Players.Keys.ToList())
        {
            if (!ulong.TryParse(playerKey, out ulong playerNetId)
                || validPlayers is not null && !validPlayers.Contains(playerNetId)
                || ResolveCanonicalRelic(_state.Players[playerKey]) is not { } relic)
            {
                _state.Players.Remove(playerKey);
                continue;
            }
            SelectedRelics[playerNetId] = relic;
        }
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
            GD.PushWarning($"OneRelic: failed to save run state. {exception.Message}");
        }
    }

    private static RelicModel? ResolveCanonicalRelic(ModelId relicId)
        => ModelDb.AllRelics.FirstOrDefault(relic => relic.Id == relicId);

    private static RelicModel? ResolveCanonicalRelic(string relicId)
        => ModelDb.AllRelics.FirstOrDefault(relic =>
            string.Equals(relic.Id.ToString(), relicId, StringComparison.Ordinal)
            || string.Equals(relic.Id.Entry, relicId, StringComparison.OrdinalIgnoreCase));

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
        netService.RegisterMessageHandler<OneRelicSnapshotMessage>(HandleSnapshot);
        netService.RegisterMessageHandler<OneRelicSnapshotRequestMessage>(HandleSnapshotRequest);
    }

    private static void UnregisterMessageHandlers(INetGameService netService)
    {
        if (!RegisteredMessageServices.Remove(netService))
            return;
        netService.UnregisterMessageHandler<OneRelicSnapshotMessage>(HandleSnapshot);
        netService.UnregisterMessageHandler<OneRelicSnapshotRequestMessage>(HandleSnapshotRequest);
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
        if (_runLobby is null)
            return;
        if (_playerRejoinedHandler is not null)
            Sts2Compatibility.UnsubscribeRunLobbyPlayerRejoined(_runLobby, _playerRejoinedHandler);
        _playerRejoinedHandler = null;
        _runLobby = null;
    }

    private static void OnPlayerRejoined(ulong playerId)
    {
        if (_runNetService?.Type == NetGameType.Host && playerId != _runNetService.NetId)
            SendSnapshot(playerId);
    }

    private static void BroadcastSnapshot()
    {
        if (_runNetService?.Type != NetGameType.Host)
            return;
        LoadoutNetworkBroadcast.SendToRunClients(
            _runNetService,
            SendSnapshot,
            "One Relic snapshot");
    }

    private static void SendSnapshot(ulong recipient)
    {
        if (_runNetService?.Type != NetGameType.Host)
            return;
        SendSnapshot(_runNetService, recipient);
    }

    private static void SendSnapshot(INetGameService netService, ulong recipient)
    {
        if (netService.Type != NetGameType.Host || recipient == netService.NetId)
            return;
        netService.SendMessage(new OneRelicSnapshotMessage
        {
            snapshotJson = JsonSerializer.Serialize(_state)
        }, recipient);
    }

    private static void HandleSnapshotRequest(OneRelicSnapshotRequestMessage _, ulong senderId)
    {
        LoadRunLobby? loadLobby = RegisteredLoadLobbies.FirstOrDefault(candidate =>
            candidate.NetService.Type == NetGameType.Host
            && Sts2Compatibility.EnumerateLoadRunLobbyPlayerIds(candidate).Contains(senderId));
        if (loadLobby is not null)
        {
            SendSnapshot(loadLobby.NetService, senderId);
            return;
        }

        if (_runNetService?.Type != NetGameType.Host || !IsCurrentRunPlayer(senderId))
            return;
        SendSnapshot(_runNetService, senderId);
    }

    private static void HandleSnapshot(OneRelicSnapshotMessage message, ulong senderId)
    {
        if (!LoadoutNetworkBroadcast.IsExpectedHostSender(
                senderId,
                _runNetService,
                RegisteredLoadLobbies.Select(lobby => lobby.NetService)))
        {
            return;
        }

        try
        {
            if (string.IsNullOrWhiteSpace(message.snapshotJson)
                || message.snapshotJson.Length > MaxSnapshotLength)
            {
                return;
            }
            OneRelicRunSaveData? incoming = JsonSerializer.Deserialize<OneRelicRunSaveData>(message.snapshotJson);
            if (incoming is null)
                return;
            long? runStartTime = GetExpectedRunStartTime();
            if (runStartTime.HasValue && incoming.RunStartTime != 0 && incoming.RunStartTime != runStartTime.Value)
                return;

            if (!RunManager.Instance.IsInProgress)
            {
                _pendingHostSnapshotJson = message.snapshotJson;
                return;
            }

            ApplyHostSnapshot(incoming);
        }
        catch (Exception exception)
        {
            GD.PushWarning($"OneRelic: failed to apply host snapshot. {exception.Message}");
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
            OneRelicRunSaveData? incoming = JsonSerializer.Deserialize<OneRelicRunSaveData>(pending);
            if (incoming is null)
                return false;
            long? runStartTime = SaveUtility.GetCurrentRunStartTime();
            if (runStartTime.HasValue && incoming.RunStartTime != 0 && incoming.RunStartTime != runStartTime.Value)
                return false;
            ApplyHostSnapshot(incoming);
            return true;
        }
        catch (Exception exception)
        {
            GD.PushWarning($"OneRelic: failed to apply pending host snapshot. {exception.Message}");
            return false;
        }
    }

    private static void ApplyHostSnapshot(OneRelicRunSaveData incoming)
    {
        _state = incoming;
        _state.Players ??= new Dictionary<string, string>(StringComparer.Ordinal);
        RebuildSelectedRelics();
        ApplyStateChange(save: false);
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

    private static bool IsCurrentRunPlayer(ulong playerId)
    {
        try
        {
            RunState? runState = RunManager.Instance.IsInProgress
                ? RunManager.Instance.DebugOnlyGetState()
                : null;
            return runState?.Players.Any(player => player.NetId == playerId) == true;
        }
        catch
        {
            return false;
        }
    }

    private static void ClearRuntimeState(bool preservePendingHostSnapshot)
    {
        _state = new OneRelicRunSaveData();
        SelectedRelics.Clear();
        ExactGrantDepth.Value = 0;
        CurrentObtainChain.Value = null;
        RelicReplacementProvenance.Clear(RelicReplacementSource.OneRelic);
        OneRelicLiveOfferService.Reset();
        OneRelicRuntimePatchManager.Reconcile(active: false);
        if (!preservePendingHostSnapshot)
            _pendingHostSnapshotJson = null;
        Changed?.Invoke();
    }

    private sealed class ExactGrantScope : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            ExactGrantDepth.Value = Math.Max(0, ExactGrantDepth.Value - 1);
        }
    }

    private sealed record ObtainChain(ulong PlayerNetId, ObtainChain? Previous);

    private sealed class ObtainChainScope(ObtainChain chain) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            if (ReferenceEquals(CurrentObtainChain.Value, chain))
                CurrentObtainChain.Value = chain.Previous;
        }
    }

    public sealed class OneRelicRunSaveData : ISerializable
    {
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;
        public long RunStartTime { get; set; }
        public Dictionary<string, string> Players { get; set; } = new(StringComparer.Ordinal);

        public void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            info.AddValue(nameof(SchemaVersion), SchemaVersion);
            info.AddValue(nameof(RunStartTime), RunStartTime);
            info.AddValue(nameof(Players), Players);
        }
    }
}

public struct OneRelicSnapshotMessage : INetMessage, IPacketSerializable
{
    public string snapshotJson;
    public bool ShouldBroadcast => false;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.VeryDebug;
    public bool ShouldBuffer => true;

    public void Serialize(PacketWriter writer) => writer.WriteString(snapshotJson ?? string.Empty);
    public void Deserialize(PacketReader reader) => snapshotJson = reader.ReadString();
}

public struct OneRelicSnapshotRequestMessage : INetMessage, IPacketSerializable
{
    public bool ShouldBroadcast => false;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.VeryDebug;
    public bool ShouldBuffer => true;

    public readonly void Serialize(PacketWriter writer)
    {
    }

    public void Deserialize(PacketReader reader)
    {
    }
}
