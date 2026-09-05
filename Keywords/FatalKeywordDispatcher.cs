#nullable enable

namespace Loadout.Keywords;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using Loadout.Services.Compatibility;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Commands.Builders;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;

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
        GetPossibleTargets = AccessTools.MethodDelegate<
            Func<AttackCommand, IReadOnlyList<Creature>>>(
            GetPossibleTargetsMethod);

    private static readonly HashSet<Type> CreatureDamageFatalCardTypes =
        [typeof(EchoingSlash)];

    internal sealed record FatalAttackState(
        CardModel Source, PlayerChoiceContext ChoiceContext,
        HashSet<Creature> EligibleTargets,
        IReadOnlyDictionary<Creature, FatalTargetSnapshot>? TargetSnapshots);

    internal static bool UsesCreatureDamageFatal(CardModel card) =>
        CreatureDamageFatalCardTypes.Contains(card.GetType());

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
            || UsesCreatureDamageFatal(source))
        {
            return;
        }

        __state = Capture(source, choiceContext, GetPossibleTargets(__instance));
    }

    internal static FatalAttackState? Capture(
        CardModel source,
        PlayerChoiceContext choiceContext,
        IEnumerable<Creature> possibleTargets)
    {
        if (!LoadoutKeywordRegistry.HasFatalEffect(source))
            return null;

        HashSet<Creature>? eligibleTargets = null;
        Dictionary<Creature, FatalTargetSnapshot>? targetSnapshots = null;
        bool captureTargetSnapshots =
            LoadoutKeywordRegistry.RequiresFatalTargetSnapshots(source);
        foreach (Creature target in possibleTargets)
        {
            if (!IsFatalEligible(target))
                continue;

            (eligibleTargets ??= []).Add(target);
            if (captureTargetSnapshots)
                (targetSnapshots ??= [])[target] = CaptureTarget(target);
        }

        return eligibleTargets is null ? null : new FatalAttackState(
            source, choiceContext, eligibleTargets, targetSnapshots);
    }

    private static FatalTargetSnapshot CaptureTarget(Creature target)
    {
        List<FatalPowerSnapshot> powers = [];
        foreach (PowerModel power in target.Powers)
        {
            PowerType type = power.TypeForCurrentAmount;
            if (type is not (PowerType.Buff or PowerType.Debuff))
                continue;

            powers.Add(new FatalPowerSnapshot(
                (PowerModel)power.ClonePreservingMutability(),
                type));
        }

        return new FatalTargetSnapshot(target.MaxHp, powers);
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
        Task<AttackCommand> original, FatalAttackState state)
    {
        AttackCommand command = await original;
        await ApplyFatal(command.Results.SelectMany(results => results), state);
        return command;
    }

    internal static async Task<IEnumerable<DamageResult>> ResolveCreatureDamageFatal(
        Task<IEnumerable<DamageResult>> original, FatalAttackState state)
    {
        IEnumerable<DamageResult> results = await original;
        await ApplyFatal(results, state);
        return results;
    }

    private static async Task ApplyFatal(
        IEnumerable<DamageResult> results, FatalAttackState state)
    {
        int fatalCount = 0;
        List<FatalTargetSnapshot>? fatalTargets = null;
        foreach (DamageResult result in results)
        {
            if (!result.WasTargetKilled
                || !state.EligibleTargets.Contains(result.Receiver))
            {
                continue;
            }

            fatalCount++;
            if (state.TargetSnapshots?.TryGetValue(
                    result.Receiver,
                    out FatalTargetSnapshot? snapshot) == true)
            {
                (fatalTargets ??= []).Add(snapshot);
            }
        }

        await LoadoutKeywordRegistry.ApplyFatalEffects(
            state.Source,
            state.ChoiceContext,
            new FatalKeywordContext(fatalCount, fatalTargets ?? []));
    }
}

[HarmonyPatch]
internal static class CreatureDamageFatalPatch
{
    private static MethodBase TargetMethod() => Sts2Compatibility.MultiTargetDamageMethod;

    [HarmonyPrefix]
    private static void Prefix(
        PlayerChoiceContext __0,
        IEnumerable<Creature> __1,
        CardModel? __5,
        out FatalKeywordAttackPatch.FatalAttackState? __state)
    {
        __state = __5 is CardModel source &&
                  FatalKeywordAttackPatch.UsesCreatureDamageFatal(source)
            ? FatalKeywordAttackPatch.Capture(source, __0, __1)
            : null;
    }

    [HarmonyPostfix]
    private static void Postfix(
        FatalKeywordAttackPatch.FatalAttackState? __state,
        ref Task<IEnumerable<DamageResult>> __result)
    {
        if (__state is not null)
            __result = FatalKeywordAttackPatch.ResolveCreatureDamageFatal(__result, __state);
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
