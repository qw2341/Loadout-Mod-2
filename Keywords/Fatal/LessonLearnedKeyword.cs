#nullable enable

namespace Loadout.Keywords;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Audio.Debug;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Vfx;

public sealed class LessonLearnedKeyword : LoadoutFatalKeywordModel
{
    public const string CardsVar = "LoadoutLessonLearnedCards";

    private const int FinalResultPreviewThreshold = 20;

    private static readonly IReadOnlyList<LoadoutKeywordDynamicVarDefinition>
        VariableDefinitions =
        [
            new(
                CardsVar,
                1m,
                int.MinValue,
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

    public override string? CardTextLocKey => "LOADOUT-LESSON_LEARNED.cardText";

    public override IReadOnlyList<LoadoutKeywordDynamicVarDefinition> DynamicVars =>
        VariableDefinitions;

    public override Task AfterFatal(
        CardModel card,
        PlayerChoiceContext choiceContext,
        int fatalCount)
    {
        Apply(card, fatalCount);
        return Task.CompletedTask;
    }

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

        bool showEachUpgrade =
            upgradeCount < FinalResultPreviewThreshold;
        List<CardModel>? upgradedCards = null;
        HashSet<CardModel>? upgradedCardSet = null;

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
                    showEachUpgrade
                        ? CardPreviewStyle.HorizontalLayout
                        : CardPreviewStyle.None);
                if (!selected.IsUpgradable)
                    candidates.RemoveAt(selectedIndex);

                if (selected.CurrentUpgradeLevel <= previousUpgradeLevel
                    || !LocalContext.IsMine(selected))
                {
                    continue;
                }

                if (showEachUpgrade)
                {
                    NDebugAudioManager.Instance?.Play(
                        TmpSfx.cardSmith,
                        1f,
                        PitchVariance.Small);
                    continue;
                }

                upgradedCardSet ??=
                    new HashSet<CardModel>(
                        ReferenceEqualityComparer.Instance);
                if (upgradedCardSet.Add(selected))
                    (upgradedCards ??= []).Add(selected);
            }

            if (upgradedCards is null)
                return;

            foreach (CardModel card in upgradedCards)
            {
                NRun.Instance?.GlobalUi.CardPreviewContainer.AddChildSafely(
                    NCardUpgradeVfx.Create(card));
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
