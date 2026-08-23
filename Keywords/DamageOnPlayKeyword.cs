#nullable enable

namespace Loadout.Keywords;

using System.Collections.Generic;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;

public sealed class DamageOnPlayKeyword : LoadoutImprovementKeywordModel
{
    public const string AmountVar = "LoadoutImprovementDamageOnPlay";

    private static readonly IReadOnlyList<LoadoutKeywordDynamicVarDefinition>
        VariableDefinitions =
        [
            new(
                AmountVar,
                1m,
                0,
                int.MaxValue,
                "DYNAMIC_VAR_LOADOUT_IMPROVEMENT_DAMAGE_ON_PLAY",
                (name, value) => new IntVar(name, value))
        ];

    public static DamageOnPlayKeyword Instance { get; } = new();

    private DamageOnPlayKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.DamageOnPlay;

    public override string StorageKey => LoadoutKeywords.DamageOnPlayKey;

    public override string TitleLocKey =>
        "LOADOUT-IMPROVEMENT_DAMAGE_ON_PLAY.title";

    public override string? CardTextLocKey =>
        "LOADOUT-IMPROVEMENT_DAMAGE_ON_PLAY.cardText";

    public override IReadOnlyList<LoadoutKeywordDynamicVarDefinition> DynamicVars =>
        VariableDefinitions;

    public override bool HasOnPlayEffect => true;

    public override Task AfterOnPlay(
        CardModel card,
        PlayerChoiceContext choiceContext,
        CardPlay cardPlay,
        object? capturedState)
    {
        IncreaseDamage(card, GetAmount(card, AmountVar));
        return Task.CompletedTask;
    }
}
