#nullable enable

namespace Loadout.Keywords;

using MegaCrit.Sts2.Core.Entities.Cards;

public sealed class PermanentFormStealStackingKeyword :
    PermanentFormStealKeywordModel
{
    public static PermanentFormStealStackingKeyword Instance { get; } = new();

    private PermanentFormStealStackingKeyword()
    {
    }

    public override CardKeyword Keyword =>
        LoadoutKeywords.PermanentFormStealStacking;

    public override string StorageKey =>
        LoadoutKeywords.PermanentFormStealStackingKey;

    public override string TitleLocKey =>
        "LOADOUT-FATAL_PERMANENT_FORM_STEAL_STACKING.title";

    public override string? CardTextLocKey =>
        "LOADOUT-FATAL_PERMANENT_FORM_STEAL_STACKING.cardText";

    protected override bool StacksPowers => true;
}
