#nullable enable

namespace Loadout.Services.ContentBans;

using BaseLib.Abstracts;
using Godot;
using Loadout.Services.Compatibility;
using Loadout.Services.Networking;
using Loadout.Services.Saving;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Text.Json;
using System.Text.Json.Serialization;

internal enum ContentBanKind : byte
{
    Card,
    Relic,
    Potion
}

internal enum ContentBanScope : byte
{
    None,
    Run,
    Permanent
}

internal readonly record struct ContentBanTarget(ContentBanKind Kind, string Id)
{
    public static ContentBanTarget Card(CardModel card) => new(ContentBanKind.Card, card.CanonicalInstance.Id.ToString());
    public static ContentBanTarget Relic(RelicModel relic) => new(ContentBanKind.Relic, relic.CanonicalInstance.Id.ToString());
    public static ContentBanTarget Potion(PotionModel potion) => new(ContentBanKind.Potion, potion.CanonicalInstance.Id.ToString());

    public bool IsValid => !string.IsNullOrWhiteSpace(Id);
}

internal readonly record struct ContentBanChangedEvent(
    ContentBanTarget Target,
    ContentBanScope PreviousScope,
    ContentBanScope Scope)
{
    public bool BecameBanned => PreviousScope == ContentBanScope.None && Scope != ContentBanScope.None;
}

internal static class ContentBanService
{
    private const int CurrentSchemaVersion = 1;
    private const string ProfilePath = "loadout/content_bans_v1.json";
    private const int MaxSnapshotLength = 512 * 1024;
    private static readonly string EmptyRunStateJson = JsonSerializer.Serialize(new RunBanSaveData());
    private static readonly object Gate = new();
    private static readonly ConditionalWeakTable<RunState, RunBanState> RunStates = new();
    private static readonly HashSet<StartRunLobby> Lobbies = [];
    private static readonly Dictionary<StartRunLobby, Delegate> ConnectedHandlers = new();
    private static readonly HashSet<INetGameService> RegisteredMessageServices = [];

    private static ProfileBanSaveData _profile = new();
    private static NetworkBanSnapshot _hostSnapshot = new();
    private static INetGameService? _runNetService;
    private static RunLobby? _runLobby;
    private static Delegate? _playerRejoinedHandler;
    private static bool _loaded;
    private static bool _registered;
    private static bool _hasHostSnapshot;
    private static volatile int _effectiveKindMask;
    private static long _revision;
    private static IReadOnlyList<ContentBanOfferReconciliation> _lastOfferReconciliations = [];

    internal static event Action<ContentBanChangedEvent>? Changed;

    internal static void Register()
    {
        if (_registered)
            return;

        _registered = true;
        SaveManager.Instance.ProfileIdChanged += OnProfileChanged;
        EnsureLoaded();
    }

    internal static void Unregister()
    {
        if (!_registered)
            return;

        SaveManager.Instance.ProfileIdChanged -= OnProfileChanged;
        foreach (StartRunLobby lobby in Lobbies.ToList())
            UnregisterLobby(lobby, clearOverlay: false);
        UnregisterRunNetService(clearOverlay: true);
        foreach (INetGameService service in RegisteredMessageServices.ToList())
            UnregisterMessageHandler(service);
        Lobbies.Clear();
        ConnectedHandlers.Clear();
        _registered = false;
    }

    internal static ContentBanScope GetScope(ContentBanTarget target)
    {
        if (!target.IsValid)
            return ContentBanScope.None;

        EnsureLoaded();
        if ((_effectiveKindMask & (1 << (int)target.Kind)) == 0)
            return ContentBanScope.None;

        lock (Gate)
        {
            if (IsGuest())
                return GetScope(_hostSnapshot, target);

            if (Contains(_profile, target))
                return ContentBanScope.Permanent;

            return TryGetCurrentRunState(out RunState? runState)
                   && Contains(GetRunState(runState!), target)
                ? ContentBanScope.Run
                : ContentBanScope.None;
        }
    }

