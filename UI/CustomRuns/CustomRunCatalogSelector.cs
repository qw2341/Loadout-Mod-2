#nullable enable

namespace Loadout.UI.CustomRuns;

using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Loadout.PanelItems;
using Loadout.Services.CustomRuns.Catalog;
using Loadout.Services.CustomRuns.Models;
using Loadout.Services.Loadouts;
using Loadout.Services.Targets;
using Loadout.UI.Screens;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Relics;

public static class CustomRunCatalogSelector
{
    public static bool TryOpenCatalogAction(
        SelectionModelKind kind,
        Action<NGenericSelectScreen, IGenericSelectItem, int> activated,
        out IDisposable? session,
        out string error)
    {
        session = null;
        if (!TryFindCatalogScreen(kind, out NGenericSelectScreen screen, out error))
            return false;

        try
        {
            IDisposable lease = screen.BeginReusedSelection(
                new SelectScreenOptions { SelectionMode = SelectSelectionMode.None },
                activationOverride: (target, item) => activated(
                    target,
                    item,
                    target.GetCurrentActivationMultiplier()),
                showSelectionChrome: false,
                useCustomRunBackdrop: true);
            session = new ActionSession(screen, lease);
            NLoadoutPanelRoot.Instance!.OpenScreen(screen);
            return true;
        }
        catch (Exception exception)
        {
            session?.Dispose();
            session = null;
            error = $"Could not open the shared {kind.ToString().ToLowerInvariant()} screen: {exception.Message}";
            return false;
        }
    }

