#nullable enable

namespace Loadout.Keywords;

using Loadout.Services.TildeKey;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;

public sealed class IncreaseDamageDealtThisTurnKeyword
    : LoadoutBasicMultiplierKeywordModel
{
    public const string PercentageVar =
        "LoadoutBasicMultiplierDamageDealtThisTurn";

    public static IncreaseDamageDealtThisTurnKeyword Instance { get; } = new();

    private IncreaseDamageDealtThisTurnKeyword()
    {
    }

    public override CardKeyword Keyword =>
        LoadoutKeywords.IncreaseDamageDealtThisTurn;

    public override string StorageKey =>
        LoadoutKeywords.IncreaseDamageDealtThisTurnKey;

    public override string TitleLocKey =>
        "LOADOUT-BASIC_MULTIPLIER_DAMAGE_DEALT_THIS_TURN.title";

    public override string? CardTextLocKey =>
        "LOADOUT-BASIC_MULTIPLIER_DAMAGE_DEALT_THIS_TURN.cardText";

    public override string PercentageVarName => PercentageVar;

    public override LoadoutBasicMultiplierTarget MultiplierTarget =>
        LoadoutBasicMultiplierTarget.DamageDealt;

    public override TildeKeyDamageMultiplierDuration Duration =>
        TildeKeyDamageMultiplierDuration.Turn;
}
