#nullable enable

namespace Loadout.Keywords;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;

public sealed class BlankSlateKeyword : LoadoutKeywordModel
{
    public static BlankSlateKeyword Instance { get; } = new();

    private BlankSlateKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.BlankSlate;

    public override string StorageKey => LoadoutKeywords.BlankSlateKey;

    public override string TitleLocKey => "LOADOUT-BLANK_SLATE.title";

    public override LoadoutKeywordPresentation Presentation =>
        LoadoutKeywordPresentation.DescriptionOnly;

    public override LoadoutKeywordEditorSection EditorSection =>
        LoadoutKeywordEditorSection.Basic;

    public override bool TransformsBaseDescription => true;

    public override int BaseDescriptionPriority => int.MinValue;

    public override bool SuppressesOriginalOnPlay => true;

    public override bool SuppressesOriginalIsPlayable => true;

    public override bool SuppressesOriginalShouldGlowGold => true;

    public override bool SuppressesOriginalModelHooks => true;

    public override string TransformBaseDescription(
        CardModel card,
        string description)
    {
        return string.Empty;
    }
}

internal static class BlankSlateModelHookState
{
    private static readonly SuppressionMarker Marker = new();
    private static ConditionalWeakTable<CardModel, SuppressionMarker> Cards = new();

    public static bool IsSuppressed(CardModel card)
    {
        return Cards.TryGetValue(card, out _);
    }

    public static void Set(CardModel card, bool suppresses)
    {
        Cards.Remove(card);
        if (suppresses)
            Cards.Add(card, Marker);
    }

    public static void Reset()
    {
        Cards = new ConditionalWeakTable<CardModel, SuppressionMarker>();
    }

    public static void MutableClonePostfix(AbstractModel __result)
    {
        if (__result is CardModel card)
        {
            LoadoutKeywordRegistry
                .SynchronizeOriginalModelHookSuppression(card);
        }
    }

    private sealed class SuppressionMarker
    {
    }
}

internal static class BlankSlateModelHookPatch
{
    // Patch each concrete card override once instead of filtering the global
    // hook listener list. Blank Slate can then fall through to AbstractModel's
    // neutral implementation while enchantments, afflictions, and Loadout
    // postfix layers remain independent listeners/effects.
    private static readonly MethodInfo SuppressionCheck = AccessTools.Method(
        typeof(LoadoutKeywordRegistry),
        nameof(LoadoutKeywordRegistry.SuppressesOriginalModelHooks))
        ?? throw new MissingMethodException(
            typeof(LoadoutKeywordRegistry).FullName,
            nameof(LoadoutKeywordRegistry.SuppressesOriginalModelHooks));

    private static readonly object PrefixLock = new();
    private static readonly Dictionary<MethodBase, DynamicMethod> Prefixes = [];

    public static IEnumerable<MethodBase> TargetMethods()
    {
        HashSet<MethodInfo> targets = [];
        const BindingFlags flags = BindingFlags.Instance
                                   | BindingFlags.Public
                                   | BindingFlags.DeclaredOnly;
        foreach (System.Type cardType in ModelDb.AllCards
                     .Select(card => card.GetType())
                     .Distinct())
        {
            for (System.Type? declaringType = cardType;
                 declaringType is not null
                 && declaringType != typeof(CardModel)
                 && declaringType != typeof(AbstractModel);
                 declaringType = declaringType.BaseType)
            {
                foreach (MethodInfo method in declaringType.GetMethods(flags))
                {
                    MethodInfo baseMethod = method.GetBaseDefinition();
                    if (baseMethod.DeclaringType != typeof(AbstractModel)
                        || baseMethod.IsAbstract
                        || method.IsStatic
                        || method.IsAbstract
                        || method.IsSpecialName
                        || method.ContainsGenericParameters
                        || method.GetMethodBody() is null
                        || !IsHookMethod(method))
                    {
                        continue;
                    }

                    targets.Add(method);
                }
            }
        }

        return targets;
    }

    // Harmony does not accept a DynamicMethod as the registered patch method.
    // Register this normal static factory instead; Harmony invokes it once for
    // each original and uses the returned generated prefix.
    public static MethodInfo PrefixFactory(MethodBase originalMethod)
    {
        if (originalMethod is not MethodInfo target)
        {
            throw new ArgumentException(
                "Blank Slate hook target must be a method.",
                nameof(originalMethod));
        }

        lock (PrefixLock)
        {
            if (!Prefixes.TryGetValue(target, out DynamicMethod? prefix))
            {
                prefix = CreatePrefix(target);
                Prefixes.Add(target, prefix);
            }

            return prefix;
        }
    }