    internal static bool IsBanned(ContentBanTarget target) => GetScope(target) != ContentBanScope.None;
    internal static bool IsBanned(CardModel card) => IsBanned(ContentBanTarget.Card(card));
    internal static bool IsBanned(RelicModel relic) => IsBanned(ContentBanTarget.Relic(relic));
    internal static bool IsBanned(PotionModel potion) => IsBanned(ContentBanTarget.Potion(potion));

    internal static bool HasAnyBans(ContentBanKind kind)
    {
        EnsureLoaded();
        return (_effectiveKindMask & (1 << (int)kind)) != 0;
    }

    internal static bool HasAnyBans()
    {
        EnsureLoaded();
        return _effectiveKindMask != 0;
    }

    internal static bool IsPermanentlyBanned(ContentBanTarget target)
        => GetScope(target) == ContentBanScope.Permanent;

    internal static bool Toggle(ContentBanTarget target, ContentBanScope requestedScope)
    {
        if (!target.IsValid || requestedScope == ContentBanScope.None || IsGuest())
            return false;
        if (requestedScope == ContentBanScope.Run && !TryGetCurrentRunState(out _))
            return false;

        EnsureLoaded();
        ContentBanChangedEvent change;
        lock (Gate)
        {
            ContentBanScope previous = GetLocalScopeLocked(target);
            ContentBanScope next = previous == requestedScope ? ContentBanScope.None : requestedScope;
            bool permanentChanged = Contains(_profile, target) != (next == ContentBanScope.Permanent);

            Remove(_profile, target);
            if (TryGetCurrentRunState(out RunState? runState))
                Remove(GetRunState(runState!), target);

            if (next == ContentBanScope.Permanent)
            {
                Add(_profile, target);
            }
            else if (next == ContentBanScope.Run && runState is not null)
            {
                Add(GetRunState(runState), target);
            }
            if (permanentChanged)
                SaveProfileLocked();

            RecomputeEffectiveKindMaskLocked();
            _revision++;
            change = new ContentBanChangedEvent(target, previous, next);
        }

        IReadOnlyList<ContentBanOfferReconciliation> reconciliations = ContentBanLiveOfferService.ReconcileHost(change);
        lock (Gate)
            _lastOfferReconciliations = reconciliations;
        Changed?.Invoke(change);
        BroadcastSnapshot();
        return true;
    }

    internal static string GetSerializedRunState(RunState runState)
    {
        if (!HasAnyBans())
            return EmptyRunStateJson;

        lock (Gate)
            return JsonSerializer.Serialize(GetRunState(runState).ToSaveData());
    }

    internal static void LoadSerializedRunState(RunState runState, string? payload)
    {
        EnsureLoaded();
        RunBanSaveData save;
        try
        {
            save = string.IsNullOrWhiteSpace(payload)
                ? new RunBanSaveData()
                : JsonSerializer.Deserialize<RunBanSaveData>(payload) ?? new RunBanSaveData();
        }
        catch (Exception exception)
        {
            GD.PushWarning($"ContentBans: ignored malformed run ban save. {exception.Message}");
            save = new RunBanSaveData();
        }
        lock (Gate)
        {
            RunBanState state = GetRunState(runState);
            state.Load(Normalize(save));
            state.Cards.ExceptWith(_profile.Cards);
            state.Relics.ExceptWith(_profile.Relics);
            state.Potions.ExceptWith(_profile.Potions);
            RecomputeEffectiveKindMaskLocked();
        }
    }

