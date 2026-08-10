#nullable enable

namespace Loadout.UI.CustomRuns;

using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Loadout.Services.CustomRuns.Catalog;
using Loadout.Services.CustomRuns.Models;
using Loadout.UI.Screens;
using MegaCrit.Sts2.Core.Models;

public static class CustomRunCatalogSelector
{
    public static bool TryOpen(
        SelectionSpec selection,
        Action<IReadOnlyList<string>> confirmed,
        out IDisposable? session,
        out string error)
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
            MinSelection = 1,
            MaxTotalSelection = GetMaximumSelection(selection.Kind),
            MaxCopiesPerItem = GetMaximumCopies(selection.Kind)
        };

        try
        {
            IDisposable selectionLease = screen.BeginReusedSelection(options, selectedAmounts);
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
}
