#nullable enable

namespace Loadout.Services.CustomRuns.Runtime;

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

internal static class CustomRunReplacementProvenance
{
    private const int MaximumPendingRelics = 256;
    private static readonly object Gate = new();
    private static readonly AsyncLocal<CardReplacementScope?> CurrentCardReplacement = new();
    private static readonly ConditionalWeakTable<CardModel, ForcedMarker> ForcedCards = new();
    private static readonly ConditionalWeakTable<RelicModel, ForcedMarker> ForcedRelics = new();
    private static readonly List<PendingRelicReplacement> PendingRelics = [];

    internal static IDisposable BeginCardReplacement(CardModel destination)
    {
        CardReplacementScope scope = new(destination.CanonicalInstance, CurrentCardReplacement.Value);
        CurrentCardReplacement.Value = scope;
        return scope;
    }

    internal static void TransferCardToMutable(CardModel canonical, CardModel mutable)
    {
        CardReplacementScope? scope = CurrentCardReplacement.Value;
        if (scope is null || !ReferenceEquals(scope.Destination, canonical.CanonicalInstance))
            return;
        Mark(ForcedCards, mutable);
    }

    internal static void MarkCard(CardModel card) => Mark(ForcedCards, card);

    internal static bool IsForced(CardModel card)
    {
        if (ForcedCards.TryGetValue(card, out _))
            return true;
        CardReplacementScope? scope = CurrentCardReplacement.Value;
        return scope is not null && ReferenceEquals(scope.Destination, card.CanonicalInstance);
    }

    internal static void RecordRelicReplacement(RelicModel destination, ulong ownerId, bool sharedTreasure)
    {
        lock (Gate)
        {
            PendingRelics.Add(new PendingRelicReplacement(destination.CanonicalInstance, ownerId, sharedTreasure));
            if (PendingRelics.Count > MaximumPendingRelics)
                PendingRelics.RemoveRange(0, PendingRelics.Count - MaximumPendingRelics);
        }
    }

    internal static void TransferRelicToMutable(RelicModel canonical, RelicModel mutable)
    {
        lock (Gate)
        {
            int index = PendingRelics.FindIndex(candidate =>
                ReferenceEquals(candidate.Destination, canonical.CanonicalInstance));
            if (index < 0)
                return;
            PendingRelics.RemoveAt(index);
            Mark(ForcedRelics, mutable);
        }
    }

    internal static bool TryAuthorizeRelicObtain(RelicModel relic, Player player)
    {
        if (ForcedRelics.TryGetValue(relic, out _))
            return true;
        lock (Gate)
        {
            int index = PendingRelics.FindIndex(candidate =>
                ReferenceEquals(candidate.Destination, relic.CanonicalInstance)
                && (candidate.SharedTreasure || candidate.OwnerId == player.NetId));
            if (index < 0)
                return false;
            PendingRelics.RemoveAt(index);
            Mark(ForcedRelics, relic);
            return true;
        }
    }

    internal static bool IsForced(RelicModel relic)
    {
        if (ForcedRelics.TryGetValue(relic, out _))
            return true;
        lock (Gate)
        {
            return PendingRelics.Exists(candidate =>
                candidate.SharedTreasure
                && ReferenceEquals(candidate.Destination, relic.CanonicalInstance));
        }
    }

    internal static void ClearSharedRelics()
    {
        lock (Gate)
            PendingRelics.RemoveAll(candidate => candidate.SharedTreasure);
    }

    internal static void Clear()
    {
        CurrentCardReplacement.Value = null;
        ForcedCards.Clear();
        ForcedRelics.Clear();
        lock (Gate)
            PendingRelics.Clear();
    }

    private static void Mark<T>(ConditionalWeakTable<T, ForcedMarker> table, T model)
        where T : class
    {
        table.Remove(model);
        table.Add(model, ForcedMarker.Instance);
    }

    private sealed class CardReplacementScope(CardModel destination, CardReplacementScope? previous) : IDisposable
    {
        private bool _disposed;

        internal CardModel Destination { get; } = destination;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            if (ReferenceEquals(CurrentCardReplacement.Value, this))
                CurrentCardReplacement.Value = previous;
        }
    }

    private sealed class ForcedMarker
    {
        internal static ForcedMarker Instance { get; } = new();
    }

    private readonly record struct PendingRelicReplacement(
        RelicModel Destination,
        ulong OwnerId,
        bool SharedTreasure);
}
