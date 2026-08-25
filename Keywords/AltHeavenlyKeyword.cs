#nullable enable

namespace Loadout.Keywords;

using System.Collections.Generic;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;

public sealed class AltHeavenlyKeyword : LoadoutKeywordModel
{
    public const string EnergyVar = "LoadoutAltHeavenlyEnergy";

    private const int LargestExactFactorialInput = 12;

    private static readonly IReadOnlyList<LoadoutKeywordDynamicVarDefinition>
        VariableDefinitions =
        [
            new(
                EnergyVar,
                4m,
                int.MinValue,
                int.MaxValue,
                "DYNAMIC_VAR_LOADOUT_ALT_HEAVENLY_ENERGY")
        ];

    public static AltHeavenlyKeyword Instance { get; } = new();

    private AltHeavenlyKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.AltHeavenly;

    public override string StorageKey => LoadoutKeywords.AltHeavenlyKey;

    public override string TitleLocKey => "LOADOUT-ALT_HEAVENLY.title";

    public override LoadoutKeywordPresentation Presentation =>
        LoadoutKeywordPresentation.DescriptionOnly;

    public override string? CardTextLocKey =>
        "LOADOUT-ALT_HEAVENLY.cardText";

    public override IReadOnlyList<LoadoutKeywordDynamicVarDefinition> DynamicVars =>
        VariableDefinitions;

    public static int Factorialize(int value)
    {
        if (value < 0)
            return value;
        if (value > LargestExactFactorialInput)
            return int.MaxValue;

        int result = 1;
        for (int factor = 2; factor <= value; factor++)
            result *= factor;

        return result;
    }
}