    public static void ClearPrefixes()
    {
        lock (PrefixLock)
            Prefixes.Clear();
    }

    private static bool IsHookMethod(MethodInfo method)
    {
        return method.Name.StartsWith("After", System.StringComparison.Ordinal)
               || method.Name.StartsWith("Before", System.StringComparison.Ordinal)
               || method.Name.StartsWith("Modify", System.StringComparison.Ordinal)
               || method.Name.StartsWith("TryModify", System.StringComparison.Ordinal)
               || method.Name.StartsWith("Should", System.StringComparison.Ordinal);
    }

    private static DynamicMethod CreatePrefix(MethodInfo target)
    {
        MethodInfo baseMethod = target.GetBaseDefinition();
        ParameterInfo[] targetParameters = target.GetParameters();
        bool hasResult = target.ReturnType != typeof(void);
        int originalArgumentOffset = hasResult ? 2 : 1;
        System.Type[] prefixParameterTypes = new System.Type[
            targetParameters.Length + originalArgumentOffset];
        prefixParameterTypes[0] = typeof(CardModel);
        if (hasResult)
            prefixParameterTypes[1] = target.ReturnType.MakeByRefType();
        for (int index = 0; index < targetParameters.Length; index++)
        {
            prefixParameterTypes[index + originalArgumentOffset] =
                targetParameters[index].ParameterType;
        }

        DynamicMethod prefix = new(
            $"Loadout_BlankSlate_{target.DeclaringType?.Name}_{target.Name}",
            typeof(bool),
            prefixParameterTypes,
            typeof(BlankSlateModelHookPatch).Module,
            skipVisibility: true);
        prefix.DefineParameter(1, ParameterAttributes.None, "__instance");
        if (hasResult)
            prefix.DefineParameter(2, ParameterAttributes.None, "__result");
        for (int index = 0; index < targetParameters.Length; index++)
        {
            prefix.DefineParameter(
                index + originalArgumentOffset + 1,
                ParameterAttributes.None,
                $"__{index}");
        }

        ILGenerator il = prefix.GetILGenerator();
        Label runOriginal = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, SuppressionCheck);
        il.Emit(OpCodes.Brfalse, runOriginal);

        if (hasResult)
            EmitLoadArgument(il, 1);
        il.Emit(OpCodes.Ldarg_0);
        for (int index = 0; index < targetParameters.Length; index++)
            EmitLoadArgument(il, index + originalArgumentOffset);
        il.Emit(OpCodes.Call, baseMethod);
        if (hasResult)
        {
            if (target.ReturnType.IsValueType)
                il.Emit(OpCodes.Stobj, target.ReturnType);
            else
                il.Emit(OpCodes.Stind_Ref);
        }
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(runOriginal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ret);
        return prefix;
    }

    private static void EmitLoadArgument(ILGenerator il, int index)
    {
        switch (index)
        {
            case 0:
                il.Emit(OpCodes.Ldarg_0);
                break;
            case 1:
                il.Emit(OpCodes.Ldarg_1);
                break;
            case 2:
                il.Emit(OpCodes.Ldarg_2);
                break;
            case 3:
                il.Emit(OpCodes.Ldarg_3);
                break;
            default:
                il.Emit(OpCodes.Ldarg, index);
                break;
        }
    }
}

internal static class BlankSlateCardLogicPatch
{
    public static IEnumerable<MethodBase> ShouldGlowGoldTargets()
    {
        return LoadoutKeywordModel.GetCardPropertyGetters(
            "ShouldGlowGoldInternal");
    }

    [HarmonyPrefix]
    public static bool IsPlayablePrefix(
        CardModel __instance,
        ref bool __result)
    {
        if (!LoadoutKeywordRegistry.SuppressesOriginalIsPlayable(__instance))
            return true;

        __result = true;
        return false;
    }

    [HarmonyPrefix]
    public static bool ShouldGlowGoldPrefix(
        CardModel __instance,
        ref bool __result)
    {
        if (!LoadoutKeywordRegistry.SuppressesOriginalShouldGlowGold(__instance))
            return true;

        __result = false;
        return false;
    }
}
