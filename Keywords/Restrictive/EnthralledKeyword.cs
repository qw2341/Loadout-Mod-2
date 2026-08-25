#nullable enable

namespace Loadout.Keywords;

using MegaCrit.Sts2.Core.Entities.Cards;

public sealed class EnthralledKeyword : LoadoutRestrictiveKeywordModel
{
    public static EnthralledKeyword Instance { get; } = new();

    private EnthralledKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.Enthralled;

    public override string StorageKey => LoadoutKeywords.EnthralledKey;

    public override string TitleLocKey =>
        "LOADOUT-RESTRICTIVE_ENTHRALLED.title";

    public override string? CardTextLocKey =>
        "LOADOUT-RESTRICTIVE_ENTHRALLED.cardText";

    public override bool HasPlayRestriction => true;
}
