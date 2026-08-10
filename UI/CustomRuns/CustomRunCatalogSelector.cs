#nullable enable

namespace Loadout.UI.CustomRuns;

using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Loadout.Services.CustomRuns.Catalog;
using Loadout.Services.CustomRuns.Models;
using Loadout.UI.Screens;
using Loadout.PanelItems;
using MegaCrit.Sts2.Core.Models;

public static class CustomRunCatalogSelector
{
    public static bool TryOpen(
        SelectionSpec selection,
        Action<IReadOnlyList<string>> confirmed,
        out IDisposable? session,
        out string error,
        bool decrementOnActivate = false)
    {
        session = null;
        error = string.Empty;
        NLoadoutPanelRoot? root = NLoadoutPanelRoot.Instance;
        NLoadoutPanel? panel = NLoadoutPanel.Instance;
        if (root is null || panel is null || !panel.LoadoutItemsInitialized)
        {
            error = "The shared Loadout catalog screens are not ready yet.";
            return false;
        }

        int catalogCount = CustomRunCatalogService.GetCatalog(selection.Kind).Count;
        NGenericSelectScreen? screen = panel.GetSelectScreensForPreload()
            .Select(entry => entry.Screen)
            .Where(candidate => !candidate.IsScreenActive)
            .Where(candidate => candidate.Items.Count > 0)
            .Where(candidate => candidate.Items.All(item =>
                item.UntypedModel is AbstractModel model
                && CustomRunCatalogService.IsModelKind(model, selection.Kind)))
            .OrderByDescending(candidate => candidate.Items.Count == catalogCount)
            .ThenByDescending(candidate => candidate.Items.Count)
            .FirstOrDefault();
        if (screen is null)
        {
            error = $"No initialized {selection.Kind.ToString().ToLowerInvariant()} catalog screen is available.";
            return false;
        }

        Dictionary<string, int> selectedAmounts = [];
        foreach (string modelId in selection.FixedModelIds)
        {
            IGenericSelectItem? item = screen.Items.FirstOrDefault(candidate =>
                candidate.UntypedModel is AbstractModel model
                && (string.Equals(model.Id.ToString(), modelId, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(model.Id.Entry, modelId, StringComparison.OrdinalIgnoreCase)));
            if (item is not null)
                selectedAmounts[item.Id] = selectedAmounts.GetValueOrDefault(item.Id) + 1;
        }

        SelectScreenOptions options = new()
        {
            SelectionMode = SelectSelectionMode.Multi,
            MinSelection = decrementOnActivate ? 0 : 1,
            MaxTotalSelection = GetMaximumSelection(selection.Kind),
            MaxCopiesPerItem = GetMaximumCopies(selection.Kind)
        };

        try
        {
            Action<NGenericSelectScreen, IGenericSelectItem>? activationOverride = decrementOnActivate
                ? static (target, item) => target.SelectItem(
                    item.Id,
                    Math.Max(0, target.SelectedAmounts.GetValueOrDefault(item.Id) - target.GetCurrentActivationMultiplier()))
                : null;
            IDisposable selectionLease = screen.BeginReusedSelection(
                options,
                selectedAmounts,
                visibilityPredicateOverride: decrementOnActivate
                    ? item => selectedAmounts.ContainsKey(item.Id)
                    : null,
                activationOverride: activationOverride);
            session = new SelectorSession(screen, selectionLease, confirmed);
            root.OpenScreen(screen);
            return true;
        }
        catch (Exception exception)
        {
            session?.Dispose();
            session = null;
            error = $"Could not open the shared {selection.Kind.ToString().ToLowerInvariant()} selector: {exception.Message}";
            return false;
        }
    }

    public static bool TryChooseExisting(
        SelectionModelKind kind,
        IReadOnlyCollection<string> allowedModelIds,
        Action<string> confirmed,
        out IDisposable? session,
        out string error)
    {
        HashSet<string> allowed = allowedModelIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!TryFindScreen(kind, out NGenericSelectScreen? screen, out error))
        {
            session = null;
            return false;
        }

        SelectScreenOptions options = new()
        {
            SelectionMode = SelectSelectionMode.Single,
            MinSelection = 1,
            MaxTotalSelection = 1,
            MaxCopiesPerItem = 1
        };
        IDisposable lease = screen.BeginReusedSelection(
            options,
            visibilityPredicateOverride: item => item.UntypedModel is AbstractModel model
                && (allowed.Contains(model.Id.ToString()) || allowed.Contains(model.Id.Entry)));
        session = new SelectorSession(screen, lease, items =>
        {
            if (items.Count > 0)
                confirmed(items[0]);
        });
        NLoadoutPanelRoot.Instance!.OpenScreen(screen);
        return true;
    }

