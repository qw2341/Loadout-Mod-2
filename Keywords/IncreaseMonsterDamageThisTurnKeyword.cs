#nullable enable

namespace Loadout.Keywords;

using Loadout.Services.TildeKey;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;

public sealed class IncreaseMonsterDamageThisTurnKeyword
    : LoadoutBasicMultiplierKeywordModel
{
    public const string PercentageVar =
        "LoadoutBasicMultiplierMonsterDamageThisTurn";

    public static IncreaseMonsterDamageThisTurnKeyword Instance { get; } = new();

    private IncreaseMonsterDamageThisTurnKeyword()
    {
    }

    public override CardKeyword Keyword =>
        LoadoutKeywords.IncreaseMonsterDamageThisTurn;

    public override string StorageKey =>
        LoadoutKeywords.IncreaseMonsterDamageThisTurnKey;

    public override string TitleLocKey =>
        "LOADOUT-BASIC_MULTIPLIER_INCREASE_MONSTER_DAMAGE_THIS_TURN.title";

    public override string? CardTextLocKey =>
        "LOADOUT-BASIC_MULTIPLIER_MONSTER_DAMAGE_THIS_TURN.cardText";

    public override string PercentageVarName => PercentageVar;

    public override LoadoutBasicMultiplierTarget MultiplierTarget =>
        LoadoutBasicMultiplierTarget.MonsterDamageTaken;

    public override TildeKeyDamageMultiplierDuration Duration =>
        TildeKeyDamageMultiplierDuration.Turn;
}
