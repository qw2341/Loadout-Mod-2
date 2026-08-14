#nullable enable

namespace Loadout.Services.Loadouts;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Text.Json.Serialization;
using Godot;
using Loadout.Patches.Loadouts;
using Loadout.Services.Compatibility;
using Loadout.Services.Saving;
using MegaCrit.Sts2.Core.Logging;
using Loadout.Services.Networking;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Runs;

public static class LoadoutPanelAccessService
{
    private const int CurrentSchemaVersion = 2;
    private const string RunDirectory = "loadout/services/panel_access";
    private const string RunFilePrefix = "panel_access_run";

    private static readonly HashSet<StartRunLobby> RegisteredLobbies = [];
    private static readonly Dictionary<StartRunLobby, Delegate> LobbyConnectedHandlers = new();
    private static readonly HashSet<LoadRunLobby> RegisteredLoadLobbies = [];

    private static INetGameService? _runNetService;
    private static RunLobby? _runLobby;
    private static Delegate? _playerRejoinedHandler;
    private static bool _hostAllowsGuests;
    private static bool _hostAllowsGuestDebugConsole = true;
    private static long? _loadedRunStartTime;

    public static event Action? AccessChanged;
    public static event Action? DebugConsoleAccessChanged;

    public static bool HostAllowsGuests => _hostAllowsGuests;
    public static bool HostAllowsGuestDebugConsole => _hostAllowsGuestDebugConsole;

    public static void SetHostAllowsGuests(bool allow)
    {
        if (_hostAllowsGuests == allow)
            return;

        _hostAllowsGuests = allow;
        LoadoutPanelAccessRunSavePatch.AttachToCurrentRun(allow, _hostAllowsGuestDebugConsole);
        SaveRunAccessIfActiveHost();
        BroadcastAccess();
        NotifyAccessChanged();
    }

    public static void SetHostAllowsGuestDebugConsole(bool allow)
    {
        if (_hostAllowsGuestDebugConsole == allow)
            return;

        _hostAllowsGuestDebugConsole = allow;
        LoadoutPanelAccessRunSavePatch.AttachToCurrentRun(_hostAllowsGuests, allow);
        SaveRunAccessIfActiveHost();
        BroadcastAccess();
        NotifyDebugConsoleAccessChanged();
    }

    public static bool CanLocalPlayerUsePanel()
    {
        return TryGetActiveNetType() != NetGameType.Client || _hostAllowsGuests;
    }

    public static bool CanLocalPlayerUseDebugConsole()
    {
        return TryGetActiveNetType() != NetGameType.Client || _hostAllowsGuestDebugConsole;
    }

    public static bool CanRequesterUsePanel(ulong requesterNetId)
    {
        try
        {
            INetGameService? netService = _runNetService ?? RunManager.Instance.NetService;
            if (netService is null || netService.Type != NetGameType.Host)
                return true;

            return requesterNetId == netService.NetId || _hostAllowsGuests;
        }
        catch
        {
            return true;
        }
    }

    public static void RegisterLobby(StartRunLobby? lobby)
    {
        if (lobby is null || !RegisteredLobbies.Add(lobby))
            return;

        lobby.NetService.RegisterMessageHandler<LoadoutPanelAccessMessage>(HandleAccessMessage);

        if (lobby.NetService.Type == NetGameType.Host)
        {
            SetHostAccessForNewLobby(allowGuests: false, allowGuestDebugConsole: true);

            Delegate connected = Sts2Compatibility.SubscribeStartRunLobbyPlayerConnected(
                lobby,
                playerId => SendAccessToLobbyPlayer(lobby, playerId));
            LobbyConnectedHandlers[lobby] = connected;

            foreach (ulong playerId in Sts2Compatibility.EnumerateStartRunLobbyPlayerIds(lobby))
            {
                if (playerId != lobby.NetService.NetId)
                    SendAccessToLobbyPlayer(lobby, playerId);
            }
        }
        else if (lobby.NetService.Type == NetGameType.Client)
        {
            SetHostAccessForNewLobby(allowGuests: false, allowGuestDebugConsole: false);
        }
    }

    public static void UnregisterLobby(StartRunLobby? lobby, bool clearClientAccess = false)
    {
        if (lobby is null || !RegisteredLobbies.Remove(lobby))
            return;

        lobby.NetService.UnregisterMessageHandler<LoadoutPanelAccessMessage>(HandleAccessMessage);
        if (LobbyConnectedHandlers.Remove(lobby, out Delegate? connected))
            Sts2Compatibility.UnsubscribeStartRunLobbyPlayerConnected(lobby, connected);

        if (clearClientAccess && lobby.NetService.Type == NetGameType.Client)
            SetHostAccessForNewLobby(allowGuests: false, allowGuestDebugConsole: true);
    }