    public static bool TryOpenPowerSelection(
        IReadOnlyDictionary<string, int> powers,
        Action<IReadOnlyDictionary<string, int>> confirmed,
        out IDisposable? session,
        out string error)
    {
        session = null;
        if (!TryFindScreen(SelectionModelKind.Power, out NGenericSelectScreen? screen, out error))
            return false;

        Dictionary<string, int> selected = [];
        foreach ((string modelId, int amount) in powers)
        {
            IGenericSelectItem? item = FindModelItem(screen, modelId);
            if (item is not null && amount != 0)
                selected[item.Id] = amount;
        }

        SelectScreenOptions options = new()
        {
            SelectionMode = SelectSelectionMode.Multi,
            MinSelection = 0,
            MaxTotalSelection = 9999,
            MaxCopiesPerItem = 999
        };
        IDisposable lease = screen.BeginReusedSelection(options, selected, allowSignedAmounts: true);
        session = new PowerSelectorSession(screen, lease, confirmed);
        NLoadoutPanelRoot.Instance!.OpenScreen(screen);
        return true;
    }

    public static bool TryOpenMorphSelection(
        string? selectedModelId,
        Action<string?> confirmed,
        out IDisposable? session,
        out string error)
    {
        session = null;
        error = string.Empty;
        NLoadoutPanel? panel = NLoadoutPanel.Instance;
        NLoadoutPanelRoot? root = NLoadoutPanelRoot.Instance;
        NGenericSelectScreen? screen = panel?.GetSelectScreensForPreload()
            .Where(entry => entry.Name.ToString().Contains("BottledMonster_Alternate", StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.Screen)
            .FirstOrDefault(candidate => !candidate.IsScreenActive);
        if (screen is null || root is null)
        {
            error = "The initialized Morph Selection screen is not available.";
            return false;
        }

        Dictionary<string, int> selected = [];
        foreach (IGenericSelectItem item in screen.Items)
        {
            if (!BottledMonster.TryGetMorphOptionModel(item.UntypedModel, out AbstractModel? model))
                continue;
            if ((selectedModelId is null && model is null)
                || (model is not null && string.Equals(model.Id.ToString(), selectedModelId, StringComparison.OrdinalIgnoreCase)))
            {
                selected[item.Id] = 1;
                break;
            }
        }

        SelectScreenOptions options = new()
        {
            SelectionMode = SelectSelectionMode.Single,
            MinSelection = 1,
            MaxTotalSelection = 1,
            MaxCopiesPerItem = 1
        };
        IDisposable lease = screen.BeginReusedSelection(options, selected);
        session = new MorphSelectorSession(screen, lease, confirmed);
        root.OpenScreen(screen);
        return true;
    }

    private static bool TryFindScreen(
        SelectionModelKind kind,
        out NGenericSelectScreen screen,
        out string error)
    {
        screen = null!;
        error = string.Empty;
        NLoadoutPanel? panel = NLoadoutPanel.Instance;
        NLoadoutPanelRoot? root = NLoadoutPanelRoot.Instance;
        if (root is null || panel is null || !panel.LoadoutItemsInitialized)
        {
            error = "The shared Loadout catalog screens are not ready yet.";
            return false;
        }

        screen = panel.GetSelectScreensForPreload()
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

    private static IGenericSelectItem? FindModelItem(NGenericSelectScreen screen, string modelId)
    {
        return screen.Items.FirstOrDefault(item => item.UntypedModel is AbstractModel model
            && (string.Equals(model.Id.ToString(), modelId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(model.Id.Entry, modelId, StringComparison.OrdinalIgnoreCase)));
    }

    private static int GetMaximumSelection(SelectionModelKind kind)
    {
        return kind switch
        {
            SelectionModelKind.Card => 99,
            SelectionModelKind.Relic => 50,
            SelectionModelKind.Potion => 20,
            _ => 1
        };
    }

    private static int GetMaximumCopies(SelectionModelKind kind)
    {
        return kind switch
        {
            SelectionModelKind.Card => 99,
            SelectionModelKind.Potion => 20,
            _ => 1
        };
    }

    private sealed class SelectorSession : IDisposable
    {
        private readonly NGenericSelectScreen _screen;
        private readonly IDisposable _selectionLease;
        private readonly Action<IReadOnlyList<string>> _confirmedAction;
        private bool _completed;

        public SelectorSession(
            NGenericSelectScreen screen,
            IDisposable selectionLease,
            Action<IReadOnlyList<string>> confirmedAction)
        {
            _screen = screen;
            _selectionLease = selectionLease;
            _confirmedAction = confirmedAction;
            _screen.Confirmed += OnConfirmed;
            _screen.Cancelled += OnCancelled;
            _screen.ScreenClosed += OnScreenClosed;
        }

        public void Dispose()
        {
            if (_completed)
                return;
            _completed = true;
            _screen.Confirmed -= OnConfirmed;
            _screen.Cancelled -= OnCancelled;
            _screen.ScreenClosed -= OnScreenClosed;
            _selectionLease.Dispose();
        }

        private void OnConfirmed(IReadOnlyList<IGenericSelectItem> selectedItems)
        {
            if (_completed)
                return;

            List<string> selectedIds = [];
            foreach (IGenericSelectItem item in selectedItems)
            {
                if (item.UntypedModel is not AbstractModel model)
                    continue;
                int amount = _screen.SelectedAmounts.GetValueOrDefault(item.Id, 1);
                for (int copy = 0; copy < amount; copy++)
                    selectedIds.Add(model.Id.ToString());
            }

            Dispose();
            _confirmedAction(selectedIds);
        }

        private void OnCancelled() => Dispose();

        private void OnScreenClosed()
        {
            Callable.From(() =>
            {
                if (!_completed)
                    Dispose();
            }).CallDeferred();
        }
    }

    private sealed class PowerSelectorSession : IDisposable
    {
        private readonly NGenericSelectScreen _screen;
        private readonly IDisposable _lease;
        private readonly Action<IReadOnlyDictionary<string, int>> _confirmed;
        private bool _done;

        public PowerSelectorSession(NGenericSelectScreen screen, IDisposable lease, Action<IReadOnlyDictionary<string, int>> confirmed)
        {
            _screen = screen;
            _lease = lease;
            _confirmed = confirmed;
            screen.Confirmed += OnConfirmed;
            screen.Cancelled += Dispose;
            screen.ScreenClosed += OnClosed;
        }

        public void Dispose()
        {
            if (_done) return;
            _done = true;
            _screen.Confirmed -= OnConfirmed;
            _screen.Cancelled -= Dispose;
            _screen.ScreenClosed -= OnClosed;
            _lease.Dispose();
        }

        private void OnConfirmed(IReadOnlyList<IGenericSelectItem> _)
        {
            Dictionary<string, int> result = [];
            foreach (IGenericSelectItem item in _screen.Items)
            {
                int amount = _screen.SelectedAmounts.GetValueOrDefault(item.Id);
                if (amount != 0 && item.UntypedModel is AbstractModel model)
                    result[model.Id.ToString()] = amount;
            }
            Dispose();
            _confirmed(result);
        }

        private void OnClosed() => Callable.From(Dispose).CallDeferred();
    }

    private sealed class MorphSelectorSession : IDisposable
    {
        private readonly NGenericSelectScreen _screen;
        private readonly IDisposable _lease;
        private readonly Action<string?> _confirmed;
        private bool _done;

        public MorphSelectorSession(NGenericSelectScreen screen, IDisposable lease, Action<string?> confirmed)
        {
            _screen = screen;
            _lease = lease;
            _confirmed = confirmed;
            screen.Confirmed += OnConfirmed;
            screen.Cancelled += Dispose;
            screen.ScreenClosed += OnClosed;
        }

        public void Dispose()
        {
            if (_done) return;
            _done = true;
            _screen.Confirmed -= OnConfirmed;
            _screen.Cancelled -= Dispose;
            _screen.ScreenClosed -= OnClosed;
            _lease.Dispose();
        }

        private void OnConfirmed(IReadOnlyList<IGenericSelectItem> items)
        {
            string? id = null;
            if (items.Count > 0
                && BottledMonster.TryGetMorphOptionModel(items[0].UntypedModel, out AbstractModel? model))
                id = model?.Id.ToString();
            Dispose();
            _confirmed(id);
        }

        private void OnClosed() => Callable.From(Dispose).CallDeferred();
    }
}
