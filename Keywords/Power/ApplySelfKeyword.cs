#nullable enable

namespace Loadout.Keywords;

using System.Collections.Generic;
using MegaCrit.Sts2.Core.Entities.Cards;

public sealed class ApplySelfKeyword : LoadoutPowerKeywordModel
{
    private static readonly IReadOnlyList<LoadoutKeywordDynamicVarDefinition>
        Variables = CreateDisplayVariables(
            "LoadoutApplySelfList",
            "DYNAMIC_VAR_LOADOUT_APPLY_SELF_LIST",
            LoadoutKeywords.ApplySelfKey);

    public static ApplySelfKeyword Instance { get; } = new();

    private ApplySelfKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.ApplySelf;
    public override string StorageKey => LoadoutKeywords.ApplySelfKey;
    public override string TitleLocKey => "LOADOUT-APPLY_SELF.title";
    public override string? CardTextLocKey => "LOADOUT-APPLY_SELF.cardText";
    public override string DisplayVarName => "LoadoutApplySelfList";
    public override string AmountLabelLocKey => "CARD_MOD_APPLY_SELF_AMOUNT";
    public override string PowerLabelLocKey => "CARD_MOD_APPLY_SELF_POWER";
    public override IReadOnlyList<LoadoutKeywordDynamicVarDefinition> DynamicVars => Variables;
    public override LoadoutPowerKeywordTargetMode TargetMode => LoadoutPowerKeywordTargetMode.Self;
}
