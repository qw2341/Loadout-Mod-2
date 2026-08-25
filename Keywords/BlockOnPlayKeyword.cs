#nullable enable

namespace Loadout.Keywords;

using System.Collections.Generic;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;

public sealed class BlockOnPlayKeyword : LoadoutImprovementKeywordModel
{
    public const string AmountVar = "LoadoutImprovementBlockOnPlay";

    private static readonly IReadOnlyList<LoadoutKeywordDynamicVarDefinition>
        VariableDefinitions =
        [
            new(
                AmountVar,
                1m,
                int.MinValue,
                int.MaxValue,
                "DYNAMIC_VAR_LOADOUT_IMPROVEMENT_BLOCK_ON_PLAY",
                (name, value) => new IntVar(name, value))
        ];

    public static BlockOnPlayKeyword Instance { get; } = new();

    private BlockOnPlayKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.BlockOnPlay;

    public override string StorageKey => LoadoutKeywords.BlockOnPlayKey;

    public override string TitleLocKey =>
        "LOADOUT-IMPROVEMENT_BLOCK_ON_PLAY.title";

    public override string? CardTextLocKey =>
        "LOADOUT-IMPROVEMENT_BLOCK_ON_PLAY.cardText";

    public override IReadOnlyList<LoadoutKeywordDynamicVarDefinition> DynamicVars =>
        VariableDefinitions;

    public override bool HasOnPlayEffect => true;

    public override Task AfterOnPlay(
        CardModel card,
        PlayerChoiceContext choiceContext,
        CardPlay cardPlay,
        object? capturedState)
    {
        IncreaseBlock(card, GetAmount(card, AmountVar));
        return Task.CompletedTask;
    }
}