    public static void RegisterLoadLobby(LoadRunLobby? lobby)
    {
        if (lobby is null || !RegisteredLoadLobbies.Add(lobby))
            return;

        lobby.NetService.RegisterMessageHandler<LoadoutPanelAccessMessage>(HandleAccessMessage);
        if (lobby.NetService.Type == NetGameType.Host)
        {
            ApplyAccess(allowGuests: false, allowGuestDebugConsole: true);
            TryLoadRunAccess(lobby.Run.StartTime);

            foreach (ulong playerId in Sts2Compatibility.EnumerateLoadRunLobbyPlayerIds(lobby))
                SendAccessToLoadLobbyPlayer(lobby, playerId);
        }
        else if (lobby.NetService.Type == NetGameType.Client)
        {
            ApplyAccess(allowGuests: false, allowGuestDebugConsole: false);
        }
    }

    public static void UnregisterLoadLobby(LoadRunLobby? lobby, bool clearClientAccess = false)
    {
        if (lobby is null || !RegisteredLoadLobbies.Remove(lobby))
            return;

        lobby.NetService.UnregisterMessageHandler<LoadoutPanelAccessMessage>(HandleAccessMessage);
        if (clearClientAccess && lobby.NetService.Type == NetGameType.Client)
            ApplyAccess(allowGuests: false, allowGuestDebugConsole: true);
    }

    public static void SendAccessToLoadLobbyPlayer(LoadRunLobby? lobby, ulong playerId)
    {
        if (lobby is null
            || lobby.NetService.Type != NetGameType.Host
            || playerId == lobby.NetService.NetId
            || !Sts2Compatibility.EnumerateLoadRunLobbyPlayerIds(lobby).Contains(playerId))
        {
            return;
        }

        lobby.NetService.SendMessage(new LoadoutPanelAccessMessage
        {
            allowGuests = _hostAllowsGuests,
            allowGuestDebugConsole = _hostAllowsGuestDebugConsole
        }, playerId);
    }

    public static void OnRunLaunched()
    {
        try
        {
            INetGameService netService = RunManager.Instance.NetService;
            if (netService is null)
                return;

            RegisterRunNetService(netService);
            RunState? runState = RunManager.Instance.DebugOnlyGetState();
            if (runState is not null
                && LoadoutPanelAccessRunSavePatch.TryGetAttachedAccess(
                    runState,
                    out bool savedAllowGuests,
                    out bool savedAllowGuestDebugConsole))
            {
                ApplyAccess(savedAllowGuests, savedAllowGuestDebugConsole);
                _loadedRunStartTime = SaveUtility.GetCurrentRunStartTime();
                if (netService.Type == NetGameType.Host)
                    SaveRunAccess();
            }
            else if (netService.Type == NetGameType.Host)
            {
                LoadOrCreateRunAccess();
            }
            else if (netService.Type is NetGameType.Singleplayer or NetGameType.Replay)
                SetHostAccessForNewLobby(allowGuests: false, allowGuestDebugConsole: true);

            if (runState is not null)
                LoadoutPanelAccessRunSavePatch.SetAttachedAccess(
                    runState,
                    _hostAllowsGuests,
                    _hostAllowsGuestDebugConsole,
                    loadedFromSave: false);

            if (netService.Type == NetGameType.Host)
                BroadcastAccess();
        }
        catch (Exception exception)
        {
            GD.PushWarning($"LoadoutPanelAccess: failed to initialize run access sync. {exception.Message}");
        }
    }

    public static void OnRunCleaningUp()
    {
        UnbindRunLobby();
        UnregisterRunNetService(clearClientAccess: true);
        _loadedRunStartTime = null;
    }

    private static void SetHostAccessForNewLobby(bool allowGuests, bool allowGuestDebugConsole)
    {
        ApplyAccess(allowGuests, allowGuestDebugConsole);
    }

