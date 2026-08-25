#nullable enable

namespace Loadout.Keywords;

using MegaCrit.Sts2.Core.Entities.Cards;

public sealed class ClashKeyword : LoadoutRestrictiveKeywordModel
{
    public static ClashKeyword Instance { get; } = new();

    private ClashKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.Clash;

    public override string StorageKey => LoadoutKeywords.ClashKey;

    public override string TitleLocKey => "LOADOUT-RESTRICTIVE_CLASH.title";

    public override string? CardTextLocKey =>
        "LOADOUT-RESTRICTIVE_CLASH.cardText";

    public override bool HasPlayRestriction => true;
}
