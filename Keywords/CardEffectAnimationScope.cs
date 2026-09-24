#nullable enable

namespace Loadout.Keywords;

using System;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Commands.Builders;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Settings;

internal static class CardEffectAnimationScope
{
    public const int DefaultFastAnimationMinimumResult = 8;
    public const int DefaultVeryFastAnimationMinimumResult = 16;
    public const int DefaultVeryVeryFastAnimationMinimumResult = 32;
    public const int DefaultExtremelyFastAnimationMinimumResult = 50;
    public const int DefaultAlmostInstantAnimationMinimumResult = 1000;
    public const int DefaultInstantAnimationMinimumResult = 10000;
    public static int FastAnimationMinimumResult { get; set; } = DefaultFastAnimationMinimumResult;
    public static int VeryFastAnimationMinimumResult { get; set; } = DefaultVeryFastAnimationMinimumResult;
    public static int VeryVeryFastAnimationMinimumResult { get; set; } = DefaultVeryVeryFastAnimationMinimumResult;
    public static int ExtremelyFastAnimationMinimumResult { get; set; } = DefaultExtremelyFastAnimationMinimumResult;
    public static int AlmostInstantAnimationMinimumResult { get; set; } = DefaultAlmostInstantAnimationMinimumResult;
    public static int InstantAnimationMinimumResult { get; set; } = DefaultInstantAnimationMinimumResult;
    public const float FastWaitMultiplier = 0.8f;
    public const float VeryFastWaitMultiplier = 0.5f;
    public const float VeryVeryFastWaitMultiplier = 0.25f;
    public const float ExtremelyFastWaitMultiplier = 0.01f;
    public const float AlmostInstantWaitMultiplier = 0.001f;

    internal enum AnimationSpeed
    {
        Normal,
        Faster,
        VeryFast,
        VeryVeryFast,
        ExtremelyFast,
        AlmostInstant,
        Instant
    }

    internal sealed class Scope(
        object source,
        Scope? previous)
    {
        public object Source { get; } = source;

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
            && !LoadoutKeywords.Has(card, LoadoutKeywords.AltXValue)
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
        => MarkRepeatedHits(card, result);

    public static Scope EnterAttack(AttackCommand attack)
    {
        Scope scope = new(attack, Current.Value);
        Current.Value = scope;
        return scope;
    }

    public static void MarkRepeatedHits(object source, int result)
    {
        AnimationSpeed speed = GetSpeed(result);
        if (speed == AnimationSpeed.Normal)
            return;

        Scope? scope = Find(source);
        if (scope is null)
            return;

        Promote(scope, speed);
    }

    public static void MarkRepeatedCardPlays(
        CardModel card,
        int additionalPlayCount)
    {
        if (additionalPlayCount <= 0)
            return;

        Scope? scope = Find(card);
        if (scope is null)
            return;

        scope.SuppressMultiCardPlay = true;
        Promote(scope, GetSpeed(additionalPlayCount));
    }

    private static AnimationSpeed GetSpeed(int count) => count switch
    {
        _ when count >= InstantAnimationMinimumResult => AnimationSpeed.Instant,
        _ when count >= AlmostInstantAnimationMinimumResult => AnimationSpeed.AlmostInstant,
        _ when count >= ExtremelyFastAnimationMinimumResult => AnimationSpeed.ExtremelyFast,
        _ when count >= VeryVeryFastAnimationMinimumResult => AnimationSpeed.VeryVeryFast,
        _ when count >= VeryFastAnimationMinimumResult => AnimationSpeed.VeryFast,
        _ when count >= FastAnimationMinimumResult => AnimationSpeed.Faster,
        _ => AnimationSpeed.Normal
    };

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

    private static Scope? Find(object source)
    {
        for (Scope? scope = Current.Value;
             scope is not null;
             scope = scope.Previous)
        {
            if (ReferenceEquals(scope.Source, source))
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
            case CardEffectAnimationScope.AnimationSpeed.VeryFast:
            case CardEffectAnimationScope.AnimationSpeed.VeryVeryFast:
            case CardEffectAnimationScope.AnimationSpeed.ExtremelyFast:
            case CardEffectAnimationScope.AnimationSpeed.AlmostInstant:
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
internal static class CardEffectAcceleratedWaitPatch
{
    [HarmonyPrefix]
    private static void Prefix(ref float seconds)
    {
        switch (CardEffectAnimationScope.CurrentSpeed)
        {
            case CardEffectAnimationScope.AnimationSpeed.Faster:
                seconds *= CardEffectAnimationScope.FastWaitMultiplier;
                break;
            case CardEffectAnimationScope.AnimationSpeed.VeryFast:
                seconds *= CardEffectAnimationScope.VeryFastWaitMultiplier;
                break;
            case CardEffectAnimationScope.AnimationSpeed.VeryVeryFast:
                seconds *= CardEffectAnimationScope.VeryVeryFastWaitMultiplier;
                break;
            case CardEffectAnimationScope.AnimationSpeed.ExtremelyFast:
                seconds *= CardEffectAnimationScope.ExtremelyFastWaitMultiplier;
                break;
            case CardEffectAnimationScope.AnimationSpeed.AlmostInstant:
                seconds *= CardEffectAnimationScope.AlmostInstantWaitMultiplier;
                break;
        }
    }
}
