#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Loadout.PanelItems;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;

namespace Loadout.UI.Screens;

public partial class NGenericSelectScreen
{
    public const double StartupFrameBudgetMsec = 8d;

    private sealed record StartupSortCapture(
        NGenericSelectScreen Screen,
        ulong Generation,
        IGenericSelectItem[] Items,
        SelectStartupSort? Sort);

    private sealed record StartupPrewarmPlan(ulong Generation, IReadOnlyList<IGenericSelectItem> Items);

    public static async Task<int> PrewarmStartupScreensAsync(
        IReadOnlyList<NGenericSelectScreen> screens,
        CancellationToken token)
    {
        long phaseStart = Stopwatch.GetTimestamp();
        List<StartupSortCapture> captures = new();
        foreach (NGenericSelectScreen screen in screens)
        {
            token.ThrowIfCancellationRequested();
            if (!CanPrewarmStartupScreen(screen))
                continue;
            try
            {
                captures.Add(screen.CaptureStartupSort());
            }
            catch (Exception exception)
            {
                Log.Error($"[Loadout] Could not capture startup catalog '{screen.Name}': {exception}");
            }
        }
        LogStartupPhase("catalog capture", phaseStart);

        phaseStart = Stopwatch.GetTimestamp();
        SelectStartupSort[] sorts = captures.Where(capture => capture.Sort is not null).Select(capture => capture.Sort!).ToArray();
        await SelectStartupSort.PrepareAsync(sorts, token);
        LogStartupPhase($"catalog workers (catalogs={sorts.Length}, limit={SelectStartupSort.WorkerLimit})", phaseStart);

        phaseStart = Stopwatch.GetTimestamp();
        List<(NGenericSelectScreen Screen, StartupPrewarmPlan Plan)> plans = new();
        HashSet<string> checkedPaths = new(StringComparer.Ordinal);
        HashSet<string> resourcePaths = new(StringComparer.Ordinal);
        foreach (StartupSortCapture capture in captures)
        {
            token.ThrowIfCancellationRequested();
            NGenericSelectScreen screen = capture.Screen;
            if (!CanPrewarmStartupScreen(screen) || capture.Generation != screen._layoutGeneration)
                continue;
            if (capture.Sort?.Error is { } sortError)
            {
                Log.Error($"[Loadout] Could not sort startup catalog '{screen.Name}': {sortError}");
                continue;
            }
            try
            {
                IReadOnlyList<IGenericSelectItem> orderedItems = capture.Sort is null
                    ? capture.Items
                    : capture.Sort.Order.Select(index => capture.Items[index]).ToArray();
                screen.PrepareHiddenPrewarmLayout(orderedItems);
                StartupPrewarmPlan plan = new(screen._layoutGeneration, screen.BuildHiddenPrewarmItemList());
                plans.Add((screen, plan));
                screen.CollectNativeResourcePaths(screen.Name, plan.Items, checkedPaths, resourcePaths, token);
            }
            catch (Exception exception)
            {
                Log.Error($"[Loadout] Could not prepare startup layout '{screen.Name}': {exception}");
            }
        }
        LogStartupPhase($"layouts and resource capture (assets={resourcePaths.Count})", phaseStart);
        if (plans.Count == 0)
            return 0;

        phaseStart = Stopwatch.GetTimestamp();
        // The native loader owns threaded resources and serializes shared VFX dependencies.
        if (!await plans[0].Screen.LoadNativeResourcePathsAsync("startup catalogs", resourcePaths, token))
            return 0;
        LogStartupPhase("shared resource loading", phaseStart);

        int completed = 0;
        phaseStart = Stopwatch.GetTimestamp();
        foreach (var (screen, plan) in plans)
        {
            token.ThrowIfCancellationRequested();
            if (!CanPrewarmStartupScreen(screen) || plan.Generation != screen._layoutGeneration)
                continue;
            screen.FlushThreadedPreloadWarnings();
            screen._hiddenPrewarmTask = screen.PrewarmForFirstOpenCoreAsync(plan, token);
            await screen._hiddenPrewarmTask;
            if (GodotObject.IsInstanceValid(screen) && screen.IsFirstOpenPrewarmed)
                completed++;
        }
        LogStartupPhase("view finalization", phaseStart);
        return completed;
    }

    private StartupSortCapture CaptureStartupSort()
    {
        RebuildActiveFilterPlan();
        IGenericSelectItem[] items = _items.Where(PassesSearchAndFilters).ToArray();
        List<Comparison<int>> comparisons = new();
        foreach (string sorterId in _sortPriority)
        {
            if (!_sortersById.TryGetValue(sorterId, out SelectSorterDefinition? sorter))
                continue;
            if (sorter.CaptureStartupComparison is null || (sorter.IsDescending && sorter.DescendingComparison is not null))
            {
                // Third-party comparison callbacks retain their main-thread behavior.
                Array.Sort(items, CompareItems);
                return new StartupSortCapture(this, _layoutGeneration, items, null);
            }

            Comparison<int> comparison = sorter.CaptureStartupComparison(items);
            comparisons.Add(sorter.IsDescending ? (left, right) => -comparison(left, right) : comparison);
        }

        SelectStartupSort sort = new(
            items.Select(item => item.OriginalIndex).ToArray(),
            items.Select(item => item.UntypedModel is AbstractModel model
                ? CommonHelpers.GetModelModId(model)
                : item.UntypedModel.GetType().Assembly.GetName().Name ?? "Loadout").ToArray(),
            comparisons.ToArray());
        return new StartupSortCapture(this, _layoutGeneration, items, sort);
    }

    private static bool CanPrewarmStartupScreen(NGenericSelectScreen screen)
    {
        return GodotObject.IsInstanceValid(screen)
               && screen.IsInsideTree()
               && !screen.IsVisibleInTree()
               && screen._isConfigured
               && screen._itemGrid is not null
               && screen._hiddenPrewarmEnabled
               && !screen._hiddenPrewarmCompleted
               && screen._hiddenPrewarmTask is not { IsCompleted: false };
    }

    private static void LogStartupPhase(string phase, long start)
    {
        Log.Info($"[Loadout] Startup {phase}: {Stopwatch.GetElapsedTime(start).TotalMilliseconds:N1}ms.");
    }
}
