#nullable enable

namespace Loadout.Powers;

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using BaseLib.Abstracts;
using HarmonyLib;
using Loadout.Keywords;
using MegaCrit.Sts2.Core.Commands.Builders;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;

public sealed class MultiHitPower : CustomPowerModel
{
    private sealed record HitCount(int Count);

    private static readonly AccessTools.FieldRef<AttackCommand, decimal> DamagePerHit =
        AccessTools.FieldRefAccess<AttackCommand, decimal>("_damagePerHit");

    public override PowerType Type => PowerType.Buff;
    public override PowerStackType StackType => PowerStackType.Single;
    public override string? CustomPackedIconPath => "res://images/powers/multi_hit_power.png";
    public override string? CustomBigIconPath => "res://images/powers/multi_hit_power.png";

    protected override IEnumerable<IHoverTip> ExtraHoverTips =>
        [HoverTipFactory.FromKeyword(LoadoutKeywords.MultiHit)];

    protected override object InitInternalData() =>
        new ConditionalWeakTable<AttackCommand, HitCount>();

    public static bool GrantsKeyword(CardModel card) =>
        !card.IsCanonical
        && card.Type == CardType.Attack
        && card.CombatState is not null
        && card.Owner?.Creature.GetPower<MultiHitPower>() is { Amount: > 0 };

    public override bool TryModifyKeywordsInCombat(CardModel card, ISet<CardKeyword> keywords) =>
        card.Owner == Owner.Player && card.Type == CardType.Attack
        && Amount > 0 && keywords.Add(LoadoutKeywords.MultiHit);

    public override Task AfterApplied(Creature? applier, CardModel? cardSource)
    {
        RefreshAttackCards(Owner);
        return Task.CompletedTask;
    }

    public override Task AfterRemoved(Creature oldOwner)
    {
        RefreshAttackCards(oldOwner);
        return Task.CompletedTask;
    }

    public override Task AfterCardEnteredCombat(CardModel card)
    {
        if (card.Owner == Owner.Player && card.Type == CardType.Attack)
            RefreshCard(card);
        return Task.CompletedTask;
    }

    public override int ModifyAttackHitCount(AttackCommand attack, int hitCount)
    {
        if (attack.Attacker != Owner || !Owner.IsMonster || attack.ModelSource is CardModel)
            return hitCount;

        ConditionalWeakTable<AttackCommand, HitCount> attacks =
            GetInternalData<ConditionalWeakTable<AttackCommand, HitCount>>();
        if (!attacks.TryGetValue(attack, out HitCount? hits))
        {
            hits = new HitCount(MultiHitKeyword.GetHitCount(DamagePerHit(attack)));
            attacks.Add(attack, hits);
            // The native builder has no setter for its base damage.
            DamagePerHit(attack) = 1m;
        }
        return (int)Math.Clamp((long)hitCount * hits.Count, 0L, int.MaxValue);
    }

    private static void RefreshAttackCards(Creature owner)
    {
        if (owner.Player?.PlayerCombatState is not { } combat)
            return;
        foreach (CardModel card in combat.AllCards)
        {
            if (card.Type == CardType.Attack)
                RefreshCard(card);
        }
    }

    private static void RefreshCard(CardModel card)
    {
        LoadoutKeywordRegistry.SynchronizeDynamicVars(card);
        NCard.FindOnTable(card)?.UpdateVisuals(card.Pile?.Type ?? PileType.None, CardPreviewMode.Normal);
    }
}
