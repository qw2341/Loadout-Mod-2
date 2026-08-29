#nullable enable

namespace Loadout.Keywords;

using MegaCrit.Sts2.Core.Entities.Cards;

public sealed class BuffStealKeyword : FatalPowerStealKeywordModel
{
    public static BuffStealKeyword Instance { get; } = new();

    private BuffStealKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.BuffSteal;

    public override string StorageKey => LoadoutKeywords.BuffStealKey;

    public override string TitleLocKey => "LOADOUT-FATAL_BUFF_STEAL.title";

    public override string? CardTextLocKey =>
        "LOADOUT-FATAL_BUFF_STEAL.cardText";

    protected override bool IncludesDebuffs => false;

    protected override bool AppliesPermanently => false;
}
