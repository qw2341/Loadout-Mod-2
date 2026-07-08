#nullable enable

namespace Loadout.Services.Loadouts;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Godot;
using Loadout.PanelItems;
using Loadout.Services.Networking;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Runs;

public static class LoadoutHostSharingService
{
    private static readonly HashSet<StartRunLobby> RegisteredLobbies = [];
    private static readonly Dictionary<StartRunLobby, Action<LobbyPlayer>> LobbyConnectedHandlers = new();
    private static readonly List<SavedLoadout> RemoteLoadouts = [];

    private static INetGameService? _runNetService;
    private static bool _registered;

    public static event Action? RemoteCatalogChanged;

    public static void Register()
    {
        if (_registered)
            return;

        _registered = true;
        LoadoutStorageService.Changed += BroadcastHostCatalog;
    }

    public static void Unregister()
    {
        if (!_registered)
            return;

        LoadoutStorageService.Changed -= BroadcastHostCatalog;
        foreach (StartRunLobby lobby in RegisteredLobbies.ToList())
            UnregisterLobby(lobby, clearRemoteCatalog: false);

        RegisteredLobbies.Clear();
        LobbyConnectedHandlers.Clear();
        UnregisterRunNetService(clearRemoteCatalog: true);
        _registered = false;
    }

    public static IReadOnlyList<SavedLoadout> GetRemoteLoadouts()
    {
        lock (RemoteLoadouts)
        {
            return RemoteLoadouts.Select(loadout => loadout.Clone()).ToList();
        }
    }

    public static void RegisterLobby(StartRunLobby? lobby)
    {
        if (!_registered || lobby is null || !RegisteredLobbies.Add(lobby))
            return;

        lobby.NetService.RegisterMessageHandler<LoadoutHostCatalogMessage>(HandleHostCatalog);

        Action<LobbyPlayer> connected = player => SendCatalogToLobbyPlayer(lobby, player.id);
        LobbyConnectedHandlers[lobby] = connected;
        lobby.PlayerConnected += connected;

        if (lobby.NetService.Type == NetGameType.Host)
        {
            foreach (LobbyPlayer player in lobby.Players)
            {
                if (player.id != lobby.NetService.NetId)
                    SendCatalogToLobbyPlayer(lobby, player.id);
            }
        }
    }

