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
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;

internal static class TurnEndInHandKeywordDispatcher
{
    public static IEnumerable<MethodBase> TargetMethods()
    {
        Type[] parameterTypes = [typeof(PlayerChoiceContext)];
        HashSet<MethodBase> targets = [];
        const BindingFlags flags = BindingFlags.Instance
                                   | BindingFlags.Public
                                   | BindingFlags.NonPublic
                                   | BindingFlags.DeclaredOnly;
        foreach (Type type in ModelDb.AllCards
                     .Select(card => card.GetType())
                     .Append(typeof(CardModel))
                     .Distinct())
        {
            MethodInfo? method = type.GetMethod(
                "OnTurnEndInHand",
                flags,
                binder: null,
                parameterTypes,
                modifiers: null);
            if (method is not null
                && !method.IsStatic
                && !method.IsAbstract
                && method.GetMethodBody() is not null
                && method.ReturnType == typeof(Task))
            {
                targets.Add(method);
            }
        }

        return targets;
    }

    [HarmonyPostfix]
    public static void Postfix(
        CardModel __instance,
        PlayerChoiceContext __0,
        ref Task __result)
    {
        if (!LoadoutKeywordRegistry.HasTurnEndInHandEffect(__instance))
            return;

        __result = Apply(__result, __instance, __0);
    }

    private static async Task Apply(
        Task originalEffect,
        CardModel card,
        PlayerChoiceContext choiceContext)
    {
        await originalEffect;

        foreach (LoadoutKeywordModel model in
                 LoadoutKeywordRegistry.WithTurnEndInHandEffect)
        {
            if (model.IsEnabled(card))
                await model.AfterTurnEndInHand(card, choiceContext);
        }
    }
}

internal static class RestrictiveHasTurnEndInHandEffectPatch
{
    public static IEnumerable<MethodBase> TargetMethods()
    {
        return LoadoutKeywordModel.GetCardPropertyGetters(
            nameof(CardModel.HasTurnEndInHandEffect));
    }

    [HarmonyPostfix]
    public static void Postfix(CardModel __instance, ref bool __result)
    {
        __result |= LoadoutKeywordRegistry.HasTurnEndInHandEffect(__instance);
    }
}

internal static class RestrictiveIsPlayablePatch
{
    public static IEnumerable<MethodBase> TargetMethods()
    {
        return LoadoutKeywordModel.GetCardPropertyGetters("IsPlayable");
    }

    [HarmonyPostfix]
    public static void Postfix(CardModel __instance, ref bool __result)
    {
        if (!__result)
            return;

        if (LoadoutKeywords.Has(__instance, LoadoutKeywords.Clash))
        {
            __result = PileType.Hand
                .GetPile(__instance.Owner)
                .Cards
                .All(card => card.Type == CardType.Attack);
        }

        if (__result
            && LoadoutKeywords.Has(__instance, LoadoutKeywords.Grand))
        {
            __result = GrandKeyword.CanPlay(__instance);
        }
    }
}

internal static class RestrictiveEnthralledShouldPlayPatch
{
    public static IEnumerable<MethodBase> TargetMethods()
    {
        Type[] parameterTypes = [typeof(CardModel), typeof(AutoPlayType)];
        HashSet<MethodBase> targets = [];
        const BindingFlags flags = BindingFlags.Instance
                                   | BindingFlags.Public
                                   | BindingFlags.NonPublic
                                   | BindingFlags.DeclaredOnly;
        foreach (Type type in ModelDb.AllCards
                     .Select(card => card.GetType())
                     .Append(typeof(AbstractModel))
                     .Distinct())
        {
            MethodInfo? method = type.GetMethod(
                nameof(AbstractModel.ShouldPlay),
                flags,
                binder: null,
                parameterTypes,
                modifiers: null);
            if (method is not null
                && !method.IsStatic
                && !method.IsAbstract
                && method.GetMethodBody() is not null
                && method.ReturnType == typeof(bool))
            {
                targets.Add(method);
            }
        }

        return targets;
    }

    [HarmonyPostfix]
    public static void Postfix(
        AbstractModel __instance,
        CardModel __0,
        AutoPlayType __1,
        ref bool __result)
    {
        if (__instance is not CardModel restrictor
            || restrictor.Owner != __0.Owner
            || restrictor.Pile?.Type != PileType.Hand
            || __1 != AutoPlayType.None)
        {
            return;
        }

        bool nativeRestrictor = restrictor is Enthralled;
        bool loadoutRestrictor = LoadoutKeywords.Has(
            restrictor,
            LoadoutKeywords.Enthralled);
        if (!nativeRestrictor && !loadoutRestrictor)
            return;

        bool targetIsEnthralled = __0 is Enthralled
                                  || LoadoutKeywords.Has(
                                      __0,
                                      LoadoutKeywords.Enthralled);
        if (nativeRestrictor && targetIsEnthralled)
        {
            __result = true;
            return;
        }

        if (__result && loadoutRestrictor && !targetIsEnthralled)
            __result = false;
    }
}

internal static class RestrictiveKeywordGlowPatch
{
    [HarmonyPostfix]
    public static void ShouldGlowGoldPostfix(
        CardModel __instance,
        ref bool __result)
    {
        if (__result
            || !LoadoutKeywords.Has(__instance, LoadoutKeywords.Clash)
            && !LoadoutKeywords.Has(__instance, LoadoutKeywords.Grand))
        {
            return;
        }

        if (LoadoutKeywords.Has(__instance, LoadoutKeywords.Clash))
        {
            __result = PileType.Hand
                .GetPile(__instance.Owner)
                .Cards
                .All(card => card.Type == CardType.Attack);
        }

        if (!__result
            && LoadoutKeywords.Has(__instance, LoadoutKeywords.Grand))
        {
            __result = GrandKeyword.CanPlay(__instance);
        }
    }

    [HarmonyPostfix]
    public static void ShouldGlowRedPostfix(
        CardModel __instance,
        ref bool __result)
    {
        __result |= LoadoutKeywords.Has(
            __instance,
            LoadoutKeywords.Enthralled);
    }
}
