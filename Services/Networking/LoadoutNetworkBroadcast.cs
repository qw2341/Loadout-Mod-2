#nullable enable

namespace Loadout.Services.Networking;

using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;

public static class LoadoutNetworkBroadcast
{
    public static bool IsExpectedHostSender(ulong senderId, INetGameService? netService)
    {
        return netService is INetClientGameService clientService
               && senderId == clientService.NetClient?.HostNetId;
    }

    public static bool IsExpectedHostSender(
        ulong senderId,
        INetGameService? primaryNetService,
        IEnumerable<INetGameService?> fallbackNetServices)
    {
        if (primaryNetService is INetClientGameService)
            return IsExpectedHostSender(senderId, primaryNetService);

        foreach (INetGameService? netService in fallbackNetServices)
        {
            if (netService is INetClientGameService)
                return IsExpectedHostSender(senderId, netService);
        }

        return false;
    }

    public static IReadOnlyList<ulong> GetRunClientRecipients(INetGameService? netService)
    {
        if (netService is null || netService.Type != NetGameType.Host)
            return [];

        try
        {
            RunState? runState = RunManager.Instance.IsInProgress
                ? RunManager.Instance.DebugOnlyGetState()
                : null;

            if (runState is null)
                return [];

            return runState.Players
                .Select(player => player.NetId)
                .Where(netId => netId != 0 && netId != netService.NetId)
                .Distinct()
                .ToList();
        }
        catch (Exception exception)
        {
            GD.PushWarning($"LoadoutNetwork: could not resolve run clients. {exception.Message}");
            return [];
        }
    }

    public static void SendToRunClients(
        INetGameService? netService,
        Action<ulong> send,
        string context)
    {
        foreach (ulong recipient in GetRunClientRecipients(netService))
        {
            try
            {
                send(recipient);
            }
            catch (Exception exception)
            {
                GD.PushWarning($"LoadoutNetwork: failed sending {context} to {recipient}. {exception.Message}");
            }
        }
    }
}
