#nullable enable

namespace Loadout.Patches.TildeKey;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
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

/// <summary>
/// Replaces the game's hard-coded CardPile.MaxCardsInHand reads with the
/// hand-size override for the player currently executing the patched method.
///
/// The context is resolved from the synchronized method's arguments or from
/// fields on its async state machine. It never assumes the local player, so
/// host and guest may safely have different hand limits.
/// </summary>
[HarmonyPatch]
public static class TildeKeyStaticHandLimitPatches
{
    public static IEnumerable<MethodBase> TargetMethods()
    {
        return TildeKeyHandLimitPatchRuntime.GetTargets(isStatic: true);
    }

    [HarmonyPrefix]
    public static void Prefix(object[] __args, out Player? __state)
    {
        TildeKeyHandLimitPatchRuntime.EnterContext(null, __args, out __state);
    }

    [HarmonyPostfix]
    public static void Postfix(Player? __state)
    {
        TildeKeyHandLimitPatchRuntime.RestoreContext(__state);
    }

    [HarmonyFinalizer]
    public static Exception? Finalizer(Exception? __exception, Player? __state)
    {
        TildeKeyHandLimitPatchRuntime.RestoreContext(__state);
        return __exception;
    }

    [HarmonyTranspiler]
    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        return TildeKeyHandLimitPatchRuntime.ReplaceMaxCardsInHandCalls(instructions);
    }
}

/// <summary>
/// Instance targets include compiler-generated async MoveNext methods. Their
/// captured Player/CardModel/CardPile fields identify the correct player.
/// </summary>
[HarmonyPatch]
public static class TildeKeyInstanceHandLimitPatches
{
    public static IEnumerable<MethodBase> TargetMethods()
    {
        return TildeKeyHandLimitPatchRuntime.GetTargets(isStatic: false);
    }

    [HarmonyPrefix]
    public static void Prefix(object __instance, object[] __args, out Player? __state)
    {
        TildeKeyHandLimitPatchRuntime.EnterContext(__instance, __args, out __state);
    }

    [HarmonyPostfix]
    public static void Postfix(Player? __state)
    {
        TildeKeyHandLimitPatchRuntime.RestoreContext(__state);
    }

    [HarmonyFinalizer]
    public static Exception? Finalizer(Exception? __exception, Player? __state)
    {
        TildeKeyHandLimitPatchRuntime.RestoreContext(__state);
        return __exception;
    }

    [HarmonyTranspiler]
    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        return TildeKeyHandLimitPatchRuntime.ReplaceMaxCardsInHandCalls(instructions);
    }
}

internal static class TildeKeyHandLimitPatchRuntime
{
    private static readonly object SyncRoot = new();
    private static readonly Dictionary<Type, FieldInfo[]> ContextFieldCache = new();
    private static readonly Lazy<IReadOnlyList<MethodInfo>> Targets = new(FindTargets);

    [ThreadStatic]
    private static Player? _contextPlayer;

    public static IEnumerable<MethodBase> GetTargets(bool isStatic)
    {
        return Targets.Value.Where(method => method.IsStatic == isStatic);
    }

    public static void EnterContext(
        object? instance,
        object[]? arguments,
        out Player? previousPlayer)
    {
        previousPlayer = _contextPlayer;
        _contextPlayer = ResolvePlayer(instance, arguments) ?? previousPlayer;
    }

    public static void RestoreContext(Player? previousPlayer)
    {
        _contextPlayer = previousPlayer;
    }