    public static bool TryOpenCatalogSelection(
        SelectionModelKind kind,
        IReadOnlyCollection<string> selectedModelIds,
        Action<IReadOnlyList<string>> changed,
        Action<NGenericSelectScreen, IGenericSelectItem, bool>? selectionToggled,
        out IDisposable? session,
        out string error)
    {
        session = null;
        if (!TryFindCatalogScreen(kind, out NGenericSelectScreen screen, out error))
            return false;

        Dictionary<string, string> modelIdByItemId = new(StringComparer.Ordinal);
        Dictionary<string, int> selectedAmounts = new(StringComparer.Ordinal);
        HashSet<string> selectedIds = new(selectedModelIds, StringComparer.OrdinalIgnoreCase);
        foreach (IGenericSelectItem item in screen.Items)
        {
            if (item.UntypedModel is not AbstractModel model)
                continue;
            string modelId = model.Id.ToString();
            modelIdByItemId[item.Id] = modelId;
            if (selectedIds.Contains(modelId) || selectedIds.Contains(model.Id.Entry))
                selectedAmounts[item.Id] = 1;
        }

        HashSet<string> current = modelIdByItemId
            .Where(pair => selectedAmounts.ContainsKey(pair.Key))
            .Select(pair => pair.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        try
        {
            IDisposable lease = screen.BeginReusedSelection(
                new SelectScreenOptions
                {
                    SelectionMode = SelectSelectionMode.Multi,
                    MinSelection = 0,
                    MaxTotalSelection = int.MaxValue,
                    MaxCopiesPerItem = 1
                },
                selectedAmounts,
                activationOverride: (target, item) =>
                {
                    bool wasSelected = target.SelectedAmounts.ContainsKey(item.Id);
                    if (wasSelected)
                        target.DeselectItem(item.Id);
                    else
                        target.SelectItem(item.Id);
                    bool isSelected = target.SelectedAmounts.ContainsKey(item.Id);
                    if (wasSelected != isSelected)
                        selectionToggled?.Invoke(target, item, isSelected);
                },
                showSelectionChrome: false,
                useCustomRunBackdrop: true,
                selectionAmountChanged: (itemId, amount) =>
                {
                    if (!modelIdByItemId.TryGetValue(itemId, out string? modelId))
                        return;
                    if (amount == 0)
                        current.Remove(modelId);
                    else
                        current.Add(modelId);
                    changed(current.OrderBy(id => id, StringComparer.Ordinal).ToList());
                });
            session = new ActionSession(screen, lease);
            NLoadoutPanelRoot.Instance!.OpenScreen(screen);
            return true;
        }
        catch (Exception exception)
        {
            session?.Dispose();
            session = null;
            error = $"Could not open the shared {kind.ToString().ToLowerInvariant()} screen: {exception.Message}";
            return false;
        }
    }

    public static bool TryOpenCatalogSingleSelection(
        SelectionModelKind kind,
        string? selectedModelId,
        Action<AbstractModel> confirmed,
        out IDisposable? session,
        out string error)
    {
        session = null;
        if (!TryFindCatalogScreen(kind, out NGenericSelectScreen screen, out error))
            return false;

        Dictionary<string, int> selectedAmounts = new(StringComparer.Ordinal);
        foreach (IGenericSelectItem item in screen.Items)
        {
            if (item.UntypedModel is AbstractModel model
                && (string.Equals(model.Id.ToString(), selectedModelId, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(model.Id.Entry, selectedModelId, StringComparison.OrdinalIgnoreCase)))
            {
                selectedAmounts[item.Id] = 1;
                break;
            }
        }

        try
        {
            IDisposable lease = screen.BeginReusedSelection(
                new SelectScreenOptions
                {
                    SelectionMode = SelectSelectionMode.Single,
                    MinSelection = 1,
                    MaxTotalSelection = 1,
                    MaxCopiesPerItem = 1
                },
                selectedAmounts,
                showSelectionChrome: true,
                useCustomRunBackdrop: true);
            ConfirmSession? activeSession = null;
            activeSession = new ConfirmSession(screen, lease, selected =>
            {
                if (selected.FirstOrDefault()?.UntypedModel is not AbstractModel model)
                    return;
                confirmed(model);
                activeSession?.Dispose();
                NLoadoutPanelRoot.Instance?.CloseTopScreen();
            });
            session = activeSession;
            NLoadoutPanelRoot.Instance!.OpenScreen(screen);
            return true;
        }
        catch (Exception exception)
        {
            session?.Dispose();
            session = null;
            error = $"Could not open the shared {kind.ToString().ToLowerInvariant()} screen: {exception.Message}";
            return false;
        }
    }

    public static bool TryOpenCatalogChoice(
        SelectionModelKind kind,
        IReadOnlyCollection<string> allowedModelIds,
        int minimum,
        int maximum,
        Action<IReadOnlyList<string>> confirmed,
        Action cancelled,
        out IDisposable? session,
        out string error)
    {
        session = null;
        if (!TryFindCatalogScreen(kind, out NGenericSelectScreen screen, out error))
            return false;

        HashSet<string> allowed = allowedModelIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<IGenericSelectItem> models = screen.Items
            .Where(item => item.UntypedModel is AbstractModel model
                && (allowed.Contains(model.Id.ToString()) || allowed.Contains(model.Id.Entry)))
            .OrderBy(item => ((AbstractModel)item.UntypedModel).Id.ToString(), StringComparer.Ordinal)
            .ToList();
        if (models.Count < minimum)
        {
            error = $"The {kind.ToString().ToLowerInvariant()} choice has only {models.Count} valid options.";
            return false;
        }

        SelectItemAdapter<IGenericSelectItem> adapter = new()
        {
            GetId = item => ((AbstractModel)item.UntypedModel).Id.ToString(),
            GetName = item => item.Name,
            GetSearchText = item => item.SearchText,
            CreateView = (item, state) =>
            {
                Control view = item.CreateView(state);
                item.SetView(view);
                return view;
            },
            PreloadResources = (item, token) => item.PreloadResources(token),
            ViewReady = (item, view) => item.NotifyViewReady(view),
            UpdateView = (item, view, state) =>
            {
                item.SetView(view);
                item.UpdateView(state);
            },
            MatchesSearch = (item, query) => item.MatchesSearch(query),
            BindActivationWithCleanup = (item, _, activate) =>
                item.TryBindActivation(activate, out Action? cleanup) ? cleanup : null
        };
        try
        {
            IDisposable configuration = screen.BeginTemporaryConfiguration(
                models,
                adapter,
                builder => builder.Options(new SelectScreenOptions
                {
                    SelectionMode = maximum == 1 ? SelectSelectionMode.Single : SelectSelectionMode.Multi,
                    MinSelection = minimum,
                    MaxTotalSelection = maximum,
                    MaxCopiesPerItem = 1
                }));
            IDisposable selection = screen.BeginReusedSelection(
                new SelectScreenOptions
                {
                    SelectionMode = maximum == 1 ? SelectSelectionMode.Single : SelectSelectionMode.Multi,
                    MinSelection = minimum,
                    MaxTotalSelection = maximum,
                    MaxCopiesPerItem = 1
                },
                showSelectionChrome: true,
                useCustomRunBackdrop: true);
            RuntimeChoiceSession? active = null;
            active = new RuntimeChoiceSession(
                screen,
                [selection, configuration],
                items =>
                {
                    confirmed(items
                        .Select(item => item.UntypedModel)
                        .OfType<IGenericSelectItem>()
                        .Select(item => ((AbstractModel)item.UntypedModel).Id.ToString())
                        .ToList());
                    active?.Dispose();
                    NLoadoutPanelRoot.Instance?.CloseTopScreen();
                },
                cancelled);
            session = active;
            NLoadoutPanelRoot.Instance!.OpenScreen(screen);
            return true;
        }
        catch (Exception exception)
        {
            session?.Dispose();
            session = null;
            error = $"Could not open the shared {kind.ToString().ToLowerInvariant()} choice: {exception.Message}";
            return false;
        }
    }

    public static bool TryOpenOwnedCardAction(
        string screenNameFragment,
        IReadOnlyList<LoadoutOwnedItem<CardModel>> cards,
        Func<NGenericSelectScreen, LoadoutOwnedItem<CardModel>, int, IReadOnlyList<LoadoutOwnedItem<CardModel>>?> activated,
        out IDisposable? session,
        out string error)
    {
        return TryOpenOwnedCardActions(
            screenNameFragment,
            cards,
            activated,
            alternateActivated: null,
            out session,
            out error);
    }

    public static bool TryOpenOwnedCardActions(
        string screenNameFragment,
        IReadOnlyList<LoadoutOwnedItem<CardModel>> cards,
        Func<NGenericSelectScreen, LoadoutOwnedItem<CardModel>, int, IReadOnlyList<LoadoutOwnedItem<CardModel>>?> activated,
        Func<NGenericSelectScreen, LoadoutOwnedItem<CardModel>, int, IReadOnlyList<LoadoutOwnedItem<CardModel>>?>? alternateActivated,
        out IDisposable? session,
        out string error)
    {
        session = null;
        if (!TryFindNamedScreen(screenNameFragment, out NGenericSelectScreen screen, out error))
            return false;

        SelectItemAdapter<LoadoutOwnedItem<CardModel>> adapter = null!;
        adapter = new SelectItemAdapter<LoadoutOwnedItem<CardModel>>
        {
            GetId = CommonHelpers.OwnedSlotItemId,
            GetName = item => CardPrinter.FormatCardTitle(item.Model),
            GetSearchText = item => $"{item.Model.Id} {CardPrinter.FormatCardTitle(item.Model)} {item.Model.GetDescriptionForPile(PileType.Deck)}",
            CapturePreloadResourcePaths = item => item.Model.AllPortraitPaths.ToArray(),
            CreateView = (item, state) => CardPrinter.CreateCardGridItem(item.Model, state, PileType.Deck),
            ViewReady = (item, view) => CardPrinter.RefreshCardVisuals(view, item.Model, PileType.Deck),
            UpdateView = (item, view, state) =>
            {
                CardPrinter.ForceRefreshCardVisuals(view, item.Model, PileType.Deck);
                CardPrinter.UpdateCardGridItem(view, state);
            },
            BindActivationWithCleanup = (boundItem, view, activate) => alternateActivated is null
                ? CardPrinter.BindCardActivationWithCleanup(view, activate)
                : CardPrinter.BindCardActivationWithCleanup(
                    view,
                    activate,
                    () => ActivateOwnedCard(
                        screen,
                        view,
                        boundItem,
                        alternateActivated,
                        adapter))
        };

        try
        {
            IDisposable configurationLease = screen.BeginTemporaryConfiguration(
                cards,
                adapter,
                builder =>
                {
                    builder.Options(new SelectScreenOptions { SelectionMode = SelectSelectionMode.None });
                    builder.Materialization(SelectMaterializationMode.Lazy);
                    builder.Layout(5, NCard.defaultSize * NCardHolder.smallScale, 32, 40, paddingTop: 200f);
                });
            IDisposable selectionLease;
            try
            {
                selectionLease = screen.BeginReusedSelection(
                    new SelectScreenOptions { SelectionMode = SelectSelectionMode.None },
                    activationOverride: (target, item) =>
                    {
                        if (item.UntypedModel is not LoadoutOwnedItem<CardModel> card)
                            return;
                        IReadOnlyList<LoadoutOwnedItem<CardModel>>? next = activated(
                            target,
                            card,
                            target.GetCurrentActivationMultiplier());
                        if (next is not null && target.IsScreenActive)
                            target.RefreshItemsPreservingViews(next, adapter, animateRelayout: true, updateExistingViews: true);
                    },
                    showSelectionChrome: false,
                    useCustomRunBackdrop: true);
            }
            catch
            {
                configurationLease.Dispose();
                throw;
            }

            session = new ActionSession(screen, selectionLease, configurationLease);
            NLoadoutPanelRoot.Instance!.OpenScreen(screen);
            return true;
        }
        catch (Exception exception)
        {
            session?.Dispose();
            session = null;
            error = $"Could not open the Custom Run card inventory: {exception.Message}";
            return false;
        }
    }

    public static bool TryOpenOwnedRelicAction(
        string screenNameFragment,
        IReadOnlyList<LoadoutOwnedItem<RelicModel>> relics,
        Func<NGenericSelectScreen, LoadoutOwnedItem<RelicModel>, int, IReadOnlyList<LoadoutOwnedItem<RelicModel>>?> activated,
        out IDisposable? session,
        out string error)
    {
        return TryOpenOwnedRelicAction(
            screenNameFragment,
            relics,
            activated,
            rightClickOnly: false,
            out session,
            out error);
    }

    public static bool TryOpenOwnedRelicRightAction(
        string screenNameFragment,
        IReadOnlyList<LoadoutOwnedItem<RelicModel>> relics,
        Func<NGenericSelectScreen, LoadoutOwnedItem<RelicModel>, int, IReadOnlyList<LoadoutOwnedItem<RelicModel>>?> activated,
        out IDisposable? session,
        out string error)
    {
        return TryOpenOwnedRelicAction(
            screenNameFragment,
            relics,
            activated,
            rightClickOnly: true,
            out session,
            out error);
    }

    private static bool TryOpenOwnedRelicAction(
        string screenNameFragment,
        IReadOnlyList<LoadoutOwnedItem<RelicModel>> relics,
        Func<NGenericSelectScreen, LoadoutOwnedItem<RelicModel>, int, IReadOnlyList<LoadoutOwnedItem<RelicModel>>?> activated,
        bool rightClickOnly,
        out IDisposable? session,
        out string error)
    {
        session = null;
        if (!TryFindNamedScreen(screenNameFragment, out NGenericSelectScreen screen, out error))
            return false;

        SelectItemAdapter<LoadoutOwnedItem<RelicModel>> adapter = null!;
        adapter = new SelectItemAdapter<LoadoutOwnedItem<RelicModel>>
        {
            GetId = CommonHelpers.OwnedSlotItemId,
            GetName = item => CommonHelpers.FormatRelicTitle(item.Model),
            GetSearchText = item => $"{item.Model.Id} {CommonHelpers.FormatRelicTitle(item.Model)} {item.Model.DynamicDescription.GetFormattedText()}",
            CapturePreloadResourcePaths = item => [item.Model.IconPath],
            CreateView = (item, _) => NLoadoutPanel.CreateOwnedRelicGridItem(item.Model),
            UpdateView = (item, view, _) => RefreshRelicView(view, item.Model),
            BindActivationWithCleanup = (boundItem, view, activate) => rightClickOnly
                ? RelicModifier.BindRightClickWithCleanup(
                    view,
                    () => ActivateOwnedRelic(screen, view, boundItem, activated, adapter))
                : LoadoutBag.BindRelicActivationWithCleanup(view, activate)
        };

        try
        {
            IDisposable configurationLease = screen.BeginTemporaryConfiguration(
                relics,
                adapter,
                builder =>
                {
                    builder.Options(new SelectScreenOptions { SelectionMode = SelectSelectionMode.None });
                    builder.Materialization(SelectMaterializationMode.Lazy);
                    builder.Layout(10, new Vector2(68f, 68f), 32, 32);
                });
            IDisposable selectionLease;
            try
            {
                selectionLease = screen.BeginReusedSelection(
                    new SelectScreenOptions { SelectionMode = SelectSelectionMode.None },
                    activationOverride: (target, item) =>
                    {
                        if (item.UntypedModel is not LoadoutOwnedItem<RelicModel> relic)
                            return;
                        IReadOnlyList<LoadoutOwnedItem<RelicModel>>? next = activated(
                            target,
                            relic,
                            target.GetCurrentActivationMultiplier());
                        if (next is not null && target.IsScreenActive)
                            target.RefreshItemsPreservingViews(next, adapter, animateRelayout: true, updateExistingViews: true);
                    },
                    showSelectionChrome: false,
                    useCustomRunBackdrop: true);
            }
            catch
            {
                configurationLease.Dispose();
                throw;
            }

            session = new ActionSession(screen, selectionLease, configurationLease);
            NLoadoutPanelRoot.Instance!.OpenScreen(screen);
            return true;
        }
        catch (Exception exception)
        {
            session?.Dispose();
            session = null;
            error = $"Could not open the Custom Run relic inventory: {exception.Message}";
            return false;
        }
    }

    public static bool TryOpenPowerSelection(
        IReadOnlyDictionary<string, int> powers,
        Action<IReadOnlyDictionary<string, int>> changed,
        out IDisposable? session,
        out string error)
    {
        session = null;
        if (!TryFindCatalogScreen(SelectionModelKind.Power, out NGenericSelectScreen screen, out error))
            return false;

        Dictionary<string, int> selected = [];
        Dictionary<string, string> modelIdByItemId = [];
        foreach (IGenericSelectItem item in screen.Items)
        {
            if (item.UntypedModel is not PowerModel power)
                continue;
            string modelId = power.Id.ToString();
            modelIdByItemId[item.Id] = modelId;
            int amount = powers.FirstOrDefault(pair =>
                string.Equals(pair.Key, modelId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(pair.Key, power.Id.Entry, StringComparison.OrdinalIgnoreCase)).Value;
            if (amount != 0)
                selected[item.Id] = amount;
        }

        Dictionary<string, int> current = powers
            .Where(pair => pair.Value != 0)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        IDisposable lease = screen.BeginReusedSelection(
            new SelectScreenOptions
            {
                SelectionMode = SelectSelectionMode.Multi,
                MinSelection = 0,
                MaxTotalSelection = int.MaxValue,
                MaxCopiesPerItem = int.MaxValue
            },
            selected,
            allowSignedAmounts: true,
            showSelectionChrome: false,
            useCustomRunBackdrop: true,
            selectionAmountChanged: (itemId, amount) =>
            {
                if (!modelIdByItemId.TryGetValue(itemId, out string? modelId))
                    return;
                if (amount == 0)
                    current.Remove(modelId);
                else
                    current[modelId] = amount;
                changed(new Dictionary<string, int>(current, StringComparer.OrdinalIgnoreCase));
            });
        session = new ActionSession(screen, lease);
        NLoadoutPanelRoot.Instance!.OpenScreen(screen);
        return true;
    }

    public static bool TryOpenMorphSelection(
        string? selectedModelId,
        Action<string?> changed,
        out IDisposable? session,
        out string error)
    {
        _ = selectedModelId;
        session = null;
        if (!TryFindNamedScreen("BottledMonster_Alternate", out NGenericSelectScreen screen, out error))
        {
            error = "The initialized Morph Selection screen is not available.";
            return false;
        }

        NLoadoutPanelRoot root = NLoadoutPanelRoot.Instance!;
        ActionSession? activeSession = null;
        IDisposable lease = screen.BeginReusedSelection(
            new SelectScreenOptions { SelectionMode = SelectSelectionMode.None },
            activationOverride: (_, item) =>
            {
                if (!BottledMonster.TryGetMorphOptionModel(item.UntypedModel, out AbstractModel? model))
                    return;
                activeSession?.Dispose();
                root.CloseTopScreen();
                changed(model?.Id.ToString());
            },
            showSelectionChrome: false,
            useCustomRunBackdrop: true);
        activeSession = new ActionSession(screen, lease);
        session = activeSession;
        root.OpenScreen(screen);
        return true;
    }

    private static void ActivateOwnedCard(
        NGenericSelectScreen screen,
        Control sourceView,
        LoadoutOwnedItem<CardModel> fallback,
        Func<NGenericSelectScreen, LoadoutOwnedItem<CardModel>, int, IReadOnlyList<LoadoutOwnedItem<CardModel>>?> activated,
        SelectItemAdapter<LoadoutOwnedItem<CardModel>> adapter)
    {
        LoadoutOwnedItem<CardModel> item = fallback;
        if (screen.TryGetItemForView(sourceView, out IGenericSelectItem current)
            && current.UntypedModel is LoadoutOwnedItem<CardModel> currentCard)
        {
            item = currentCard;
        }
        IReadOnlyList<LoadoutOwnedItem<CardModel>>? next = activated(
            screen,
            item,
            screen.GetCurrentActivationMultiplier());
        if (next is not null && screen.IsScreenActive)
            screen.RefreshItemsPreservingViews(next, adapter, animateRelayout: true, updateExistingViews: true);
    }

    private static void ActivateOwnedRelic(
        NGenericSelectScreen screen,
        Control sourceView,
        LoadoutOwnedItem<RelicModel> fallback,
        Func<NGenericSelectScreen, LoadoutOwnedItem<RelicModel>, int, IReadOnlyList<LoadoutOwnedItem<RelicModel>>?> activated,
        SelectItemAdapter<LoadoutOwnedItem<RelicModel>> adapter)
    {
        LoadoutOwnedItem<RelicModel> item = fallback;
        if (screen.TryGetItemForView(sourceView, out IGenericSelectItem current)
            && current.UntypedModel is LoadoutOwnedItem<RelicModel> currentRelic)
        {
            item = currentRelic;
        }
        IReadOnlyList<LoadoutOwnedItem<RelicModel>>? next = activated(
            screen,
            item,
            screen.GetCurrentActivationMultiplier());
        if (next is not null && screen.IsScreenActive)
            screen.RefreshItemsPreservingViews(next, adapter, animateRelayout: true, updateExistingViews: true);
    }

    private static bool TryFindCatalogScreen(
        SelectionModelKind kind,
        out NGenericSelectScreen screen,
        out string error)
    {
        screen = null!;
        error = string.Empty;
        if (!TryGetAvailableScreens(out IReadOnlyList<NLoadoutPanel.SelectScreenPreloadEntry> screens, out error))
            return false;

        screen = screens
            .Select(entry => entry.Screen)
            .Where(candidate => !candidate.IsScreenActive && candidate.Items.Count > 0)
            .Where(candidate => candidate.Items.All(item => item.UntypedModel is AbstractModel model
                && CustomRunCatalogService.IsModelKind(model, kind)))
            .OrderByDescending(candidate => candidate.Items.Count)
            .FirstOrDefault()!;
        if (screen is null)
        {
            error = $"No initialized {kind.ToString().ToLowerInvariant()} catalog screen is available.";
            return false;
        }
        return true;
    }

    private static bool TryFindNamedScreen(
        string nameFragment,
        out NGenericSelectScreen screen,
        out string error)
    {
        screen = null!;
        if (!TryGetAvailableScreens(out IReadOnlyList<NLoadoutPanel.SelectScreenPreloadEntry> screens, out error))
            return false;
        screen = screens
            .Where(entry => entry.Name.ToString().Contains(nameFragment, StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.Screen)
            .FirstOrDefault(candidate => !candidate.IsScreenActive)!;
        if (screen is null)
        {
            error = $"The initialized {nameFragment} screen is not available.";
            return false;
        }
        return true;
    }

    private static bool TryGetAvailableScreens(
        out IReadOnlyList<NLoadoutPanel.SelectScreenPreloadEntry> screens,
        out string error)
    {
        screens = [];
        error = string.Empty;
        NLoadoutPanel? panel = NLoadoutPanel.Instance;
        if (NLoadoutPanelRoot.Instance is null || panel is null || !panel.LoadoutItemsInitialized)
        {
            error = "The shared Loadout screens are not ready yet.";
            return false;
        }
        screens = panel.GetSelectScreensForPreload();
        return true;
    }

    private static void RefreshRelicView(Control view, RelicModel model)
    {
        if (!CommonHelpers.TryFindDescendantOrSelf(view, out NRelicBasicHolder holder)
            || holder.Relic is not { } relic)
            return;
        relic.Model = model;
        if (relic.IsNodeReady())
            model.UpdateTexture(relic.Icon);
    }

    private sealed class ActionSession : IDisposable
    {
        private readonly NGenericSelectScreen _screen;
        private readonly IDisposable[] _leases;
        private bool _done;

        public ActionSession(NGenericSelectScreen screen, params IDisposable[] leases)
        {
            _screen = screen;
            _leases = leases;
            screen.Cancelled += Dispose;
            screen.ScreenClosed += OnClosed;
        }

        public void Dispose()
        {
            if (_done)
                return;
            _done = true;
            _screen.Cancelled -= Dispose;
            _screen.ScreenClosed -= OnClosed;
            foreach (IDisposable lease in _leases)
                lease.Dispose();
        }

        private void OnClosed() => Callable.From(Dispose).CallDeferred();
    }

    private sealed class ConfirmSession : IDisposable
    {
        private readonly NGenericSelectScreen _screen;
        private readonly IDisposable _lease;
        private readonly Action<IReadOnlyList<IGenericSelectItem>> _confirmed;
        private bool _done;

        public ConfirmSession(
            NGenericSelectScreen screen,
            IDisposable lease,
            Action<IReadOnlyList<IGenericSelectItem>> confirmed)
        {
            _screen = screen;
            _lease = lease;
            _confirmed = confirmed;
            screen.Confirmed += confirmed;
            screen.Cancelled += Dispose;
            screen.ScreenClosed += OnClosed;
        }

        public void Dispose()
        {
            if (_done)
                return;
            _done = true;
            _screen.Confirmed -= _confirmed;
            _screen.Cancelled -= Dispose;
            _screen.ScreenClosed -= OnClosed;
            _lease.Dispose();
        }

        private void OnClosed() => Callable.From(Dispose).CallDeferred();
    }

    private sealed class RuntimeChoiceSession : IDisposable
    {
        private readonly NGenericSelectScreen _screen;
        private readonly IDisposable[] _leases;
        private readonly Action<IReadOnlyList<IGenericSelectItem>> _confirmed;
        private readonly Action _cancelled;
        private bool _done;

        public RuntimeChoiceSession(
            NGenericSelectScreen screen,
            IDisposable[] leases,
            Action<IReadOnlyList<IGenericSelectItem>> confirmed,
            Action cancelled)
        {
            _screen = screen;
            _leases = leases;
            _confirmed = confirmed;
            _cancelled = cancelled;
            screen.Confirmed += confirmed;
            screen.Cancelled += OnCancelled;
            screen.ScreenClosed += OnClosed;
        }

        public void Dispose()
        {
            if (_done)
                return;
            _done = true;
            _screen.Confirmed -= _confirmed;
            _screen.Cancelled -= OnCancelled;
            _screen.ScreenClosed -= OnClosed;
            foreach (IDisposable lease in _leases)
                lease.Dispose();
        }

        private void OnCancelled()
        {
            _cancelled();
            Dispose();
        }

        private void OnClosed()
        {
            if (_done)
                return;
            Callable.From(FinishClosed).CallDeferred();
        }

        private void FinishClosed()
        {
            if (_done)
                return;
            _cancelled();
            Dispose();
        }
    }
}
