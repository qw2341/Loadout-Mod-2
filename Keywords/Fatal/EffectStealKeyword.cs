#nullable enable

namespace Loadout.Keywords;

using MegaCrit.Sts2.Core.Entities.Cards;

public sealed class EffectStealKeyword : FatalPowerStealKeywordModel
{
    public static EffectStealKeyword Instance { get; } = new();

    private EffectStealKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.EffectSteal;

    public override string StorageKey => LoadoutKeywords.EffectStealKey;

    public override string TitleLocKey => "LOADOUT-FATAL_EFFECT_STEAL.title";

    public override string? CardTextLocKey =>
        "LOADOUT-FATAL_EFFECT_STEAL.cardText";

    protected override bool IncludesDebuffs => true;

    protected override bool AppliesPermanently => false;
}
