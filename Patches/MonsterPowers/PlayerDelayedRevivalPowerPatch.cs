#nullable enable

namespace Loadout.Patches.MonsterPowers;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;

public static class PlayerDelayedRevivalPowerRuntime
{
    private const string ReattachIntentNodeName =
        "LoadoutReattachReviveIntent";

    private sealed class RevivalState
    {
        public bool IsWaiting { get; set; }

        public int PlayerTurnStarts { get; set; }
    }

    private static readonly ConditionalWeakTable<IllusionPower, RevivalState>
        IllusionStates = new();

    private static readonly ConditionalWeakTable<ReattachPower, RevivalState>
        ReattachStates = new();

    public static void BeginIllusionRevival(IllusionPower power)
    {
        RevivalState state = IllusionStates.GetValue(
            power,
            static _ => new RevivalState());
        state.IsWaiting = true;
        state.PlayerTurnStarts = 0;
    }

    public static void BeginReattachRevival(ReattachPower power)
    {
        RevivalState state = ReattachStates.GetValue(
            power,
            static _ => new RevivalState());
        state.IsWaiting = true;
        state.PlayerTurnStarts = 0;
        RemoveReattachIntent(power.Owner);
    }

    public static void CancelReattachRevival(ReattachPower power)
    {
        if (ReattachStates.TryGetValue(power, out RevivalState? state))
        {
            state.IsWaiting = false;
            state.PlayerTurnStarts = 0;
        }

        RemoveReattachIntent(power.Owner);
    }

    public static bool IsWaiting(IllusionPower power) =>
        IllusionStates.TryGetValue(power, out RevivalState? state)
        && state.IsWaiting;

    public static bool HasLivingReattachTeammate(ReattachPower power)
    {
        Creature owner = power.Owner;
        if (owner.CombatState is not { } combatState)
            return false;

        foreach (Creature teammate in combatState.GetTeammatesOf(owner))
        {
            if (teammate != owner
                && teammate.IsPlayer
                && teammate.IsAlive
                && teammate.HasPower<ReattachPower>())
            {
                return true;
            }
        }

        return false;
    }

    public static bool ShouldFinishPlayerDefeat(
        bool allPlayersDead,
        bool force)
    {
        if (!allPlayersDead || force)
            return allPlayersDead;

        CombatState? combatState = CombatManager.Instance.DebugOnlyGetState();
        if (combatState is null)
            return true;

        foreach (Player player in combatState.Players)
        {
            IllusionPower? illusion = player.Creature.GetPower<IllusionPower>();
            if (player.Creature.IsDead
                && illusion is not null
                && IsWaiting(illusion))
            {
                return false;
            }
        }

        return true;
    }

    public static bool HasPendingTurnStartRevival(
        IReadOnlyList<Creature> participants)
    {
        foreach (Creature creature in participants)
        {
            if (!creature.IsPlayer || !creature.IsDead)
                continue;

            if (creature.GetPower<IllusionPower>() is { } illusion
                && IsWaiting(illusion))
            {
                return true;
            }

            if (creature.GetPower<ReattachPower>() is { } reattach
                && ReattachStates.TryGetValue(
                    reattach,
                    out RevivalState? state)
                && state.IsWaiting)
            {
                return true;
            }
        }

        return false;
    }

