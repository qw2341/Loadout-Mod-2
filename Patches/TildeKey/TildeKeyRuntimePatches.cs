#nullable enable

namespace Loadout.Patches.TildeKey;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using Loadout.Services.Actions;
using Loadout.Services.TildeKey;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Relics;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.ValueProps;

[HarmonyPatch(typeof(Hook), nameof(Hook.ModifyHandDraw))]
public static class TildeKeyModifyHandDrawPatch
{
    [HarmonyPostfix]
    public static void Postfix(Player player, ref decimal __result)
    {
        if (TildeKeyStateService.TryGetDrawPerTurnOverride(player, out int drawPerTurn))
            __result = drawPerTurn;
    }
}

[HarmonyPatch(typeof(PlayerCombatState), nameof(PlayerCombatState.ResetEnergy))]
public static class TildeKeyInfiniteEnergyResetPatch
{
    [HarmonyPostfix]
    public static void Postfix(PlayerCombatState __instance)
    {
        TildeKeyStateService.RestoreInfiniteEnergyAfterReset(__instance);
    }
}

[HarmonyPatch(typeof(PlayerCombatState), nameof(PlayerCombatState.LoseEnergy))]
public static class TildeKeyInfiniteEnergyLossPatch
{
    [HarmonyPrefix]
    public static bool Prefix(PlayerCombatState __instance)
    {
        return !TildeKeyStateService.IsInfiniteEnergyEnabled(__instance);
    }
}

[HarmonyPatch(typeof(PlayerCombatState), nameof(PlayerCombatState.LoseStars))]
public static class TildeKeyInfiniteStarsLossPatch
{
    [HarmonyPrefix]
    public static bool Prefix(PlayerCombatState __instance)
    {
        return !TildeKeyStateService.IsInfiniteEnergyEnabled(__instance);
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.ModifyDamage))]
public static class TildeKeyModifyDamagePatch
{
    private static readonly System.Threading.AsyncLocal<int> SuppressDepth = new();

    [HarmonyPostfix]
    public static void Postfix(
        IRunState runState,
        ICombatState? combatState,
        Creature? target,
        Creature? dealer,
        ValueProp props,
        CardModel? cardSource,
        ModifyDamageHookType modifyDamageHookType,
        CardPreviewMode previewMode,
        ref decimal __result)
    {
        if (SuppressDepth.Value > 0 || !modifyDamageHookType.HasFlag(ModifyDamageHookType.Multiplicative))
            return;

        if (!TryGetMultiplier(target, dealer, out int multiplier) || multiplier == 100)
            return;

        decimal multiplied = __result * multiplier / 100m;
        SuppressDepth.Value++;
        try
        {
            __result = Hook.ModifyDamage(
                runState,
                combatState,
                target,
                dealer,
                multiplied,
                props,
                cardSource,
                ModifyDamageHookType.Cap,
                previewMode,
                out _);
        }
        finally
        {
            SuppressDepth.Value = Math.Max(0, SuppressDepth.Value - 1);
        }
    }

    private static bool TryGetMultiplier(Creature? target, Creature? dealer, out int multiplier)
    {
        Player? dealerOwner = dealer?.Player ?? dealer?.PetOwner;
        if (dealerOwner is not null && TildeKeyStateService.TryGetPlayerDamageMultiplier(dealerOwner, out multiplier))
            return true;

        Player? targetOwner = target?.Player ?? target?.PetOwner;
        if (dealer?.IsMonster == true
            && targetOwner is not null
            && TildeKeyStateService.TryGetEnemyDamageMultiplier(targetOwner, out multiplier))
        {
            return true;
        }

        multiplier = 100;
        return false;
    }
}


/// <summary>
/// Queues the refill only after a real synchronized PlayCardAction completes.
///
/// Do not draw directly from CardModel.OnPlayWrapper: that method is also used
/// by nested/automatic card plays, and its PlayerChoiceContext belongs to the
/// card action that is already finishing. A direct CardPileCmd.Draw there makes
/// every peer independently calculate and execute an extra hook-heavy draw
/// inside the original action.
///
/// Requesting a separate networked draw action gives the refill its own action
/// boundary, player-choice context, hook IDs, and post-action checksum. The
/// IsLocalPlayer gate in RequestDrawTillHandLimitForLocalPlayer ensures exactly
/// one peer requests it; the host then broadcasts the same action to everyone.
/// </summary>
[HarmonyPatch]
public static class TildeKeyDrawTillHandLimitAfterPlayCardActionPatch
{
    public static MethodBase TargetMethod()
    {
        return AccessTools.Method(typeof(PlayCardAction), "ExecuteAction")
               ?? throw new MissingMethodException(typeof(PlayCardAction).FullName, "ExecuteAction");
    }

