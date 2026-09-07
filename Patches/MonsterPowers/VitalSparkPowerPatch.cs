#nullable enable

namespace Loadout.Patches.MonsterPowers;

using System;
using System.Threading.Tasks;
using HarmonyLib;
using Loadout.Services.Morphing;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Afflictions;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Powers;

[HarmonyPatch(typeof(VitalSparkPower), nameof(VitalSparkPower.AfterCardPlayed))]
public static class VitalSparkPowerPatch
{
    public const string PlayerTaintedDescriptionKey =
        "LOADOUT-TAINTED.playerInfestedPrismDescription";

    public const string PlayerTaintedExtraCardTextKey =
        "LOADOUT-TAINTED.playerInfestedPrismExtraCardText";

    public const string PlayerTaintedCombinedDescriptionKey =
        "LOADOUT-TAINTED.playerInfestedPrismCombinedDescription";

    public const string PlayerTaintedCombinedExtraCardTextKey =
        "LOADOUT-TAINTED.playerInfestedPrismCombinedExtraCardText";

    private static readonly Action<PowerModel> FlashPower =
        AccessTools.MethodDelegate<Action<PowerModel>>(
            AccessTools.DeclaredMethod(typeof(PowerModel), "Flash")
            ?? throw new MissingMethodException(typeof(PowerModel).FullName, "Flash"));

    [HarmonyPrefix]
    public static bool Prefix(
        VitalSparkPower __instance,
        PlayerChoiceContext choiceContext,
        CardPlay cardPlay,
        ref Task __result)
    {
        if (!__instance.Owner.IsPlayer
            || !BottledMonsterMorphService.IsPlayerMorphedAs<InfestedPrism>(
                __instance.Owner.Player))
        {
            return true;
        }

        __result = AfterPlayerCardPlayed(__instance, choiceContext, cardPlay);
        return false;
    }

    private static async Task AfterPlayerCardPlayed(
        VitalSparkPower power,
        PlayerChoiceContext choiceContext,
        CardPlay cardPlay)
    {
        if (cardPlay.Card.Affliction is not Tainted)
            return;

        FlashPower(power);
        await PowerCmd.Apply<TaintedPower>(
            choiceContext,
            power.CombatState.HittableEnemies,
            power.Amount,
            null,
            null);
    }

    public static bool TryGetPlayerInfestedPrismTaintedAmounts(
        AfflictionModel affliction,
        out int selfAmount,
        out int enemyAmount)
    {
        selfAmount = 0;
        enemyAmount = 0;
        if (affliction is not Tainted
            || !affliction.IsMutable
            || !affliction.HasCard)
        {
            return false;
        }

        Player? cardOwner = affliction.Card.Owner;
        if (!BottledMonsterMorphService.IsPlayerMorphedAs<InfestedPrism>(cardOwner))
            return false;

        ICombatState? combatState = affliction.Card.CombatState
                                    ?? cardOwner?.Creature.CombatState;
        if (combatState is null)
            return false;

        foreach (Creature creature in combatState.Creatures)
        {
            foreach (PowerModel power in creature.Powers)
            {
                if (power is not VitalSparkPower)
                    continue;

                if (power.Owner.IsPlayer
                    && BottledMonsterMorphService.IsPlayerMorphedAs<InfestedPrism>(
                        power.Owner.Player))
                {
                    enemyAmount += power.Amount;
                }
                else
                {
                    selfAmount += power.Amount;
                }
            }
        }

        return enemyAmount > 0;
    }
}

[HarmonyPatch]
public static class TaintedAfflictionDescriptionPatch
{
    [HarmonyPatch(
        typeof(AfflictionModel),
        nameof(AfflictionModel.DynamicDescription),
        MethodType.Getter)]
    [HarmonyPostfix]
    public static void DynamicDescriptionPostfix(
        AfflictionModel __instance,
        ref LocString __result)
    {
        if (VitalSparkPowerPatch.TryGetPlayerInfestedPrismTaintedAmounts(
                __instance,
                out int selfAmount,
                out int enemyAmount))
        {
            __result = CreateDescription(
                selfAmount > 0
                    ? VitalSparkPowerPatch.PlayerTaintedCombinedDescriptionKey
                    : VitalSparkPowerPatch.PlayerTaintedDescriptionKey,
                selfAmount,
                enemyAmount);
        }
    }

    [HarmonyPatch(
        typeof(AfflictionModel),
        nameof(AfflictionModel.DynamicExtraCardText),
        MethodType.Getter)]
    [HarmonyPostfix]
    public static void DynamicExtraCardTextPostfix(
        AfflictionModel __instance,
        ref LocString? __result)
    {
        if (VitalSparkPowerPatch.TryGetPlayerInfestedPrismTaintedAmounts(
                __instance,
                out int selfAmount,
                out int enemyAmount))
        {
            __result = CreateDescription(
                selfAmount > 0
                    ? VitalSparkPowerPatch.PlayerTaintedCombinedExtraCardTextKey
                    : VitalSparkPowerPatch.PlayerTaintedExtraCardTextKey,
                selfAmount,
                enemyAmount);
        }
    }

    private static LocString CreateDescription(
        string key,
        int selfAmount,
        int enemyAmount)
    {
        LocString description = new(AfflictionModel.locTable, key);
        description.Add("SelfAmount", selfAmount);
        description.Add("EnemyAmount", enemyAmount);
        return description;
    }
}
