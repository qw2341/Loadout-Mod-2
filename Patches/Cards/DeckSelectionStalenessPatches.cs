#nullable enable

namespace Loadout.Patches.Cards;

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using Loadout.Services.Actions;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Screens.CardLibrary;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;

[HarmonyPatch(typeof(NCardGridSelectionScreen), nameof(NCardGridSelectionScreen.AfterOverlayShown))]
public static class LoadoutCardGridSelectionShownPatch
{
    [HarmonyPostfix]
    public static void Postfix(NCardGridSelectionScreen __instance)
    {
        LoadoutDeckSelectionRefreshBridge.Register(__instance);
    }
}

[HarmonyPatch(typeof(NCardGridSelectionScreen), nameof(NCardGridSelectionScreen.AfterOverlayHidden))]
public static class LoadoutCardGridSelectionHiddenPatch
{
    [HarmonyPostfix]
    public static void Postfix(NCardGridSelectionScreen __instance)
    {
        LoadoutDeckSelectionRefreshBridge.Unregister(__instance);
    }
}

[HarmonyPatch(typeof(NCardGridSelectionScreen), nameof(NCardGridSelectionScreen._ExitTree))]
public static class LoadoutCardGridSelectionExitPatch
{
    [HarmonyPrefix]
    public static void Prefix(NCardGridSelectionScreen __instance)
    {
        LoadoutDeckSelectionRefreshBridge.Unregister(__instance);
    }
}

internal static class LoadoutDeckSelectionRefreshBridge
{
    private static readonly HashSet<NCardGridSelectionScreen> Screens = [];
    private static readonly HashSet<NCardGridSelectionScreen> PendingRefreshes = [];
    private static readonly FieldInfo? GridField = AccessTools.Field(typeof(NCardGridSelectionScreen), "_grid");

    static LoadoutDeckSelectionRefreshBridge()
    {
        LoadoutRunContentChangeService.Changed += OnRunContentChanged;
    }

    public static void Register(NCardGridSelectionScreen screen)
    {
        if (IsDeckSelectionScreen(screen))
            Screens.Add(screen);
    }

    public static void Unregister(NCardGridSelectionScreen screen)
    {
        Screens.Remove(screen);
        PendingRefreshes.Remove(screen);
    }

    private static void OnRunContentChanged(LoadoutRunContentChangedEventArgs change)
    {
        if (change.Kind != LoadoutRunContentKind.Cards)
            return;

        foreach (NCardGridSelectionScreen screen in Screens.ToList())
        {
            if (!GodotObject.IsInstanceValid(screen))
            {
                Unregister(screen);
                continue;
            }

            ScheduleRefresh(screen, change);
        }
    }

    private static void ScheduleRefresh(NCardGridSelectionScreen screen, LoadoutRunContentChangedEventArgs change)
    {
        if (!PendingRefreshes.Add(screen))
            return;

        Callable.From(() =>
        {
            PendingRefreshes.Remove(screen);
            Refresh(screen, change);
        }).CallDeferred();
    }

    private static void Refresh(NCardGridSelectionScreen screen, LoadoutRunContentChangedEventArgs change)
    {
        if (!GodotObject.IsInstanceValid(screen) || !screen.IsInsideTree() || !IsDeckSelectionScreen(screen))
            return;

        FieldInfo? cardsField = FindField(screen.GetType(), "_cards");
        if (cardsField?.GetValue(screen) is not IEnumerable<CardModel> currentCards)
            return;

        List<CardModel> refreshedCards = currentCards
            .Where(card => !IsStaleAffectedDeckCard(card, change))
            .ToList();

        if (refreshedCards.Count == currentCards.Count())
            return;

        cardsField.SetValue(screen, refreshedCards);
        RemoveStaleSelectedCards(screen, refreshedCards);
        RefreshGrid(screen, refreshedCards);
        InvokeOptional(screen, "RefreshConfirmButtonVisibility");

        if (refreshedCards.Count == 0)
            InvokeOptional(screen, "CancelSelection");
    }

    private static bool IsStaleAffectedDeckCard(CardModel? card, LoadoutRunContentChangedEventArgs change)
    {
        if (card?.Owner is null || !change.AffectsPlayer(card.Owner.NetId))
            return false;

        return card.Pile?.Type != PileType.Deck || !card.Owner.Deck.Cards.Contains(card);
    }

    private static void RemoveStaleSelectedCards(NCardGridSelectionScreen screen, IReadOnlyList<CardModel> refreshedCards)
    {
        foreach (FieldInfo field in EnumerateFields(screen.GetType()))
        {
            if (!field.Name.Contains("selected", StringComparison.OrdinalIgnoreCase))
                continue;

            if (field.GetValue(screen) is not IEnumerable selectedEnumerable)
                continue;

            List<CardModel> staleCards = selectedEnumerable
                .OfType<CardModel>()
                .Where(card => !refreshedCards.Any(allowed => ReferenceEquals(allowed, card)))
                .ToList();
            if (staleCards.Count == 0)
                continue;

            if (field.GetValue(screen) is IList list)
            {
                foreach (CardModel staleCard in staleCards)
                    list.Remove(staleCard);
            }
            else
            {
                MethodInfo? removeMethod = field.FieldType.GetMethod("Remove", [typeof(CardModel)]);
                object? selectedValue = field.GetValue(screen);
                foreach (CardModel staleCard in staleCards)
                    removeMethod?.Invoke(selectedValue, [staleCard]);
            }
        }
    }

    private static void RefreshGrid(NCardGridSelectionScreen screen, IReadOnlyList<CardModel> cards)
    {
        object? grid = GridField?.GetValue(screen);
        if (grid is null)
            return;

        MethodInfo? setCards = grid.GetType().GetMethods()
            .FirstOrDefault(method => method.Name == "SetCards" && method.GetParameters().Length == 4);
        setCards?.Invoke(grid, [cards, PileType.None, new List<SortingOrders>(), Task.CompletedTask]);
    }

    private static bool IsDeckSelectionScreen(NCardGridSelectionScreen? screen)
    {
        return screen is not null
               && screen.GetType().Name.StartsWith("NDeck", StringComparison.Ordinal);
    }

    private static FieldInfo? FindField(Type type, string name)
    {
        for (Type? current = type; current is not null; current = current.BaseType)
        {
            FieldInfo? field = current.GetField(
                name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field is not null)
                return field;
        }

        return null;
    }

    private static IEnumerable<FieldInfo> EnumerateFields(Type type)
    {
        for (Type? current = type; current is not null; current = current.BaseType)
        {
            foreach (FieldInfo field in current.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                yield return field;
        }
    }

    private static void InvokeOptional(NCardGridSelectionScreen screen, string methodName)
    {
        try
        {
            AccessTools.Method(screen.GetType(), methodName)?.Invoke(screen, null);
        }
        catch (Exception exception)
        {
            GD.PushWarning($"Loadout: deck-selection refresh could not invoke {methodName}. {exception.Message}");
        }
    }
}
