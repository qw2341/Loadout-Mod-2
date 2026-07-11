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
    public const string XCostKey = "XCost";
    public const string InfiniteUpgradeKey = "InfiniteUpgrade";

    [CustomEnum, KeywordProperties(AutoKeywordPosition.Before)]
    public static CardKeyword Inevitable;

    [CustomEnum, KeywordProperties(AutoKeywordPosition.Before)]
    public static CardKeyword Sticky;

    [CustomEnum("X_COST"), KeywordProperties(AutoKeywordPosition.Before)]
    public static CardKeyword XCost;

    [CustomEnum("INFINITE_UPGRADE"), KeywordProperties(AutoKeywordPosition.Before)]
    public static CardKeyword InfiniteUpgrade;

    public static IEnumerable<CardKeyword> All
    {
        get
        {
            yield return Inevitable;
            yield return Sticky;
            yield return XCost;
            yield return InfiniteUpgrade;
        }
    }

    public static bool Has(CardModel? card, CardKeyword keyword)
    {
        return card is not null
               && keyword != CardKeyword.None
               && card.GetKeywordsWithSources(KeywordSources.Local).Contains(keyword);
    }

    public static string GetStorageKey(CardKeyword keyword)
    {
        if (keyword.Equals(Inevitable))
            return InevitableKey;
        if (keyword.Equals(Sticky))
            return StickyKey;
        if (keyword.Equals(XCost))
            return XCostKey;
        if (keyword.Equals(InfiniteUpgrade))
            return InfiniteUpgradeKey;

        return keyword.ToString();
    }

    public static bool TryResolve(string? key, out CardKeyword keyword)
    {
        switch (key?.Trim())
        {
            case InevitableKey:
                keyword = Inevitable;
                return keyword != CardKeyword.None;
            case StickyKey:
                keyword = Sticky;
                return keyword != CardKeyword.None;
            case XCostKey:
                keyword = XCost;
                return keyword != CardKeyword.None;
            case InfiniteUpgradeKey:
                keyword = InfiniteUpgrade;
                return keyword != CardKeyword.None;
            default:
                return Enum.TryParse(key, ignoreCase: true, out keyword);
        }
    }
}
