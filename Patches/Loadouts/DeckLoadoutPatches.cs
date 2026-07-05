#nullable enable

namespace Loadout.Patches.Loadouts;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using HarmonyLib;
using Loadout.Services.Actions;
using Loadout.Services.Loadouts;
using Loadout.UI;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Runs;

[HarmonyPatch(typeof(NDeckViewScreen), nameof(NDeckViewScreen._Ready))]
public static class DeckViewScreenLoadoutPanelReadyPatch
{
    private static readonly FieldInfo? PlayerField = AccessTools.Field(typeof(NDeckViewScreen), "_player");

    [HarmonyPostfix]
    public static void Postfix(NDeckViewScreen __instance)
    {
        Player? player = PlayerField?.GetValue(__instance) as Player;
        DeckViewRefreshService.Register(__instance);
        NDeckLoadoutPanel.AttachTo(__instance, player);
    }
}

[HarmonyPatch(typeof(NDeckViewScreen), nameof(NDeckViewScreen._ExitTree))]
public static class DeckViewScreenLoadoutPanelExitPatch
{
    [HarmonyPrefix]
    public static void Prefix(NDeckViewScreen __instance)
    {
        DeckViewRefreshService.Unregister(__instance);
        NDeckLoadoutPanel.DetachFrom(__instance);
    }
}

internal static class DeckViewRefreshService
{
    private static readonly FieldInfo? PlayerField = AccessTools.Field(typeof(NDeckViewScreen), "_player");
    private static readonly FieldInfo? PileField = AccessTools.Field(typeof(NDeckViewScreen), "_pile");
    private static readonly FieldInfo? CardsField = AccessTools.Field(typeof(NCardsViewScreen), "_cards");
    private static readonly MethodInfo? DisplayCardsMethod = AccessTools.Method(typeof(NDeckViewScreen), "DisplayCards");
    private static readonly HashSet<NDeckViewScreen> Screens = [];
    private static readonly HashSet<NDeckViewScreen> PendingRefreshes = [];

    static DeckViewRefreshService()
    {
        LoadoutRunContentChangeService.Changed += OnRunContentChanged;
    }

    public static void Register(NDeckViewScreen screen)
    {
        Screens.Add(screen);
    }

    public static void Unregister(NDeckViewScreen screen)
    {
        Screens.Remove(screen);
        PendingRefreshes.Remove(screen);
    }

    private static void OnRunContentChanged(LoadoutRunContentChangedEventArgs change)
    {
        if (change.Kind != LoadoutRunContentKind.Cards)
            return;

        foreach (NDeckViewScreen screen in Screens.ToList())
        {
            if (!GodotObject.IsInstanceValid(screen))
            {
                Screens.Remove(screen);
                PendingRefreshes.Remove(screen);
                continue;
            }

            Player? player = PlayerField?.GetValue(screen) as Player;
            if (player is not null && !change.AffectsPlayer(player.NetId))
                continue;

            ScheduleRefresh(screen);
        }
    }

    private static void ScheduleRefresh(NDeckViewScreen screen)
    {
        if (!PendingRefreshes.Add(screen))
            return;

        Callable.From(() =>
        {
            PendingRefreshes.Remove(screen);
            Refresh(screen);
        }).CallDeferred();
    }

    private static void Refresh(NDeckViewScreen screen)
    {
        if (!GodotObject.IsInstanceValid(screen) || !screen.IsInsideTree())
            return;

        try
        {
            CardPile? pile = PileField?.GetValue(screen) as CardPile;
            if (pile is null)
                return;

            CardsField?.SetValue(screen, pile.Cards.ToList());
            DisplayCardsMethod?.Invoke(screen, null);
        }
        catch (Exception exception)
        {
            GD.PushWarning($"Loadout: failed to refresh deck view after loadout card change. {exception.Message}");
        }
    }
}

[HarmonyPatch(typeof(StartRunLobby))]
public static class StartRunLobbyLoadoutSharingConstructorPatch
{
    public static System.Collections.Generic.IEnumerable<MethodBase> TargetMethods()
    {
        return AccessTools.GetDeclaredConstructors(typeof(StartRunLobby));
    }

    [HarmonyPostfix]
    public static void Postfix(StartRunLobby __instance)
    {
        LoadoutHostSharingService.RegisterLobby(__instance);
    }
}

[HarmonyPatch(typeof(StartRunLobby), nameof(StartRunLobby.CleanUp))]
public static class StartRunLobbyLoadoutSharingCleanUpPatch
{
    [HarmonyPrefix]
    public static void Prefix(StartRunLobby __instance, bool disconnectSession)
    {
        LoadoutHostSharingService.UnregisterLobby(__instance, disconnectSession);
    }
}

[HarmonyPatch(typeof(RunManager), nameof(RunManager.Launch))]
public static class RunManagerLaunchLoadoutSharingPatch
{
    [HarmonyPostfix]
    public static void Postfix()
    {
        LoadoutHostSharingService.OnRunLaunched();
    }
}

[HarmonyPatch(typeof(RunManager), nameof(RunManager.CleanUp))]
public static class RunManagerCleanUpLoadoutSharingPatch
{
    [HarmonyPrefix]
    public static void Prefix()
    {
        LoadoutHostSharingService.OnRunCleaningUp();
    }
}