    public static IEnumerable<CodeInstruction> ReplaceMaxCardsInHandCalls(
        IEnumerable<CodeInstruction> instructions)
    {
        MethodInfo? original = AccessTools.PropertyGetter(
            typeof(CardPile),
            nameof(CardPile.MaxCardsInHand));
        MethodInfo? replacement = AccessTools.Method(
            typeof(TildeKeyHandLimitPatchRuntime),
            nameof(GetContextualMaxCardsInHand));

        foreach (CodeInstruction instruction in instructions)
        {
            if (original is not null
                && replacement is not null
                && (instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt)
                && Equals(instruction.operand, original))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = replacement;
            }

            yield return instruction;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int GetContextualMaxCardsInHand()
    {
        return _contextPlayer is { } player
            ? TildeKeyStateService.GetEffectiveHandSize(player)
            : CardPile.MaxCardsInHand;
    }

    private static IReadOnlyList<MethodInfo> FindTargets()
    {
        MethodInfo? maxCardsGetter = AccessTools.PropertyGetter(
            typeof(CardPile),
            nameof(CardPile.MaxCardsInHand));
        if (maxCardsGetter is null)
        {
            GD.PushWarning("TildeKey: could not resolve CardPile.MaxCardsInHand.");
            return [];
        }

        List<(Type? Type, string MethodName)> candidates =
        [
            (typeof(CardPileCmd), nameof(CardPileCmd.Add)),
            (typeof(CardPileCmd), nameof(CardPileCmd.Draw)),
            (typeof(CardPileCmd), "CheckIfDrawIsPossibleAndShowThoughtBubbleIfNot"),
            (typeof(CombatManager), "SetupPlayerTurn"),
            (AccessTools.TypeByName("MegaCrit.Sts2.Core.DevConsole.ConsoleCommands.CardConsoleCmd"), "Process"),
            (AccessTools.TypeByName("MegaCrit.Sts2.Core.Models.Cards.Anointed"), "OnPlay"),
            (AccessTools.TypeByName("MegaCrit.Sts2.Core.Models.Cards.CrashLanding"), "OnPlay"),
            (AccessTools.TypeByName("MegaCrit.Sts2.Core.Models.Cards.Dredge"), "OnPlay"),
            (AccessTools.TypeByName("MegaCrit.Sts2.Core.Models.Cards.NeowsFury"), "OnPlay"),
            (AccessTools.TypeByName("MegaCrit.Sts2.Core.Models.Cards.Pillage"), "OnPlay"),
            (AccessTools.TypeByName("MegaCrit.Sts2.Core.Models.Cards.Scrawl"), "OnPlay")
        ];

        HashSet<MethodInfo> targets = [];
        foreach ((Type? type, string methodName) in candidates)
        {
            if (type is null)
                continue;

            MethodInfo[] methods;
            try
            {
                methods = type.GetMethods(
                    BindingFlags.Instance
                    | BindingFlags.Static
                    | BindingFlags.Public
                    | BindingFlags.NonPublic
                    | BindingFlags.DeclaredOnly);
            }
            catch (Exception exception)
            {
                GD.PushWarning(
                    $"TildeKey: failed inspecting hand-limit target type '{type.FullName}'. " +
                    exception.Message);
                continue;
            }

            foreach (MethodInfo method in methods.Where(candidate =>
                         string.Equals(candidate.Name, methodName, StringComparison.Ordinal)))
            {
                if (CallsMethod(method, maxCardsGetter.MetadataToken))
                    targets.Add(method);

                AsyncStateMachineAttribute? stateMachine =
                    method.GetCustomAttribute<AsyncStateMachineAttribute>();
                MethodInfo? moveNext = stateMachine?.StateMachineType.GetMethod(
                    nameof(IAsyncStateMachine.MoveNext),
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (moveNext is not null
                    && CallsMethod(moveNext, maxCardsGetter.MetadataToken))
                {
                    targets.Add(moveNext);
                }
            }
        }

        if (targets.Count == 0)
        {
            GD.PushWarning(
                "TildeKey: no current CardPile.MaxCardsInHand call sites were found. " +
                "The game may have changed its hand-limit implementation.");
        }

        return targets.ToList();
    }

    private static bool CallsMethod(MethodInfo method, int metadataToken)
    {
        byte[]? il;
        try
        {
            il = method.GetMethodBody()?.GetILAsByteArray();
        }
        catch
        {
            return false;
        }

        if (il is null || il.Length < 5)
            return false;

        for (int i = 0; i <= il.Length - 5; i++)
        {
            if (il[i] is not 0x28 and not 0x6F)
                continue;

            if (BitConverter.ToInt32(il, i + 1) == metadataToken)
                return true;
        }

        return false;
    }

    private static Player? ResolvePlayer(object? instance, object[]? arguments)
    {
        if (arguments is not null)
        {
            foreach (object? argument in arguments)
            {
                if (TryResolvePlayer(argument, out Player? player))
                    return player;
            }
        }

        if (TryResolvePlayer(instance, out Player? directPlayer))
            return directPlayer;

        if (instance is null)
            return null;

        foreach (FieldInfo field in GetContextFields(instance.GetType()))
        {
            object? value;
            try
            {
                value = field.GetValue(instance);
            }
            catch
            {
                continue;
            }

            if (TryResolvePlayer(value, out Player? player))
                return player;
        }

        return null;
    }

    private static bool TryResolvePlayer(object? value, out Player? player)
    {
        switch (value)
        {
            case Player directPlayer:
                player = directPlayer;
                return true;

            case CardModel card:
                try
                {
                    player = card.Owner;
                    return player is not null;
                }
                catch
                {
                    break;
                }

            case CardPile pile:
                player = ResolvePlayerOwningHandPile(pile);
                return player is not null;
        }

        player = null;
        return false;
    }

    private static Player? ResolvePlayerOwningHandPile(CardPile pile)
    {
        RunState? runState;
        try
        {
            runState = RunManager.Instance.IsInProgress
                ? RunManager.Instance.DebugOnlyGetState()
                : null;
        }
        catch
        {
            return null;
        }

        if (runState is null)
            return null;

        foreach (Player player in runState.Players)
        {
            try
            {
                if (ReferenceEquals(PileType.Hand.GetPile(player), pile))
                    return player;
            }
            catch
            {
                // Combat piles may not be initialized for every player yet.
            }
        }

        return null;
    }

    private static FieldInfo[] GetContextFields(Type type)
    {
        lock (SyncRoot)
        {
            if (ContextFieldCache.TryGetValue(type, out FieldInfo[]? cached))
                return cached;

            FieldInfo[] fields = type
                .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(field =>
                    typeof(Player).IsAssignableFrom(field.FieldType)
                    || typeof(CardModel).IsAssignableFrom(field.FieldType)
                    || typeof(CardPile).IsAssignableFrom(field.FieldType))
                .ToArray();

            ContextFieldCache[type] = fields;
            return fields;
        }
    }
}
