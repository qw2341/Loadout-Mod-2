#nullable enable

namespace Loadout.Keywords;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using Loadout.Services.Compatibility;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;

public sealed class StickyKeyword : LoadoutKeywordModel
{
    public static StickyKeyword Instance { get; } = new();

    private StickyKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.Sticky;

    public override string StorageKey => LoadoutKeywords.StickyKey;

    public override string TitleLocKey => "LOADOUT-STICKY.title";
}

public static class StickyDiscardPatch
{
    [HarmonyPrefix]
    public static void Prefix(
        ref IEnumerable<CardModel> cardsToDiscard,
        out List<CardModel>? __state)
    {
        // Do not remove Sticky cards from the native discard operation.
        // The game must see them so that discard history, hooks, and Sly work.
        IReadOnlyList<CardModel> cards;

        if (cardsToDiscard is IReadOnlyList<CardModel> readOnlyList)
        {
            cards = readOnlyList;
        }
        else
        {
            List<CardModel> materialized = cardsToDiscard.ToList();
            cardsToDiscard = materialized;
            cards = materialized;
        }

        __state = null;

        for (int index = 0; index < cards.Count; index++)
        {
            CardModel card = cards[index];
            if (!LoadoutKeywords.Has(card, LoadoutKeywords.Sticky))
                continue;

            (__state ??= new List<CardModel>(1)).Add(card);
        }
    }

    [HarmonyPostfix]
    public static void Postfix(
        ref Task __result,
        List<CardModel>? __state)
    {
        if (__state is not { Count: > 0 })
            return;

        __result = ReturnStickyCardsAfterDiscard(__result, __state);
    }

    private static async Task ReturnStickyCardsAfterDiscard(
        Task originalDiscard,
        IReadOnlyList<CardModel> stickyCards)
    {
        await originalDiscard;

        List<CardModel>? cardsToReturn = null;
        for (int index = 0; index < stickyCards.Count; index++)
        {
            CardModel card = stickyCards[index];

            // A Sticky + Sly card will normally already be back in hand.
            // Only return cards that remain in the discard pile.
            if (card.Pile?.Type != PileType.Discard)
                continue;

            (cardsToReturn ??= new List<CardModel>(stickyCards.Count)).Add(card);
        }

        if (cardsToReturn is null)
            return;

        await Sts2Compatibility.AddCards(
            cardsToReturn,
            PileType.Hand,
            CardPilePosition.Bottom);
    }
}

public static class StickyFlushPlayerHandPatch
{
    public static MethodBase TargetMethod()
    {
        return AccessTools.Method(
                   typeof(CombatManager),
                   "FlushPlayerHand",
                   [
                       typeof(Player),
                       typeof(HookPlayerChoiceContext)
                   ])
               ?? throw new MissingMethodException(
                   typeof(CombatManager).FullName,
                   "FlushPlayerHand(Player, HookPlayerChoiceContext)");
    }

    [HarmonyPrefix]
    public static void Prefix(
        Player player,
        out List<CardModel> __state)
    {
        __state = PileType.Hand
            .GetPile(player)
            .Cards
            .Where(card => LoadoutKeywords.Has(card, LoadoutKeywords.Sticky))
            .ToList();
    }

    [HarmonyPostfix]
    public static void Postfix(
        ref Task __result,
        List<CardModel> __state)
    {
        if (__state.Count == 0)
            return;

        __result = ReturnStickyCardsAfterFlush(__result, __state);
    }

    private static async Task ReturnStickyCardsAfterFlush(
        Task originalFlush,
        IReadOnlyList<CardModel> stickyCards)
    {
        await originalFlush;

        List<CardModel> cardsToReturn = stickyCards
            .Where(card => card.Pile?.Type == PileType.Discard)
            .ToList();

        if (cardsToReturn.Count == 0)
            return;

        await Sts2Compatibility.AddCards(
            cardsToReturn,
            PileType.Hand,
            CardPilePosition.Bottom);
    }
}
