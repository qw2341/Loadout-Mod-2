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

[HarmonyPatch(typeof(WitheringPresencePower), nameof(WitheringPresencePower.AfterCardPlayed))]
public static class WitheringPresencePowerPatch
{
    public const string PlayerDescriptionKey =
        "LOADOUT-WITHERING_PRESENCE_POWER.playerDescription";

    private const int BaseCardsLeft = 6;
    private const string CardsLeftKey = "CardsLeft";

    private static readonly Action<PowerModel> FlashPower =
        AccessTools.MethodDelegate<Action<PowerModel>>(
            AccessTools.DeclaredMethod(typeof(PowerModel), "Flash")
            ?? throw new MissingMethodException(typeof(PowerModel).FullName, "Flash"));

    private static readonly Action<PowerModel> NotifyDisplayAmountChanged =
        AccessTools.MethodDelegate<Action<PowerModel>>(
            AccessTools.DeclaredMethod(typeof(PowerModel), "InvokeDisplayAmountChanged")
            ?? throw new MissingMethodException(
                typeof(PowerModel).FullName,
                "InvokeDisplayAmountChanged"));

    [HarmonyPrefix]
    public static bool Prefix(
        WitheringPresencePower __instance,
        PlayerChoiceContext choiceContext,
        CardPlay cardPlay,
        ref Task __result)
    {
        if (!UsesPlayerAeonglassBehavior(__instance))
            return true;

        __result = AfterPlayerCardPlayed(__instance, choiceContext, cardPlay);
        return false;
    }

    public static bool UsesPlayerAeonglassBehavior(PowerModel power)
    {
        return power is WitheringPresencePower
               && power.IsMutable
               && power.Owner.IsPlayer
               && BottledMonsterMorphService.IsPlayerMorphedAs<Aeonglass>(
                   power.Owner.Player);
    }

    private static async Task AfterPlayerCardPlayed(
        WitheringPresencePower power,
        PlayerChoiceContext choiceContext,
        CardPlay cardPlay)
    {
        if (cardPlay.Card.Owner != power.Target?.Player)
            return;

        power.DynamicVars[CardsLeftKey].BaseValue--;
        NotifyDisplayAmountChanged(power);
        if (power.DynamicVars[CardsLeftKey].IntValue > 0)
            return;

        await Cmd.Wait(0.5f);
        foreach (Creature target in power.CombatState.HittableEnemies)
        {
            WitherPower? existingWither = target.GetPower<WitherPower>();
            WitherPower? appliedWither = await PowerCmd.Apply<WitherPower>(
                choiceContext,
                target,
                1,
                power.Owner,
                null);
            if (existingWither is not null
                && ReferenceEquals(existingWither, appliedWither))
            {
                appliedWither.UpgradeDamagePerStack();
            }
        }
        FlashPower(power);
        power.DynamicVars[CardsLeftKey].BaseValue = BaseCardsLeft;
        NotifyDisplayAmountChanged(power);
    }
}

[HarmonyPatch]
public static class WitheringPresencePowerDescriptionPatch
{
    [HarmonyPatch(typeof(PowerModel), nameof(PowerModel.Description), MethodType.Getter)]
    [HarmonyPostfix]
    public static void DescriptionPostfix(
        PowerModel __instance,
        ref LocString __result)
    {
        if (WitheringPresencePowerPatch.UsesPlayerAeonglassBehavior(__instance))
        {
            __result = new LocString(
                PowerModel.locTable,
                WitheringPresencePowerPatch.PlayerDescriptionKey);
        }
    }

    [HarmonyPatch(typeof(PowerModel), nameof(PowerModel.SmartDescription), MethodType.Getter)]
    [HarmonyPostfix]
    public static void SmartDescriptionPostfix(
        PowerModel __instance,
        ref LocString __result)
    {
        if (WitheringPresencePowerPatch.UsesPlayerAeonglassBehavior(__instance))
        {
            __result = new LocString(
                PowerModel.locTable,
                WitheringPresencePowerPatch.PlayerDescriptionKey);
        }
    }
}

[HarmonyPatch(typeof(WitheringPresencePower), "ExtraHoverTips", MethodType.Getter)]
public static class WitheringPresencePowerHoverTipsPatch
{
    [HarmonyPostfix]
    public static void Postfix(
        WitheringPresencePower __instance,
        ref IEnumerable<IHoverTip> __result)
    {
        if (WitheringPresencePowerPatch.UsesPlayerAeonglassBehavior(__instance))
            __result = [HoverTipFactory.FromPower<WitherPower>()];
    }
}
