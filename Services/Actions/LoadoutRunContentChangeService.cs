#nullable enable

namespace Loadout.Services.Actions;

using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

public enum LoadoutRunContentKind
{
    Cards,
    Relics
}

public sealed class LoadoutRunContentChangedEventArgs
{
    public LoadoutRunContentChangedEventArgs(LoadoutRunContentKind kind, IEnumerable<ulong>? playerNetIds)
    {
        Kind = kind;
        PlayerNetIds = playerNetIds?
            .Where(id => id != 0)
            .ToHashSet()
            ?? new HashSet<ulong>();
    }

    public LoadoutRunContentKind Kind { get; }

    public IReadOnlySet<ulong> PlayerNetIds { get; }

    public bool AffectsPlayer(ulong playerNetId)
    {
        return PlayerNetIds.Count == 0 || PlayerNetIds.Contains(playerNetId);
    }
}

public static class LoadoutRunContentChangeService
{
    public static event Action<LoadoutRunContentChangedEventArgs>? Changed;

    public static void Notify(LoadoutRunContentKind kind, ulong playerNetId)
    {
        Notify(kind, [playerNetId]);
    }

    public static void Notify(LoadoutRunContentKind kind, IEnumerable<ulong> playerNetIds)
    {
        LoadoutRunContentChangedEventArgs args = new(kind, playerNetIds);
        Action<LoadoutRunContentChangedEventArgs>? handlers = Changed;
        if (handlers is null)
            return;

        foreach (Action<LoadoutRunContentChangedEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(args);
            }
            catch (Exception exception)
            {
                GD.PushWarning($"LoadoutPanel: run content changed handler failed. {exception.Message}");
            }
        }
    }
}
