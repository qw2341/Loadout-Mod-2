#nullable enable

namespace Loadout.UI.Screens;

using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using Loadout.Services.Targets;
using Loadout.UI.Managers;
using Loadout.UI.Screens.Controls;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rooms;

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
    private NSelectFilterDropdown? _pileTargetDropdown;
    private LoadoutCardPileTarget _defaultPileTarget = LoadoutCardPileTarget.Deck;
    private IReadOnlyList<LoadoutCardPileTarget> _pileTargetOptions = LoadoutCardPileTargets.OwnedCardOptions;
    private Action<LoadoutCardPileTarget>? _pileTargetChangedCallback;
    private Func<IEnumerable<CardPile>>? _observedPileProvider;
    private Action? _observedPileRefresh;
    private readonly HashSet<CardPile> _observedPiles = [];
    private bool _observedPileRefreshQueued;
    private bool _pileLifecycleBound;
    private List<ObservedPileSnapshot>? _observedPileSnapshot;

    public LoadoutCardPileTarget SelectedPileTarget { get; private set; } = LoadoutCardPileTarget.Deck;
    public event Action<LoadoutCardPileTarget>? PileTargetChanged;

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

    public override void _Ready()
    {
        base._Ready();
        if (!_pileLifecycleBound)
        {
            ScreenOpened += OnCardScreenOpened;
            ScreenClosed += OnCardScreenClosed;
            _pileLifecycleBound = true;
        }
    }

    public override void _ExitTree()
    {
        if (_pileLifecycleBound)
        {
            ScreenOpened -= OnCardScreenOpened;
            ScreenClosed -= OnCardScreenClosed;
            _pileLifecycleBound = false;
        }
        CombatManager.Instance.CombatEnded -= OnCombatEnded;
        DisconnectObservedPiles();
        base._ExitTree();
    }

    public void ConfigurePileTarget(
        LoadoutCardPileTarget defaultTarget,
        IReadOnlyList<LoadoutCardPileTarget> options,
        Action<LoadoutCardPileTarget>? onChanged = null)
    {
        LoadoutCardPileTarget previousTarget = SelectedPileTarget;
        _defaultPileTarget = defaultTarget;
        _pileTargetOptions = options;
        _pileTargetChangedCallback = onChanged;
        SelectedPileTarget = defaultTarget;

        if (_pileTargetDropdown is null
            || !GodotObject.IsInstanceValid(_pileTargetDropdown)
            || _pileTargetDropdown.GetParent() is null)
        {
            _pileTargetDropdown = new NSelectFilterDropdown
            {
                Name = "CardPileTargetDropdown",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(256f, 52f)
            };
            _pileTargetDropdown.SelectedItemChanged += OnPileTargetDropdownChanged;
            AddCustomSidebarControl(_pileTargetDropdown);
        }

        bool inCombat = LoadoutCardPileTargets.IsCombatInProgress();
        _pileTargetDropdown.Visible = inCombat;
        _pileTargetDropdown.SetItems(
            LocMan.Loc("CARD_PILE_TARGET", "Card Pile Target"),
            options.Select(target => target.ToDropdownOption()).ToArray(),
            defaultTarget.ToOptionId());

        if (inCombat && IsScreenActive)
            ReconnectObservedPiles();
        else
            DisconnectObservedPiles();

        if (previousTarget != defaultTarget)
            onChanged?.Invoke(defaultTarget);
    }

    public void ConfigureObservedPiles(Func<IEnumerable<CardPile>> provider, Action refresh)
    {
        _observedPileProvider = provider;
        _observedPileRefresh = refresh;
        if (IsScreenActive)
            ReconnectObservedPiles();
    }

    public void RefreshObservedPiles()
    {
        if (IsScreenActive)
            ReconnectObservedPiles();
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

        if (CurrentMaterializationMode != SelectMaterializationMode.Lazy
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

    private void OnPileTargetDropdownChanged(string selectedId)
    {
        if (!LoadoutCardPileTargets.TryParseOptionId(selectedId, out LoadoutCardPileTarget selected)
            || !_pileTargetOptions.Contains(selected))
        {
            return;
        }

        SelectedPileTarget = selected;
        ReconnectObservedPiles();
        _pileTargetChangedCallback?.Invoke(selected);
        PileTargetChanged?.Invoke(selected);
    }

    private void OnCardScreenOpened()
    {
        CombatManager.Instance.CombatEnded -= OnCombatEnded;
        CombatManager.Instance.CombatEnded += OnCombatEnded;

        bool inCombat = LoadoutCardPileTargets.IsCombatInProgress();
        if (_pileTargetDropdown is not null && GodotObject.IsInstanceValid(_pileTargetDropdown))
            _pileTargetDropdown.Visible = inCombat;

        if (!inCombat && SelectedPileTarget != _defaultPileTarget)
        {
            SelectedPileTarget = _defaultPileTarget;
            _pileTargetChangedCallback?.Invoke(SelectedPileTarget);
            PileTargetChanged?.Invoke(SelectedPileTarget);
        }

        bool observedPileChanged = HasObservedPileSnapshotChanged();
        ReconnectObservedPiles();
        if (observedPileChanged)
            _observedPileRefresh?.Invoke();
    }

    private void OnCardScreenClosed()
    {
        CombatManager.Instance.CombatEnded -= OnCombatEnded;
        if (!_observedPileRefreshQueued)
            CaptureObservedPileSnapshot();
        DisconnectObservedPiles();
    }

    private void OnCombatEnded(CombatRoom _)
    {
        SelectedPileTarget = _defaultPileTarget;
        if (_pileTargetDropdown is not null && GodotObject.IsInstanceValid(_pileTargetDropdown))
            _pileTargetDropdown.Visible = false;

        DisconnectObservedPiles();
        _pileTargetChangedCallback?.Invoke(SelectedPileTarget);
        PileTargetChanged?.Invoke(SelectedPileTarget);
    }

    private void ReconnectObservedPiles()
    {
        DisconnectObservedPiles();
        if (!IsInsideTree()
            || !IsScreenActive
            || !LoadoutCardPileTargets.IsCombatInProgress()
            || _observedPileProvider is null)
        {
            return;
        }

        foreach (CardPile pile in _observedPileProvider())
        {
            if (!_observedPiles.Add(pile))
                continue;

            pile.ContentsChanged += OnObservedPileContentsChanged;
        }
    }

    private void DisconnectObservedPiles()
    {
        foreach (CardPile pile in _observedPiles)
            pile.ContentsChanged -= OnObservedPileContentsChanged;

        _observedPiles.Clear();
        _observedPileRefreshQueued = false;
    }

    private void OnObservedPileContentsChanged()
    {
        if (_observedPileRefreshQueued)
            return;

        _observedPileRefreshQueued = true;
        Callable.From(FlushObservedPileRefresh).CallDeferred();
    }

    private void FlushObservedPileRefresh()
    {
        if (!_observedPileRefreshQueued)
            return;

        _observedPileRefreshQueued = false;
        if (!IsInsideTree() || !IsScreenActive)
            return;

        _observedPileRefresh?.Invoke();
        CaptureObservedPileSnapshot();
        ReconnectObservedPiles();
    }

    private bool HasObservedPileSnapshotChanged()
    {
        List<ObservedPileSnapshot> current = BuildObservedPileSnapshot();
        bool changed = _observedPileSnapshot is not null && !SnapshotsMatch(_observedPileSnapshot, current);
        _observedPileSnapshot = current;
        return changed;
    }

    private void CaptureObservedPileSnapshot()
    {
        _observedPileSnapshot = BuildObservedPileSnapshot();
    }

    private List<ObservedPileSnapshot> BuildObservedPileSnapshot()
    {
        if (_observedPileProvider is null || !LoadoutCardPileTargets.IsCombatInProgress())
            return [];

        return _observedPileProvider()
            .Distinct()
            .Select(pile => new ObservedPileSnapshot(pile, pile.Cards.ToArray()))
            .ToList();
    }

    private static bool SnapshotsMatch(
        IReadOnlyList<ObservedPileSnapshot> left,
        IReadOnlyList<ObservedPileSnapshot> right)
    {
        if (left.Count != right.Count)
            return false;

        for (int pileIndex = 0; pileIndex < left.Count; pileIndex++)
        {
            if (!ReferenceEquals(left[pileIndex].Pile, right[pileIndex].Pile)
                || left[pileIndex].Cards.Count != right[pileIndex].Cards.Count)
            {
                return false;
            }

            for (int cardIndex = 0; cardIndex < left[pileIndex].Cards.Count; cardIndex++)
            {
                if (!ReferenceEquals(left[pileIndex].Cards[cardIndex], right[pileIndex].Cards[cardIndex]))
                    return false;
            }
        }

        return true;
    }

    private sealed record ObservedPileSnapshot(CardPile Pile, IReadOnlyList<CardModel> Cards);
}
