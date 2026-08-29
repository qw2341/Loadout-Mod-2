#nullable enable

namespace Loadout.Keywords;

using Loadout.Services.TildeKey;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;

public sealed class IncreaseMonsterDamageThisCombatKeyword
    : LoadoutBasicMultiplierKeywordModel
{
    public const string PercentageVar =
        "LoadoutBasicMultiplierMonsterDamageThisCombat";

    public static IncreaseMonsterDamageThisCombatKeyword Instance { get; } = new();

    private IncreaseMonsterDamageThisCombatKeyword()
    {
    }

    public override CardKeyword Keyword =>
        LoadoutKeywords.IncreaseMonsterDamageThisCombat;

    public override string StorageKey =>
        LoadoutKeywords.IncreaseMonsterDamageThisCombatKey;

    public override string TitleLocKey =>
        "LOADOUT-BASIC_MULTIPLIER_MONSTER_DAMAGE_THIS_COMBAT.title";

    public override string? CardTextLocKey =>
        "LOADOUT-BASIC_MULTIPLIER_MONSTER_DAMAGE_THIS_COMBAT.cardText";

    public override string PercentageVarName => PercentageVar;

    public override LoadoutBasicMultiplierTarget MultiplierTarget =>
        LoadoutBasicMultiplierTarget.MonsterDamageTaken;

    public override TildeKeyDamageMultiplierDuration Duration =>
        TildeKeyDamageMultiplierDuration.Combat;
}
