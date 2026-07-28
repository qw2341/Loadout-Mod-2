#nullable enable

namespace Loadout.Keywords;

using System.Collections.Generic;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;

public sealed class InevitableKeyword : LoadoutKeywordModel
{
    public static InevitableKeyword Instance { get; } = new();

    private InevitableKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.Inevitable;

    public override string StorageKey => LoadoutKeywords.InevitableKey;

    public override string TitleLocKey => "LOADOUT-INEVITABLE.title";
}

public static class InevitableExhaustPatch
{
    [HarmonyPostfix]
    public static void Postfix(
        CardModel card,
        ref Task __result)
    {
        if (!LoadoutKeywords.Has(card, LoadoutKeywords.Inevitable))
            return;

        __result = AddCopyToHandAfterExhaust(__result, card);
    }

    private static async Task AddCopyToHandAfterExhaust(
        Task originalExhaust,
        CardModel exhaustedCard)
    {
        await originalExhaust;

        // Do not produce a copy if another exhaust hook already moved or
        // removed the original card.
        if (exhaustedCard.Pile?.Type != PileType.Exhaust)
            return;

        CardModel copy = exhaustedCard.CreateClone();
        await CardPileCmd.AddGeneratedCardToCombat(
            copy,
            PileType.Hand,
            exhaustedCard.Owner,
            CardPilePosition.Bottom);
    }
}

public static class InevitableTransformPatch
{
    [HarmonyPrefix]
    public static void Prefix(ref IEnumerable<CardTransformation> transformations)
    {
        List<CardTransformation> rewritten = [];
        foreach (CardTransformation transformation in transformations)
        {
            if (!LoadoutKeywords.Has(
                    transformation.Original,
                    LoadoutKeywords.Inevitable))
            {
                rewritten.Add(transformation);
                continue;
            }

            if (transformation.Replacement is { IsCanonical: false } discardedReplacement)
                discardedReplacement.CardScope!.RemoveCard(discardedReplacement);

            CardModel replacement =
                transformation.Original.CardScope!.CloneCard(transformation.Original);
            rewritten.Add(new CardTransformation(transformation.Original, replacement));
        }

        transformations = rewritten;
    }
}
