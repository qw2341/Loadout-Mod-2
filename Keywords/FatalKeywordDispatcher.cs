#nullable enable

namespace Loadout.Keywords;

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using Loadout.Services.Compatibility;
using MegaCrit.Sts2.Core.Commands.Builders;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;

[HarmonyPatch(
    typeof(AttackCommand),
    nameof(AttackCommand.Execute),
    typeof(PlayerChoiceContext))]
internal static class FatalKeywordAttackPatch
{
    private static readonly MethodInfo GetPossibleTargetsMethod =
        AccessTools.DeclaredMethod(typeof(AttackCommand), "GetPossibleTargets")
        ?? throw new MissingMethodException(
            typeof(AttackCommand).FullName,
            "GetPossibleTargets()");

    private static readonly Func<AttackCommand, IReadOnlyList<Creature>>
        GetPossibleTargets =
            AccessTools.MethodDelegate<
                Func<AttackCommand, IReadOnlyList<Creature>>>(
                GetPossibleTargetsMethod);

    private sealed record FatalAttackState(
        CardModel Source,
        PlayerChoiceContext ChoiceContext,
        HashSet<Creature> EligibleTargets);

    [HarmonyPrefix]
    private static void Prefix(
        AttackCommand __instance,
        PlayerChoiceContext choiceContext,
        out FatalAttackState? __state)
    {
        __state = null;
        if ((!__instance.IsSingleTargeted && !__instance.IsMultiTargeted)
            || __instance.ModelSource is not CardModel source
            || !Sts2Compatibility.MatchesAttackCardPlay(__instance, source)
            || !LoadoutKeywordRegistry.HasFatalEffect(source))
        {
            return;
        }

        IReadOnlyList<Creature> possibleTargets = GetPossibleTargets(__instance);
        HashSet<Creature>? eligibleTargets = null;
        foreach (Creature target in possibleTargets)
        {
            if (!IsFatalEligible(target))
                continue;

            (eligibleTargets ??= []).Add(target);
        }

        if (eligibleTargets is not null)
        {
            __state = new FatalAttackState(
                source,
                choiceContext,
                eligibleTargets);
        }
    }

    private static bool IsFatalEligible(Creature target)
    {
        if (target.IsDead)
            return false;

        foreach (PowerModel power in target.Powers)
        {
            if (!power.ShouldOwnerDeathTriggerFatal())
                return false;
        }

        return true;
    }

    [HarmonyPostfix]
    private static void Postfix(
        FatalAttackState? __state,
        ref Task<AttackCommand> __result)
    {
        if (__state is not null)
            __result = ResolveFatal(__result, __state);
    }

    private static async Task<AttackCommand> ResolveFatal(
        Task<AttackCommand> original,
        FatalAttackState state)
    {
        AttackCommand command = await original;
        int fatalCount = 0;
        foreach (List<DamageResult> hitResults in command.Results)
        {
            foreach (DamageResult result in hitResults)
            {
                if (result.WasTargetKilled
                    && state.EligibleTargets.Contains(result.Receiver))
                {
                    fatalCount++;
                }
            }
        }

        await LoadoutKeywordRegistry.ApplyFatalEffects(
            state.Source,
            state.ChoiceContext,
            fatalCount);
        return command;
    }
}

internal static class LessonLearnedCombatEndGuard
{
    [ThreadStatic]
    private static int _depth;

    internal static bool IsActive => _depth > 0;

    internal static void Enter()
    {
        _depth++;
    }

    internal static void Exit()
    {
        if (_depth > 0)
            _depth--;
    }
}

[HarmonyPatch(
    typeof(Hook),
    nameof(Hook.ShouldStopCombatFromEnding),
    typeof(ICombatState))]
internal static class LessonLearnedCombatEndGuardPatch
{
    [HarmonyPostfix]
    private static void Postfix(ref bool __result)
    {
        if (LessonLearnedCombatEndGuard.IsActive)
            __result = true;
    }
}
