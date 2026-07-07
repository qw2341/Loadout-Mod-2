#nullable enable

namespace Loadout.Services.Loadouts;

using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Runs;

public static class LoadoutPanelAccessService
{
    private static readonly HashSet<StartRunLobby> RegisteredLobbies = [];
    private static readonly Dictionary<StartRunLobby, Action<LobbyPlayer>> LobbyConnectedHandlers = new();

    private static INetGameService? _runNetService;
    private static bool _hostAllowsGuests;

    public static event Action? AccessChanged;

    public static bool HostAllowsGuests => _hostAllowsGuests;

    public static void SetHostAllowsGuests(bool allow)
    {
        if (_hostAllowsGuests == allow)
            return;

        _hostAllowsGuests = allow;
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
                BroadcastAccess();
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
        UnregisterRunNetService(clearClientAccess: true);
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

    private static void BroadcastAccess()
    {
        LoadoutPanelAccessMessage message = new()
        {
            allowGuests = _hostAllowsGuests
        };

        foreach (StartRunLobby lobby in RegisteredLobbies.ToList())
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
        if (IsHostSession())
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