    internal static void RegisterLobby(StartRunLobby lobby)
    {
        if (!_registered || !Lobbies.Add(lobby))
            return;

        lock (Gate)
            RecomputeEffectiveKindMaskLocked();
        RegisterMessageHandler(lobby.NetService);
        Delegate connected = Sts2Compatibility.SubscribeStartRunLobbyPlayerConnected(
            lobby,
            playerId => SendSnapshot(lobby.NetService, playerId));
        ConnectedHandlers[lobby] = connected;
        if (lobby.NetService.Type == NetGameType.Host)
        {
            foreach (ulong playerId in Sts2Compatibility.EnumerateStartRunLobbyPlayerIds(lobby)
                         .Where(playerId => playerId != lobby.NetService.NetId))
                SendSnapshot(lobby.NetService, playerId);
        }
    }

    internal static void UnregisterLobby(StartRunLobby lobby, bool clearOverlay)
    {
        if (!Lobbies.Remove(lobby))
            return;

        if (ConnectedHandlers.Remove(lobby, out Delegate? handler))
            Sts2Compatibility.UnsubscribeStartRunLobbyPlayerConnected(lobby, handler);
        if (clearOverlay && !ReferenceEquals(_runNetService, lobby.NetService))
            UnregisterMessageHandler(lobby.NetService);
        if (clearOverlay && lobby.NetService.Type == NetGameType.Client)
            ClearHostSnapshot();
    }

    internal static void PrepareRunLaunch()
    {
        try
        {
            RegisterRunNetService(RunManager.Instance.NetService);
            BindRunLobby(RunManager.Instance.RunLobby);
        }
        catch (Exception exception)
        {
            GD.PushWarning($"ContentBans: failed to prepare multiplayer sync. {exception.Message}");
        }
    }

    internal static void OnRunLaunched()
    {
        try
        {
            RegisterRunNetService(RunManager.Instance.NetService);
            BindRunLobby(RunManager.Instance.RunLobby);
            lock (Gate)
                RecomputeEffectiveKindMaskLocked();
            if (RunManager.Instance.NetService.Type == NetGameType.Host)
                BroadcastSnapshot();
            else if (RunManager.Instance.NetService.Type is NetGameType.Singleplayer or NetGameType.Replay)
                ClearHostSnapshot();
        }
        catch (Exception exception)
        {
            GD.PushWarning($"ContentBans: failed to initialize multiplayer sync. {exception.Message}");
        }
    }

    internal static void OnRunCleaningUp()
    {
        UnregisterRunNetService(clearOverlay: true);
        ContentBanLiveOfferService.Reset();
        lock (Gate)
        {
            _lastOfferReconciliations = [];
            RecomputeEffectiveKindMaskLocked(includeRun: false);
        }
    }

    internal static void SendSnapshotToRunPlayer(ulong playerId)
    {
        if (_runNetService is not null)
            SendSnapshot(_runNetService, playerId);
    }

    private static bool IsGuest()
    {
        if (Lobbies.Any(lobby => lobby.NetService.Type == NetGameType.Client))
            return true;
        try
        {
            return RunManager.Instance.NetService.Type == NetGameType.Client;
        }
        catch
        {
            return false;
        }
    }

    private static void EnsureLoaded()
    {
        if (_loaded)
            return;

        lock (Gate)
        {
            if (_loaded)
                return;
            _profile = Normalize(SaveUtility.LoadProfileJson(ProfilePath, new ProfileBanSaveData()).Value);
            _loaded = true;
            RecomputeEffectiveKindMaskLocked();
        }
    }

    private static void OnProfileChanged(int _)
    {
        List<ContentBanTarget> previousPermanent;
        lock (Gate)
        {
            previousPermanent = Enumerate(_profile).ToList();
            _loaded = false;
            _profile = new ProfileBanSaveData();
        }
        EnsureLoaded();
        List<ContentBanTarget> currentPermanent;
        lock (Gate)
        {
            currentPermanent = Enumerate(_profile).ToList();
            _revision++;
        }
        foreach (ContentBanTarget target in previousPermanent.Concat(currentPermanent).Distinct())
        {
            ContentBanScope previous = previousPermanent.Contains(target)
                ? ContentBanScope.Permanent
                : ContentBanScope.None;
            ContentBanScope current = GetScope(target);
            if (previous != current)
                Changed?.Invoke(new ContentBanChangedEvent(target, previous, current));
        }
        BroadcastSnapshot();
    }