    public static void UnregisterLobby(StartRunLobby? lobby, bool clearRemoteCatalog = false)
    {
        if (lobby is null || !RegisteredLobbies.Remove(lobby))
            return;

        lobby.NetService.UnregisterMessageHandler<LoadoutHostCatalogMessage>(HandleHostCatalog);
        if (LobbyConnectedHandlers.Remove(lobby, out Action<LobbyPlayer>? connected))
            lobby.PlayerConnected -= connected;

        if (clearRemoteCatalog && lobby.NetService.Type == NetGameType.Client)
            ClearRemoteCatalog();
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
                BroadcastHostCatalog();
            else if (netService.Type is NetGameType.Singleplayer or NetGameType.Replay)
                ClearRemoteCatalog();
        }
        catch (Exception exception)
        {
            GD.PushWarning($"Loadout: failed to initialize host loadout sharing. {exception.Message}");
        }
    }

    public static void OnRunCleaningUp()
    {
        UnregisterRunNetService(clearRemoteCatalog: true);
    }

    public static void BroadcastHostCatalog()
    {
        if (!_registered)
            return;

        HostLoadoutCatalog catalog = BuildHostCatalog();
        string payload = JsonSerializer.Serialize(catalog, LoadoutSerializationService.SharedJsonOptions);
        LoadoutHostCatalogMessage message = new()
        {
            payload = payload
        };

        foreach (StartRunLobby lobby in RegisteredLobbies)
        {
            if (lobby.NetService.Type != NetGameType.Host)
                continue;

            foreach (LobbyPlayer player in lobby.Players)
            {
                if (player.id != lobby.NetService.NetId)
                    SendCatalogToLobbyPlayer(lobby, player.id);
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
                    "host loadout catalog");
            }
        }
        catch
        {
            // There may be no active run service while still in the lobby.
        }
    }

    private static HostLoadoutCatalog BuildHostCatalog()
    {
        List<SavedLoadout> loadouts = LoadoutStorageService.GetLoadouts()
            .Select(loadout => loadout.Clone())
            .ToList();

        Player? localPlayer = CommonHelpers.GetLocalRunPlayer();
        if (localPlayer is not null)
        {
            loadouts.Add(BuildCurrentRunLoadout(localPlayer, LoadoutKind.Cards, "Host Current Cards"));
            loadouts.Add(BuildCurrentRunLoadout(localPlayer, LoadoutKind.Relics, "Host Current Relics"));
            loadouts.Add(BuildCurrentRunLoadout(localPlayer, LoadoutKind.CardsAndRelics, "Host Current Cards + Relics"));
        }

        foreach (SavedLoadout loadout in loadouts)
        {
            loadout.IsRemote = true;
            loadout.RemoteOwnerLabel = "Host";
        }

        return new HostLoadoutCatalog
        {
            Loadouts = loadouts
        };
    }

    private static SavedLoadout BuildCurrentRunLoadout(Player player, LoadoutKind kind, string name)
    {
        SavedLoadout loadout = LoadoutSerializationService.Capture(player, kind);
        loadout.Id = $"host-current-{kind.ToString().ToLowerInvariant()}";
        loadout.Name = name;
        return loadout;
    }

    private static void RegisterRunNetService(INetGameService netService)
    {
        if (_runNetService == netService)
            return;

        UnregisterRunNetService(clearRemoteCatalog: false);
        _runNetService = netService;
        _runNetService.RegisterMessageHandler<LoadoutHostCatalogMessage>(HandleHostCatalog);
    }

    private static void UnregisterRunNetService(bool clearRemoteCatalog)
    {
        if (_runNetService is null)
            return;

        NetGameType type = _runNetService.Type;
        _runNetService.UnregisterMessageHandler<LoadoutHostCatalogMessage>(HandleHostCatalog);
        _runNetService = null;

        if (clearRemoteCatalog && type == NetGameType.Client)
            ClearRemoteCatalog();
    }

    private static void SendCatalogToLobbyPlayer(StartRunLobby lobby, ulong playerId)
    {
        if (lobby.NetService.Type != NetGameType.Host || playerId == lobby.NetService.NetId)
            return;

        lobby.NetService.SendMessage(new LoadoutHostCatalogMessage
        {
            payload = JsonSerializer.Serialize(BuildHostCatalog(), LoadoutSerializationService.SharedJsonOptions)
        }, playerId);
    }

    private static void HandleHostCatalog(LoadoutHostCatalogMessage message, ulong senderId)
    {
        if (IsHostSession())
            return;

        try
        {
            HostLoadoutCatalog? catalog = JsonSerializer.Deserialize<HostLoadoutCatalog>(
                message.payload ?? string.Empty,
                LoadoutSerializationService.SharedJsonOptions);
            List<SavedLoadout> remote = catalog?.Loadouts
                .Select(LoadoutSerializationService.Normalize)
                .Select(loadout =>
                {
                    loadout.IsRemote = true;
                    loadout.RemoteOwnerLabel = "Host";
                    return loadout;
                })
                .ToList() ?? [];

            lock (RemoteLoadouts)
            {
                RemoteLoadouts.Clear();
                RemoteLoadouts.AddRange(remote);
            }

            RemoteCatalogChanged?.Invoke();
        }
        catch (Exception exception)
        {
            GD.PushWarning($"Loadout: failed to read host loadout catalog. {exception.Message}");
        }
    }

    private static void ClearRemoteCatalog()
    {
        lock (RemoteLoadouts)
            RemoteLoadouts.Clear();

        RemoteCatalogChanged?.Invoke();
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
}

public struct LoadoutHostCatalogMessage : INetMessage, IPacketSerializable
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
