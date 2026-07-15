#nullable enable

namespace Loadout.UI.Screens;

using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Relic-specialized select screen.
///
/// Relic holders are compact and comparatively cheap once constructed, so normal
/// catalogs are materialized gradually while hidden, retained in memory, culled
/// outside the viewport, and reused across opens. Generic filtering, grouping,
/// sorting, selection and layout remain in <see cref="NGenericSelectScreen"/>.
/// Extremely large modded catalogs fall back to the generic recycling policy.
/// </summary>
public partial class NRelicSelectScreen : NGenericSelectScreen
{
    public const string ScenePath = "res://UI/Screens/RelicSelectScreen.tscn";

    // Above this threshold, bounded recycling is safer than retaining a very large
    // third-party catalog. Ordinary owned-relic lists and normal relic databases
    // stay on the retained path.
    private const int RetainAllRelicLimit = 512;
    private const int SmallCatalogPrewarmBatch = 16;
    private const int LargeCatalogPrewarmBatch = 8;

    // Layout remains list/index based. This dictionary is only a secondary O(1)
    // lookup for event-driven refreshes; it is never used as network identity.
    private readonly Dictionary<string, IGenericSelectItem> _itemsById = new(StringComparer.Ordinal);
    private float _retainedLayoutViewportWidth = float.NaN;

    public bool UsesRetainedCatalog => ConfiguredItemCount <= RetainAllRelicLimit;

    protected override void OnItemsConfigured()
    {
        _itemsById.Clear();
        foreach (IGenericSelectItem item in ConfiguredItems)
            _itemsById.TryAdd(item.Id, item);

        SetHiddenPrewarmEnabled(true);
        _retainedLayoutViewportWidth = float.NaN;

        // Lazy here means viewport culling remains active. The specialization below
        // prevents recycling for normal catalogs, so views are still created once
        // and retained rather than recreated while scrolling.
        SetMaterializationMode(SelectMaterializationMode.Lazy);
    }

    protected override IReadOnlyList<IGenericSelectItem> BuildHiddenPrewarmItemList()
    {
        _retainedLayoutViewportWidth = CurrentViewportLayoutWidth;
        return UsesRetainedCatalog
            ? ConfiguredLayoutItems.ToArray()
            : base.BuildHiddenPrewarmItemList();
    }

    protected override int GetHiddenPrewarmBatchSize()
    {
        return ConfiguredItemCount <= 160
            ? SmallCatalogPrewarmBatch
            : LargeCatalogPrewarmBatch;
    }

    protected override void ApplyRetainedItemLayouts()
    {
        // ResumeRetainedLayout reaches this path only when no layout refresh is
        // pending. Retained relic nodes already have their final positions, so
        // walking every materialized holder on every reopen is unnecessary. A
        // viewport-width change is the exception because centered columns move.
        float currentWidth = CurrentViewportLayoutWidth;
        if (float.IsNaN(_retainedLayoutViewportWidth))
        {
            _retainedLayoutViewportWidth = currentWidth;
            base.ApplyRetainedItemLayouts();
            return;
        }

        if (Mathf.Abs(currentWidth - _retainedLayoutViewportWidth) <= 0.5f)
            return;

        _retainedLayoutViewportWidth = currentWidth;
        base.ApplyRetainedItemLayouts();
    }

    protected override void SetLayoutNodeActive(Control control, bool active)
    {
        if (control.Visible != active)
            control.Visible = active;

        ProcessModeEnum desiredMode = active
            ? ProcessModeEnum.Inherit
            : ProcessModeEnum.Disabled;
        if (control.ProcessMode != desiredMode)
            control.ProcessMode = desiredMode;
    }

    protected override void RecycleDistantItemViews()
    {
        if (UsesRetainedCatalog)
            return;

        base.RecycleDistantItemViews();
    }

    public bool TryGetItemById(string itemId, out IGenericSelectItem item)
    {
        return _itemsById.TryGetValue(itemId, out item!);
    }

    public bool RefreshItemById(string itemId)
    {
        return _itemsById.TryGetValue(itemId, out IGenericSelectItem? item)
               && RefreshItemView(item);
    }

    public int RefreshItemsById(IEnumerable<string> itemIds)
    {
        int refreshed = 0;
        foreach (string itemId in itemIds)
        {
            if (RefreshItemById(itemId))
                refreshed++;
        }

        return refreshed;
    }
}
