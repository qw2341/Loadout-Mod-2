#nullable enable

namespace Loadout.Services.CustomRuns.Runtime;

using System;
using System.Runtime.CompilerServices;
using System.Threading;
using MegaCrit.Sts2.Core.Models;

internal static class CustomRunReplacementProvenance
{
    private static readonly AsyncLocal<CardReplacementScope?> CurrentCardReplacement = new();
    private static readonly ConditionalWeakTable<CardModel, ForcedMarker> ForcedCards = new();
    private static readonly ConditionalWeakTable<RelicModel, ForcedMarker> ForcedRelics = new();

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
        if (!CustomRunRuleRuntimeService.CardReplacementEnabled)
            return false;
        if (ForcedCards.TryGetValue(card, out _))
            return true;
        CardReplacementScope? scope = CurrentCardReplacement.Value;
        return scope is not null && ReferenceEquals(scope.Destination, card.CanonicalInstance);
    }

    internal static RelicModel CreateRelicOccurrence(RelicModel destination)
    {
        RelicModel occurrence = destination.CanonicalInstance.ToMutable();
        Mark(ForcedRelics, occurrence);
        return occurrence;
    }

    internal static bool TryReuseRelicOccurrence(RelicModel relic, out RelicModel occurrence)
    {
        if (CustomRunRuleRuntimeService.RelicReplacementEnabled
            && ForcedRelics.TryGetValue(relic, out _))
        {
            occurrence = relic;
            return true;
        }
        occurrence = null!;
        return false;
    }

    internal static bool TryConsumeRelicAuthorization(RelicModel relic)
    {
        if (!CustomRunRuleRuntimeService.RelicReplacementEnabled
            || !ForcedRelics.TryGetValue(relic, out _))
            return false;
        ForcedRelics.Remove(relic);
        return true;
    }

    internal static bool IsForced(RelicModel relic)
    {
        return CustomRunRuleRuntimeService.RelicReplacementEnabled
               && ForcedRelics.TryGetValue(relic, out _);
    }

    internal static void Clear()
    {
        CurrentCardReplacement.Value = null;
        ForcedCards.Clear();
        ForcedRelics.Clear();
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
}
