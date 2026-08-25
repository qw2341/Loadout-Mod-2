#nullable enable

namespace Loadout.Keywords;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;

public static class LoadoutKeywordRegistry
{
    private static readonly FieldInfo DynamicVarDictionaryField =
        AccessTools.Field(typeof(DynamicVarSet), "_vars")
        ?? throw new MissingFieldException(typeof(DynamicVarSet).FullName, "_vars");

    private static readonly IReadOnlyList<LoadoutKeywordModel> Models =
    [
        InevitableKeyword.Instance,
        StickyKeyword.Instance,
        PassingKeyword.Instance,
        LividKeyword.Instance,
        XCostKeyword.Instance,
        InfiniteUpgradeKeyword.Instance,
        HeavenlyKeyword.Instance,
        AltHeavenlyKeyword.Instance,
        LifestealKeyword.Instance,
        WallopKeyword.Instance,
        AutoplayKeyword.Instance,
        BasicDamageKeyword.Instance,
        BasicDamageAoeKeyword.Instance,
        BasicMultiHitKeyword.Instance,
        BasicMultiHitAoeKeyword.Instance,
        BasicBlockKeyword.Instance,
        BasicDrawKeyword.Instance,
        BasicDiscardKeyword.Instance,
        BasicExhaustKeyword.Instance,
        BasicTransformKeyword.Instance,
        BasicHealKeyword.Instance,
        BasicLoseHealthKeyword.Instance,
        BasicEnergyKeyword.Instance,
        BasicStarsKeyword.Instance,
        DiscardHandKeyword.Instance,
        NoDrawKeyword.Instance,
        InHandLoseHealthKeyword.Instance,
        InHandTakeDamageKeyword.Instance,
        InHandLoseGoldKeyword.Instance,
        EnthralledKeyword.Instance,
        ClashKeyword.Instance,
        EndTurnKeyword.Instance,
        GrandKeyword.Instance,
        BorrowedKeyword.Instance,
        LoseStrengthKeyword.Instance,
        LoseDexterityKeyword.Instance,
        LessonLearnedKeyword.Instance,
        FeedKeyword.Instance,
        GreedKeyword.Instance,
        HuntKeyword.Instance,
        SunderKeyword.Instance,
        AlchemyKeyword.Instance,
        VintageKeyword.Instance,
        DamageOnPlayKeyword.Instance,
        DoubleDamageOnPlayKeyword.Instance,
        AllDamageOnPlayKeyword.Instance,
        PermanentDamageOnPlayKeyword.Instance,
        PermanentDamageOnFatalKeyword.Instance,
        BlockOnPlayKeyword.Instance,
        AllBlockOnPlayKeyword.Instance,
        PermanentBlockOnPlayKeyword.Instance,
        DoubleBlockOnPlayKeyword.Instance,
        VariablesOnPlayKeyword.Instance,
        AllVariablesOnPlayKeyword.Instance,
        PermanentVariablesOnPlayKeyword.Instance,
        PermanentVariablesOnFatalKeyword.Instance,
        DoubleVariablesOnPlayKeyword.Instance
    ];

    private static readonly IReadOnlyList<LoadoutKeywordModel>
        DescriptionModels =
        Models
            .Where(model =>
                model.Presentation
                == LoadoutKeywordPresentation.DescriptionOnly)
            .ToArray();

    private static readonly IReadOnlyList<LoadoutKeywordModel>
        PostOnPlayModels =
        Models
            .Where(model => model.HasOnPlayEffect)
            .OrderBy(model => model.OnPlayPriority)
            .ToArray();

    private static readonly IReadOnlyList<LoadoutKeywordModel>
        UnblockedDamageModels =
        Models
            .Where(model => model.HasUnblockedDamageEffect)
            .ToArray();

    private static readonly IReadOnlyList<LoadoutKeywordModel>
        FatalModels =
        Models
            .Where(model => model.HasFatalEffect)
            .ToArray();

    private static readonly IReadOnlyList<LoadoutKeywordModel>
        TurnEndInHandModels =
        Models
            .Where(model => model.HasTurnEndInHandEffect)
            .ToArray();

    private static readonly IReadOnlyList<LoadoutKeywordModel>
        TargetChangingModels =
        Models
            .Where(model => model.ChangesTargeting)
            .ToArray();

    public static IReadOnlyList<LoadoutKeywordModel> All => Models;

    public static IReadOnlyList<LoadoutKeywordModel> DescriptionOnly =>
        DescriptionModels;

    public static IReadOnlyList<LoadoutKeywordModel> WithPostOnPlayEffect =>
        PostOnPlayModels;

    public static IReadOnlyList<LoadoutKeywordModel> WithUnblockedDamageEffect =>
        UnblockedDamageModels;

    public static IReadOnlyList<LoadoutKeywordModel> WithFatalEffect =>
        FatalModels;

    public static IReadOnlyList<LoadoutKeywordModel> WithTurnEndInHandEffect =>
        TurnEndInHandModels;

    public static bool TryGet(
        CardKeyword keyword,
        out LoadoutKeywordModel model)
    {
        foreach (LoadoutKeywordModel candidate in Models)
        {
            if (candidate.Keyword.Equals(keyword))
            {
                model = candidate;
                return true;
            }
        }

        model = null!;
        return false;
    }

