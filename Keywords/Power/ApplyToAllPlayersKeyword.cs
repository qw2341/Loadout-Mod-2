#nullable enable

namespace Loadout.Keywords;

using System.Collections.Generic;
using MegaCrit.Sts2.Core.Entities.Cards;

public sealed class ApplyToAllPlayersKeyword : LoadoutPowerKeywordModel
{
    private static readonly IReadOnlyList<LoadoutKeywordDynamicVarDefinition>
        Variables = CreateDisplayVariables(
            "LoadoutApplyToAllPlayersList",
            "DYNAMIC_VAR_LOADOUT_APPLY_TO_ALL_PLAYERS_LIST",
            LoadoutKeywords.ApplyToAllPlayersKey);

    public static ApplyToAllPlayersKeyword Instance { get; } = new();

    private ApplyToAllPlayersKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.ApplyToAllPlayers;
    public override string StorageKey => LoadoutKeywords.ApplyToAllPlayersKey;
    public override string TitleLocKey => "LOADOUT-APPLY_TO_ALL_PLAYERS.title";
    public override string? CardTextLocKey => "LOADOUT-APPLY_TO_ALL_PLAYERS.cardText";
    public override string DisplayVarName => "LoadoutApplyToAllPlayersList";
    public override string AmountLabelLocKey => "CARD_MOD_APPLY_TO_ALL_PLAYERS_AMOUNT";
    public override string PowerLabelLocKey => "CARD_MOD_APPLY_TO_ALL_PLAYERS_POWER";
    public override IReadOnlyList<LoadoutKeywordDynamicVarDefinition> DynamicVars => Variables;
    public override LoadoutPowerKeywordTargetMode TargetMode => LoadoutPowerKeywordTargetMode.AllPlayers;
}
