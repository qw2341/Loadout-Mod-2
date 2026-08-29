#nullable enable

namespace Loadout.Keywords;

using Loadout.Services.TildeKey;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;

public sealed class IncreaseMonsterDamagePermanentlyKeyword
    : LoadoutBasicMultiplierKeywordModel
{
    public const string PercentageVar =
        "LoadoutBasicMultiplierMonsterDamagePermanent";

    public static IncreaseMonsterDamagePermanentlyKeyword Instance { get; } = new();

    private IncreaseMonsterDamagePermanentlyKeyword()
    {
    }

    public override CardKeyword Keyword =>
        LoadoutKeywords.IncreaseMonsterDamagePermanently;

    public override string StorageKey =>
        LoadoutKeywords.IncreaseMonsterDamagePermanentlyKey;

    public override string TitleLocKey =>
        "LOADOUT-BASIC_MULTIPLIER_MONSTER_DAMAGE_PERMANENTLY.title";

    public override string? CardTextLocKey =>
        "LOADOUT-BASIC_MULTIPLIER_MONSTER_DAMAGE_PERMANENTLY.cardText";

    public override string PercentageVarName => PercentageVar;

    public override LoadoutBasicMultiplierTarget MultiplierTarget =>
        LoadoutBasicMultiplierTarget.MonsterDamageTaken;

    public override TildeKeyDamageMultiplierDuration Duration =>
        TildeKeyDamageMultiplierDuration.Permanent;
}