    [HarmonyPostfix]
    public static void Postfix(PlayCardAction __instance, ref Task __result)
    {
        __result = RequestRefillAfterPlayActionAsync(__result, __instance.Player);
    }

    private static async Task RequestRefillAfterPlayActionAsync(Task original, Player owner)
    {
        await original;
        TildeKeyStateService.RequestDrawTillHandLimitForLocalPlayer(owner);
    }
}


[HarmonyPatch(typeof(NClickableControl), nameof(NClickableControl._GuiInput))]
public static class TildeKeyRelicCounterInputPatch
{
    [HarmonyPrefix]
    public static bool Prefix(NClickableControl __instance, InputEvent inputEvent)
    {
        if (__instance is not NRelicInventoryHolder holder
            || inputEvent is not InputEventMouseButton mouseButton
            || !TildeKeyStateService.IsScrollRelicCounterEnabledForLocalPlayer())
        {
            return true;
        }

        RelicModel relic = holder.Relic.Model;
        if (!TildeKeyStateService.TryGetRelicCounterMember(relic, out string counterMember))
            return true;

        if (mouseButton.Pressed && mouseButton.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
        {
            __instance.AcceptEvent();
            if (!TildeKeyStateService.IsRelicCounterLocked(relic, counterMember))
            {
                int delta = mouseButton.ButtonIndex == MouseButton.WheelUp ? 1 : -1;
                LoadoutImmediateMutationService.RequestTildeRelicCounterDelta(relic, delta, counterMember);
            }

            return false;
        }

        if (mouseButton.ButtonIndex == MouseButton.Middle)
        {
            __instance.AcceptEvent();
            if (mouseButton.Pressed)
                return false;

            bool isLocked = TildeKeyStateService.IsRelicCounterLocked(relic, counterMember);
            if (!isLocked && !TildeKeyStateService.TryGetRelicCounterValue(relic, counterMember, out _))
                return false;

            int value = TildeKeyStateService.TryGetRelicCounterValue(relic, counterMember, out int currentValue)
                ? currentValue
                : 0;
            LoadoutImmediateMutationService.RequestTildeRelicCounterLock(relic, counterMember, value, !isLocked);
            return false;
        }

        return true;
    }
}

[HarmonyPatch(typeof(NRelicInventoryHolder), nameof(NRelicInventoryHolder._Ready))]
public static class TildeKeyRelicCounterBadgeReadyPatch
{
    [HarmonyPostfix]
    public static void Postfix(NRelicInventoryHolder __instance)
    {
        TildeKeyStateService.RefreshRelicCounterLockBadge(__instance);
    }
}

[HarmonyPatch(typeof(NRelicInventoryHolder), "OnModelChanged")]
public static class TildeKeyRelicCounterBadgeModelChangedPatch
{
    [HarmonyPostfix]
    public static void Postfix(NRelicInventoryHolder __instance)
    {
        TildeKeyStateService.RefreshRelicCounterLockBadge(__instance);
    }
}

[HarmonyPatch(typeof(CreatureCmd), nameof(CreatureCmd.Damage), typeof(PlayerChoiceContext), typeof(IEnumerable<Creature>), typeof(decimal), typeof(ValueProp), typeof(Creature), typeof(CardModel))]
public static class TildeKeyGodmodeDamagePatch
{
    [HarmonyPrefix]
    public static bool Prefix(ref IEnumerable<Creature> targets, ValueProp props, ref Task<IEnumerable<DamageResult>> __result)
    {
        List<Creature> targetList = targets.ToList();
        if (targetList.Count == 0)
            return true;

        List<Creature> unprotectedTargets = targetList
            .Where(target => !TildeKeyStateService.IsGodmodeProtected(target))
            .ToList();
        if (unprotectedTargets.Count == targetList.Count)
            return true;

        if (unprotectedTargets.Count == 0)
        {
            __result = Task.FromResult<IEnumerable<DamageResult>>(Array.Empty<DamageResult>());
            return false;
        }

        targets = unprotectedTargets;
        return true;
    }
}

[HarmonyPatch(typeof(Creature), nameof(Creature.LoseHpInternal))]
public static class TildeKeyGodmodeLoseHpPatch
{
    [HarmonyPrefix]
    public static bool Prefix(Creature __instance, ValueProp props, ref DamageResult __result)
    {
        if (!TildeKeyStateService.IsGodmodeProtected(__instance))
            return true;

        __result = new DamageResult(__instance, props);
        return false;
    }
}

[HarmonyPatch(typeof(CreatureCmd), "KillWithoutCheckingWinCondition")]
public static class TildeKeyGodmodeKillPatch
{
    [HarmonyPrefix]
    public static bool Prefix(Creature creature, ref Task __result)
    {
        if (!TildeKeyStateService.IsGodmodeProtected(creature))
            return true;

        __result = Task.CompletedTask;
        return false;
    }
}
