#nullable enable

namespace Loadout.Keywords;

using System.Collections.Generic;
using MegaCrit.Sts2.Core.Entities.Cards;

public sealed class ApplyPowerKeyword : LoadoutPowerKeywordModel
{
    public override string DisplayVarName => "LoadoutApplyPowerList";

    private static readonly IReadOnlyList<LoadoutKeywordDynamicVarDefinition>
        VariableDefinitions =
        CreateDisplayVariables(
            "LoadoutApplyPowerList",
            "DYNAMIC_VAR_LOADOUT_APPLY_POWER_LIST",
            LoadoutKeywords.ApplyPowerKey);

    public static ApplyPowerKeyword Instance { get; } = new();

    private ApplyPowerKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.ApplyPower;

    public override string StorageKey => LoadoutKeywords.ApplyPowerKey;

    public override string TitleLocKey => "LOADOUT-APPLY_POWER.title";

    public override string? CardTextLocKey => "LOADOUT-APPLY_POWER.cardText";

    public override IReadOnlyList<LoadoutKeywordDynamicVarDefinition> DynamicVars =>
        VariableDefinitions;

    public override LoadoutPowerKeywordTargetMode TargetMode =>
        LoadoutPowerKeywordTargetMode.SelectedCreature;

    public override string AmountLabelLocKey =>
        "CARD_MOD_APPLY_POWER_AMOUNT";

    public override string PowerLabelLocKey =>
        "CARD_MOD_APPLY_POWER_POWER";
}
