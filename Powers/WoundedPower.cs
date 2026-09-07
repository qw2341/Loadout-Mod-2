#nullable enable

namespace Loadout.Powers;

using System.Collections.Generic;
using System.Threading.Tasks;
using BaseLib.Abstracts;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;

public sealed class WoundedPower : CustomPowerModel
{
    public const int StunThreshold = 10;

    public override PowerType Type => PowerType.Debuff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override string? CustomPackedIconPath =>
        ModelDb.Power<PainfulStabsPower>().PackedIconPath;

    public override string? CustomBigIconPath =>
        "res://images/powers/painful_stabs_power.png";

    protected override IEnumerable<IHoverTip> ExtraHoverTips =>
        [HoverTipFactory.Static(StaticHoverTip.Stun)];

    public override async Task AfterPowerAmountChanged(
        PlayerChoiceContext choiceContext,
        PowerModel power,
        decimal amount,
        Creature? applier,
        MegaCrit.Sts2.Core.Models.CardModel? cardSource)
    {
        if (!ReferenceEquals(power, this)
            || Amount < StunThreshold
            || !Owner.IsMonster
            || Owner.IsDead)
        {
            return;
        }

        Creature owner = Owner;
        Flash();
        await PowerCmd.Remove(this);
        if (!owner.IsStunned)
            await CreatureCmd.Stun(owner);
    }
}
