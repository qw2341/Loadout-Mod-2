#nullable enable

namespace Loadout.Keywords;

using System.Collections.Generic;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;

public sealed class PermanentDamageOnPlayKeyword : LoadoutImprovementKeywordModel
{
    public const string AmountVar = "LoadoutImprovementPermanentDamageOnPlay";

    private static readonly IReadOnlyList<LoadoutKeywordDynamicVarDefinition>
        VariableDefinitions =
        [
            new(
                AmountVar,
                1m,
                int.MinValue,
                int.MaxValue,
                "DYNAMIC_VAR_LOADOUT_IMPROVEMENT_PERMANENT_DAMAGE_ON_PLAY",
                (name, value) => new IntVar(name, value))
        ];

    public static PermanentDamageOnPlayKeyword Instance { get; } = new();

    private PermanentDamageOnPlayKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.PermanentDamageOnPlay;

    public override string StorageKey => LoadoutKeywords.PermanentDamageOnPlayKey;

    public override string TitleLocKey =>
        "LOADOUT-IMPROVEMENT_PERMANENT_DAMAGE_ON_PLAY.title";

    public override string? CardTextLocKey =>
        "LOADOUT-IMPROVEMENT_PERMANENT_DAMAGE_ON_PLAY.cardText";

    public override IReadOnlyList<LoadoutKeywordDynamicVarDefinition> DynamicVars =>
        VariableDefinitions;

    public override bool HasOnPlayEffect => true;

    public override Task AfterOnPlay(
        CardModel card,
        PlayerChoiceContext choiceContext,
        CardPlay cardPlay,
        object? capturedState)
    {
        IncreaseDamagePermanently(card, GetAmount(card, AmountVar));
        return Task.CompletedTask;
    }
}