    public static async Task ProcessPlayerTurnStart(
        Task nativeTask,
        IReadOnlyList<Creature> participants)
    {
        await nativeTask;

        foreach (Creature creature in participants)
        {
            if (!creature.IsPlayer
                || !creature.IsDead
                || creature.GetPower<IllusionPower>() is not { } illusion
                || !IllusionStates.TryGetValue(
                    illusion,
                    out RevivalState? state)
                || !state.IsWaiting)
            {
                continue;
            }

            state.IsWaiting = false;
            await CreatureCmd.Heal(
                creature,
                creature.MaxHp - creature.CurrentHp);
        }

        foreach (Creature creature in participants)
        {
            if (!creature.IsPlayer
                || creature.GetPower<ReattachPower>() is not { } reattach
                || !ReattachStates.TryGetValue(
                    reattach,
                    out RevivalState? state)
                || !state.IsWaiting)
            {
                continue;
            }

            if (creature.IsAlive)
            {
                CancelReattachRevival(reattach);
                continue;
            }

            if (!HasLivingReattachTeammate(reattach))
            {
                CancelReattachRevival(reattach);
                continue;
            }

            state.PlayerTurnStarts++;
            if (state.PlayerTurnStarts == 1)
            {
                ShowReattachIntent(creature);
                continue;
            }

            PlayReattachIntent(creature);
            await reattach.DoReattach();
            CancelReattachRevival(reattach);
        }
    }

    private static void ShowReattachIntent(Creature owner)
    {
        NCreature? creatureNode =
            NCombatRoom.Instance?.GetCreatureNode(owner);
        if (creatureNode is null
            || owner.CombatState is not { } combatState
            || creatureNode.IntentContainer.GetNodeOrNull<NIntent>(
                ReattachIntentNodeName) is not null)
        {
            return;
        }

        NIntent intent = NIntent.Create(owner.GetHashCode() * 0.01f);
        intent.Name = ReattachIntentNodeName;
        creatureNode.IntentContainer.AddChildSafely(intent);
        intent.UpdateIntent(
            new HealIntent(),
            combatState.PlayerCreatures,
            owner);
        creatureNode.IntentContainer.Modulate = Colors.White;
    }

    private static void PlayReattachIntent(Creature owner)
    {
        NIntent? intent = GetReattachIntent(owner);
        intent?.SetFrozen(true);
        intent?.PlayPerform();
    }

    private static void RemoveReattachIntent(Creature owner)
    {
        NIntent? intent = GetReattachIntent(owner);
        if (intent is null)
            return;

        intent.GetParent()?.RemoveChildSafely(intent);
        intent.QueueFreeSafely();
    }

    private static NIntent? GetReattachIntent(Creature owner) =>
        NCombatRoom.Instance
            ?.GetCreatureNode(owner)
            ?.IntentContainer
            .GetNodeOrNull<NIntent>(ReattachIntentNodeName);
}

[HarmonyPatch(typeof(IllusionPower), nameof(IllusionPower.AfterApplied))]
public static class PlayerIllusionAfterAppliedPatch
{
    [HarmonyPrefix]
    public static bool Prefix(IllusionPower __instance, ref Task __result)
    {
        if (!__instance.Owner.IsPlayer)
            return true;

        __result = Task.CompletedTask;
        return false;
    }
}

[HarmonyPatch(typeof(IllusionPower), nameof(IllusionPower.AfterDeath))]
public static class PlayerIllusionAfterDeathPatch
{
    [HarmonyPrefix]
    public static bool Prefix(
        IllusionPower __instance,
        Creature creature,
        bool wasRemovalPrevented,
        ref Task __result)
    {
        if (!__instance.Owner.IsPlayer)
            return true;

        if (!wasRemovalPrevented && creature == __instance.Owner)
        {
            PlayerDelayedRevivalPowerRuntime.BeginIllusionRevival(
                __instance);
        }

        __result = Task.CompletedTask;
        return false;
    }
}

[HarmonyPatch(typeof(IllusionPower), nameof(IllusionPower.ShouldAllowHitting))]
public static class PlayerIllusionShouldAllowHittingPatch
{
    [HarmonyPrefix]
    public static bool Prefix(
        IllusionPower __instance,
        Creature creature,
        ref bool __result)
    {
        if (!__instance.Owner.IsPlayer
            || creature != __instance.Owner
            || !PlayerDelayedRevivalPowerRuntime.IsWaiting(__instance))
        {
            return true;
        }

        __result = false;
        return false;
    }
}

