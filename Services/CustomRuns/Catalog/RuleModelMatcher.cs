#nullable enable

namespace Loadout.Services.CustomRuns.Catalog;

using System;
using System.Collections.Generic;
using System.Linq;
using Loadout.PanelItems;
using Loadout.Services.CustomRuns.Models;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;

public static class RuleModelMatcher
{
    public static IReadOnlyList<AbstractModel> Resolve(ModelMatchSpec matcher)
    {
        return CustomRunCatalogService.GetCatalog(matcher.ModelKind)
            .Where(entry => Matches(entry, matcher))
            .Select(entry => entry.Model)
            .ToList();
    }

    public static bool Matches(AbstractModel model, ModelMatchSpec matcher)
    {
        return CustomRunCatalogService.TryResolve(matcher.ModelKind, model.Id.ToString(), out CustomRunCatalogEntry entry)
               && Matches(entry, matcher);
    }

    private static bool Matches(CustomRunCatalogEntry entry, ModelMatchSpec matcher)
    {
        string value = matcher.Value;
        return matcher.Kind switch
        {
            ModelMatchKind.SpecificModels => matcher.ModelIds.Any(id =>
                string.Equals(id, entry.ModelId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(id, entry.Model.Id.Entry, StringComparison.OrdinalIgnoreCase)),
            ModelMatchKind.Pool or ModelMatchKind.Rarity or ModelMatchKind.Act =>
                entry.Categories.Contains(value),
            ModelMatchKind.Type or ModelMatchKind.MonsterCategory => entry.Types.Contains(value),
            ModelMatchKind.Keyword => entry.Model is CardModel keywordCard
                                      && keywordCard.GetKeywordsWithSources(KeywordSources.Local)
                                          .Any(keyword => string.Equals(keyword.ToString(), value, StringComparison.OrdinalIgnoreCase)),
            ModelMatchKind.Tag => entry.Model is CardModel tagCard
                                  && tagCard.Tags.Any(tag => string.Equals(tag.ToString(), value, StringComparison.OrdinalIgnoreCase)),
            ModelMatchKind.EnergyCost => entry.Model is CardModel costCard && MatchesCost(costCard, value),
            ModelMatchKind.TextContains => GetSearchText(entry.Model)
                .Contains(value, StringComparison.OrdinalIgnoreCase),
            ModelMatchKind.Mod => string.Equals(entry.ModId, value, StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private static bool MatchesCost(CardModel card, string value)
    {
        if (value == "X")
            return card.EnergyCost.CostsX;
        if (value == "3+")
            return card.EnergyCost.Canonical >= 3;
        if (value == "unplayable")
            return card.EnergyCost.Canonical < 0;
        return int.TryParse(value, out int cost) && card.EnergyCost.Canonical == cost;
    }

    private static string GetSearchText(AbstractModel model)
    {
        string titleAndText = model switch
        {
            CardModel card => $"{CardPrinter.FormatCardTitle(card)} {card.GetDescriptionForPile(PileType.None)}",
            RelicModel relic => $"{CommonHelpers.FormatRelicTitle(relic)} {relic.DynamicDescription.GetFormattedText()}",
            PotionModel potion => CommonHelpers.FormatPotionTitle(potion),
            PowerModel power => CommonHelpers.FormatPowerTitle(power),
            EventModel eventModel => CommonHelpers.FormatEventTitle(eventModel),
            _ => model.Id.Entry
        };
        return $"{model.Id} {titleAndText}";
    }
}
