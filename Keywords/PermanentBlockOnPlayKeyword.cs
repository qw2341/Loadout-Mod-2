#nullable enable

namespace Loadout.Keywords;

using System.Collections.Generic;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;

public sealed class PermanentBlockOnPlayKeyword : LoadoutImprovementKeywordModel
{
    public const string AmountVar = "LoadoutImprovementPermanentBlockOnPlay";

    private static readonly IReadOnlyList<LoadoutKeywordDynamicVarDefinition>
        VariableDefinitions =
        [
            new(
                AmountVar,
                1m,
                0,
                int.MaxValue,
                "DYNAMIC_VAR_LOADOUT_IMPROVEMENT_PERMANENT_BLOCK_ON_PLAY",
                (name, value) => new IntVar(name, value))
        ];

    public static PermanentBlockOnPlayKeyword Instance { get; } = new();

    private PermanentBlockOnPlayKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.PermanentBlockOnPlay;

    public override string StorageKey => LoadoutKeywords.PermanentBlockOnPlayKey;

    public override string TitleLocKey =>
        "LOADOUT-IMPROVEMENT_PERMANENT_BLOCK_ON_PLAY.title";

    public override string? CardTextLocKey =>
        "LOADOUT-IMPROVEMENT_PERMANENT_BLOCK_ON_PLAY.cardText";

    public override IReadOnlyList<LoadoutKeywordDynamicVarDefinition> DynamicVars =>
        VariableDefinitions;

    public override bool HasOnPlayEffect => true;

    public override Task AfterOnPlay(
        CardModel card,
        PlayerChoiceContext choiceContext,
        CardPlay cardPlay,
        object? capturedState)
    {
        IncreaseBlockPermanently(card, GetAmount(card, AmountVar));
        return Task.CompletedTask;
    }
}
