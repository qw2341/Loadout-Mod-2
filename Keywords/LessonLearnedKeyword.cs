#nullable enable

namespace Loadout.Keywords;

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using Loadout.Services.Compatibility;
using MegaCrit.Sts2.Core.Audio.Debug;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Commands.Builders;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.CommonUi;

public sealed class LessonLearnedKeyword : LoadoutKeywordModel
{
    public const string CardsVar = "LoadoutLessonLearnedCards";

    private static readonly IReadOnlyList<LoadoutKeywordDynamicVarDefinition>
        VariableDefinitions =
        [
            new(
                CardsVar,
                1m,
                0,
                int.MaxValue,
                "DYNAMIC_VAR_LOADOUT_LESSON_LEARNED_CARDS")
        ];

    public static LessonLearnedKeyword Instance { get; } = new();

    private LessonLearnedKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.LessonLearned;

    public override string StorageKey => LoadoutKeywords.LessonLearnedKey;

    public override string TitleLocKey => "LOADOUT-LESSON_LEARNED.title";

    public override LoadoutKeywordPresentation Presentation =>
        LoadoutKeywordPresentation.DescriptionOnly;

    public override string? CardTextLocKey => "LOADOUT-LESSON_LEARNED.cardText";

    public override IReadOnlyList<LoadoutKeywordDynamicVarDefinition> DynamicVars =>
        VariableDefinitions;

    internal static void Apply(CardModel source, int fatalCount)
    {
        if (fatalCount <= 0
            || !LoadoutKeywordRegistry.TryGetValue(
                source,
                CardsVar,
                out DynamicVar countVar))
        {
            return;
        }

        long upgradeCount =
            (long)Math.Max(0, countVar.IntValue) * fatalCount;
        if (upgradeCount <= 0)
            return;

        IReadOnlyList<CardModel> deckCards =
            PileType.Deck.GetPile(source.Owner).Cards;
        List<CardModel> candidates = new(deckCards.Count);
        foreach (CardModel card in deckCards)
        {
            if (card.IsUpgradable)
                candidates.Add(card);
        }

        if (candidates.Count == 0)
            return;

        LessonLearnedCombatEndGuard.Enter();
        try
        {
            for (long upgradeIndex = 0;
                 upgradeIndex < upgradeCount;
                 upgradeIndex++)
            {
                if (candidates.Count == 0)
                    break;

                int selectedIndex = source.Owner.RunState.Rng
                    .CombatCardSelection
                    .NextInt(0, candidates.Count);
                CardModel selected = candidates[selectedIndex];
                int previousUpgradeLevel = selected.CurrentUpgradeLevel;
                CardCmd.Upgrade(
                    selected,
                    CardPreviewStyle.HorizontalLayout);
                if (!selected.IsUpgradable)
                    candidates.RemoveAt(selectedIndex);

                if (selected.CurrentUpgradeLevel <= previousUpgradeLevel
                    || !LocalContext.IsMine(selected))
                {
                    continue;
                }

                // CardCmd.Upgrade supplies the native NCardUpgradeVfx. The
                // card-smith sound is presentation-only, so only the owning
                // local peer plays it.
                NDebugAudioManager.Instance?.Play(
                    TmpSfx.cardSmith,
                    1f,
                    PitchVariance.Small);
            }
        }
        finally
        {
            LessonLearnedCombatEndGuard.Exit();
        }
    }
}

[HarmonyPatch(
    typeof(AttackCommand),
    nameof(AttackCommand.Execute),
    typeof(PlayerChoiceContext))]
internal static class LessonLearnedAttackFatalPatch
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
        HashSet<Creature> EligibleTargets);

    [HarmonyPrefix]
    private static void Prefix(
        AttackCommand __instance,
        out FatalAttackState? __state)
    {
        __state = null;

        // Every AttackCommand produced by a card now uses one Feed-style
        // result path. This avoids relying on dynamic patches over every
        // concrete CardModel.OnPlay implementation for single-target cards.
        if ((!__instance.IsSingleTargeted && !__instance.IsMultiTargeted)
            || __instance.ModelSource is not CardModel source
            || !Sts2Compatibility.MatchesAttackCardPlay(__instance, source)
            || !LoadoutKeywords.Has(source, LoadoutKeywords.LessonLearned))
        {
            return;
        }

        IReadOnlyList<Creature> possibleTargets =
            GetPossibleTargets(__instance);

        HashSet<Creature>? eligibleTargets = null;
        foreach (Creature target in possibleTargets)
        {
            if (!IsFatalEligible(target))
                continue;

            (eligibleTargets ??= []).Add(target);
        }

        if (eligibleTargets is null)
            return;

        __state = new FatalAttackState(
            source,
            eligibleTargets);
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

        LessonLearnedKeyword.Apply(state.Source, fatalCount);
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