    private static void ApplyAccess(bool allowGuests, bool allowGuestDebugConsole)
    {
        bool panelChanged = _hostAllowsGuests != allowGuests;
        bool debugConsoleChanged = _hostAllowsGuestDebugConsole != allowGuestDebugConsole;

        _hostAllowsGuests = allowGuests;
        _hostAllowsGuestDebugConsole = allowGuestDebugConsole;

        if (panelChanged)
            NotifyAccessChanged();
        if (debugConsoleChanged)
            NotifyDebugConsoleAccessChanged();
    }

    private static void RegisterRunNetService(INetGameService netService)
    {
        if (_runNetService == netService)
            return;

        UnregisterRunNetService(clearClientAccess: false);
        _runNetService = netService;
        _runNetService.RegisterMessageHandler<LoadoutPanelAccessMessage>(HandleAccessMessage);
        BindRunLobby(RunManager.Instance.RunLobby);
    }

    private static void UnregisterRunNetService(bool clearClientAccess)
    {
        if (_runNetService is null)
            return;

        NetGameType type = _runNetService.Type;
        _runNetService.UnregisterMessageHandler<LoadoutPanelAccessMessage>(HandleAccessMessage);
        _runNetService = null;

        if (clearClientAccess && type == NetGameType.Client)
            SetHostAccessForNewLobby(allowGuests: false, allowGuestDebugConsole: true);
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
                OnPlayerRejoined);
        }
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
        if (_runNetService?.Type != NetGameType.Host || playerId == _runNetService.NetId)
            return;

        _runNetService.SendMessage(new LoadoutPanelAccessMessage
        {
            allowGuests = _hostAllowsGuests,
            allowGuestDebugConsole = _hostAllowsGuestDebugConsole
        }, playerId);
    }

    private static void BroadcastAccess()
    {
        LoadoutPanelAccessMessage message = new()
        {
            allowGuests = _hostAllowsGuests,
            allowGuestDebugConsole = _hostAllowsGuestDebugConsole
        };

        foreach (StartRunLobby lobby in RegisteredLobbies.ToList())
        {
            if (lobby.NetService.Type != NetGameType.Host)
                continue;

            foreach (ulong playerId in Sts2Compatibility.EnumerateStartRunLobbyPlayerIds(lobby))
            {
                if (playerId != lobby.NetService.NetId)
                    SendAccessToLobbyPlayer(lobby, playerId);
            }
        }

        foreach (LoadRunLobby lobby in RegisteredLoadLobbies.ToList())
        {
            if (lobby.NetService.Type != NetGameType.Host)
                continue;

            foreach (ulong playerId in Sts2Compatibility.EnumerateLoadRunLobbyPlayerIds(lobby))
                SendAccessToLoadLobbyPlayer(lobby, playerId);
        }

        try
        {
            INetGameService netService = RunManager.Instance.NetService;
            if (RunManager.Instance.IsInProgress && netService.Type == NetGameType.Host)
            {
                LoadoutNetworkBroadcast.SendToRunClients(
                    netService,
                    recipient => netService.SendMessage(message, recipient),
                    "panel access");
            }
        }
        catch
        {
            // There may be no active run service while still in the lobby.
        }
    }

    private static void SendAccessToLobbyPlayer(StartRunLobby lobby, ulong playerId)
    {
        if (lobby.NetService.Type != NetGameType.Host || playerId == lobby.NetService.NetId)
            return;

        lobby.NetService.SendMessage(new LoadoutPanelAccessMessage
        {
            allowGuests = _hostAllowsGuests,
            allowGuestDebugConsole = _hostAllowsGuestDebugConsole
        }, playerId);
    }

    private static void HandleAccessMessage(LoadoutPanelAccessMessage message, ulong senderId)
    {
        if (IsHostSession() || !IsExpectedHostSender(senderId))
            return;

        if (_hostAllowsGuests == message.allowGuests
            && _hostAllowsGuestDebugConsole == message.allowGuestDebugConsole)
            return;

        LoadoutPanelAccessRunSavePatch.AttachToCurrentRun(
            message.allowGuests,
            message.allowGuestDebugConsole);
        ApplyAccess(message.allowGuests, message.allowGuestDebugConsole);
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

        foreach (LoadRunLobby lobby in RegisteredLoadLobbies)
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

    private static bool IsExpectedHostSender(ulong senderId)
    {
        return LoadoutNetworkBroadcast.IsExpectedHostSender(
            senderId,
            _runNetService,
            RegisteredLobbies.Select(lobby => lobby.NetService)
                .Concat(RegisteredLoadLobbies.Select(lobby => lobby.NetService)));
    }

    private static NetGameType TryGetActiveNetType()
    {
        if (_runNetService is not null)
            return _runNetService.Type;

        foreach (StartRunLobby lobby in RegisteredLobbies)
            return lobby.NetService.Type;

        foreach (LoadRunLobby lobby in RegisteredLoadLobbies)
            return lobby.NetService.Type;

        try
        {
            return RunManager.Instance.NetService.Type;
        }
        catch
        {
            return NetGameType.Singleplayer;
        }
    }

    private static void NotifyAccessChanged()
    {
        try
        {
            AccessChanged?.Invoke();
        }
        catch (Exception exception)
        {
            GD.PushWarning($"LoadoutPanelAccess: access changed handler failed. {exception.Message}");
        }
    }

    private static void NotifyDebugConsoleAccessChanged()
    {
        try
        {
            DebugConsoleAccessChanged?.Invoke();
        }
        catch (Exception exception)
        {
            GD.PushWarning($"LoadoutPanelAccess: debug console access changed handler failed. {exception.Message}");
        }
    }

    private static void LoadOrCreateRunAccess()
    {
        long? runStartTime = SaveUtility.GetCurrentRunStartTime();
        if (!runStartTime.HasValue)
            return;

        if (TryLoadRunAccess(runStartTime.Value))
            return;

        SaveRunAccess();
    }

    private static bool TryLoadRunAccess(long runStartTime)
    {
        _loadedRunStartTime = runStartTime;
        string path = SaveUtility.GetRunSidecarPath(RunDirectory, RunFilePrefix, runStartTime);
        SaveUtility.LoadResult<RunAccessSaveData> loaded = SaveUtility.LoadProfileJson(
            path,
            new RunAccessSaveData
            {
                SchemaVersion = CurrentSchemaVersion,
                RunStartTime = runStartTime,
                AllowGuests = _hostAllowsGuests,
                AllowGuestDebugConsole = _hostAllowsGuestDebugConsole
            });

        if (loaded.Loaded && loaded.Value.RunStartTime == runStartTime)
        {
            bool allowGuestDebugConsole = loaded.Value.SchemaVersion >= 2
                ? loaded.Value.AllowGuestDebugConsole
                : true;
            ApplyAccess(loaded.Value.AllowGuests, allowGuestDebugConsole);
            return true;
        }

        return false;
    }

    private static void SaveRunAccessIfActiveHost()
    {
        try
        {
            if (RunManager.Instance.IsInProgress
                && RunManager.Instance.NetService.Type == NetGameType.Host)
            {
                _loadedRunStartTime ??= SaveUtility.GetCurrentRunStartTime();
                SaveRunAccess();
            }
        }
        catch
        {
            // The host may still be in the start-run lobby.
        }
    }

    private static void SaveRunAccess()
    {
        if (!_loadedRunStartTime.HasValue)
            return;

        SaveUtility.SaveProfileJson(
            SaveUtility.GetRunSidecarPath(RunDirectory, RunFilePrefix, _loadedRunStartTime.Value),
            new RunAccessSaveData
            {
                SchemaVersion = CurrentSchemaVersion,
                RunStartTime = _loadedRunStartTime.Value,
                AllowGuests = _hostAllowsGuests,
                AllowGuestDebugConsole = _hostAllowsGuestDebugConsole
            });
    }

    private struct RunAccessSaveData : ISerializable
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; set; }

        [JsonPropertyName("runStartTime")]
        public long RunStartTime { get; set; }

        [JsonPropertyName("allowGuests")]
        public bool AllowGuests { get; set; }

        [JsonPropertyName("allowGuestDebugConsole")]
        public bool AllowGuestDebugConsole { get; set; }

        public void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            info.AddValue(nameof(SchemaVersion), CurrentSchemaVersion);
            info.AddValue(nameof(RunStartTime), RunStartTime);
            info.AddValue(nameof(AllowGuests), AllowGuests);
            info.AddValue(nameof(AllowGuestDebugConsole), AllowGuestDebugConsole);
        }
    }

}

public struct LoadoutPanelAccessMessage : INetMessage, IPacketSerializable
{
    public bool allowGuests;
    public bool allowGuestDebugConsole;

    public bool ShouldBroadcast => false;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.VeryDebug;
    public bool ShouldBuffer => true;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteBool(allowGuests);
        writer.WriteBool(allowGuestDebugConsole);
    }

    public void Deserialize(PacketReader reader)
    {
        allowGuests = reader.ReadBool();
        allowGuestDebugConsole = reader.ReadBool();
    }
}
