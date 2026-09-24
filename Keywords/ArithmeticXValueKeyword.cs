#nullable enable

namespace Loadout.Keywords;

using System;
using System.Collections.Generic;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;

public sealed class ArithmeticXValueKeyword : LoadoutKeywordModel
{
    public const string CoefficientVar = "LoadoutArithmeticXValueCoefficient";
    public const string ConstantVar = "LoadoutArithmeticXValueConstant";
    private static readonly IReadOnlyList<LoadoutKeywordDynamicVarDefinition> VariableDefinitions =
    [
        new(CoefficientVar, 1m, int.MinValue, int.MaxValue, "DYNAMIC_VAR_LOADOUT_ARITHMETIC_X_VALUE_COEFFICIENT", AffectedByXValue: false),
        new(ConstantVar, 0m, int.MinValue, int.MaxValue, "DYNAMIC_VAR_LOADOUT_ARITHMETIC_X_VALUE_CONSTANT", AffectedByXValue: false)
    ];

    public static ArithmeticXValueKeyword Instance { get; } = new();
    private ArithmeticXValueKeyword() { }
    public override CardKeyword Keyword => LoadoutKeywords.ArithmeticXValue;
    public override LoadoutCardModificationFlags ModificationFlags =>
        LoadoutCardModificationFlags.OverrideXCost | LoadoutCardModificationFlags.OverrideXValue;
    public override string StorageKey => LoadoutKeywords.ArithmeticXValueKey;
    public override string TitleLocKey => "LOADOUT-ARITHMETIC_X_VALUE.title";
    public override LoadoutKeywordPresentation Presentation => LoadoutKeywordPresentation.DescriptionOnly;
    public override IReadOnlyList<LoadoutKeywordDynamicVarDefinition> DynamicVars => VariableDefinitions;

    public static int GetCoefficient(CardModel card) =>
        LoadoutKeywordRegistry.TryGetValue(card, CoefficientVar, out DynamicVar coefficient)
            ? coefficient.IntValue : 1;

    public static string FormatExpression(int coefficient, int constant)
    {
        if (coefficient == 0)
            return constant.ToString();
        long magnitude = Math.Abs((long)coefficient);
        string term = magnitude == 1 ? "X" : $"{magnitude}X";
        if (coefficient < 0)
            return $"{constant} - {term}";
        return constant switch
        {
            > 0 => $"{term} + {constant}",
            < 0 => $"{term} - {Math.Abs((long)constant)}",
            _ => term
        };
    }
}
