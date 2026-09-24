#nullable enable

namespace Loadout.Keywords;

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Localization.Formatters;
using MegaCrit.Sts2.Core.Models;
using SmartFormat.Core.Extensions;

public sealed class XValueKeyword : LoadoutKeywordModel
{
    public const string AdditionalAmountVar = "LoadoutXValueAdditionalAmount";
    private static readonly IReadOnlyList<LoadoutKeywordDynamicVarDefinition> VariableDefinitions =
    [
        new(AdditionalAmountVar, 0m, 0, int.MaxValue, "DYNAMIC_VAR_LOADOUT_X_VALUE_ADDITIONAL_AMOUNT")
    ];

    public static XValueKeyword Instance { get; } = new();
    private XValueKeyword() { }
    public override CardKeyword Keyword => LoadoutKeywords.XValue;
    public override string StorageKey => LoadoutKeywords.XValueKey;
    public override string TitleLocKey => "LOADOUT-X_VALUE.title";
    public override LoadoutKeywordPresentation Presentation => LoadoutKeywordPresentation.DescriptionOnly;
    public override IReadOnlyList<LoadoutKeywordDynamicVarDefinition> DynamicVars => VariableDefinitions;
}

public static class XValueKeywordRuntime
{
    public sealed record Scope(CardModel Card, int Value, Scope? Parent);
    private static readonly AsyncLocal<Scope?> Current = new();
    private static readonly Harmony Harmony = new("Loadout.Keyword.XValue");
    private static readonly HashSet<MethodBase> Prepared = [];
    private static readonly AccessTools.FieldRef<DynamicVar, AbstractModel?> Owner =
        AccessTools.FieldRefAccess<DynamicVar, AbstractModel?>("_owner");

    public static void Prepare(CardModel card)
    {
        for (Type? type = card.GetType(); type is not null && type != typeof(CardModel); type = type.BaseType)
        {
            MethodInfo? method = AccessTools.DeclaredMethod(type, "OnPlay");
            if (method is null || method.IsAbstract || Prepared.Contains(method))
                continue;
            Harmony.Patch(method,
                prefix: new HarmonyMethod(typeof(XValueKeywordRuntime), nameof(EnterPrefix)) { priority = Priority.First },
                finalizer: new HarmonyMethod(typeof(XValueKeywordRuntime), nameof(ExitFinalizer)));
            Prepared.Add(method);
        }
    }

    public static void EnterPrefix(CardModel __instance, out Scope? __state)
    {
        __state = null;
        if (!LoadoutKeywords.Has(__instance, LoadoutKeywords.XValue))
            return;
        // Resolve modifiers before their own dynamic variables become X.
        int value = Math.Max(0, __instance.ResolveEnergyXValue());
        __state = new Scope(__instance, value, Current.Value);
        Current.Value = __state;
    }

    public static Exception? ExitFinalizer(Exception? __exception, Scope? __state)
    {
        if (__state is not null && ReferenceEquals(Current.Value, __state))
            Current.Value = __state.Parent;
        return __exception;
    }

    public static bool TryGetValue(CardModel card, out int value)
    {
        for (Scope? scope = Current.Value; scope is not null; scope = scope.Parent)
        {
            if (!ReferenceEquals(scope.Card, card))
                continue;
            value = scope.Value;
            return true;
        }
        value = 0;
        return false;
    }

    public static bool TryGetValue(DynamicVar variable, out int value)
    {
        value = 0;
        return variable.Name != XValueKeyword.AdditionalAmountVar
               && Current.Value is not null && Owner(variable) is CardModel card
               && TryGetValue(card, out value);
    }

    public static bool HasXValue(DynamicVar variable) =>
        variable.Name != XValueKeyword.AdditionalAmountVar
        && Owner(variable) is CardModel card && LoadoutKeywords.Has(card, LoadoutKeywords.XValue);

    public static int GetAdditionalAmount(CardModel card) =>
        LoadoutKeywordRegistry.TryGetValue(card, XValueKeyword.AdditionalAmountVar, out DynamicVar amount)
            ? amount.IntValue : 0;

    public static decimal GetInstanceValue(CardModel card) => TryGetValue(card, out int value) ? value : 1m;

