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
    public const string AltHeavenlyKey = "AltHeavenly";
    public const string LifestealKey = "Lifesteal";
    public const string WallopKey = "Wallop";
    public const string BasicDamageKey = "BasicDamage";
    public const string BasicDamageAoeKey = "BasicDamageAoe";
    public const string BasicMultiHitKey = "BasicMultiHit";
    public const string BasicMultiHitAoeKey = "BasicMultiHitAoe";
    public const string BasicBlockKey = "BasicBlock";
    public const string BasicDrawKey = "BasicDraw";
    public const string BasicDiscardKey = "BasicDiscard";
    public const string BasicExhaustKey = "BasicExhaust";
    public const string BasicHealKey = "BasicHeal";
    public const string BasicLoseHealthKey = "BasicLoseHealth";
    public const string BasicEnergyKey = "BasicEnergy";
    public const string DamageOnPlayKey = "ImprovementDamageOnPlay";
    public const string DoubleDamageOnPlayKey =
        "ImprovementDoubleDamageOnPlay";
    public const string AllDamageOnPlayKey = "ImprovementAllDamageOnPlay";
    public const string PermanentDamageOnPlayKey =
        "ImprovementPermanentDamageOnPlay";
    public const string PermanentDamageOnFatalKey =
        "ImprovementPermanentDamageOnFatal";

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

    [CustomEnum("ALT_HEAVENLY")]
    public static CardKeyword AltHeavenly;

    [CustomEnum("LIFESTEAL")]
    public static CardKeyword Lifesteal;

    [CustomEnum("WALLOP")]
    public static CardKeyword Wallop;

    [CustomEnum("BASIC_DAMAGE")]
    public static CardKeyword BasicDamage;

    [CustomEnum("BASIC_DAMAGE_AOE")]
    public static CardKeyword BasicDamageAoe;

    [CustomEnum("BASIC_MULTI_HIT")]
    public static CardKeyword BasicMultiHit;

    [CustomEnum("BASIC_MULTI_HIT_AOE")]
    public static CardKeyword BasicMultiHitAoe;

    [CustomEnum("BASIC_BLOCK")]
    public static CardKeyword BasicBlock;

    [CustomEnum("BASIC_DRAW")]
    public static CardKeyword BasicDraw;

    [CustomEnum("BASIC_DISCARD")]
    public static CardKeyword BasicDiscard;

    [CustomEnum("BASIC_EXHAUST")]
    public static CardKeyword BasicExhaust;

    [CustomEnum("BASIC_HEAL")]
    public static CardKeyword BasicHeal;

    [CustomEnum("BASIC_LOSE_HEALTH")]
    public static CardKeyword BasicLoseHealth;

    [CustomEnum("BASIC_ENERGY")]
    public static CardKeyword BasicEnergy;

    [CustomEnum("IMPROVEMENT_DAMAGE_ON_PLAY")]
    public static CardKeyword DamageOnPlay;

    [CustomEnum("IMPROVEMENT_DOUBLE_DAMAGE_ON_PLAY")]
    public static CardKeyword DoubleDamageOnPlay;

    [CustomEnum("IMPROVEMENT_ALL_DAMAGE_ON_PLAY")]
    public static CardKeyword AllDamageOnPlay;

    [CustomEnum("IMPROVEMENT_PERMANENT_DAMAGE_ON_PLAY")]
    public static CardKeyword PermanentDamageOnPlay;

    [CustomEnum("IMPROVEMENT_PERMANENT_DAMAGE_ON_FATAL")]
    public static CardKeyword PermanentDamageOnFatal;

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