    public static bool TryGetDynamicVar(
        string name,
        out LoadoutKeywordDynamicVarDefinition definition)
    {
        foreach (LoadoutKeywordModel keyword in Models)
        {
            foreach (LoadoutKeywordDynamicVarDefinition candidate in keyword.DynamicVars)
            {
                if (string.Equals(candidate.Name, name, StringComparison.Ordinal))
                {
                    definition = candidate;
                    return true;
                }
            }
        }

        definition = null!;
        return false;
    }

    public static bool IsDescriptionKeyword(CardKeyword keyword)
    {
        return TryGet(keyword, out LoadoutKeywordModel model)
               && model.Presentation == LoadoutKeywordPresentation.DescriptionOnly;
    }

    public static bool ChangesTargeting(CardModel card)
    {
        foreach (LoadoutKeywordModel model in TargetChangingModels)
        {
            if (model.IsEnabled(card))
                return true;
        }

        return false;
    }

    public static bool HasFatalEffect(CardModel card)
    {
        foreach (LoadoutKeywordModel model in FatalModels)
        {
            if (model.IsEnabled(card))
                return true;
        }

        return false;
    }

    public static bool HasTurnEndInHandEffect(CardModel card)
    {
        foreach (LoadoutKeywordModel model in TurnEndInHandModels)
        {
            if (model.IsEnabled(card))
                return true;
        }

        return false;
    }

    public static async Task ApplyFatalEffects(
        CardModel card,
        PlayerChoiceContext choiceContext,
        int fatalCount)
    {
        if (fatalCount <= 0)
            return;

        foreach (LoadoutKeywordModel model in FatalModels)
        {
            if (model.IsEnabled(card))
                await model.AfterFatal(card, choiceContext, fatalCount);
        }
    }

    public static string GetTitle(LoadoutKeywordModel model)
    {
        return model.GetTitle();
    }

    public static bool IsEnabled(
        CardModel card,
        LoadoutKeywordModel model,
        IReadOnlyDictionary<string, bool>? overrides = null)
    {
        return model.IsEnabled(card, overrides);
    }

    public static void SynchronizeDynamicVars(
        CardModel card,
        IReadOnlyDictionary<string, bool>? overrides = null)
    {
        Dictionary<string, DynamicVar> variables = GetMutableVariables(card.DynamicVars);
        foreach (LoadoutKeywordModel model in Models)
        {
            bool enabled = model.IsEnabled(card, overrides);
            foreach (LoadoutKeywordDynamicVarDefinition dynamicVar in model.DynamicVars)
            {
                if (enabled)
                {
                    if (variables.ContainsKey(dynamicVar.Name))
                        continue;

                    DynamicVar value = dynamicVar.Create();
                    value.SetOwner(card);
                    variables.Add(dynamicVar.Name, value);
                }
                else
                {
                    variables.Remove(dynamicVar.Name);
                }
            }
        }
    }

    public static bool TryGetValue(
        CardModel card,
        string name,
        out DynamicVar dynamicVar)
    {
        if (card.DynamicVars.TryGetValue(name, out DynamicVar? value)
            && value is not null)
        {
            dynamicVar = value;
            return true;
        }

        dynamicVar = null!;
        return false;
    }

    public static string AddDescriptionLines(CardModel card, string description)
    {
        List<string>? before = null;
        List<string>? after = null;

        foreach (LoadoutKeywordModel model in DescriptionModels)
        {
            if (!model.IsEnabled(card))
            {
                continue;
            }

            string formatted = model.GetCardText(card);
            if (string.IsNullOrWhiteSpace(formatted))
                continue;

            if (model.TextPosition == LoadoutKeywordTextPosition.Before)
                (before ??= []).Add(formatted);
            else
                (after ??= []).Add(formatted);
        }

        if (before is null && after is null)
            return description;

        IEnumerable<string> lines = (before ?? [])
            .Append(description)
            .Concat(after ?? [])
            .Where(line => !string.IsNullOrWhiteSpace(line));
        return string.Join('\n', lines);
    }

    public static IEnumerable<IHoverTip> RemoveDescriptionKeywordHoverTips(
        CardModel card,
        IEnumerable<IHoverTip> hoverTips)
    {
        HashSet<string>? excludedIds = null;
        foreach (LoadoutKeywordModel model in DescriptionModels)
        {
            if (model.ShowKeywordHoverTip || !model.IsEnabled(card))
            {
                continue;
            }

            excludedIds ??= [];
            excludedIds.Add(HoverTipFactory.FromKeyword(model.Keyword).Id);
        }

        return excludedIds is null
            ? hoverTips
            : hoverTips.Where(tip => !excludedIds.Contains(tip.Id));
    }

    private static Dictionary<string, DynamicVar> GetMutableVariables(DynamicVarSet dynamicVars)
    {
        return DynamicVarDictionaryField.GetValue(dynamicVars) as Dictionary<string, DynamicVar>
               ?? throw new InvalidOperationException(
                   $"{typeof(DynamicVarSet).FullName}._vars was not a DynamicVar dictionary.");
    }
}

public static class LoadoutDescriptionKeywordPatch
{
    [HarmonyPostfix]
    public static void Postfix(CardModel __instance, ref string __result)
    {
        __result = LoadoutKeywordRegistry.AddDescriptionLines(__instance, __result);
    }
}

public static class LoadoutDescriptionKeywordHoverTipsPatch
{
    [HarmonyPostfix]
    public static void Postfix(
        CardModel __instance,
        ref IEnumerable<IHoverTip> __result)
    {
        __result = LoadoutKeywordRegistry.RemoveDescriptionKeywordHoverTips(
            __instance,
            __result);
    }
}
