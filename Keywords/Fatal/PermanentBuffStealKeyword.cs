#nullable enable

namespace Loadout.Keywords;

using MegaCrit.Sts2.Core.Entities.Cards;

public sealed class PermanentBuffStealKeyword : FatalPowerStealKeywordModel
{
    public static PermanentBuffStealKeyword Instance { get; } = new();

    private PermanentBuffStealKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.PermanentBuffSteal;

    public override string StorageKey =>
        LoadoutKeywords.PermanentBuffStealKey;

    public override string TitleLocKey =>
        "LOADOUT-FATAL_PERMANENT_BUFF_STEAL.title";

    public override string? CardTextLocKey =>
        "LOADOUT-FATAL_PERMANENT_BUFF_STEAL.cardText";

    protected override bool IncludesDebuffs => false;

    protected override bool AppliesPermanently => true;
}
