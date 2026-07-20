#nullable enable

namespace Loadout.Patches.TildeKey;

using BaseLib.Hooks;
using BaseLib.Patches.Hooks;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using Loadout.Services.Actions;
using Loadout.Services.Compatibility;
using Loadout.Services.TildeKey;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Relics;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.ValueProps;

/// <summary>
/// BaseLib's max-hand-size helper walks the complete run deck and every combat
/// pile and allocates a modifier list each time it is called. Native draw calls
/// that helper several times per card. When Loadout is the only implementation
/// of the BaseLib modifier interface, avoid subscribing a model to every native
/// run hook and apply the exact same modifier directly at BaseLib's API boundary.
/// If another assembly supplies a modifier, retain BaseLib's full ordered hook
/// iteration so inter-mod behavior remains unchanged.
/// </summary>
internal static class TildeKeyMaxHandSizeFastPath
{
    private static bool _initialized;
    private static bool _hasExternalModifier;

    public static void Warmup()
    {
        if (_initialized)
            return;

        foreach (Mod mod in ModManager.GetLoadedMods())
        {
            foreach (Assembly assembly in Sts2Compatibility.GetModAssemblies(mod))
            {
                if (!ContainsExternalModifier(assembly))
                    continue;

                _hasExternalModifier = true;
                break;
            }

            if (_hasExternalModifier)
                break;
        }

        _initialized = true;
    }

    public static bool CanApplyDirectly => _initialized && !_hasExternalModifier;

    private static bool ContainsExternalModifier(Assembly assembly)
    {
        try
        {
            foreach (Type type in assembly.GetTypes())
            {
                if (type == typeof(IMaxHandSizeModifier)
                    || type == typeof(LoadoutMaxHandSizeModifier)
                    || !typeof(IMaxHandSizeModifier).IsAssignableFrom(type))
                {
                    continue;
                }

                return true;
            }

            return false;
        }
        catch
        {
            // Unknown types must keep BaseLib's compatibility path.
            return true;
        }
    }
}

[HarmonyPatch(
    typeof(MaxHandSizePatch),
    nameof(MaxHandSizePatch.GetMaxHandSize),
    typeof(Player),
    typeof(int))]
public static class TildeKeyMaxHandSizeFastPathPatch
{
    [HarmonyPrefix]
    public static bool Prefix(Player player, int baseLimit, ref int __result)
    {
        if (!TildeKeyMaxHandSizeFastPath.CanApplyDirectly)
            return true;

        __result = Math.Max(0, TildeKeyStateService.ApplyMaxHandSizeModifier(player, baseLimit));
        return false;
    }
}

public static class TildeKeyCreatureLockBoundaryPatch
{
    public static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (string name in new[]
                 {
                     nameof(Creature.LoseHpInternal), nameof(Creature.HealInternal),
                     nameof(Creature.SetCurrentHpInternal), nameof(Creature.SetMaxHpInternal),
                     nameof(Creature.GainBlockInternal), nameof(Creature.LoseBlockInternal)
                 })
        {
            MethodInfo? method = AccessTools.Method(typeof(Creature), name);
            if (method is not null) yield return method;
        }
    }

    public static void Postfix(Creature __instance) => TildeKeyStateService.ReassertCreatureLocks(__instance);
}

public static class TildeKeyPlayerLockBoundaryPatch
{
    public static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (string name in new[] { nameof(Player.Gold), nameof(Player.MaxEnergy), nameof(Player.BaseOrbSlotCount) })
        {
            MethodInfo? setter = AccessTools.PropertySetter(typeof(Player), name);
            if (setter is not null) yield return setter;
        }
    }

    public static void Postfix(Player __instance) => TildeKeyStateService.ReassertPlayerLocks(__instance);
}

public static class TildeKeyCombatStatLockBoundaryPatch
{
    public static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (string name in new[] { nameof(PlayerCombatState.Energy), nameof(PlayerCombatState.Stars) })
        {
            MethodInfo? setter = AccessTools.PropertySetter(typeof(PlayerCombatState), name);
            if (setter is not null) yield return setter;
        }
    }

    public static void Postfix(PlayerCombatState __instance) => TildeKeyStateService.ReassertCombatLocks(__instance);
}

public static class TildeKeyExtraStatLockBoundaryPatch
{
    public static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (string name in new[]
                 {
                     nameof(ExtraPlayerFields.CardShopRemovalsUsed), nameof(ExtraPlayerFields.WongoPoints),
                     "DamageDealt", "DebuffsApplied"
                 })
        {
            MethodInfo? setter = AccessTools.PropertySetter(typeof(ExtraPlayerFields), name);
            if (setter is not null) yield return setter;
        }
    }

    public static void Postfix(ExtraPlayerFields __instance) => TildeKeyStateService.ReassertExtraFieldLocks(__instance);
}