    private static void SaveProfileLocked()
    {
        _profile.SchemaVersion = CurrentSchemaVersion;
        SaveUtility.SaveProfileJson(ProfilePath, _profile);
    }

    private static ContentBanScope GetLocalScopeLocked(ContentBanTarget target)
    {
        if (Contains(_profile, target))
            return ContentBanScope.Permanent;
        return TryGetCurrentRunState(out RunState? runState) && Contains(GetRunState(runState!), target)
            ? ContentBanScope.Run
            : ContentBanScope.None;
    }

    private static bool TryGetCurrentRunState(out RunState? runState)
    {
        try
        {
            if (RunManager.Instance.IsInProgress)
            {
                runState = RunManager.Instance.DebugOnlyGetState();
                return runState is not null;
            }
        }
        catch
        {
            // No active run is a normal main-menu state.
        }

        runState = null;
        return false;
    }

    private static RunBanState GetRunState(RunState runState) => RunStates.GetOrCreateValue(runState);

    private static HashSet<string> GetSet(ProfileBanSaveData save, ContentBanKind kind) => kind switch
    {
        ContentBanKind.Card => save.Cards,
        ContentBanKind.Relic => save.Relics,
        _ => save.Potions
    };

    private static HashSet<string> GetSet(RunBanState state, ContentBanKind kind) => kind switch
    {
        ContentBanKind.Card => state.Cards,
        ContentBanKind.Relic => state.Relics,
        _ => state.Potions
    };

    private static HashSet<string> GetSet(NetworkBanSnapshot state, ContentBanKind kind, ContentBanScope scope)
    {
        return (kind, scope) switch
        {
            (ContentBanKind.Card, ContentBanScope.Permanent) => state.PermanentCards,
            (ContentBanKind.Relic, ContentBanScope.Permanent) => state.PermanentRelics,
            (ContentBanKind.Potion, ContentBanScope.Permanent) => state.PermanentPotions,
            (ContentBanKind.Card, _) => state.RunCards,
            (ContentBanKind.Relic, _) => state.RunRelics,
            _ => state.RunPotions
        };
    }

    private static bool Contains(ProfileBanSaveData save, ContentBanTarget target) => GetSet(save, target.Kind).Contains(target.Id);
    private static bool Contains(RunBanState state, ContentBanTarget target) => GetSet(state, target.Kind).Contains(target.Id);
    private static void Add(ProfileBanSaveData save, ContentBanTarget target) => GetSet(save, target.Kind).Add(target.Id);
    private static void Add(RunBanState state, ContentBanTarget target) => GetSet(state, target.Kind).Add(target.Id);
    private static void Remove(ProfileBanSaveData save, ContentBanTarget target) => GetSet(save, target.Kind).Remove(target.Id);
    private static void Remove(RunBanState state, ContentBanTarget target) => GetSet(state, target.Kind).Remove(target.Id);

    private static ContentBanScope GetScope(NetworkBanSnapshot state, ContentBanTarget target)
    {
        if (GetSet(state, target.Kind, ContentBanScope.Permanent).Contains(target.Id))
            return ContentBanScope.Permanent;
        return GetSet(state, target.Kind, ContentBanScope.Run).Contains(target.Id)
            ? ContentBanScope.Run
            : ContentBanScope.None;
    }

    private static ProfileBanSaveData Normalize(ProfileBanSaveData save)
    {
        save.SchemaVersion = CurrentSchemaVersion;
        save.Cards = NormalizeSet(save.Cards);
        save.Relics = NormalizeSet(save.Relics);
        save.Potions = NormalizeSet(save.Potions);
        return save;
    }

