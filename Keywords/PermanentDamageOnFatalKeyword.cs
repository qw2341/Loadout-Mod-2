#nullable enable

namespace Loadout.Keywords;

using System.Collections.Generic;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;

public sealed class PermanentDamageOnFatalKeyword : LoadoutImprovementKeywordModel
{
    public const string AmountVar = "LoadoutImprovementPermanentDamageOnFatal";

    private static readonly IReadOnlyList<LoadoutKeywordDynamicVarDefinition>
        VariableDefinitions =
        [
            new(
                AmountVar,
                1m,
                int.MinValue,
                int.MaxValue,
                "DYNAMIC_VAR_LOADOUT_IMPROVEMENT_PERMANENT_DAMAGE_ON_FATAL",
                (name, value) => new IntVar(name, value))
        ];

    public static PermanentDamageOnFatalKeyword Instance { get; } = new();

    private PermanentDamageOnFatalKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.PermanentDamageOnFatal;

    public override string StorageKey => LoadoutKeywords.PermanentDamageOnFatalKey;

    public override string TitleLocKey =>
        "LOADOUT-IMPROVEMENT_PERMANENT_DAMAGE_ON_FATAL.title";

    public override string? CardTextLocKey =>
        "LOADOUT-IMPROVEMENT_PERMANENT_DAMAGE_ON_FATAL.cardText";

    public override IReadOnlyList<LoadoutKeywordDynamicVarDefinition> DynamicVars =>
        VariableDefinitions;

    public override bool HasFatalEffect => true;

    public override void AfterFatal(CardModel card, int fatalCount)
    {
        decimal increase = GetAmount(card, AmountVar) * fatalCount;
        IncreaseDamagePermanently(card, increase);
    }
}
