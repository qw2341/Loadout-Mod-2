#nullable enable

namespace Loadout.Keywords;

using System.Collections.Generic;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;

public sealed class BasicSummonKeyword : LoadoutBasicKeywordModel
{
    public const string AmountVar = "LoadoutBasicSummon";

    private static readonly IReadOnlyList<LoadoutKeywordDynamicVarDefinition>
        VariableDefinitions =
        [
            new(
                AmountVar,
                1m,
                int.MinValue,
                int.MaxValue,
                "DYNAMIC_VAR_LOADOUT_BASIC_SUMMON",
                (name, value) => new SummonVar(name, value))
        ];

    public static BasicSummonKeyword Instance { get; } = new();

    private BasicSummonKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.BasicSummon;

    public override string StorageKey => LoadoutKeywords.BasicSummonKey;

    public override string TitleLocKey => "LOADOUT-BASIC_SUMMON.title";

    public override string? CardTextLocKey => "LOADOUT-BASIC_SUMMON.cardText";

    public override IReadOnlyList<LoadoutKeywordDynamicVarDefinition> DynamicVars =>
        VariableDefinitions;

    public override IEnumerable<IHoverTip> GetAdditionalCardHoverTips(
        CardModel card) => [HoverTipFactory.Static(StaticHoverTip.SummonStatic)];

    public override async Task AfterOnPlay(
        CardModel card,
        PlayerChoiceContext choiceContext,
        CardPlay cardPlay,
        object? capturedState)
    {
        DynamicVar amount = GetAmount(card, AmountVar);
        if (amount.BaseValue <= 0m || card.CombatState is null || card.Owner.Creature.IsDead)
            return;

        await TriggerSummonAnimation(card);
        await OstyCmd.Summon(choiceContext, card.Owner, amount.BaseValue, card);
    }

    public static Task TriggerSummonAnimation(CardModel card) =>
        CreatureCmd.TriggerAnim(
            card.Owner.Creature,
            Necrobinder.GetSummonAnimIfApplicable(card.Owner.Character),
            Necrobinder.GetSummonDelayIfApplicable(card.Owner.Character));
}