    private static RunBanSaveData Normalize(RunBanSaveData save)
    {
        save.SchemaVersion = CurrentSchemaVersion;
        save.Cards = NormalizeSet(save.Cards);
        save.Relics = NormalizeSet(save.Relics);
        save.Potions = NormalizeSet(save.Potions);
        return save;
    }

    private static HashSet<string> NormalizeSet(HashSet<string>? values)
    {
        return values is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : values.Where(value => !string.IsNullOrWhiteSpace(value)).ToHashSet(StringComparer.Ordinal);
    }

    private static IEnumerable<ContentBanTarget> Enumerate(ProfileBanSaveData save)
    {
        foreach (string id in save.Cards)
            yield return new ContentBanTarget(ContentBanKind.Card, id);
        foreach (string id in save.Relics)
            yield return new ContentBanTarget(ContentBanKind.Relic, id);
        foreach (string id in save.Potions)
            yield return new ContentBanTarget(ContentBanKind.Potion, id);
    }

    private static NetworkBanSnapshot CreateSnapshot()
    {
        EnsureLoaded();
        lock (Gate)
        {
            NetworkBanSnapshot snapshot = new()
            {
                Revision = _revision,
                PermanentCards = [.. _profile.Cards],
                PermanentRelics = [.. _profile.Relics],
                PermanentPotions = [.. _profile.Potions]
            };
            if (TryGetCurrentRunState(out RunState? runState))
            {
                RunBanState current = GetRunState(runState!);
                snapshot.RunCards = [.. current.Cards.Except(snapshot.PermanentCards)];
                snapshot.RunRelics = [.. current.Relics.Except(snapshot.PermanentRelics)];
                snapshot.RunPotions = [.. current.Potions.Except(snapshot.PermanentPotions)];
            }
            snapshot.Offers = _lastOfferReconciliations.ToList();
            return snapshot;
        }
    }

    private static void BroadcastSnapshot()
    {
        NetworkBanSnapshot snapshot = CreateSnapshot();
        LoadoutContentBanSyncMessage message = new() { Payload = JsonSerializer.Serialize(snapshot) };
        foreach (StartRunLobby lobby in Lobbies.Where(lobby => lobby.NetService.Type == NetGameType.Host))
        {
            foreach (ulong playerId in Sts2Compatibility.EnumerateStartRunLobbyPlayerIds(lobby)
                         .Where(playerId => playerId != lobby.NetService.NetId))
                lobby.NetService.SendMessage(message, playerId);
        }

        try
        {
            INetGameService net = RunManager.Instance.NetService;
            if (RunManager.Instance.IsInProgress && net.Type == NetGameType.Host)
            {
                LoadoutNetworkBroadcast.SendToRunClients(
                    net,
                    recipient => net.SendMessage(message, recipient),
                    "content ban snapshot");
            }
        }
        catch
        {
            // A lobby-only update has no run network service yet.
        }
    }

    private static void SendSnapshot(INetGameService netService, ulong playerId)
    {
        if (netService.Type != NetGameType.Host || playerId == netService.NetId)
            return;
        netService.SendMessage(
            new LoadoutContentBanSyncMessage { Payload = JsonSerializer.Serialize(CreateSnapshot()) },
            playerId);
    }

    private static void RegisterRunNetService(INetGameService netService)
    {
        if (ReferenceEquals(_runNetService, netService))
            return;
        UnregisterRunNetService(clearOverlay: false);
        _runNetService = netService;
        RegisterMessageHandler(netService);
    }

    private static void UnregisterRunNetService(bool clearOverlay)
    {
        if (_runNetService is null)
            return;
        NetGameType type = _runNetService.Type;
        UnbindRunLobby();
        UnregisterMessageHandler(_runNetService);
        _runNetService = null;
        if (clearOverlay && type == NetGameType.Client)
            ClearHostSnapshot();
    }

    private static void RegisterMessageHandler(INetGameService service)
    {
        if (RegisteredMessageServices.Add(service))
            service.RegisterMessageHandler<LoadoutContentBanSyncMessage>(HandleSnapshot);
    }

