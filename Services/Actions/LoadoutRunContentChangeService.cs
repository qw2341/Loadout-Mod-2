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

public enum LoadoutCardVisualRefreshKind
{
    Lightweight,
    Reload
}

public readonly record struct LoadoutChangedCard(
    ulong OwnerNetId,
    int Index,
    ModelId ModelId,
    LoadoutCardVisualRefreshKind RefreshKind = LoadoutCardVisualRefreshKind.Lightweight);

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
    private static readonly object QueueGate = new();
    private static readonly List<LoadoutRunContentChangedEventArgs> QueuedChanges = [];
    private static bool _queueFlushScheduled;

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


    /// <summary>
    /// Coalesces native command postfixes that may fire many times in one frame
    /// (for example, adding or upgrading an entire deck). Structural changes
    /// collapse to one parent-view rebuild; pure updates remain item-targeted.
    /// </summary>
    public static void Queue(
        LoadoutRunContentKind kind,
        IEnumerable<ulong> playerNetIds,
        LoadoutRunContentChangeMode mode = LoadoutRunContentChangeMode.Unknown,
        IEnumerable<LoadoutChangedCard>? changedCards = null)
    {
        lock (QueueGate)
        {
            QueuedChanges.Add(new LoadoutRunContentChangedEventArgs(kind, playerNetIds, mode, changedCards));
            if (_queueFlushScheduled)
                return;

            _queueFlushScheduled = true;
        }

        Callable.From(FlushQueued).CallDeferred();
    }

    public static void Queue(
        LoadoutRunContentKind kind,
        ulong playerNetId,
        LoadoutRunContentChangeMode mode = LoadoutRunContentChangeMode.Unknown)
    {
        Queue(kind, [playerNetId], mode);
    }

    public static void ResetQueuedChanges()
    {
        lock (QueueGate)
        {
            QueuedChanges.Clear();
            _queueFlushScheduled = false;
        }
    }

    private static void FlushQueued()
    {
        List<LoadoutRunContentChangedEventArgs> pending;
        lock (QueueGate)
        {
            _queueFlushScheduled = false;
            if (QueuedChanges.Count == 0)
                return;

            pending = QueuedChanges.ToList();
            QueuedChanges.Clear();
        }

        foreach (IGrouping<LoadoutRunContentKind, LoadoutRunContentChangedEventArgs> group in pending.GroupBy(change => change.Kind))
        {
            HashSet<ulong> players = group.SelectMany(change => change.PlayerNetIds).ToHashSet();
            List<LoadoutRunContentChangeMode> modes = group.Select(change => change.Mode).Distinct().ToList();
            bool hasStructuralChange = modes.Any(mode => mode != LoadoutRunContentChangeMode.Update);
            LoadoutRunContentChangeMode mergedMode = modes.Count == 1
                ? modes[0]
                : hasStructuralChange
                    ? LoadoutRunContentChangeMode.Replace
                    : LoadoutRunContentChangeMode.Update;

            List<LoadoutChangedCard> cards = group
                .SelectMany(change => change.ChangedCards)
                .GroupBy(card => (card.OwnerNetId, card.Index, Id: card.ModelId.ToString()))
                .Select(cardGroup =>
                {
                    LoadoutChangedCard first = cardGroup.First();
                    LoadoutCardVisualRefreshKind refreshKind = cardGroup.Any(card => card.RefreshKind == LoadoutCardVisualRefreshKind.Reload)
                        ? LoadoutCardVisualRefreshKind.Reload
                        : LoadoutCardVisualRefreshKind.Lightweight;
                    return first with { RefreshKind = refreshKind };
                })
                .ToList();

            Notify(group.Key, players, mergedMode, cards);
        }
    }

    public static void NotifyCardUpdated(
        LoadoutOwnedItem<CardModel> item,
        LoadoutCardVisualRefreshKind refreshKind = LoadoutCardVisualRefreshKind.Lightweight)
    {
        Notify(
            LoadoutRunContentKind.Cards,
            [item.OwnerNetId],
            LoadoutRunContentChangeMode.Update,
            [new LoadoutChangedCard(item.OwnerNetId, item.Index, item.Model.Id, refreshKind)]);
    }
}
