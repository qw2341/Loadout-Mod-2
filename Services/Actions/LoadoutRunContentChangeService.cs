#nullable enable

namespace Loadout.Services.Actions;

using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Loadout.Services.Targets;
using MegaCrit.Sts2.Core.Models;

public enum LoadoutRunContentKind
{
    Cards,
    Relics
}

public enum LoadoutRunContentChangeMode
{
    Unknown,
    Add,
    Remove,
    Update,
    Replace
}

public readonly record struct LoadoutChangedCard(ulong OwnerNetId, int Index, ModelId ModelId);

public sealed class LoadoutRunContentChangedEventArgs
{
    public LoadoutRunContentChangedEventArgs(
        LoadoutRunContentKind kind,
        IEnumerable<ulong>? playerNetIds,
        LoadoutRunContentChangeMode mode = LoadoutRunContentChangeMode.Unknown,
        IEnumerable<LoadoutChangedCard>? changedCards = null)
    {
        Kind = kind;
        Mode = mode;
        PlayerNetIds = playerNetIds?
            .Where(id => id != 0)
            .ToHashSet()
            ?? new HashSet<ulong>();
        ChangedCards = changedCards?.ToList() ?? [];
    }

    public LoadoutRunContentKind Kind { get; }

    public LoadoutRunContentChangeMode Mode { get; }

    public IReadOnlySet<ulong> PlayerNetIds { get; }

    public IReadOnlyList<LoadoutChangedCard> ChangedCards { get; }

    public bool AffectsPlayer(ulong playerNetId)
    {
        return PlayerNetIds.Count == 0 || PlayerNetIds.Contains(playerNetId);
    }
}

public static class LoadoutRunContentChangeService
{
    public static event Action<LoadoutRunContentChangedEventArgs>? Changed;

    public static void Notify(
        LoadoutRunContentKind kind,
        ulong playerNetId,
        LoadoutRunContentChangeMode mode = LoadoutRunContentChangeMode.Unknown)
    {
        Notify(kind, [playerNetId], mode);
    }

    public static void Notify(
        LoadoutRunContentKind kind,
        IEnumerable<ulong> playerNetIds,
        LoadoutRunContentChangeMode mode = LoadoutRunContentChangeMode.Unknown,
        IEnumerable<LoadoutChangedCard>? changedCards = null)
    {
        LoadoutRunContentChangedEventArgs args = new(kind, playerNetIds, mode, changedCards);
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

    public static void NotifyCardUpdated(LoadoutOwnedItem<CardModel> item)
    {
        Notify(
            LoadoutRunContentKind.Cards,
            [item.OwnerNetId],
            LoadoutRunContentChangeMode.Update,
            [new LoadoutChangedCard(item.OwnerNetId, item.Index, item.Model.Id)]);
    }
}
