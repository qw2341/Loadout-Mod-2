#nullable enable

namespace Loadout.Keywords;

using Loadout.Services.TildeKey;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;

public sealed class IncreaseDamageDealtPermanentlyKeyword
    : LoadoutBasicMultiplierKeywordModel
{
    public const string PercentageVar =
        "LoadoutBasicMultiplierDamageDealtPermanent";

    public static IncreaseDamageDealtPermanentlyKeyword Instance { get; } = new();

    private IncreaseDamageDealtPermanentlyKeyword()
    {
    }

    public override CardKeyword Keyword =>
        LoadoutKeywords.IncreaseDamageDealtPermanently;

    public override string StorageKey =>
        LoadoutKeywords.IncreaseDamageDealtPermanentlyKey;

    public override string TitleLocKey =>
        "LOADOUT-BASIC_MULTIPLIER_DAMAGE_DEALT_PERMANENTLY.title";

    public override string? CardTextLocKey =>
        "LOADOUT-BASIC_MULTIPLIER_DAMAGE_DEALT_PERMANENTLY.cardText";

    public override string PercentageVarName => PercentageVar;

    public override LoadoutBasicMultiplierTarget MultiplierTarget =>
        LoadoutBasicMultiplierTarget.DamageDealt;

    public override TildeKeyDamageMultiplierDuration Duration =>
        TildeKeyDamageMultiplierDuration.Permanent;
}
