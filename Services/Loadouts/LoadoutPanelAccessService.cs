#nullable enable

namespace Loadout.Services.Loadouts;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Text.Json.Serialization;
using Godot;
using Loadout.Services.Saving;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Logging;
using Loadout.Services.Networking;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Runs;

public static class LoadoutPanelAccessService
{
    private const int CurrentSchemaVersion = 1;
    private const string RunDirectory = "loadout/services/panel_access";
    private const string RunFilePrefix = "panel_access_run";

    private static readonly HashSet<StartRunLobby> RegisteredLobbies = [];
    private static readonly Dictionary<StartRunLobby, Action<LobbyPlayer>> LobbyConnectedHandlers = new();

    private static INetGameService? _runNetService;
    private static RunLobby? _runLobby;
    private static bool _hostAllowsGuests;
    private static long? _loadedRunStartTime;

    public static event Action? AccessChanged;

    public static bool HostAllowsGuests => _hostAllowsGuests;

    public static void SetHostAllowsGuests(bool allow)
    {
        if (_hostAllowsGuests == allow)
            return;

        _hostAllowsGuests = allow;
        SaveRunAccessIfActiveHost();
        BroadcastAccess();
        NotifyAccessChanged();
    }

    public static bool CanLocalPlayerUsePanel()
    {
        return TryGetActiveNetType() != NetGameType.Client || _hostAllowsGuests;
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
            SetHostAllowsGuestsForNewLobby(false);

            Action<LobbyPlayer> connected = player => SendAccessToLobbyPlayer(lobby, player.id);
            LobbyConnectedHandlers[lobby] = connected;
            lobby.PlayerConnected += connected;

            foreach (LobbyPlayer player in lobby.Players)
            {
                if (player.id != lobby.NetService.NetId)
                    SendAccessToLobbyPlayer(lobby, player.id);
            }
        }
        else if (lobby.NetService.Type == NetGameType.Client)
        {
            SetHostAllowsGuestsForNewLobby(false);
        }
    }

    public static void UnregisterLobby(StartRunLobby? lobby, bool clearClientAccess = false)
    {
        if (lobby is null || !RegisteredLobbies.Remove(lobby))
            return;

        lobby.NetService.UnregisterMessageHandler<LoadoutPanelAccessMessage>(HandleAccessMessage);
        if (LobbyConnectedHandlers.Remove(lobby, out Action<LobbyPlayer>? connected))
            lobby.PlayerConnected -= connected;

        if (clearClientAccess && lobby.NetService.Type == NetGameType.Client)
            SetHostAllowsGuestsForNewLobby(false);
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
            {
                LoadOrCreateRunAccess();
                BroadcastAccess();
            }
            else if (netService.Type is NetGameType.Singleplayer or NetGameType.Replay)
                SetHostAllowsGuestsForNewLobby(false);
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

    private static void SetHostAllowsGuestsForNewLobby(bool allow)
    {
        if (_hostAllowsGuests == allow)
            return;

        _hostAllowsGuests = allow;
        NotifyAccessChanged();
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
            SetHostAllowsGuestsForNewLobby(false);
    }

    private static void BindRunLobby(RunLobby? runLobby)
    {
        if (ReferenceEquals(_runLobby, runLobby))
            return;

        UnbindRunLobby();
        _runLobby = runLobby;
        if (_runLobby is not null)
            _runLobby.PlayerRejoined += OnPlayerRejoined;
    }

    private static void UnbindRunLobby()
    {
        if (_runLobby is null)
            return;

        _runLobby.PlayerRejoined -= OnPlayerRejoined;
        _runLobby = null;
    }

    private static void OnPlayerRejoined(ulong playerId)
    {
        if (_runNetService?.Type != NetGameType.Host || playerId == _runNetService.NetId)
            return;

        _runNetService.SendMessage(new LoadoutPanelAccessMessage
        {
            allowGuests = _hostAllowsGuests
        }, playerId);
    }

    private static void BroadcastAccess()
    {
        LoadoutPanelAccessMessage message = new()
        {
            allowGuests = _hostAllowsGuests
        };

        foreach (StartRunLobby lobby in RegisteredLobbies.ToList())
        {
            if (lobby.NetService.Type != NetGameType.Host)
                continue;

            foreach (LobbyPlayer player in lobby.Players)
            {
                if (player.id != lobby.NetService.NetId)
                    SendAccessToLobbyPlayer(lobby, player.id);
            }
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
            allowGuests = _hostAllowsGuests
        }, playerId);
    }

    private static void HandleAccessMessage(LoadoutPanelAccessMessage message, ulong senderId)
    {
        if (IsHostSession() || !IsExpectedHostSender(senderId))
            return;

        if (_hostAllowsGuests == message.allowGuests)
            return;

        _hostAllowsGuests = message.allowGuests;
        NotifyAccessChanged();
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

    private static bool IsExpectedHostSender(ulong senderId)
    {
        return LoadoutNetworkBroadcast.IsExpectedHostSender(
            senderId,
            _runNetService,
            RegisteredLobbies.Select(lobby => lobby.NetService));
    }

    private static NetGameType TryGetActiveNetType()
    {
        if (_runNetService is not null)
            return _runNetService.Type;

        foreach (StartRunLobby lobby in RegisteredLobbies)
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

    private static void LoadOrCreateRunAccess()
    {
        long? runStartTime = SaveUtility.GetCurrentRunStartTime();
        if (!runStartTime.HasValue)
            return;

        _loadedRunStartTime = runStartTime.Value;
        string path = SaveUtility.GetRunSidecarPath(RunDirectory, RunFilePrefix, runStartTime.Value);
        SaveUtility.LoadResult<RunAccessSaveData> loaded = SaveUtility.LoadProfileJson(
            path,
            new RunAccessSaveData
            {
                SchemaVersion = CurrentSchemaVersion,
                RunStartTime = runStartTime.Value,
                AllowGuests = _hostAllowsGuests
            });

        if (loaded.Loaded && loaded.Value.RunStartTime == runStartTime.Value)
        {
            bool changed = _hostAllowsGuests != loaded.Value.AllowGuests;
            _hostAllowsGuests = loaded.Value.AllowGuests;
            if (changed)
                NotifyAccessChanged();
            return;
        }

        SaveRunAccess();
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
                AllowGuests = _hostAllowsGuests
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

        public void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            info.AddValue(nameof(SchemaVersion), CurrentSchemaVersion);
            info.AddValue(nameof(RunStartTime), RunStartTime);
            info.AddValue(nameof(AllowGuests), AllowGuests);
        }
    }
}

public struct LoadoutPanelAccessMessage : INetMessage, IPacketSerializable
{
    public bool allowGuests;

    public bool ShouldBroadcast => false;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.VeryDebug;
    public bool ShouldBuffer => true;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteBool(allowGuests);
    }

    public void Deserialize(PacketReader reader)
    {
        allowGuests = reader.ReadBool();
    }
}
