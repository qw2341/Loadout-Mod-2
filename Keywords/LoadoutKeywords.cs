#nullable enable

namespace Loadout.Keywords;

using System;
using System.Collections.Generic;
using System.Linq;
using BaseLib.Patches.Content;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;

public static class LoadoutKeywords
{
    public const string InevitableKey = "Inevitable";
    public const string StickyKey = "Sticky";
    public const string PassingKey = "Passing";
    public const string LividKey = "Livid";
    public const string XCostKey = "XCost";
    public const string InfiniteUpgradeKey = "InfiniteUpgrade";
    public const string LessonLearnedKey = "LessonLearned";
    public const string HeavenlyKey = "Heavenly";

    [CustomEnum, KeywordProperties(AutoKeywordPosition.Before)]
    public static CardKeyword Inevitable;

    [CustomEnum, KeywordProperties(AutoKeywordPosition.Before)]
    public static CardKeyword Sticky;

    [CustomEnum, KeywordProperties(AutoKeywordPosition.Before)]
    public static CardKeyword Passing;

    [CustomEnum, KeywordProperties(AutoKeywordPosition.Before)]
    public static CardKeyword Livid;

    [CustomEnum("X_COST")]
    public static CardKeyword XCost;

    [CustomEnum("INFINITE_UPGRADE"), KeywordProperties(AutoKeywordPosition.Before)]
    public static CardKeyword InfiniteUpgrade;

    [CustomEnum("LESSON_LEARNED")]
    public static CardKeyword LessonLearned;

    [CustomEnum("HEAVENLY")]
    public static CardKeyword Heavenly;

    public static IEnumerable<CardKeyword> All =>
        LoadoutKeywordRegistry.All.Select(model => model.Keyword);

    public static bool Has(CardModel? card, CardKeyword keyword)
    {
        return card is not null
               && keyword != CardKeyword.None
               && card.GetKeywordsWithSources(KeywordSources.Local).Contains(keyword);
    }

    public static string GetStorageKey(CardKeyword keyword)
    {
        return LoadoutKeywordRegistry.TryGet(keyword, out LoadoutKeywordModel model)
            ? model.StorageKey
            : keyword.ToString();
    }

    public static bool TryResolve(string? key, out CardKeyword keyword)
    {
        string? normalized = key?.Trim();
        foreach (LoadoutKeywordModel model in LoadoutKeywordRegistry.All)
        {
            if (!string.Equals(
                    model.StorageKey,
                    normalized,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            keyword = model.Keyword;
            return keyword != CardKeyword.None;
        }

        if (Enum.TryParse(normalized, ignoreCase: true, out keyword)
            && keyword != CardKeyword.None)
            return true;

        keyword = CardKeyword.None;
        return false;
    }
}
