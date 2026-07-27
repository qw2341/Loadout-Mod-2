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

public enum LoadoutKeywordPresentation
{
    TitleKeyword,
    DescriptionLine
}

public enum LoadoutKeywordTextPosition
{
    Before,
    After
}

public sealed record LoadoutKeywordDynamicVarDefinition(
    string Name,
    decimal DefaultValue,
    int Minimum,
    int Maximum,
    string LabelLocKey);

public sealed record LoadoutSpecialKeywordDefinition(
    CardKeyword Keyword,
    string StorageKey,
    LoadoutKeywordPresentation Presentation,
    LoadoutKeywordTextPosition TextPosition,
    string TitleLocKey,
    string CardTextLocKey,
    IReadOnlyList<LoadoutKeywordDynamicVarDefinition> DynamicVars);

public static class LoadoutSpecialKeywords
{
    private static readonly FieldInfo DynamicVarDictionaryField =
        AccessTools.Field(typeof(DynamicVarSet), "_vars")
        ?? throw new MissingFieldException(typeof(DynamicVarSet).FullName, "_vars");

    private sealed record Registration(
        Func<CardKeyword> GetKeyword,
        Func<LoadoutSpecialKeywordDefinition> CreateDefinition);

    private static readonly IReadOnlyList<Registration> Registrations =
    [
        new(
            () => LoadoutKeywords.LessonLearned,
            LessonLearnedKeyword.CreateDefinition)
    ];

    private static IReadOnlyList<LoadoutSpecialKeywordDefinition> _definitions = [];

    public static IReadOnlyList<LoadoutSpecialKeywordDefinition> All
    {
        get
        {
            EnsureDefinitions();
            return _definitions;
        }
    }

    public static bool TryGet(
        CardKeyword keyword,
        out LoadoutSpecialKeywordDefinition definition)
    {
        EnsureDefinitions();
        foreach (LoadoutSpecialKeywordDefinition candidate in _definitions)
        {
            if (candidate.Keyword.Equals(keyword))
            {
                definition = candidate;
                return true;
            }
        }

        definition = null!;
        return false;
    }

    public static bool TryGetDynamicVar(
        string name,
        out LoadoutKeywordDynamicVarDefinition definition)
    {
        EnsureDefinitions();
        foreach (LoadoutSpecialKeywordDefinition keyword in _definitions)
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
        return TryGet(keyword, out LoadoutSpecialKeywordDefinition definition)
               && definition.Presentation == LoadoutKeywordPresentation.DescriptionLine;
    }

    public static string GetTitle(LoadoutSpecialKeywordDefinition definition)
    {
        return new LocString("card_keywords", definition.TitleLocKey).GetFormattedText();
    }

    public static bool IsEnabled(
        CardModel card,
        LoadoutSpecialKeywordDefinition definition,
        IReadOnlyDictionary<string, bool>? overrides = null)
    {
        return overrides?.TryGetValue(definition.StorageKey, out bool enabled) == true
            ? enabled
            : LoadoutKeywords.Has(card, definition.Keyword);
    }

    public static void SynchronizeDynamicVars(
        CardModel card,
        IReadOnlyDictionary<string, bool>? overrides = null)
    {
        Dictionary<string, DynamicVar> variables = GetMutableVariables(card.DynamicVars);
        foreach (LoadoutSpecialKeywordDefinition definition in All)
        {
            bool enabled = IsEnabled(card, definition, overrides);
            foreach (LoadoutKeywordDynamicVarDefinition dynamicVar in definition.DynamicVars)
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

        foreach (LoadoutSpecialKeywordDefinition definition in All)
        {
            if (definition.Presentation != LoadoutKeywordPresentation.DescriptionLine
                || !LoadoutKeywords.Has(card, definition.Keyword))
            {
                continue;
            }

            LocString cardText = new("card_keywords", definition.CardTextLocKey);
            card.DynamicVars.AddTo(cardText);
            string formatted = cardText.GetFormattedText();
            if (string.IsNullOrWhiteSpace(formatted))
                continue;

            if (definition.TextPosition == LoadoutKeywordTextPosition.Before)
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
        foreach (LoadoutSpecialKeywordDefinition definition in All)
        {
            if (definition.Presentation != LoadoutKeywordPresentation.DescriptionLine
                || !LoadoutKeywords.Has(card, definition.Keyword))
            {
                continue;
            }

            excludedIds ??= [];
            excludedIds.Add(HoverTipFactory.FromKeyword(definition.Keyword).Id);
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

    private static void EnsureDefinitions()
    {
        bool current = _definitions.Count == Registrations.Count;
        for (int index = 0; current && index < Registrations.Count; index++)
            current = Registrations[index].GetKeyword().Equals(_definitions[index].Keyword);

        if (current)
        {
            return;
        }

        _definitions = Registrations
            .Select(registration => registration.CreateDefinition())
            .ToArray();
    }
}
