#nullable enable

namespace Loadout.UI.Screens;

using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Card-specialized select screen.
///
/// Full card holders are much more expensive to create than relic holders. This
/// screen therefore spreads card creation over frames, prefetches in the current
/// scroll direction, retains initialized holders for ordinary catalogs, and
/// disables offscreen holders instead of continuously rendering or processing them.
/// Generic filtering, grouping, sorting, selection and layout stay in the base
/// <see cref="NGenericSelectScreen"/>.
/// </summary>
public partial class NCardSelectScreen : NGenericSelectScreen
{
    public const string ScenePath = "res://UI/Screens/CardSelectScreen.tscn";

    // Static card databases can be warmed before the player opens the screen.
    // Dynamic owned decks avoid full hidden warming so gameplay is not taxed by
    // constructing hundreds of card nodes after every deck mutation.
    private const int StaticCatalogRetainLimit = 480;
    private const int DynamicCatalogRetainLimit = 320;

    private const int InitialCardBudget = 12;
    private const int RemovalCardBudget = 8;
    private const int ScrollCardBudget = 1;
    private const int StaticHiddenPrewarmBatch = 2;
    private const int DynamicHiddenPrewarmBatch = 1;
    private const int VisibleIdleWarmBatch = 1;
    private const double StaticVisibleWarmIntervalSeconds = 1.0 / 60.0;
    private const double DynamicVisibleWarmIntervalSeconds = 1.0 / 45.0;
    private const float DirectionalWarmRows = 24f;
    private const float DirectionalWarmNearRows = 1.5f;
    private const float LargeCatalogRecycleRowsBehind = 18f;
    private const float LargeCatalogRecycleRowsAhead = 28f;

    // Ordered filtering/layout remains list based. This dictionary is a local,
    // non-networked secondary index used only for one-item visual refreshes.
    private readonly Dictionary<string, IGenericSelectItem> _itemsById = new(StringComparer.Ordinal);
    private readonly HashSet<Control> _activeViewportCardViews = new();
    private readonly HashSet<Control> _nextViewportCardViews = new();
    private bool _usesDynamicOwnedCardPolicy;
    private float _retainedLayoutViewportWidth = float.NaN;
    private int _backgroundWarmCursor;
    private double _visibleWarmAccumulator;

    public bool UsesDynamicOwnedCardPolicy => _usesDynamicOwnedCardPolicy;
    public bool UsesRetainedCatalog => ConfiguredItemCount <= GetRetainLimit();

    /// <summary>
    /// Must be selected before the first Configure call for Card Shredder and
    /// Card Modifier. It prevents full-catalog hidden warming during gameplay.
    /// </summary>
    public void UseDynamicOwnedCardPolicy()
    {
        _usesDynamicOwnedCardPolicy = true;
    }

    protected override void OnItemsConfigured()
    {
        _itemsById.Clear();
        foreach (IGenericSelectItem item in ConfiguredItems)
            _itemsById.TryAdd(item.Id, item);

        _activeViewportCardViews.Clear();
        _nextViewportCardViews.Clear();
        _backgroundWarmCursor = 0;
        _visibleWarmAccumulator = 0d;
        _retainedLayoutViewportWidth = float.NaN;
        SetHiddenPrewarmEnabled(true);

        // Lazy preserves viewport culling. Retention is controlled separately so
        // cards can be created once without leaving every holder active.
        SetMaterializationMode(SelectMaterializationMode.Lazy);
    }

    protected override void OnItemsAdded(IReadOnlyList<IGenericSelectItem> addedItems)
    {
        foreach (IGenericSelectItem item in addedItems)
            _itemsById.TryAdd(item.Id, item);

        _backgroundWarmCursor = 0;
        _visibleWarmAccumulator = 0d;
    }

    public override void _Process(double delta)
    {
        base._Process(delta);

        if (!IsVisibleInTree()
            || CurrentMaterializationMode != SelectMaterializationMode.Lazy
            || ConfiguredItemCount == 0)
        {
            return;
        }

        _visibleWarmAccumulator += delta;
        double interval = _usesDynamicOwnedCardPolicy
            ? DynamicVisibleWarmIntervalSeconds
            : StaticVisibleWarmIntervalSeconds;
        if (_visibleWarmAccumulator < interval || !IsScrollMotionSettled)
            return;

        _visibleWarmAccumulator = 0d;
        bool movingDown = TargetScrollOffset >= CurrentScrollOffset;
        float rowsBehind = movingDown ? DirectionalWarmNearRows : DirectionalWarmRows;
        float rowsAhead = movingDown ? DirectionalWarmRows : DirectionalWarmNearRows;

        int materialized = MaterializeSpecializationWindow(
            TargetScrollOffset,
            rowsBehind,
            rowsAhead,
            VisibleIdleWarmBatch,
            updateExistingViews: false);

        // Retained catalogs are slowly completed while the card screen itself is
        // open and idle. Dynamic screens never do this work while hidden.
        if (materialized == 0 && UsesRetainedCatalog)
        {
            MaterializeSpecializationFromCursor(
                ref _backgroundWarmCursor,
                VisibleIdleWarmBatch,
                updateExistingViews: false);
        }
    }

