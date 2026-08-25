#nullable enable

namespace Loadout.Keywords;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;

public sealed class AlchemyKeyword : LoadoutFatalKeywordModel
{
    public const string AmountVar = "LoadoutFatalPotions";

    private static readonly IReadOnlyList<LoadoutKeywordDynamicVarDefinition>
        VariableDefinitions =
        [
            new(
                AmountVar,
                1m,
                int.MinValue,
                int.MaxValue,
                "DYNAMIC_VAR_LOADOUT_FATAL_POTIONS",
                (name, value) => new IntVar(name, value))
        ];

    public static AlchemyKeyword Instance { get; } = new();

    private AlchemyKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.Alchemy;

    public override string StorageKey => LoadoutKeywords.AlchemyKey;

    public override string TitleLocKey => "LOADOUT-FATAL_ALCHEMY.title";

    public override string? CardTextLocKey => "LOADOUT-FATAL_ALCHEMY.cardText";

    public override IReadOnlyList<LoadoutKeywordDynamicVarDefinition> DynamicVars =>
        VariableDefinitions;

    public override async Task AfterFatal(
        CardModel card,
        PlayerChoiceContext choiceContext,
        int fatalCount)
    {
        int potionCount = decimal.ToInt32(
            GetTotalAmount(card, AmountVar, fatalCount));
        for (int potionIndex = 0; potionIndex < potionCount; potionIndex++)
        {
            PotionModel potion = PotionFactory.CreateRandomPotionInCombat(
                card.Owner,
                card.Owner.RunState.Rng.CombatPotionGeneration);
            await PotionCmd.TryToProcure(potion.ToMutable(), card.Owner);
        }
    }
}