[HarmonyPatch(typeof(ReattachPower), nameof(ReattachPower.AfterDeath))]
public static class PlayerReattachAfterDeathPatch
{
    [HarmonyPrefix]
    public static bool Prefix(
        ReattachPower __instance,
        Creature creature,
        bool wasRemovalPrevented,
        ref Task __result)
    {
        if (!__instance.Owner.IsPlayer
            || wasRemovalPrevented
            || creature != __instance.Owner)
        {
            return true;
        }

        if (PlayerDelayedRevivalPowerRuntime.HasLivingReattachTeammate(
                __instance))
        {
            PlayerDelayedRevivalPowerRuntime.BeginReattachRevival(
                __instance);
            return true;
        }

        PlayerDelayedRevivalPowerRuntime.CancelReattachRevival(__instance);
        __result = Task.CompletedTask;
        return false;
    }
}

[HarmonyPatch(
    typeof(Hook),
    nameof(Hook.BeforeSideTurnStart),
    typeof(ICombatState),
    typeof(CombatSide),
    typeof(IReadOnlyList<Creature>))]
public static class PlayerDelayedRevivalTurnStartPatch
{
    [HarmonyPostfix]
    public static void Postfix(
        CombatSide side,
        IReadOnlyList<Creature> participants,
        ref Task __result)
    {
        if (side == CombatSide.Player
            && PlayerDelayedRevivalPowerRuntime
                .HasPendingTurnStartRevival(participants))
        {
            __result = PlayerDelayedRevivalPowerRuntime
                .ProcessPlayerTurnStart(__result, participants);
        }
    }
}

[HarmonyPatch]
public static class PlayerIllusionDefeatDelayPatch
{
    private static FieldInfo? _forceField;

    [HarmonyTargetMethod]
    public static MethodBase TargetMethod()
    {
        MethodInfo kill = AccessTools.Method(
            typeof(CreatureCmd),
            nameof(CreatureCmd.Kill),
            [typeof(IReadOnlyCollection<Creature>), typeof(bool)])
            ?? throw new MissingMethodException(
                typeof(CreatureCmd).FullName,
                "Kill(IReadOnlyCollection<Creature>, bool)");
        MethodInfo moveNext = AccessTools.AsyncMoveNext(kill)
            ?? throw new MissingMethodException(
                kill.DeclaringType?.FullName,
                "Kill async MoveNext");
        _forceField = AccessTools.Field(moveNext.DeclaringType, "force")
            ?? throw new MissingFieldException(
                moveNext.DeclaringType?.FullName,
                "force");
        return moveNext;
    }

    [HarmonyTranspiler]
    public static IEnumerable<CodeInstruction> Transpiler(
        IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> original = instructions.ToList();
        List<int> allPlayersDeadCalls = [];
        for (int index = 0; index < original.Count; index++)
        {
            if (original[index].operand is MethodInfo method
                && method.DeclaringType == typeof(Enumerable)
                && method.Name == nameof(Enumerable.All)
                && method.IsGenericMethod
                && method.GetGenericArguments() is [Type argument]
                && argument == typeof(Player))
            {
                allPlayersDeadCalls.Add(index);
            }
        }

        if (allPlayersDeadCalls.Count != 1 || _forceField is null)
        {
            GD.PushWarning(
                "Loadout: CreatureCmd.Kill changed; player Illusion defeat delay was left disabled.");
            return original;
        }

        int insertionIndex = allPlayersDeadCalls[0] + 1;
        original.Insert(
            insertionIndex,
            new CodeInstruction(OpCodes.Ldarg_0));
        original.Insert(
            insertionIndex + 1,
            new CodeInstruction(OpCodes.Ldfld, _forceField));
        original.Insert(
            insertionIndex + 2,
            new CodeInstruction(
                OpCodes.Call,
                AccessTools.Method(
                    typeof(PlayerDelayedRevivalPowerRuntime),
                    nameof(PlayerDelayedRevivalPowerRuntime
                        .ShouldFinishPlayerDefeat))));
        return original;
    }
}
