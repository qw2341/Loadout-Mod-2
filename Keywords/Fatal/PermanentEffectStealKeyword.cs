#nullable enable

namespace Loadout.Keywords;

using MegaCrit.Sts2.Core.Entities.Cards;

public sealed class PermanentEffectStealKeyword : FatalPowerStealKeywordModel
{
    public static PermanentEffectStealKeyword Instance { get; } = new();

    private PermanentEffectStealKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.PermanentEffectSteal;

    public override string StorageKey =>
        LoadoutKeywords.PermanentEffectStealKey;

    public override string TitleLocKey =>
        "LOADOUT-FATAL_PERMANENT_EFFECT_STEAL.title";

    public override string? CardTextLocKey =>
        "LOADOUT-FATAL_PERMANENT_EFFECT_STEAL.cardText";

    protected override bool IncludesDebuffs => true;

    protected override bool AppliesPermanently => true;
}
