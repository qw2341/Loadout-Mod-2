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
    private const int CurrentSchemaVersion = 1;
    private const string RunDirectory = "loadout/services/one_relic";
    private const string RunFilePrefix = "one_relic_run";

    private static readonly AsyncLocal<int> ExactGrantDepth = new();
    private static readonly AsyncLocal<ObtainChain?> CurrentObtainChain = new();
    private static OneRelicRunSaveData _state = new();
    private static readonly Dictionary<ulong, RelicModel> SelectedRelics = new();
    private static INetGameService? _runNetService;
    private static RunLobby? _runLobby;
    private static Delegate? _playerRejoinedHandler;

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

    public static void PrepareRunLaunch()
    {
        ClearRuntimeState();
        try
        {
            RegisterRunNetService(RunManager.Instance.NetService);
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
        ClearRuntimeState();
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
        _state = new OneRelicRunSaveData();
        SelectedRelics.Clear();
        if (!runStartTime.HasValue)
            return;

        string path = SaveUtility.GetRunSidecarPath(RunDirectory, RunFilePrefix, runStartTime.Value);
        _state = SaveUtility.LoadProfileJson(path, new OneRelicRunSaveData()).Value;
        _state.SchemaVersion = CurrentSchemaVersion;
        _state.RunStartTime = runStartTime.Value;
        _state.Players ??= new Dictionary<string, string>(StringComparer.Ordinal);
        RebuildSelectedRelics();
    }

    private static void RebuildSelectedRelics()
    {
        SelectedRelics.Clear();
        RunState? runState = TryGetRunState();
        HashSet<ulong>? validPlayers = runState?.Players.Select(player => player.NetId).ToHashSet();
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
        _runNetService.RegisterMessageHandler<OneRelicSnapshotMessage>(HandleSnapshot);
    }

    private static void UnregisterRunNetService()
    {
        if (_runNetService is null)
            return;
        _runNetService.UnregisterMessageHandler<OneRelicSnapshotMessage>(HandleSnapshot);
        _runNetService = null;
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
        _runNetService.SendMessage(new OneRelicSnapshotMessage
        {
            snapshotJson = JsonSerializer.Serialize(_state)
        }, recipient);
    }

    private static void HandleSnapshot(OneRelicSnapshotMessage message, ulong senderId)
    {
        if (_runNetService?.Type != NetGameType.Client
            || !LoadoutNetworkBroadcast.IsExpectedHostSender(senderId, _runNetService))
        {
            return;
        }

        try
        {
            OneRelicRunSaveData? incoming = JsonSerializer.Deserialize<OneRelicRunSaveData>(message.snapshotJson);
            if (incoming is null)
                return;
            long? runStartTime = SaveUtility.GetCurrentRunStartTime();
            if (runStartTime.HasValue && incoming.RunStartTime != 0 && incoming.RunStartTime != runStartTime.Value)
                return;
            _state = incoming;
            _state.Players ??= new Dictionary<string, string>(StringComparer.Ordinal);
            RebuildSelectedRelics();
            ApplyStateChange(save: false);
        }
        catch (Exception exception)
        {
            GD.PushWarning($"OneRelic: failed to apply host snapshot. {exception.Message}");
        }
    }

    private static void ClearRuntimeState()
    {
        _state = new OneRelicRunSaveData();
        SelectedRelics.Clear();
        ExactGrantDepth.Value = 0;
        CurrentObtainChain.Value = null;
        RelicReplacementProvenance.Clear(RelicReplacementSource.OneRelic);
        OneRelicLiveOfferService.Reset();
        OneRelicRuntimePatchManager.Reconcile(active: false);
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
    public bool ShouldBuffer => false;

    public void Serialize(PacketWriter writer) => writer.WriteString(snapshotJson ?? string.Empty);
    public void Deserialize(PacketReader reader) => snapshotJson = reader.ReadString();
}
