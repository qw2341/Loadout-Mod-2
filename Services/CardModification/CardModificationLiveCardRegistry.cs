#nullable enable

namespace Loadout.Services.CardModification;

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;

/// <summary>
/// Weak index of live mutable deck/combat card models.
///
/// The registry is updated only when STS2 creates, loads or clones a CardModel.
/// It never polls decks and never owns card lifetime. Permanent modification edits
/// use it to update only already-existing matching copies instead of scanning every
/// card owned by every player.
/// </summary>
internal static class CardModificationLiveCardRegistry
{
    private static readonly object Gate = new();
    private static readonly object RegistrationValue = new();
    private static ConditionalWeakTable<CardModel, object> _registered = new();
    private static readonly Dictionary<ModelId, List<WeakReference<CardModel>>> CardsByModelId = new();

    public static bool IsRegistered(CardModel? card)
    {
        return card is not null
               && !card.IsCanonical
               && _registered.TryGetValue(card, out _);
    }

    public static bool Register(CardModel? card)
    {
        if (card is null || card.IsCanonical)
            return false;

        lock (Gate)
        {
            if (_registered.TryGetValue(card, out _))
                return false;

            _registered.Add(card, RegistrationValue);
            if (!CardsByModelId.TryGetValue(card.Id, out List<WeakReference<CardModel>>? cards))
            {
                cards = [];
                CardsByModelId[card.Id] = cards;
            }

            cards.Add(new WeakReference<CardModel>(card));
            // Amortize weak-reference cleanup. Compacting on every insertion after a
            // threshold would itself become O(n²) for duplicate-heavy large decks.
            if ((cards.Count & 127) == 0)
                CompactLocked(card.Id, cards);
            return true;
        }
    }

    public static IReadOnlyList<CardModel> GetLiveCards(ModelId cardId)
    {
        lock (Gate)
        {
            if (!CardsByModelId.TryGetValue(cardId, out List<WeakReference<CardModel>>? cards))
                return [];

            List<CardModel> result = new(cards.Count);
            int writeIndex = 0;
            for (int readIndex = 0; readIndex < cards.Count; readIndex++)
            {
                WeakReference<CardModel> reference = cards[readIndex];
                if (!reference.TryGetTarget(out CardModel? card)
                    || card.IsCanonical
                    || !card.Id.Equals(cardId))
                {
                    continue;
                }

                cards[writeIndex++] = reference;
                result.Add(card);
            }

            if (writeIndex < cards.Count)
                cards.RemoveRange(writeIndex, cards.Count - writeIndex);
            if (cards.Count == 0)
                CardsByModelId.Remove(cardId);

            return result;
        }
    }

    public static void Clear()
    {
        lock (Gate)
        {
            CardsByModelId.Clear();
            _registered = new ConditionalWeakTable<CardModel, object>();
        }
    }

    private static void CompactLocked(ModelId cardId, List<WeakReference<CardModel>> cards)
    {
        int writeIndex = 0;
        for (int readIndex = 0; readIndex < cards.Count; readIndex++)
        {
            WeakReference<CardModel> reference = cards[readIndex];
            if (!reference.TryGetTarget(out CardModel? card)
                || card.IsCanonical
                || !card.Id.Equals(cardId))
            {
                continue;
            }

            cards[writeIndex++] = reference;
        }

        if (writeIndex < cards.Count)
            cards.RemoveRange(writeIndex, cards.Count - writeIndex);
        if (cards.Count == 0)
            CardsByModelId.Remove(cardId);
    }

}
