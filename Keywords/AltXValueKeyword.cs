#nullable enable

namespace Loadout.Keywords;

using System.Collections.Generic;
using MegaCrit.Sts2.Core.Entities.Cards;

public sealed class AltXValueKeyword : LoadoutKeywordModel
{
    public const string AdditionalAmountVar = "LoadoutAltXValueAdditionalAmount";
    private static readonly IReadOnlyList<LoadoutKeywordDynamicVarDefinition> VariableDefinitions =
    [
        new(AdditionalAmountVar, 0m, 0, int.MaxValue, "DYNAMIC_VAR_LOADOUT_X_VALUE_ADDITIONAL_AMOUNT")
    ];

    public static AltXValueKeyword Instance { get; } = new();
    private AltXValueKeyword() { }
    public override CardKeyword Keyword => LoadoutKeywords.AltXValue;
    public override string StorageKey => LoadoutKeywords.AltXValueKey;
    public override string TitleLocKey => "LOADOUT-ALT_X_VALUE.title";
    public override LoadoutKeywordEditorSection EditorSection => LoadoutKeywordEditorSection.Joke;
    public override LoadoutKeywordPresentation Presentation => LoadoutKeywordPresentation.DescriptionOnly;
    public override IReadOnlyList<LoadoutKeywordDynamicVarDefinition> DynamicVars => VariableDefinitions;
}
