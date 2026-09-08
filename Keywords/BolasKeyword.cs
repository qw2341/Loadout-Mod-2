#nullable enable

namespace Loadout.Keywords;

using System.Collections.Generic;
using System.Threading.Tasks;
using HarmonyLib;
using Loadout.Services.Compatibility;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;

public sealed class BolasKeyword : LoadoutKeywordModel
{
    public static BolasKeyword Instance { get; } = new();

    private BolasKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.Bolas;

    public override string StorageKey => LoadoutKeywords.BolasKey;

    public override string TitleLocKey => "LOADOUT-BOLAS.title";

    public override LoadoutKeywordPresentation Presentation =>
        LoadoutKeywordPresentation.DescriptionOnly;

    public override string? CardTextLocKey => "LOADOUT-BOLAS.cardText";
}

[HarmonyPatch(typeof(Hook), nameof(Hook.BeforeHandDraw))]
internal static class BolasBeforeHandDrawPatch
{
    [HarmonyPostfix]
    private static void Postfix(
        ICombatState combatState,
        Player player,
        ref Task __result)
    {
        __result = ReturnCards(__result, combatState, player);
    }

    private static async Task ReturnCards(
        Task originalHooks,
        ICombatState combatState,
        Player player)
    {
        await originalHooks;

        List<CardModel>? cardsToReturn = null;
        foreach (AbstractModel model in combatState.IterateHookListeners())
        {
            if (model is not CardModel card
                || card.Owner != player
                || card.Pile?.Type == PileType.Hand
                || !LoadoutKeywords.Has(card, LoadoutKeywords.Bolas)
                || !WasPlayedLastTurn(card, player))
            {
                continue;
            }

            (cardsToReturn ??= []).Add(card);
        }

        if (cardsToReturn is null)
            return;

        await Sts2Compatibility.AddCards(
            cardsToReturn,
            PileType.Hand,
            CardPilePosition.Bottom);
    }

    private static bool WasPlayedLastTurn(CardModel card, Player player)
    {
        foreach (CardPlayFinishedEntry entry in
                 CombatManager.Instance.History.CardPlaysFinished)
        {
            if (entry.CardPlay.Card == card
                && entry.HappenedLastPlayerTurn(player))
            {
                return true;
            }
        }

        return false;
    }
}