    protected override IReadOnlyList<IGenericSelectItem> BuildHiddenPrewarmItemList()
    {
        if (!_usesDynamicOwnedCardPolicy && UsesRetainedCatalog)
            return ConfiguredLayoutItems.ToArray();

        // Owned decks and unusually large card databases warm only the first
        // viewport while hidden. Additional cards are warmed directionally while
        // the user is actually using this screen.
        return base.BuildHiddenPrewarmItemList();
    }

    protected override int GetHiddenPrewarmBatchSize()
    {
        return _usesDynamicOwnedCardPolicy
            ? DynamicHiddenPrewarmBatch
            : StaticHiddenPrewarmBatch;
    }

    protected override int GetInitialMaterializeBudget() => InitialCardBudget;
    protected override int GetRemovalMaterializeBudget() => RemovalCardBudget;
    protected override int GetScrollMaterializeBudget() => ScrollCardBudget;
    protected override float GetMaterializeRowsBehind() => 3f;
    protected override float GetMaterializeRowsAhead() => 8f;
    protected override float GetRecycleRowsBehind() => LargeCatalogRecycleRowsBehind;
    protected override float GetRecycleRowsAhead() => LargeCatalogRecycleRowsAhead;

    protected override void ApplyRetainedItemLayouts()
    {
        float currentWidth = CurrentViewportLayoutWidth;
        if (float.IsNaN(_retainedLayoutViewportWidth))
        {
            _retainedLayoutViewportWidth = currentWidth;
            base.ApplyRetainedItemLayouts();
            return;
        }

        // Existing card holders already have their final positions. Avoid walking
        // every retained NCard tree on each reopen unless the viewport width changed.
        if (Mathf.Abs(currentWidth - _retainedLayoutViewportWidth) <= 0.5f)
            return;

        _retainedLayoutViewportWidth = currentWidth;
        base.ApplyRetainedItemLayouts();
    }

    protected override void ApplyViewportCulling(float cullTop, float cullBottom)
    {
        CullNonItemLayoutNodes(cullTop, cullBottom);

        _nextViewportCardViews.Clear();
        CollectMaterializedItemViewsInWindow(cullTop, cullBottom, _nextViewportCardViews);

        foreach (Control view in _activeViewportCardViews)
        {
            if (!_nextViewportCardViews.Contains(view) && GodotObject.IsInstanceValid(view))
                SetLayoutNodeActive(view, active: false);
        }

        foreach (Control view in _nextViewportCardViews)
        {
            // Layout rebuilds intentionally deactivate every retained holder before
            // culling. Reassert the current window even when the view was also in
            // the previous active set; SetLayoutNodeActive itself avoids redundant
            // Godot property writes.
            SetLayoutNodeActive(view, active: true);
        }

        _activeViewportCardViews.Clear();
        _activeViewportCardViews.UnionWith(_nextViewportCardViews);
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

    /// <summary>
    /// Refreshes only one wrapper and, when already materialized, one card holder.
    /// Layout-only reevaluation updates filters/sorts without rebuilding unrelated
    /// card visuals.
    /// </summary>
    public bool RefreshItemById(
        string itemId,
        Action<IGenericSelectItem, Control>? refreshMaterializedView = null,
        bool refreshMetadata = true,
        bool refreshLayout = false)
    {
        if (!_itemsById.TryGetValue(itemId, out IGenericSelectItem? item))
            return false;

        if (refreshMetadata)
            item.RefreshMetadata();

        if (item.View is Control view && GodotObject.IsInstanceValid(view))
        {
            if (refreshMaterializedView is not null)
                refreshMaterializedView(item, view);
            else
                RefreshItemView(item);
        }

        if (refreshLayout)
            RefreshLayout(resetScroll: false, updateExistingViews: false);

        return true;
    }

    private int GetRetainLimit()
    {
        return _usesDynamicOwnedCardPolicy
            ? DynamicCatalogRetainLimit
            : StaticCatalogRetainLimit;
    }
}
