#nullable enable

namespace Loadout.Keywords;

using Loadout.Services.TildeKey;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;

public sealed class IncreaseDamageDealtThisCombatKeyword
    : LoadoutBasicMultiplierKeywordModel
{
    public const string PercentageVar =
        "LoadoutBasicMultiplierDamageDealtThisCombat";

    public static IncreaseDamageDealtThisCombatKeyword Instance { get; } = new();

    private IncreaseDamageDealtThisCombatKeyword()
    {
    }

    public override CardKeyword Keyword =>
        LoadoutKeywords.IncreaseDamageDealtThisCombat;

    public override string StorageKey =>
        LoadoutKeywords.IncreaseDamageDealtThisCombatKey;

    public override string TitleLocKey =>
        "LOADOUT-BASIC_MULTIPLIER_INCREASE_DAMAGE_DEALT_THIS_COMBAT.title";

    public override string? CardTextLocKey =>
        "LOADOUT-BASIC_MULTIPLIER_DAMAGE_DEALT_THIS_COMBAT.cardText";

    public override string PercentageVarName => PercentageVar;

    public override LoadoutBasicMultiplierTarget MultiplierTarget =>
        LoadoutBasicMultiplierTarget.DamageDealt;

    public override TildeKeyDamageMultiplierDuration Duration =>
        TildeKeyDamageMultiplierDuration.Combat;
}
