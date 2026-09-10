#nullable enable

namespace Loadout.Powers;

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BaseLib.Abstracts;
using BaseLib.Hooks;
using BaseLib.Utils;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.ValueProps;

public sealed class WitherPower : CustomPowerModel
{
    public const int DamageUpgradeAmount = 3;

    private const string DamagePerStackKey = "DamagePerStack";

    private static readonly Color ForecastLabelColor = new("4A4A4F");

    private static ShaderMaterial? _forecastMaterial;

    public override PowerType Type => PowerType.Debuff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public int DamagePerStack => DynamicVars[DamagePerStackKey].IntValue;

    public int TotalDamage => Amount * DamagePerStack;

    protected override IEnumerable<DynamicVar> CanonicalVars =>
        [new DynamicVar(DamagePerStackKey, DamageUpgradeAmount)];

    public override LocString Description
    {
        get
        {
            LocString description = base.Description;
            description.Add("Damage", TotalDamage);
            return description;
        }
    }

    public override string? CustomPackedIconPath =>
        "res://images/powers/wither_power.png";

    public override string? CustomBigIconPath =>
        "res://images/powers/wither_power.png";

    public void UpgradeDamagePerStack()
    {
        DynamicVars[DamagePerStackKey].BaseValue += DamageUpgradeAmount;
        InvokeDisplayAmountChanged();
    }

    public override async Task AfterSideTurnEndLate(
        PlayerChoiceContext choiceContext,
        CombatSide side,
        IEnumerable<Creature> participants)
    {
        if (!participants.Contains(Owner) || Amount <= 0)
            return;

        await CreatureCmd.Damage(
            choiceContext,
            Owner,
            TotalDamage,
            ValueProp.Unpowered,
            Owner);
        VfxCmd.PlayOnCreatureCenter(Owner, "vfx/vfx_attack_blunt");
    }

    public override IEnumerable<HealthBarForecastSegment> GetHealthBarForecastSegments(
        HealthBarForecastContext context)
    {
        int damage = TotalDamage;
        if (!ReferenceEquals(context.Creature, Owner)
            || Owner.IsDead
            || damage <= 0)
        {
            return [];
        }

        _forecastMaterial ??= ShaderUtils.CreateDoomBarShaderMaterial(
            CreateBlackForecastGradient());

        return HealthBarForecasts
            .FromLeft(context, ForecastLabelColor)
            .Add(
                damage,
                HealthBarForecastOrder.ForSideTurnEnd(Owner, Owner.Side),
                _forecastMaterial)
            .Build();
    }

    private static GradientTexture1D CreateBlackForecastGradient()
    {
        Gradient gradient = new();
        gradient.SetOffsets([0f, 0.5f, 1f]);
        gradient.SetColors([
            new Color("000000"),
            new Color("101012"),
            new Color("030303")
        ]);
        return new GradientTexture1D { Gradient = gradient };
    }
}