    public static string FormatValue(DynamicVar variable)
    {
        string text = "X";
        if (Owner(variable) is CardModel card)
        {
            int additional = GetAdditionalAmount(card);
            if (additional != 0)
                text = $"X + {additional}";
            if ((variable is DamageVar && variable.Name == DamageVar.defaultName
                 && LoadoutKeywords.Has(card, LoadoutKeywords.MultiHit))
                || (variable is BlockVar && variable.Name == BlockVar.defaultName
                    && LoadoutKeywords.Has(card, LoadoutKeywords.MultiBlock)))
                text = additional == 0 ? "X x X" : $"({text}) x ({text})";
        }
        return LocManager.Instance?.Language is "zhs" or "zht" ? $" {text} " : text;
    }

    public static string GetEnergyPrefix(DynamicVar variable) =>
        variable is EnergyVar { ColorPrefix.Length: > 0 } energy ? energy.ColorPrefix
        : Owner(variable) is CardModel card ? EnergyIconHelper.GetPrefix(card) : "colorless";
}

// Install the small getter at startup, before card logic can inline it.
[HarmonyPatch(typeof(DynamicVar), nameof(DynamicVar.BaseValue), MethodType.Getter)]
public static class XValueBaseValuePatch
{
    [HarmonyPostfix]
    public static void Postfix(DynamicVar __instance, ref decimal __result)
    {
        if (XValueKeywordRuntime.TryGetValue(__instance, out int value))
            __result = value;
    }
}

[HarmonyPatch(typeof(CalculatedVar), nameof(CalculatedVar.Calculate))]
public static class XValueCalculatedVarPatch
{
    [HarmonyPrefix]
    public static bool Prefix(CalculatedVar __instance, ref decimal __result)
    {
        if (!XValueKeywordRuntime.TryGetValue(__instance, out int value))
            return true;
        __result = value;
        return false;
    }
}

[HarmonyPatch]
public static class XValueHighlightPatch
{
    public static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(DynamicVar), nameof(DynamicVar.ToHighlightedString));
        yield return AccessTools.DeclaredMethod(typeof(DynamicVar), nameof(DynamicVar.ToString), Type.EmptyTypes);
        yield return AccessTools.DeclaredMethod(typeof(DynamicVar), nameof(DynamicVar.ToString), [typeof(IFormatProvider)]);
        yield return AccessTools.DeclaredMethod(typeof(CalculatedVar), nameof(CalculatedVar.ToString), Type.EmptyTypes);
    }

    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    public static void Postfix(DynamicVar __instance, ref string __result)
    {
        if (XValueKeywordRuntime.HasXValue(__instance))
            __result = XValueKeywordRuntime.FormatValue(__instance);
    }
}

[HarmonyPatch(typeof(EnergyIconsFormatter), nameof(EnergyIconsFormatter.TryEvaluateFormat))]
public static class XValueEnergyIconsPatch
{
    [HarmonyPrefix]
    public static bool Prefix(IFormattingInfo formattingInfo, ref bool __result)
    {
        if (formattingInfo.CurrentValue is not DynamicVar variable
            || !XValueKeywordRuntime.HasXValue(variable))
            return true;
        string prefix = XValueKeywordRuntime.GetEnergyPrefix(variable);
        string value = XValueKeywordRuntime.FormatValue(variable).TrimEnd();
        formattingInfo.Write($"{value} [img]res://images/packed/sprite_fonts/{prefix}_energy_icon.png[/img]");
        __result = true;
        return false;
    }
}

[HarmonyPatch(typeof(CardModel), nameof(CardModel.ResolveEnergyXValue))]
public static class XValueResolvedXPatch
{
    [HarmonyPrefix]
    public static bool Prefix(CardModel __instance, ref int __result)
    {
        if (!XValueKeywordRuntime.TryGetValue(__instance, out int value))
            return true;
        __result = value;
        return false;
    }

    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    public static void Postfix(CardModel __instance, ref int __result)
    {
        if (XValueKeywordRuntime.TryGetValue(__instance, out int value))
            __result = value;
        else if (LoadoutKeywords.Has(__instance, LoadoutKeywords.XValue))
            __result = (int)Math.Clamp((long)__result + XValueKeywordRuntime.GetAdditionalAmount(__instance), 0, int.MaxValue);
    }
}
