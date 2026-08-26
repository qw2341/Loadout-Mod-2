#nullable enable

namespace Loadout.Keywords;

using System.Collections.Generic;
using MegaCrit.Sts2.Core.Entities.Cards;

public sealed class ApplyToAllEnemiesKeyword : LoadoutPowerKeywordModel
{
    private static readonly IReadOnlyList<LoadoutKeywordDynamicVarDefinition>
        Variables = CreateDisplayVariables(
            "LoadoutApplyToAllEnemiesList",
            "DYNAMIC_VAR_LOADOUT_APPLY_TO_ALL_ENEMIES_LIST",
            LoadoutKeywords.ApplyToAllEnemiesKey);

    public static ApplyToAllEnemiesKeyword Instance { get; } = new();

    private ApplyToAllEnemiesKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.ApplyToAllEnemies;
    public override string StorageKey => LoadoutKeywords.ApplyToAllEnemiesKey;
    public override string TitleLocKey => "LOADOUT-APPLY_TO_ALL_ENEMIES.title";
    public override string? CardTextLocKey => "LOADOUT-APPLY_TO_ALL_ENEMIES.cardText";
    public override string DisplayVarName => "LoadoutApplyToAllEnemiesList";
    public override string AmountLabelLocKey => "CARD_MOD_APPLY_TO_ALL_ENEMIES_AMOUNT";
    public override string PowerLabelLocKey => "CARD_MOD_APPLY_TO_ALL_ENEMIES_POWER";
    public override IReadOnlyList<LoadoutKeywordDynamicVarDefinition> DynamicVars => Variables;
    public override LoadoutPowerKeywordTargetMode TargetMode => LoadoutPowerKeywordTargetMode.AllEnemies;
}
