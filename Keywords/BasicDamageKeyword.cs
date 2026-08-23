#nullable enable

namespace Loadout.Keywords;

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using BaseLib.Patches.Features;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.ValueProps;

public sealed class BasicDamageKeyword : LoadoutBasicKeywordModel
{
    public const string AmountVar = "LoadoutBasicDamage";

    private static readonly IReadOnlyList<LoadoutKeywordDynamicVarDefinition>
        VariableDefinitions =
        [
            new(
                AmountVar,
                1m,
                0,
                int.MaxValue,
                "DYNAMIC_VAR_LOADOUT_BASIC_DAMAGE",
                (name, value) => new DamageVar(name, value, ValueProp.Move))
        ];

    public static BasicDamageKeyword Instance { get; } = new();

    private BasicDamageKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.BasicDamage;

    public override string StorageKey => LoadoutKeywords.BasicDamageKey;

    public override string TitleLocKey => "LOADOUT-BASIC_DAMAGE.title";

    public override string? CardTextLocKey => "LOADOUT-BASIC_DAMAGE.cardText";

    public override IReadOnlyList<LoadoutKeywordDynamicVarDefinition> DynamicVars =>
        VariableDefinitions;

    public override async Task AfterOnPlay(
        CardModel card,
        PlayerChoiceContext choiceContext,
        CardPlay cardPlay,
        object? capturedState)
    {
        DynamicVar amount = GetAmount(card, AmountVar);
        if (amount.BaseValue <= 0m)
            return;

        Creature? target = cardPlay.Target;
        if (target is null && card.TargetType == TargetType.AnyPlayer)
            target = card.Owner.Creature;
        else if (target is null && card.TargetType == TargetType.Osty)
            target = card.Owner.Osty;

        if (target is null || target.IsDead)
            return;

        await DamageCmd.Attack(amount.BaseValue)
            .FromCard(card, cardPlay)
            .Targeting(target)
            .WithHitFx("vfx/vfx_attack_slash")
            .Execute(choiceContext);
    }
}

internal static class LoadoutBasicKeywordTargetTypePatch
{
    public static IEnumerable<MethodBase> TargetMethods()
    {
        return LoadoutBasicKeywordModel.GetPropertyGetters(
            nameof(CardModel.TargetType));
    }

    [HarmonyPostfix]
    public static void Postfix(CardModel __instance, ref TargetType __result)
    {
        if (!LoadoutKeywords.Has(__instance, LoadoutKeywords.BasicDamage))
            return;

        if (__result == TargetType.Self)
        {
            __result = CustomTargetType.Anyone;
            return;
        }

        if (__result is TargetType.AnyEnemy
            or TargetType.AnyPlayer
            or TargetType.AnyAlly
            or TargetType.Osty
            || CustomTargetType.IsCustomSingleTargetType(__result))
        {
            return;
        }

        if (__result is TargetType.None
            or TargetType.AllEnemies
            or TargetType.RandomEnemy
            or TargetType.AllAllies
            or TargetType.TargetedNoCreature
            || CustomTargetType.IsCustomMultiTargetType(__result))
        {
            __result = TargetType.AnyEnemy;
        }
    }
}