    private static void UnregisterMessageHandler(INetGameService service)
    {
        if (RegisteredMessageServices.Remove(service))
            service.UnregisterMessageHandler<LoadoutContentBanSyncMessage>(HandleSnapshot);
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

    private static void HandleSnapshot(LoadoutContentBanSyncMessage message, ulong senderId)
    {
        if (!LoadoutNetworkBroadcast.IsExpectedHostSender(senderId, _runNetService, Lobbies.Select(lobby => lobby.NetService))
            || string.IsNullOrWhiteSpace(message.Payload)
            || message.Payload.Length > MaxSnapshotLength)
            return;

        try
        {
            NetworkBanSnapshot incoming = Normalize(JsonSerializer.Deserialize<NetworkBanSnapshot>(message.Payload) ?? new NetworkBanSnapshot());
            List<ContentBanChangedEvent> changes = [];
            lock (Gate)
            {
                if (_hasHostSnapshot && incoming.Revision < _hostSnapshot.Revision)
                    return;
                foreach (ContentBanKind kind in Enum.GetValues<ContentBanKind>())
                {
                    HashSet<string> ids = [.. GetSet(_hostSnapshot, kind, ContentBanScope.Permanent), .. GetSet(_hostSnapshot, kind, ContentBanScope.Run), .. GetSet(incoming, kind, ContentBanScope.Permanent), .. GetSet(incoming, kind, ContentBanScope.Run)];
                    foreach (string id in ids)
                    {
                        ContentBanTarget target = new(kind, id);
                        ContentBanScope previous = GetScope(_hostSnapshot, target);
                        ContentBanScope next = GetScope(incoming, target);
                        if (previous != next)
                            changes.Add(new ContentBanChangedEvent(target, previous, next));
                    }
                }
                _hostSnapshot = incoming;
                _hasHostSnapshot = true;
                RecomputeEffectiveKindMaskLocked();
            }
            ContentBanLiveOfferService.Apply(incoming.Offers);
            foreach (ContentBanChangedEvent change in changes)
                Changed?.Invoke(change);
        }
        catch (Exception exception)
        {
            GD.PushWarning($"ContentBans: ignored malformed host snapshot. {exception.Message}");
        }
    }

    private static void ClearHostSnapshot()
    {
        lock (Gate)
        {
            _hostSnapshot = new NetworkBanSnapshot();
            _hasHostSnapshot = false;
            RecomputeEffectiveKindMaskLocked();
        }
    }

    private static void RecomputeEffectiveKindMaskLocked(bool includeRun = true)
    {
        int mask = 0;
        if (IsGuest())
        {
            foreach (ContentBanKind kind in Enum.GetValues<ContentBanKind>())
            {
                if (GetSet(_hostSnapshot, kind, ContentBanScope.Permanent).Count > 0
                    || GetSet(_hostSnapshot, kind, ContentBanScope.Run).Count > 0)
                    mask |= 1 << (int)kind;
            }
        }
        else
        {
            RunBanState? runBans = includeRun
                && TryGetCurrentRunState(out RunState? runState)
                && runState is not null
                ? GetRunState(runState)
                : null;
            foreach (ContentBanKind kind in Enum.GetValues<ContentBanKind>())
            {
                if (GetSet(_profile, kind).Count > 0
                    || runBans is not null && GetSet(runBans, kind).Count > 0)
                    mask |= 1 << (int)kind;
            }
        }
        _effectiveKindMask = mask;
    }

    private static NetworkBanSnapshot Normalize(NetworkBanSnapshot snapshot)
    {
        snapshot.PermanentCards = NormalizeSet(snapshot.PermanentCards);
        snapshot.PermanentRelics = NormalizeSet(snapshot.PermanentRelics);
        snapshot.PermanentPotions = NormalizeSet(snapshot.PermanentPotions);
        snapshot.RunCards = NormalizeSet(snapshot.RunCards);
        snapshot.RunRelics = NormalizeSet(snapshot.RunRelics);
        snapshot.RunPotions = NormalizeSet(snapshot.RunPotions);
        snapshot.RunCards.ExceptWith(snapshot.PermanentCards);
        snapshot.RunRelics.ExceptWith(snapshot.PermanentRelics);
        snapshot.RunPotions.ExceptWith(snapshot.PermanentPotions);
        snapshot.Offers ??= [];
        return snapshot;
    }

    private struct ProfileBanSaveData : ISerializable
    {
        public ProfileBanSaveData() { }

        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;
        [JsonPropertyName("cards")]
        public HashSet<string> Cards { get; set; } = new(StringComparer.Ordinal);
        [JsonPropertyName("relics")]
        public HashSet<string> Relics { get; set; } = new(StringComparer.Ordinal);
        [JsonPropertyName("potions")]
        public HashSet<string> Potions { get; set; } = new(StringComparer.Ordinal);

        public readonly void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            info.AddValue(nameof(SchemaVersion), SchemaVersion);
            info.AddValue(nameof(Cards), Cards);
            info.AddValue(nameof(Relics), Relics);
            info.AddValue(nameof(Potions), Potions);
        }
    }

