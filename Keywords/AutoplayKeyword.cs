#nullable enable

namespace Loadout.Keywords;

using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;

public sealed class AutoplayKeyword : LoadoutKeywordModel
{
    public static AutoplayKeyword Instance { get; } = new();

    private AutoplayKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.Autoplay;

    public override string StorageKey => LoadoutKeywords.AutoplayKey;

    public override string TitleLocKey => "LOADOUT-AUTOPLAY.title";
}

[HarmonyPatch(typeof(Hook), nameof(Hook.AfterCardDrawn))]
internal static class AutoplayAfterCardDrawnPatch
{
    [HarmonyPostfix]
    private static void Postfix(
        PlayerChoiceContext choiceContext,
        CardModel card,
        ref Task __result)
    {
        if (!LoadoutKeywords.Has(card, LoadoutKeywords.Autoplay))
            return;

        __result = AutoPlayAfterDraw(__result, choiceContext, card);
    }

    private static async Task AutoPlayAfterDraw(
        Task originalDrawHooks,
        PlayerChoiceContext choiceContext,
        CardModel card)
    {
        await originalDrawHooks;

        if (card.Pile?.Type != PileType.Hand)
            return;

        await CardCmd.AutoPlay(choiceContext, card, null);
    }
}
