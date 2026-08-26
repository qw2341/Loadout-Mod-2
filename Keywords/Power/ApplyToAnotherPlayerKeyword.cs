#nullable enable

namespace Loadout.Keywords;

using System.Collections.Generic;
using MegaCrit.Sts2.Core.Entities.Cards;

public sealed class ApplyToAnotherPlayerKeyword : LoadoutPowerKeywordModel
{
    private static readonly IReadOnlyList<LoadoutKeywordDynamicVarDefinition>
        Variables = CreateDisplayVariables(
            "LoadoutApplyToAnotherPlayerList",
            "DYNAMIC_VAR_LOADOUT_APPLY_TO_ANOTHER_PLAYER_LIST",
            LoadoutKeywords.ApplyToAnotherPlayerKey);

    public static ApplyToAnotherPlayerKeyword Instance { get; } = new();

    private ApplyToAnotherPlayerKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.ApplyToAnotherPlayer;
    public override string StorageKey => LoadoutKeywords.ApplyToAnotherPlayerKey;
    public override string TitleLocKey => "LOADOUT-APPLY_TO_ANOTHER_PLAYER.title";
    public override string? CardTextLocKey => "LOADOUT-APPLY_TO_ANOTHER_PLAYER.cardText";
    public override string DisplayVarName => "LoadoutApplyToAnotherPlayerList";
    public override string AmountLabelLocKey => "CARD_MOD_APPLY_TO_ANOTHER_PLAYER_AMOUNT";
    public override string PowerLabelLocKey => "CARD_MOD_APPLY_TO_ANOTHER_PLAYER_POWER";
    public override IReadOnlyList<LoadoutKeywordDynamicVarDefinition> DynamicVars => Variables;
    public override LoadoutPowerKeywordTargetMode TargetMode => LoadoutPowerKeywordTargetMode.AnotherPlayer;
}
