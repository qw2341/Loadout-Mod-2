#nullable enable

namespace Loadout.Keywords;

using MegaCrit.Sts2.Core.Entities.Cards;

public sealed class PermanentFormStealKeyword : PermanentFormStealKeywordModel
{
    public static PermanentFormStealKeyword Instance { get; } = new();

    private PermanentFormStealKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.PermanentFormSteal;

    public override string StorageKey =>
        LoadoutKeywords.PermanentFormStealKey;

    public override string TitleLocKey =>
        "LOADOUT-FATAL_PERMANENT_FORM_STEAL.title";

    public override string? CardTextLocKey =>
        "LOADOUT-FATAL_PERMANENT_FORM_STEAL.cardText";

    protected override bool StacksPowers => false;
}
