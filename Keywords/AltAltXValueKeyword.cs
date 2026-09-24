#nullable enable

namespace Loadout.Keywords;

using System.Collections.Generic;
using MegaCrit.Sts2.Core.Entities.Cards;

public sealed class AltAltXValueKeyword : LoadoutKeywordModel
{
    public const string AdditionalAmountVar = "LoadoutAltAltXValueAdditionalAmount";
    private static readonly IReadOnlyList<LoadoutKeywordDynamicVarDefinition> VariableDefinitions =
    [
        new(AdditionalAmountVar, 0m, 0, int.MaxValue, "DYNAMIC_VAR_LOADOUT_X_VALUE_ADDITIONAL_AMOUNT")
    ];

    public static AltAltXValueKeyword Instance { get; } = new();
    private AltAltXValueKeyword() { }
    public override CardKeyword Keyword => LoadoutKeywords.AltAltXValue;
    public override string StorageKey => LoadoutKeywords.AltAltXValueKey;
    public override string TitleLocKey => "LOADOUT-ALT_ALT_X_VALUE.title";
    public override LoadoutKeywordEditorSection EditorSection => LoadoutKeywordEditorSection.Joke;
    public override LoadoutKeywordPresentation Presentation => LoadoutKeywordPresentation.DescriptionOnly;
    public override IReadOnlyList<LoadoutKeywordDynamicVarDefinition> DynamicVars => VariableDefinitions;

    public static int RaiseToSelf(int value)
    {
        if (value < 0)
            return value;
        if (value > 9)
            return int.MaxValue;

        int result = 1;
        for (int exponent = 0; exponent < value; exponent++)
            result *= value;
        return result;
    }
}
