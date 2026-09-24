#nullable enable

namespace Loadout.Keywords;

using System.Collections.Generic;
using MegaCrit.Sts2.Core.Entities.Cards;

public sealed class AltAltAltXValueKeyword : LoadoutKeywordModel
{
    public const string AdditionalAmountVar = "LoadoutAltAltAltXValueAdditionalAmount";
    private static readonly IReadOnlyList<LoadoutKeywordDynamicVarDefinition> VariableDefinitions =
    [
        new(AdditionalAmountVar, 0m, 0, int.MaxValue, "DYNAMIC_VAR_LOADOUT_X_VALUE_ADDITIONAL_AMOUNT", AffectedByXValue: false)
    ];

    public static AltAltAltXValueKeyword Instance { get; } = new();
    private AltAltAltXValueKeyword() { }
    public override CardKeyword Keyword => LoadoutKeywords.AltAltAltXValue;
    public override LoadoutCardModificationFlags ModificationFlags =>
        LoadoutCardModificationFlags.OverrideXCost | LoadoutCardModificationFlags.OverrideXValue | LoadoutCardModificationFlags.FastAnimation;
    public override string StorageKey => LoadoutKeywords.AltAltAltXValueKey;
    public override string TitleLocKey => "LOADOUT-ALT_ALT_ALT_X_VALUE.title";
    public override LoadoutKeywordEditorSection EditorSection => LoadoutKeywordEditorSection.Joke;
    public override LoadoutKeywordPresentation Presentation => LoadoutKeywordPresentation.DescriptionOnly;
    public override IReadOnlyList<LoadoutKeywordDynamicVarDefinition> DynamicVars => VariableDefinitions;

    // 3 ↑↑ 3 already exceeds Int32; a height-zero tower is 1.
    public static int TetrateSelf(int value) => value switch
    {
        < 0 => value,
        0 or 1 => 1,
        2 => 4,
        _ => int.MaxValue
    };
}
