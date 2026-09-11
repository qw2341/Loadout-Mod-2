#nullable enable

namespace Loadout.Keywords;

using System;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Settings;

internal static class CardEffectAnimationScope
{
    internal enum AnimationSpeed
    {
        Normal,
        Faster,
        ExtremelyFast,
        Instant
    }

    internal sealed class Scope(
        CardModel card,
        Scope? previous)
    {
        public CardModel Card { get; } = card;

        public Scope? Previous { get; } = previous;

        public bool SuppressMultiCardPlay { get; set; }

        public AnimationSpeed Speed { get; set; }
    }

    private static readonly AsyncLocal<Scope?> Current = new();

    public static AnimationSpeed CurrentSpeed
    {
        get
        {
            AnimationSpeed speed = AnimationSpeed.Normal;
            for (Scope? scope = Current.Value;
                 scope is not null;
                 scope = scope.Previous)
            {
                if (scope.Speed > speed)
                    speed = scope.Speed;
            }

            return speed;
        }
    }

    public static Scope? Enter(CardModel card)
    {
        if (!LoadoutKeywords.Has(card, LoadoutKeywords.AltHeavenly)
            && !LoadoutKeywords.Has(card, LoadoutKeywords.MultiHit)
            && !LoadoutKeywords.Has(card, LoadoutKeywords.MultiBlock)
            && !LoadoutKeywords.Has(card, LoadoutKeywords.Gatling)
            && !LoadoutKeywords.Has(card, LoadoutKeywords.MegaGatling)
            && !LoadoutKeywords.Has(card, LoadoutKeywords.ReplayX))
        {
            return null;
        }

        Scope scope = new(card, Current.Value);
        Current.Value = scope;
        return scope;
    }

    public static void MarkAltHeavenlyResult(CardModel card, int result)
    {
        if (result < AltHeavenlyKeyword.FastAnimationMinimumResult)
            return;

        Scope? scope = Find(card);
        if (scope is null)
            return;

        AnimationSpeed speed = result switch
        {
            >= AltHeavenlyKeyword.InstantAnimationMinimumResult =>
                AnimationSpeed.Instant,
            >= AltHeavenlyKeyword.ExtremelyFastAnimationMinimumResult =>
                AnimationSpeed.ExtremelyFast,
            _ => AnimationSpeed.Faster
        };
        Promote(scope, speed);
    }

    public static void MarkGatlingReplay(
        CardModel card,
        int additionalPlayCount)
    {
        MarkRepeatedCardPlays(
            card,
            additionalPlayCount,
            GatlingKeyword.FastAnimationAdditionalPlayThreshold,
            GatlingKeyword.ExtremelyFastAnimationAdditionalPlayThreshold,
            GatlingKeyword.InstantAnimationAdditionalPlayThreshold);
    }

    public static void MarkReplayXReplay(
        CardModel card,
        int additionalPlayCount)
    {
        MarkRepeatedCardPlays(
            card,
            additionalPlayCount,
            ReplayXKeyword.FastAnimationAdditionalPlayThreshold,
            ReplayXKeyword.ExtremelyFastAnimationAdditionalPlayThreshold,
            ReplayXKeyword.InstantAnimationAdditionalPlayThreshold);
    }

    public static void MarkMegaGatlingReplay(
        CardModel card,
        int additionalPlayCount)
    {
        MarkRepeatedCardPlays(
            card,
            additionalPlayCount,
            GatlingKeyword.FastAnimationAdditionalPlayThreshold,
            GatlingKeyword.ExtremelyFastAnimationAdditionalPlayThreshold,
            GatlingKeyword.InstantAnimationAdditionalPlayThreshold);
    }

    private static void MarkRepeatedCardPlays(
        CardModel card,
        int additionalPlayCount,
        int fastAnimationThreshold,
        int extremelyFastAnimationThreshold,
        int instantAnimationThreshold)
    {
        if (additionalPlayCount <= 0)
            return;

        Scope? scope = Find(card);
        if (scope is null)
            return;

        scope.SuppressMultiCardPlay = true;
        AnimationSpeed speed = additionalPlayCount switch
        {
            _ when additionalPlayCount > instantAnimationThreshold =>
                AnimationSpeed.Instant,
            _ when additionalPlayCount > extremelyFastAnimationThreshold =>
                AnimationSpeed.ExtremelyFast,
            _ when additionalPlayCount > fastAnimationThreshold =>
                AnimationSpeed.Faster,
            _ => AnimationSpeed.Normal
        };
        Promote(scope, speed);
    }

    private static void Promote(Scope scope, AnimationSpeed speed)
    {
        if (speed > scope.Speed)
            scope.Speed = speed;
    }

    public static bool ShouldSuppressMultiCardPlay(CardModel? card)
    {
        return card is not null
               && Find(card)?.SuppressMultiCardPlay == true;
    }

    public static void Exit(Scope? scope)
    {
        if (scope is not null && ReferenceEquals(Current.Value, scope))
            Current.Value = scope.Previous;
    }

    private static Scope? Find(CardModel card)
    {
        for (Scope? scope = Current.Value;
             scope is not null;
             scope = scope.Previous)
        {
            if (ReferenceEquals(scope.Card, card))
                return scope;
        }

        return null;
    }
}

[HarmonyPatch(typeof(CardModel), nameof(CardModel.OnPlayWrapper))]
internal static class CardEffectAnimationScopePatch
{
    [HarmonyPrefix]
    private static void Prefix(
        CardModel __instance,
        out CardEffectAnimationScope.Scope? __state)
    {
        __state = CardEffectAnimationScope.Enter(__instance);
    }

    [HarmonyFinalizer]
    private static Exception? Finalizer(
        Exception? __exception,
        CardEffectAnimationScope.Scope? __state)
    {
        CardEffectAnimationScope.Exit(__state);
        return __exception;
    }
}

[HarmonyPatch(typeof(NCard), nameof(NCard.AnimMultiCardPlay))]
internal static class RepeatedCardMultiPlayAnimationPatch
{
    [HarmonyPrefix]
    private static bool Prefix(NCard __instance, ref Task __result)
    {
        if (!CardEffectAnimationScope.ShouldSuppressMultiCardPlay(
                __instance.Model))
        {
            return true;
        }

        __result = Task.CompletedTask;
        return false;
    }
}

[HarmonyPatch(typeof(PrefsSave), nameof(PrefsSave.FastMode), MethodType.Getter)]
internal static class CardEffectFastModePatch
{
    [HarmonyPostfix]
    private static void Postfix(ref FastModeType __result)
    {
        switch (CardEffectAnimationScope.CurrentSpeed)
        {
            case CardEffectAnimationScope.AnimationSpeed.Faster:
            case CardEffectAnimationScope.AnimationSpeed.ExtremelyFast:
                if (__result < FastModeType.Fast)
                    __result = FastModeType.Fast;
                break;
            case CardEffectAnimationScope.AnimationSpeed.Instant:
                __result = FastModeType.Instant;
                break;
        }
    }
}

[HarmonyPatch(
    typeof(Cmd),
    nameof(Cmd.Wait),
    typeof(float),
    typeof(CancellationToken),
    typeof(bool))]
internal static class CardEffectExtremelyFastWaitPatch
{
    private const float WaitMultiplier = 0.01f;

    [HarmonyPrefix]
    private static void Prefix(ref float seconds)
    {
        if (CardEffectAnimationScope.CurrentSpeed
            == CardEffectAnimationScope.AnimationSpeed.ExtremelyFast)
        {
            seconds *= WaitMultiplier;
        }
    }
}