[HarmonyPatch(typeof(NMapScreen), "_Ready")]
public static class TildeKeyMapReadyPatch
{
    [HarmonyPostfix]
    public static void Postfix(NMapScreen __instance)
        => TildeKeyStateService.RefreshMapDebugTravel(__instance);
}

[HarmonyPatch(typeof(RunManager), nameof(RunManager.EnterMapCoord))]
public static class TildeKeyVisitedMapReentryPatch
{
    [HarmonyPrefix]
    public static void Prefix(RunManager __instance, MapCoord coord)
        => TildeKeyStateService.PrepareMapCoordForDebugReentry(__instance, coord);
}

public static class TildeKeyRelicCounterLockBoundaryPatch
{
    public static MethodBase TargetMethod() =>
        AccessTools.Method(typeof(RelicModel), "InvokeDisplayAmountChanged")
        ?? throw new MissingMethodException(typeof(RelicModel).FullName, "InvokeDisplayAmountChanged");

    public static void Postfix(RelicModel __instance) => TildeKeyStateService.ReassertRelicCounterLock(__instance);
}

internal static class TildeKeyDynamicLockPatches
{
    private const string HarmonyId = "Loadout.TildeKey.DynamicLocks";
    private static readonly Harmony Harmony = new(HarmonyId);
    private static int _configuration;

    public static void Configure(bool creature, bool player, bool combat, bool extra, bool relic)
    {
        int next = (creature ? 1 : 0)
                   | (player ? 2 : 0)
                   | (combat ? 4 : 0)
                   | (extra ? 8 : 0)
                   | (relic ? 16 : 0);
        if (next == _configuration)
            return;

        Harmony.UnpatchAll(HarmonyId);
        _configuration = next;
        if (creature) PatchAll(TildeKeyCreatureLockBoundaryPatch.TargetMethods(), typeof(TildeKeyCreatureLockBoundaryPatch));
        if (player) PatchAll(TildeKeyPlayerLockBoundaryPatch.TargetMethods(), typeof(TildeKeyPlayerLockBoundaryPatch));
        if (combat) PatchAll(TildeKeyCombatStatLockBoundaryPatch.TargetMethods(), typeof(TildeKeyCombatStatLockBoundaryPatch));
        if (extra) PatchAll(TildeKeyExtraStatLockBoundaryPatch.TargetMethods(), typeof(TildeKeyExtraStatLockBoundaryPatch));
        if (relic)
        {
            Harmony.Patch(
                TildeKeyRelicCounterLockBoundaryPatch.TargetMethod(),
                postfix: new HarmonyMethod(typeof(TildeKeyRelicCounterLockBoundaryPatch), nameof(TildeKeyRelicCounterLockBoundaryPatch.Postfix)));
        }
    }

    public static void Reset() => Configure(false, false, false, false, false);

    private static void PatchAll(IEnumerable<MethodBase> targets, Type patchType)
    {
        HarmonyMethod postfix = new(patchType, "Postfix");
        foreach (MethodBase target in targets)
            Harmony.Patch(target, postfix: postfix);
    }
}