    internal sealed class RunBanSaveData
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;
        [JsonPropertyName("cards")]
        public HashSet<string> Cards { get; set; } = new(StringComparer.Ordinal);
        [JsonPropertyName("relics")]
        public HashSet<string> Relics { get; set; } = new(StringComparer.Ordinal);
        [JsonPropertyName("potions")]
        public HashSet<string> Potions { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class RunBanState
    {
        internal HashSet<string> Cards { get; } = new(StringComparer.Ordinal);
        internal HashSet<string> Relics { get; } = new(StringComparer.Ordinal);
        internal HashSet<string> Potions { get; } = new(StringComparer.Ordinal);

        internal void Load(RunBanSaveData save)
        {
            Cards.Clear(); Relics.Clear(); Potions.Clear();
            Cards.UnionWith(save.Cards); Relics.UnionWith(save.Relics); Potions.UnionWith(save.Potions);
        }

        internal RunBanSaveData ToSaveData() => new()
        {
            Cards = [.. Cards],
            Relics = [.. Relics],
            Potions = [.. Potions]
        };
    }

    private sealed class NetworkBanSnapshot
    {
        [JsonPropertyName("revision")]
        public long Revision { get; set; }
        [JsonPropertyName("permanentCards")]
        public HashSet<string> PermanentCards { get; set; } = new(StringComparer.Ordinal);
        [JsonPropertyName("permanentRelics")]
        public HashSet<string> PermanentRelics { get; set; } = new(StringComparer.Ordinal);
        [JsonPropertyName("permanentPotions")]
        public HashSet<string> PermanentPotions { get; set; } = new(StringComparer.Ordinal);
        [JsonPropertyName("runCards")]
        public HashSet<string> RunCards { get; set; } = new(StringComparer.Ordinal);
        [JsonPropertyName("runRelics")]
        public HashSet<string> RunRelics { get; set; } = new(StringComparer.Ordinal);
        [JsonPropertyName("runPotions")]
        public HashSet<string> RunPotions { get; set; } = new(StringComparer.Ordinal);
        [JsonPropertyName("offers")]
        public List<ContentBanOfferReconciliation> Offers { get; set; } = [];
    }
}

internal struct LoadoutContentBanSyncMessage : INetMessage, IPacketSerializable
{
    public string Payload;
    public bool ShouldBroadcast => false;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.VeryDebug;
    public bool ShouldBuffer => true;
    public void Serialize(PacketWriter writer) => writer.WriteString(Payload ?? string.Empty);
    public void Deserialize(PacketReader reader) => Payload = reader.ReadString();
}
