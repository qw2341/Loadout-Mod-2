#nullable enable

namespace Loadout.Patches.Core;

using System.Collections.Generic;
using System.Threading.Tasks;
using HarmonyLib;
using Loadout.Services.Actions;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

/// <summary>
/// Bridges native game commands back into the Loadout views. The mutation
/// service only asks the game to perform the action; these postfixes publish
/// the smallest UI invalidation after the native command has actually finished.
/// </summary>
[HarmonyPatch(
    typeof(CardPileCmd),
    nameof(CardPileCmd.Add),
    typeof(IEnumerable<CardModel>),
    typeof(CardPile),
    typeof(CardPilePosition),
    typeof(AbstractModel),
    typeof(bool))]
public static class LoadoutNativeDeckAddContentPatch
{
    [HarmonyPostfix]
    public static void Postfix(
        CardPile newPile,
        ref Task<IReadOnlyList<CardPileAddResult>> __result)
    {
        __result = PublishAfterAddAsync(__result, newPile);
    }

    private static async Task<IReadOnlyList<CardPileAddResult>> PublishAfterAddAsync(
        Task<IReadOnlyList<CardPileAddResult>> original,
        CardPile newPile)
    {
        IReadOnlyList<CardPileAddResult> results = await original;
        if (newPile.Type != PileType.Deck)
            return results;

        List<LoadoutChangedCard> changedCards = [];
        HashSet<ulong> changedPlayers = [];
        foreach (CardPileAddResult result in results)
        {
            if (!result.success || result.cardAdded.Owner is not { } owner)
                continue;

            int index = FindReferenceIndex(owner.Deck.Cards, result.cardAdded);
            if (index < 0)
                continue;

            changedPlayers.Add(owner.NetId);
            changedCards.Add(new LoadoutChangedCard(owner.NetId, index, result.cardAdded.Id));
        }

        if (changedCards.Count > 0)
        {
            LoadoutRunContentChangeService.Queue(
                LoadoutRunContentKind.Cards,
                changedPlayers,
                LoadoutRunContentChangeMode.Add,
                changedCards);
        }

        return results;
    }

    private static int FindReferenceIndex(IReadOnlyList<CardModel> cards, CardModel target)
    {
        for (int index = 0; index < cards.Count; index++)
        {
            if (ReferenceEquals(cards[index], target))
                return index;
        }

        return -1;
    }
}

[HarmonyPatch(
    typeof(RelicCmd),
    nameof(RelicCmd.Obtain),
    typeof(RelicModel),
    typeof(Player),
    typeof(int))]
public static class LoadoutNativeRelicObtainContentPatch
{
    [HarmonyPostfix]
    public static void Postfix(Player player, ref Task<RelicModel> __result)
    {
        __result = PublishAfterObtainAsync(__result, player);
    }

    private static async Task<RelicModel> PublishAfterObtainAsync(Task<RelicModel> original, Player player)
    {
        RelicModel result = await original;
        LoadoutRunContentChangeService.Queue(
            LoadoutRunContentKind.Relics,
            player.NetId,
            LoadoutRunContentChangeMode.Add);
        return result;
    }
}

[HarmonyPatch(typeof(RelicCmd), nameof(RelicCmd.Remove), typeof(RelicModel))]
public static class LoadoutNativeRelicRemoveContentPatch
{
    [HarmonyPrefix]
    public static void Prefix(RelicModel relic, out ulong __state)
    {
        __state = relic.Owner?.NetId ?? 0;
    }

    [HarmonyPostfix]
    public static void Postfix(ulong __state, ref Task __result)
    {
        __result = PublishAfterRemoveAsync(__result, __state);
    }

    private static async Task PublishAfterRemoveAsync(Task original, ulong ownerNetId)
    {
        await original;
        if (ownerNetId == 0)
            return;

        LoadoutRunContentChangeService.Queue(
            LoadoutRunContentKind.Relics,
            ownerNetId,
            LoadoutRunContentChangeMode.Remove);
    }
}
