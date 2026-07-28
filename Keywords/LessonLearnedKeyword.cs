#nullable enable

namespace Loadout.Keywords;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Audio.Debug;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.CommonUi;

public sealed class LessonLearnedKeyword : LoadoutDescriptionKeywordModel
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

    public override string CardTextLocKey => "LOADOUT-LESSON_LEARNED.cardText";

    public override IReadOnlyList<LoadoutKeywordDynamicVarDefinition> DynamicVars =>
        VariableDefinitions;

    public override bool HasOnPlayEffect => true;

    public override object? CaptureBeforeOnPlay(
        CardModel card,
        CardPlay cardPlay)
    {
        Creature? target = cardPlay.Target;
        if (target is null
            || target.IsDead
            || target.Powers.Any(power => !power.ShouldOwnerDeathTriggerFatal()))
        {
            return null;
        }

        // This is the same pre-effect eligibility snapshot Feed takes at the
        // start of its OnPlay body.
        return target;
    }

    public override Task AfterOnPlay(
        CardModel card,
        CardPlay cardPlay,
        object? capturedState)
    {
        if (capturedState is Creature { IsDead: true })
            Apply(card);

        return Task.CompletedTask;
    }

    private static void Apply(CardModel source)
    {
        if (!LoadoutSpecialKeywords.TryGetValue(
                source,
                CardsVar,
                out DynamicVar countVar))
        {
            return;
        }

        int count = Math.Max(0, countVar.IntValue);
        for (int upgradeIndex = 0; upgradeIndex < count; upgradeIndex++)
        {
            List<CardModel> candidates = PileType.Deck
                .GetPile(source.Owner)
                .Cards
                .Where(card => card.IsUpgradable)
                .ToList();
            CardModel? selected =
                source.Owner.RunState.Rng.CombatCardSelection.NextItem(candidates);
            if (selected is null)
                break;

            int previousUpgradeLevel = selected.CurrentUpgradeLevel;
            CardCmd.Upgrade(selected, CardPreviewStyle.HorizontalLayout);
            if (selected.CurrentUpgradeLevel <= previousUpgradeLevel
                || !LocalContext.IsMine(selected))
            {
                continue;
            }

            // CardCmd.Upgrade supplies the native NCardUpgradeVfx. The base
            // game's card-smith sound is presentation-only, so only the owning
            // local peer plays it.
            NDebugAudioManager.Instance?.Play(
                TmpSfx.cardSmith,
                1f,
                PitchVariance.Small);
        }
    }
}
