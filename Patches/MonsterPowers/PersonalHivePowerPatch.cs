#nullable enable

namespace Loadout.Patches.MonsterPowers;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using HarmonyLib;
using Loadout.Powers;
using Loadout.Services.Morphing;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;

[HarmonyPatch(typeof(PersonalHivePower), nameof(PersonalHivePower.AfterDamageReceived))]
public static class PersonalHivePowerPatch
{
    public const string PlayerDescriptionKey =
        "LOADOUT-PERSONAL_HIVE_POWER.playerDescription";

    private static readonly Action<PowerModel> FlashPower =
        AccessTools.MethodDelegate<Action<PowerModel>>(
            AccessTools.DeclaredMethod(typeof(PowerModel), "Flash")
            ?? throw new MissingMethodException(typeof(PowerModel).FullName, "Flash"));

    [HarmonyPrefix]
    public static bool Prefix(
        PersonalHivePower __instance,
        PlayerChoiceContext choiceContext,
        Creature target,
        Creature? dealer,
        ValueProp props,
        CardModel? cardSource,
        ref Task __result)
    {
        if (!__instance.Owner.IsPlayer
            || target != __instance.Owner
            || dealer is null
            || dealer.Player is not null)
        {
            return true;
        }

        __result = ApplyDazedAfterHit(
            __instance,
            choiceContext,
            target,
            props,
            dealer,
            cardSource);
        return false;
    }

    public static bool UsesPlayerEntomancerBehavior(PowerModel power)
    {
        return power is PersonalHivePower
               && power.IsMutable
               && power.Owner.IsPlayer
               && BottledMonsterMorphService.IsPlayerMorphedAs<Entomancer>(
                   power.Owner.Player);
    }

    private static async Task ApplyDazedAfterHit(
        PersonalHivePower power,
        PlayerChoiceContext choiceContext,
        Creature target,
        ValueProp props,
        Creature? dealer,
        CardModel? cardSource)
    {
        Creature owner = power.Owner;
        if (!UsesPlayerEntomancerBehavior(power)
            || target != owner
            || dealer is null
            || !dealer.IsMonster
            || dealer.Side == owner.Side
            || dealer.IsDead
            || power.Amount <= 0
            || !props.IsPoweredAttack())
        {
            return;
        }

        FlashPower(power);
        await PowerCmd.Apply<DazedPower>(
            choiceContext,
            dealer,
            power.Amount,
            owner,
            cardSource);
    }
}

[HarmonyPatch]
public static class PersonalHivePowerDescriptionPatch
{
    [HarmonyPatch(typeof(PowerModel), nameof(PowerModel.Description), MethodType.Getter)]
    [HarmonyPostfix]
    public static void DescriptionPostfix(
        PowerModel __instance,
        ref LocString __result)
    {
        if (PersonalHivePowerPatch.UsesPlayerEntomancerBehavior(__instance))
        {
            __result = new LocString(
                PowerModel.locTable,
                PersonalHivePowerPatch.PlayerDescriptionKey);
        }
    }

    [HarmonyPatch(typeof(PowerModel), nameof(PowerModel.SmartDescription), MethodType.Getter)]
    [HarmonyPostfix]
    public static void SmartDescriptionPostfix(
        PowerModel __instance,
        ref LocString __result)
    {
        if (PersonalHivePowerPatch.UsesPlayerEntomancerBehavior(__instance))
        {
            __result = new LocString(
                PowerModel.locTable,
                PersonalHivePowerPatch.PlayerDescriptionKey);
        }
    }
}

[HarmonyPatch(typeof(PersonalHivePower), "ExtraHoverTips", MethodType.Getter)]
public static class PersonalHivePowerHoverTipsPatch
{
    [HarmonyPostfix]
    public static void Postfix(
        PersonalHivePower __instance,
        ref IEnumerable<IHoverTip> __result)
    {
        if (PersonalHivePowerPatch.UsesPlayerEntomancerBehavior(__instance))
            __result = [HoverTipFactory.FromPower<DazedPower>()];
    }
}