public static class TildeKeyModifyHandDrawPatch
{
    [HarmonyPrefix]
    public static void Prefix(Player player, ref decimal originalCardCount)
    {
        if (TildeKeyStateService.TryGetDrawPerTurnOverride(player, out int drawPerTurn))
            originalCardCount = drawPerTurn;
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

public static class TildeKeyModifyDamagePatch
{
    private static readonly System.Threading.AsyncLocal<int> SuppressDepth = new();

    internal static string GetPostfixMethodName() => Sts2Compatibility.UsesNewModifyDamage
        ? nameof(NewPostfix)
        // 0.107-only compatibility fallback; remove or replace when 0.107 support is dropped.
        : nameof(LegacyPostfix);

    // Maintained newer API shape; CardPlay is preserved when the newer hook is available.
    [HarmonyPostfix]
    public static void NewPostfix(
        IRunState runState,
        ICombatState? combatState,
        Creature? target,
        Creature? dealer,
        ValueProp props,
        CardModel? cardSource,
        CardPlay? cardPlay,
        ModifyDamageHookType modifyDamageHookType,
        CardPreviewMode previewMode,
        ref decimal __result)
    {
        ApplyMultiplier(
            runState,
            combatState,
            target,
            dealer,
            props,
            cardSource,
            cardPlay,
            modifyDamageHookType,
            previewMode,
            ref __result);
    }

    // 0.107-only compatibility fallback; remove or replace when 0.107 support is dropped.
    [HarmonyPostfix]
    public static void LegacyPostfix(
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
        ApplyMultiplier(
            runState,
            combatState,
            target,
            dealer,
            props,
            cardSource,
            cardPlay: null,
            modifyDamageHookType,
            previewMode,
            ref __result);
    }

    private static void ApplyMultiplier(
        IRunState runState,
        ICombatState? combatState,
        Creature? target,
        Creature? dealer,
        ValueProp props,
        CardModel? cardSource,
        CardPlay? cardPlay,
        ModifyDamageHookType modifyDamageHookType,
        CardPreviewMode previewMode,
        ref decimal result)
    {
        if (SuppressDepth.Value > 0 || !modifyDamageHookType.HasFlag(ModifyDamageHookType.Multiplicative))
            return;

        if (!TryGetMultiplier(target, dealer, out int multiplier) || multiplier == 100)
            return;

        decimal multiplied = result * multiplier / 100m;
        SuppressDepth.Value++;
        try
        {
            result = Sts2Compatibility.ModifyDamage(
                runState,
                combatState,
                target,
                dealer,
                multiplied,
                props,
                cardSource,
                cardPlay,
                ModifyDamageHookType.Cap,
                previewMode);
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
/// Dynamically installed only while a draw-to-limit toggle exists. Patching the
/// generated completion boundary avoids replacing every PlayCardAction Task.
/// </summary>
public static class TildeKeyDrawTillHandLimitAfterPlayCardMoveNextPatch
{
    private static FieldInfo? _stateField;
    private static FieldInfo? _actionField;
    private static FieldInfo? _builderField;

    public static MethodInfo TargetMethod()
    {
        MethodInfo execute = AccessTools.Method(typeof(PlayCardAction), "ExecuteAction")
                             ?? throw new MissingMethodException(typeof(PlayCardAction).FullName, "ExecuteAction");
        Type stateMachine = execute.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType
                            ?? throw new MissingMemberException(typeof(PlayCardAction).FullName, "ExecuteAction async state machine");
        _stateField = AccessTools.Field(stateMachine, "<>1__state")
                      ?? throw new MissingFieldException(stateMachine.FullName, "<>1__state");
        _actionField = AccessTools.Field(stateMachine, "<>4__this")
                       ?? throw new MissingFieldException(stateMachine.FullName, "<>4__this");
        _builderField = AccessTools.Field(stateMachine, "<>t__builder")
                        ?? throw new MissingFieldException(stateMachine.FullName, "<>t__builder");
        return AccessTools.Method(stateMachine, "MoveNext")
               ?? throw new MissingMethodException(stateMachine.FullName, "MoveNext");
    }

    public static void Prefix(object __instance, out int __state)
    {
        __state = _stateField?.GetValue(__instance) as int? ?? int.MinValue;
    }

    public static void Postfix(object __instance, int __state)
    {
        if (__state == -2
            || (_stateField?.GetValue(__instance) as int?) != -2
            || _builderField?.GetValue(__instance) is not AsyncTaskMethodBuilder builder
            || !builder.Task.IsCompletedSuccessfully
            || _actionField?.GetValue(__instance) is not PlayCardAction action)
        {
            return;
        }

        TildeKeyStateService.RequestDrawTillHandLimitForLocalPlayer(action.Player);
    }
}

internal static class TildeKeyDynamicDrawPatches
{
    private const string HarmonyId = "Loadout.TildeKey.DynamicDraw";
    private static readonly Harmony Harmony = new(HarmonyId);
    private static int _configuration;

    public static void Configure(bool drawPerTurn, bool drawTillHandLimit)
    {
        int next = (drawPerTurn ? 1 : 0) | (drawTillHandLimit ? 2 : 0);
        if (next == _configuration)
            return;

        Harmony.UnpatchAll(HarmonyId);
        _configuration = next;
        if (drawPerTurn)
        {
            Harmony.Patch(
                AccessTools.Method(typeof(Hook), nameof(Hook.ModifyHandDraw))
                ?? throw new MissingMethodException(typeof(Hook).FullName, nameof(Hook.ModifyHandDraw)),
                prefix: new HarmonyMethod(typeof(TildeKeyModifyHandDrawPatch), nameof(TildeKeyModifyHandDrawPatch.Prefix)));
        }

        if (drawTillHandLimit)
        {
            Harmony.Patch(
                TildeKeyDrawTillHandLimitAfterPlayCardMoveNextPatch.TargetMethod(),
                prefix: new HarmonyMethod(typeof(TildeKeyDrawTillHandLimitAfterPlayCardMoveNextPatch), nameof(TildeKeyDrawTillHandLimitAfterPlayCardMoveNextPatch.Prefix)),
                postfix: new HarmonyMethod(typeof(TildeKeyDrawTillHandLimitAfterPlayCardMoveNextPatch), nameof(TildeKeyDrawTillHandLimitAfterPlayCardMoveNextPatch.Postfix)));
        }
    }

    public static void Reset() => Configure(false, false);
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
