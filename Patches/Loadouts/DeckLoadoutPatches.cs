#nullable enable

namespace Loadout.Patches.Loadouts;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using HarmonyLib;
using Loadout.PanelItems;
using Loadout.Services.Actions;
using Loadout.Services.Configuration;
using Loadout.Services.Loadouts;
using Loadout.UI;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
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
        if (LoadoutConfigService.EnableDeckLoadoutScreen && LoadoutPanelAccessService.CanLocalPlayerUsePanel())
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
    private static readonly FieldInfo? GridField = AccessTools.Field(typeof(NCardsViewScreen), "_grid");
    private static readonly HashSet<NDeckViewScreen> Screens = [];
    private static readonly HashSet<NDeckViewScreen> PendingRefreshes = [];

    static DeckViewRefreshService()
    {
        LoadoutRunContentChangeService.Changed += OnRunContentChanged;
        LoadoutPanelAccessService.AccessChanged += OnLoadoutPanelAccessChanged;
        LoadoutConfigService.DeckLoadoutScreenVisibilityChanged += OnLoadoutPanelAccessChanged;
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
        if (change.Kind != LoadoutRunContentKind.Cards
            || change.Mode is not (LoadoutRunContentChangeMode.Update or LoadoutRunContentChangeMode.Replace))
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

            ScheduleRefresh(screen, change);
        }
    }

    private static void OnLoadoutPanelAccessChanged()
    {
        foreach (NDeckViewScreen screen in Screens.ToList())
        {
            if (!GodotObject.IsInstanceValid(screen))
            {
                Screens.Remove(screen);
                PendingRefreshes.Remove(screen);
                continue;
            }

            if (!LoadoutConfigService.EnableDeckLoadoutScreen || !LoadoutPanelAccessService.CanLocalPlayerUsePanel())
            {
                NDeckLoadoutPanel.DetachFrom(screen);
                continue;
            }

            Player? player = PlayerField?.GetValue(screen) as Player;
            NDeckLoadoutPanel.AttachTo(screen, player);
        }
    }

    private static void ScheduleRefresh(NDeckViewScreen screen, LoadoutRunContentChangedEventArgs change)
    {
        if (!PendingRefreshes.Add(screen))
            return;

        Callable.From(() =>
        {
            PendingRefreshes.Remove(screen);
            RefreshVisibleCardVisuals(screen, change);
        }).CallDeferred();
    }

    private static void RefreshVisibleCardVisuals(NDeckViewScreen screen, LoadoutRunContentChangedEventArgs change)
    {
        if (!GodotObject.IsInstanceValid(screen) || !screen.IsInsideTree())
            return;

        try
        {
            CardPile? pile = PileField?.GetValue(screen) as CardPile;
            NCardGrid? grid = GridField?.GetValue(screen) as NCardGrid;
            if (pile is null || grid is null)
                return;

            Dictionary<(ulong OwnerNetId, int Index, string ModelId), LoadoutCardVisualRefreshKind> changedByIdentity = new();
            foreach (LoadoutChangedCard changed in change.ChangedCards)
            {
                var key = (changed.OwnerNetId, changed.Index, changed.ModelId.ToString());
                if (!changedByIdentity.TryGetValue(key, out LoadoutCardVisualRefreshKind existing)
                    || existing != LoadoutCardVisualRefreshKind.Reload)
                {
                    changedByIdentity[key] = changed.RefreshKind;
                }
            }

            foreach (NGridCardHolder holder in grid.CurrentlyDisplayedCardHolders.ToList())
            {
                CardModel? card = holder.CardModel;
                if (card?.Owner is null || !change.AffectsPlayer(card.Owner.NetId))
                    continue;

                LoadoutCardVisualRefreshKind refreshKind = change.Mode == LoadoutRunContentChangeMode.Replace
                    ? LoadoutCardVisualRefreshKind.Reload
                    : LoadoutCardVisualRefreshKind.Lightweight;
                if (change.ChangedCards.Count > 0)
                {
                    int index = FindCardIndex(card.Owner.Deck.Cards, card);
                    var key = (card.Owner.NetId, index, card.Id.ToString());
                    if (index < 0 || !changedByIdentity.TryGetValue(key, out refreshKind))
                        continue;
                }

                if (refreshKind == LoadoutCardVisualRefreshKind.Reload)
                    CardPrinter.ReloadCardVisuals(holder, card);
                else
                    holder.CardNode?.UpdateVisuals(pile.Type, CardPreviewMode.Normal);

                if (grid.IsShowingUpgrades && holder.CardModel.IsUpgradable)
                    holder.SetIsPreviewingUpgrade(true);
            }
        }
        catch (Exception exception)
        {
            GD.PushWarning($"Loadout: failed to refresh visible deck cards after loadout card update. {exception.Message}");
        }
    }

    private static int FindCardIndex(IReadOnlyList<CardModel> cards, CardModel card)
    {
        for (int index = 0; index < cards.Count; index++)
        {
            if (ReferenceEquals(cards[index], card))
                return index;
        }

        return -1;
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
        LoadoutPanelAccessService.RegisterLobby(__instance);
        LoadoutHostSharingService.RegisterLobby(__instance);
    }
}

[HarmonyPatch(typeof(StartRunLobby), nameof(StartRunLobby.CleanUp))]
public static class StartRunLobbyLoadoutSharingCleanUpPatch
{
    [HarmonyPrefix]
    public static void Prefix(StartRunLobby __instance, bool disconnectSession)
    {
        LoadoutPanelAccessService.UnregisterLobby(__instance, disconnectSession);
        LoadoutHostSharingService.UnregisterLobby(__instance, disconnectSession);
    }
}

[HarmonyPatch(typeof(RunManager), nameof(RunManager.Launch))]
public static class RunManagerLaunchLoadoutSharingPatch
{
    [HarmonyPostfix]
    public static void Postfix()
    {
        LoadoutPanelAccessService.OnRunLaunched();
        LoadoutHostSharingService.OnRunLaunched();
    }
}

[HarmonyPatch(typeof(RunManager), nameof(RunManager.CleanUp))]
public static class RunManagerCleanUpLoadoutSharingPatch
{
    [HarmonyPrefix]
    public static void Prefix()
    {
        LoadoutPanelAccessService.OnRunCleaningUp();
        LoadoutHostSharingService.OnRunCleaningUp();
    }
}
