#nullable enable

namespace Loadout.Keywords;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;

public static class LoadoutSpecialKeywords
{
    private static readonly FieldInfo DynamicVarDictionaryField =
        AccessTools.Field(typeof(DynamicVarSet), "_vars")
        ?? throw new MissingFieldException(typeof(DynamicVarSet).FullName, "_vars");

    private static readonly IReadOnlyList<LoadoutKeywordModel> Models =
    [
        LessonLearnedKeyword.Instance
    ];

    public static IReadOnlyList<LoadoutKeywordModel> All => Models;

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
               && model is LoadoutDescriptionKeywordModel;
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

                    DynamicVar value = new(dynamicVar.Name, dynamicVar.DefaultValue);
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

        foreach (LoadoutKeywordModel model in Models)
        {
            if (model is not LoadoutDescriptionKeywordModel descriptionKeyword
                || !model.IsEnabled(card))
            {
                continue;
            }

            string formatted = descriptionKeyword.GetCardText(card);
            if (string.IsNullOrWhiteSpace(formatted))
                continue;

            if (descriptionKeyword.TextPosition == LoadoutKeywordTextPosition.Before)
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
        foreach (LoadoutKeywordModel model in Models)
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
        __result = LoadoutSpecialKeywords.AddDescriptionLines(__instance, __result);
    }
}

public static class LoadoutDescriptionKeywordHoverTipsPatch
{
    [HarmonyPostfix]
    public static void Postfix(
        CardModel __instance,
        ref IEnumerable<IHoverTip> __result)
    {
        __result = LoadoutSpecialKeywords.RemoveDescriptionKeywordHoverTips(
            __instance,
            __result);
    }
}
