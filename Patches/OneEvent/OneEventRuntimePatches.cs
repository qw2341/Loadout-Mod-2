#nullable enable

namespace Loadout.Patches.OneEvent;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using HarmonyLib;
using Loadout.Services.ContentBans;
using Loadout.Services.Events;
using Loadout.Services.OneEvent;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves.Runs;

[HarmonyPatch(typeof(Hook), nameof(Hook.ModifyNextEvent))]
internal static class OneEventModifyNextEventPatch
{
    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    internal static void Postfix(IRunState runState, ref EventModel __result)
    {
        if (OneEventModeService.TryResolveOrdinaryEvent(__result, out EventModel selected))
        {
            __result = selected;
            return;
        }

        if (ContentBanService.HasAnyBans(ContentBanKind.Event))
            __result = ContentBanEventRerollService.Resolve(runState, __result);
    }
}

[HarmonyPatch(typeof(ActModel), nameof(ActModel.PullAncient))]
internal static class OneEventPullAncientPatch
{
    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    internal static void Postfix(ref EventModel __result)
    {
        if (OneEventModeService.TryResolveAncientEvent(out EventModel selected))
        {
            __result = selected;
            return;
        }

        RunState? runState = TryGetRunState();
        if (runState is not null && ContentBanService.HasAnyBans(ContentBanKind.Event))
            __result = ContentBanEventRerollService.Resolve(runState, __result);
    }

    private static RunState? TryGetRunState()
    {
        try
        {
            return RunManager.Instance.IsInProgress ? RunManager.Instance.DebugOnlyGetState() : null;
        }
        catch
        {
            return null;
        }
    }
}

[HarmonyPatch]
internal static class OneEventOrdinaryRoomPatch
{
    internal static MethodBase TargetMethod()
    {
        return AccessTools.GetDeclaredMethods(typeof(RunManager))
                   .Single(method => method.Name == "CreateRoom"
                                     && method.GetParameters().Length == 3
                                     && method.GetParameters()[0].ParameterType == typeof(RoomType));
    }

    [HarmonyPostfix]
    internal static void Postfix(RoomType __0, MapPointType __1, AbstractModel? __2, AbstractRoom __result)
    {
        if (__0 == RoomType.Event
            && __1 != MapPointType.Ancient
            && __2 is null
            && __result is EventRoom eventRoom)
        {
            OneEventOrdinaryRoomRegistry.Mark(eventRoom);
        }
    }
}

[HarmonyPatch]
internal static class OneEventRoomEntryPatch
{
    internal static MethodBase TargetMethod()
    {
        return AccessTools.GetDeclaredMethods(typeof(RunManager))
                   .Single(method => method.Name == "EnterRoomInternal"
                                     && method.GetParameters().Length == 2
                                     && method.GetParameters()[0].ParameterType == typeof(AbstractRoom));
    }

    [HarmonyPrefix]
    internal static void Prefix(ref AbstractRoom __0, bool __1, out IDisposable? __state)
    {
        __state = null;
        if (__0 is not EventRoom room
            || !OneEventModeService.TryPrepareRoomEntry(
                room,
                __1,
                OneEventOrdinaryRoomRegistry.Contains(room),
                OneEventRestoredRoomRegistry.Contains(room),
                out EventModel selected,
                out long occurrence,
                out bool replaceRoom))
        {
            return;
        }

        if (replaceRoom)
        {
            OneEventModeService.RecordRoomReplacement(room.CanonicalEvent, selected);
            __0 = new EventRoom(selected);
        }
        __state = EventRngScope.Begin(occurrence);
    }

    [HarmonyPostfix]
    internal static void Postfix(IDisposable? __state, ref Task __result)
    {
        if (__state is not null)
            __result = DisposeAfterAsync(__result, __state);
    }

    private static async Task DisposeAfterAsync(Task nativeTask, IDisposable scope)
    {
        try
        {
            await nativeTask;
        }
        finally
        {
            scope.Dispose();
        }
    }
}

[HarmonyPatch(typeof(EventRoom), MethodType.Constructor, typeof(SerializableRoom))]
internal static class OneEventRestoredRoomPatch
{
    [HarmonyPostfix]
    internal static void Postfix(EventRoom __instance)
        => OneEventRestoredRoomRegistry.Mark(__instance);
}

internal static class OneEventRestoredRoomRegistry
{
    private static readonly ConditionalWeakTable<EventRoom, object> RestoredRooms = new();

    internal static void Mark(EventRoom room)
        => RestoredRooms.GetValue(room, static _ => new object());

    internal static bool Contains(EventRoom room)
        => RestoredRooms.TryGetValue(room, out _);
}

internal static class OneEventOrdinaryRoomRegistry
{
    private static readonly ConditionalWeakTable<EventRoom, object> OrdinaryRooms = new();

    internal static void Mark(EventRoom room)
        => OrdinaryRooms.GetValue(room, static _ => new object());

    internal static bool Contains(EventRoom room)
        => OrdinaryRooms.TryGetValue(room, out _);
}

[HarmonyPatch]
internal static class OneEventRngPatch
{
    internal static MethodBase TargetMethod()
    {
        return AccessTools.PropertySetter(typeof(EventModel), nameof(EventModel.Rng))
               ?? throw new MissingMethodException(typeof(EventModel).FullName, $"set_{nameof(EventModel.Rng)}");
    }

    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    internal static void Prefix(EventModel __instance, ref Rng __0)
    {
        if (EventRngScope.TryCreate(__instance, out Rng mixed))
            __0 = mixed;
    }
}

[HarmonyPatch(typeof(LoadRunLobby))]
internal static class OneEventLoadRunLobbyConstructorPatch
{
    internal static IEnumerable<MethodBase> TargetMethods()
        => AccessTools.GetDeclaredConstructors(typeof(LoadRunLobby));

    [HarmonyPostfix]
    internal static void Postfix(LoadRunLobby __instance)
        => OneEventModeService.RegisterLoadLobby(__instance);
}

[HarmonyPatch(typeof(LoadRunLobby), nameof(LoadRunLobby.CleanUp))]
internal static class OneEventLoadRunLobbyCleanupPatch
{
    [HarmonyPrefix]
    internal static void Prefix(LoadRunLobby __instance, bool disconnectSession)
        => OneEventModeService.UnregisterLoadLobby(__instance, disconnectSession);
}

[HarmonyPatch(typeof(RunManager), nameof(RunManager.Launch))]
internal static class OneEventRunLaunchPatch
{
    [HarmonyPrefix]
    internal static void Prefix() => OneEventModeService.PrepareRunLaunch();

    [HarmonyPostfix]
    internal static void Postfix() => OneEventModeService.OnRunLaunched();
}

[HarmonyPatch(typeof(RunManager), nameof(RunManager.CleanUp))]
internal static class OneEventRunCleanupPatch
{
    [HarmonyPrefix]
    internal static void Prefix() => OneEventModeService.OnRunCleaningUp();
}
