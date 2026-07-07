#nullable enable

using Loadout.UI.Managers;

namespace Loadout.UI.Screens;

using Godot;
using Loadout.UI;
using Loadout.UI.Screens.Controls;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Generic, reusable select-screen root for Godot/STS2 mods.
///
/// The Godot node itself is intentionally non-generic because Godot C# scene scripts
/// should be concrete node types. Type safety is provided by SelectItemAdapter<TModel>
/// and the typed AddFilter/AddSorter overloads.
/// </summary>
public partial class NGenericSelectScreen : Control
{
    private const string AllFilterOptionId = "__all__";
    private const string SelectSortButtonInnerPath = "screens/card_library/library_sort_button";
    private const float SidebarWidth = 288f;
    private const float CardVisualLeftOverhang = 96f;
    private const float CardScrollbarReserve = 48f;
    private const int InitialMaterializeBudget = 96;
    private const int RemovalMaterializeBudget = 12;
    private const int ScrollMaterializeBudget = 164;
    private const float ScrollSmoothingRate = 15f;
    private const float ScrollSpeedMultiplier = 3f;
    private const float MaterializeRowsAhead = 5f;
    private const float MaterializeRowsBehind = 3f;
    private const float CullRetentionRows = 2.5f;
    private const float CullUpdateRowsThreshold = 0.5f;
    private const float RelayoutTweenSeconds = 0.18f;
    private const float RelayoutTweenMinDistance = 1f;

    [Export]
    public NodePath SearchLineEditPath = "Sidebar/MarginContainer/TopVBox/SearchBar/TextArea";

    [Export]
    public NodePath ClearSearchButtonPath = "Sidebar/MarginContainer/TopVBox/SearchBar/ClearButton";

    [Export]
    public NodePath FilterControlsPath = "Sidebar/MarginContainer/TopVBox";

    [Export]
    public NodePath ItemGridPath = "CardGrid/ScreenContents/Mask/Content";

    [Export]
    public NodePath ScrollContainerPath = "CardGrid/ScreenContents";

    [Export]
    public NodePath ConfirmButtonPath = "ConfirmButton";

    [Export]
    public NodePath CancelButtonPath = "BackButton";

    [Export]
    public NodePath SelectedCountLabelPath = "Sidebar/MarginContainer/TopVBox/SelectedCountLabel";

    [Export(PropertyHint.Range, "0,1000,1")]
    public int SearchDelayMsec = 160;

    [Export(PropertyHint.Range, "0,20,1")]
    public int GridColumns = 0;

    [Export(PropertyHint.Range, "0,64,1")]
    public int ItemHorizontalGap = 12;

    [Export(PropertyHint.Range, "0,64,1")]
    public int ItemVerticalGap = 12;

    [Export(PropertyHint.Range, "1,2048,1")]
    public int FallbackItemWidth = 220;

    [Export(PropertyHint.Range, "1,2048,1")]
    public int FallbackItemHeight = 300;

    public event Action<IReadOnlyList<IGenericSelectItem>>? Confirmed;
    public event Action? Cancelled;
    public event Action? ScreenClosed;
    public event Action<IGenericSelectItem, SelectItemState>? ItemActivated;
    public event Action<IGenericSelectItem, SelectItemState>? ItemSelectionChanged;
    public event Action? LocaleChanged;

    private readonly List<IGenericSelectItem> _items = new();
    private readonly List<IGenericSelectItem> _visibleItems = new();

    private readonly List<SelectFilterDefinition> _filters = new();
    private readonly Dictionary<string, SelectFilterDefinition> _filtersById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SelectFilterGroupDefinition> _filterGroupsById = new(StringComparer.Ordinal);
    private readonly List<string> _filterGroupOrder = new();

    private readonly List<SelectSorterDefinition> _sorters = new();
    private readonly Dictionary<string, SelectSorterDefinition> _sortersById = new(StringComparer.Ordinal);
    private readonly List<string> _sortPriority = new();
    private readonly List<SelectActionButtonDefinition> _actionButtons = new();
    private readonly Dictionary<string, SelectActionButtonDefinition> _actionButtonsById = new(StringComparer.Ordinal);
    private readonly List<SelectToggleDefinition> _toggles = new();
    private readonly Dictionary<string, SelectToggleDefinition> _togglesById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _toggleStates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SelectGroupDefinition> _groupsBySorterId = new(StringComparer.Ordinal);
    private readonly List<Control> _generatedGroupContainers = new();
    private readonly List<Control> _visibleLayoutNodes = new();
    private readonly HashSet<Control> _visibleLayoutNodeSet = new();
    private readonly Dictionary<IGenericSelectItem, SelectItemLayout> _itemLayouts = new();
    private readonly List<IGenericSelectItem> _itemLayoutOrder = new();
    private readonly HashSet<IGenericSelectItem> _itemsUpdatedForCurrentLayout = new();

    private readonly Dictionary<string, NLoadoutDropdown> _filterDropdownsByGroupId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NCardViewSortButton> _sortButtonsById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NLoadoutActionButton> _actionButtonNodesById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NLoadoutToggle> _toggleButtonsById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _selectedAmounts = new(StringComparer.Ordinal);
    private readonly Dictionary<Control, Tween> _relayoutTweens = new();
    private readonly Dictionary<Control, Vector2> _relayoutTargets = new();
    private readonly HashSet<Control> _relayoutPositionLockedViews = new();
    private readonly Dictionary<Control, IGenericSelectItem> _activationItemsByView = new();
    private readonly HashSet<Control> _activationBoundViews = new();
    private readonly List<Control> _pendingCustomSidebarControls = new();

    private LineEdit? _searchLineEdit;
    private BaseButton? _clearSearchButton;
    private NButton? _clearSearchNButton;
    private NClickableControl? _clearSearchClickable;
    private Control? _clearSearchControl;
    private bool _clearSearchPressStarted;
    private VBoxContainer? _filterControls;
    private Container? _sortButtonsContainer;
    private VBoxContainer? _actionButtonsContainer;
    private VBoxContainer? _customControlsContainer;
    private VBoxContainer? _togglesContainer;
    private VBoxContainer? _filtersContainer;
    private Control? _itemGrid;
    private Control? _scrollMask;
    private Control? _scrollContainer;
    private NScrollbar? _scrollbar;
    private BaseButton? _confirmButton;
    private NClickableControl? _confirmClickable;
    private BaseButton? _cancelButton;
    private NClickableControl? _cancelClickable;
    private Control? _selectedCountLabel;

    private SelectScreenOptions _options = new();
    private SelectLayoutDefinition _layout = SelectLayoutDefinition.Default;
    private string _query = string.Empty;
    private bool _isSyncingFilterDropdowns;
    private bool _isConfigured;
    private float _lastMeasuredItemWidth = -1f;
    private SelectMaterializationMode _materializationMode = SelectMaterializationMode.Eager;
    private float _scrollY;
    private float _targetScrollY;
    private float _maxScrollY;
    private bool _scrollbarPressed;
    private System.Threading.CancellationTokenSource? _searchDelayCts;
    private ulong _layoutGeneration;
    private ulong _scheduledEagerRefreshGeneration;
    private ulong _scheduledVisibleRefreshGeneration;
    private ulong _lastEagerMismatchWarningGeneration;
    private Control? _multiplierBadge;
    private MegaLabel? _multiplierBadgeLabel;
    private Func<IGenericSelectItem, bool>? _customVisibilityPredicate;
    private bool _isSubscribedToLocaleChanges;
    private string _configuredLocaleLanguage = string.Empty;
    private float _lastCullScrollY = float.NaN;
    private int _lastCullNodeCount = -1;
    private CancellationTokenSource? _preloadCts;
    private long _completedPreloadGeneration = -1;
    private readonly ConcurrentQueue<string> _preloadWarnings = new();

    public IReadOnlyList<IGenericSelectItem> Items => _items;
    public IReadOnlyList<IGenericSelectItem> VisibleItems => _visibleItems;
    public IReadOnlyDictionary<string, int> SelectedAmounts => _selectedAmounts;
    public bool IsConfiguredForCurrentLocale => string.Equals(_configuredLocaleLanguage, GetCurrentLocaleLanguage(), StringComparison.Ordinal);

    public override void _Ready()
    {
        BindSceneNodes();
        BuildUtilityContainersIfMissing();
        EnsureGameScrollbar();
        EnsureActionButtons();
        BindSceneSignals();
        SubscribeToLocaleChanges();
        RebuildSortButtons();
        RebuildActionButtons();
        RebuildCustomControls();
        RebuildToggleButtons();
        RebuildFilterButtons();
        ApplyLayoutSettings();

        if (_isConfigured)
            RefreshNow(resetScroll: true);
    }

    public override void _ExitTree()
    {
        CloseOpenDropdowns();
        _searchDelayCts?.Cancel();
        _searchDelayCts?.Dispose();
        _searchDelayCts = null;
        CancelPendingMaterialization();
        UnsubscribeFromLocaleChanges();
    }

    public override void _Notification(int what)
    {
        if (what == NotificationVisibilityChanged && !Visible)
        {
            CloseOpenDropdowns();
            ReleaseFocusInsideScreen();
            SetActionButtonsActive(false);
            ScreenClosed?.Invoke();
        }

        if (what == NotificationVisibilityChanged && Visible)
        {
            SetActionButtonsActive(true);
            UpdateConfirmButtonState();
            ScheduleDeferredVisibleRefresh();
        }
    }

    public override void _Process(double delta)
    {
        CompleteThreadedPreloadIfReady();
        UpdateSelectScroll(delta);
        UpdateMultiplierBadge();
        KeepHoverTipsAboveSelectScreen();
    }

    public override void _Input(InputEvent @event)
    {
        if (!IsVisibleInTree() || _scrollMask is null)
            return;

        if (@event is not InputEventMouseButton { Pressed: true } mouseButton)
            return;

        if (mouseButton.ButtonIndex != MouseButton.WheelUp && mouseButton.ButtonIndex != MouseButton.WheelDown)
            return;

        if (!_scrollMask.GetGlobalRect().HasPoint(mouseButton.GlobalPosition))
            return;

        float drag = ScrollHelper.GetDragForScrollEvent(@event) * ScrollSpeedMultiplier;
        if (!Mathf.IsZeroApprox(drag))
            SetTargetScroll(_targetScrollY - drag);

        GetViewport().SetInputAsHandled();
    }
    
    private void SetActionButtonsActive(bool active)
    {
        if (_cancelButton is not null)
        {
            _cancelButton.Visible = active;
            _cancelButton.Disabled = !active;
        }

        if (_cancelClickable is not null)
        {
            _cancelClickable.Visible = active;
            _cancelClickable.SetEnabled(active);
            ResetActionButtonVisualState(_cancelClickable);
        }

        bool usesSelection = _options.SelectionMode != SelectSelectionMode.None;

        if (_confirmButton is not null)
        {
            _confirmButton.Visible = active && usesSelection;
            _confirmButton.Disabled = !active || !usesSelection || !IsConfirmAllowed();
        }

        if (_confirmClickable is not null)
        {
            _confirmClickable.Visible = active && usesSelection;
            _confirmClickable.SetEnabled(active && usesSelection && IsConfirmAllowed());
            ResetActionButtonVisualState(_confirmClickable);
        }
    }

    private void ReleaseFocusInsideScreen()
    {
        Viewport viewport = GetViewport();
        Control? focusOwner = viewport?.GuiGetFocusOwner();

        if (GodotObject.IsInstanceValid(focusOwner)
            && (focusOwner == this || IsAncestorOf(focusOwner)))
        {
            focusOwner.ReleaseFocus();
        }
    }

    /// <summary>
    /// The main entry point. Build wrappers from any model type, then configure filters/sorters/options with a typed builder.
    /// </summary>
    public void Configure<TModel>(
        IEnumerable<TModel> models,
        SelectItemAdapter<TModel> adapter,
        Action<SelectScreenBuilder<TModel>>? build = null)
    {
        CancelRelayoutAnimations(applyFinalPositions: true);
        ResetConfiguration();

        SelectScreenBuilder<TModel> builder = new(this, adapter);
        build?.Invoke(builder);

        int index = 0;
        foreach (TModel model in models)
        {
            _items.Add(new GenericSelectItem<TModel>(model, adapter, index));
            index++;
        }

        _isConfigured = true;
        _configuredLocaleLanguage = GetCurrentLocaleLanguage();
        RefreshNow(resetScroll: true);
    }

    public void ConfigurePreservingViews<TModel>(
        IEnumerable<TModel> models,
        SelectItemAdapter<TModel> adapter,
        Action<SelectScreenBuilder<TModel>>? build = null,
        bool animateRelayout = false)
    {
        CancelRelayoutAnimations(applyFinalPositions: false);
        Dictionary<string, Control> reusableViews = CaptureReusableItemViewsById();
        Dictionary<string, Vector2> previousPositions = CaptureItemPositionsById();
        Dictionary<Control, Vector2> relayoutStartPositions = new();

        ResetConfiguration(clearItemViews: false);

        SelectScreenBuilder<TModel> builder = new(this, adapter);
        build?.Invoke(builder);

        int index = 0;
        foreach (TModel model in models)
        {
            GenericSelectItem<TModel> item = new(model, adapter, index);
            if (reusableViews.Remove(item.Id, out Control? view) && GodotObject.IsInstanceValid(view))
            {
                SetItemView(item, view);
                if (animateRelayout && previousPositions.TryGetValue(item.Id, out Vector2 previousPosition))
                    relayoutStartPositions[view] = previousPosition;
            }

            _items.Add(item);
            index++;
        }

        foreach (Control staleView in reusableViews.Values)
        {
            if (!GodotObject.IsInstanceValid(staleView))
                continue;

            staleView.GetParent()?.RemoveChild(staleView);
            ClearActivationBinding(staleView);
            staleView.QueueFreeSafely();
        }

        if (animateRelayout)
            LockRelayoutPositions(relayoutStartPositions);

        _isConfigured = true;
        _configuredLocaleLanguage = GetCurrentLocaleLanguage();
        RefreshNow(resetScroll: false);

        if (animateRelayout)
            AnimateRelayoutFrom(relayoutStartPositions);
    }

    public void RefreshItemsPreservingViews<TModel>(
        IEnumerable<TModel> models,
        SelectItemAdapter<TModel> adapter,
        bool animateRelayout = false,
        bool resetScroll = false,
        bool updateExistingViews = true)
    {
        ApplyModelSnapshotPreservingViews(models, adapter, animateRelayout, resetScroll, updateExistingViews);
    }

    public bool TryApplySingleItemRemoval<TModel>(
        IEnumerable<TModel> models,
        SelectItemAdapter<TModel> adapter,
        bool animateRelayout = true,
        bool updateExistingViews = false)
    {
        List<TModel> modelSnapshot = models.ToList();
        HashSet<string> nextIds = modelSnapshot
            .Select(adapter.GetId)
            .ToHashSet(StringComparer.Ordinal);
        List<IGenericSelectItem> removedItems = _items
            .Where(item => !nextIds.Contains(item.Id))
            .ToList();

        if (removedItems.Count != 1 || modelSnapshot.Count != _items.Count - 1)
            return false;

        ApplySingleItemRemoval(modelSnapshot, adapter, removedItems[0], animateRelayout, updateExistingViews);
        return true;
    }

    public bool RefreshItemView(string itemId)
    {
        IGenericSelectItem? item = _items.FirstOrDefault(candidate => string.Equals(candidate.Id, itemId, StringComparison.Ordinal));
        if (item is null || item.View is null || !GodotObject.IsInstanceValid(item.View))
            return false;

        item.UpdateView(BuildState(item, _visibleItems.IndexOf(item)));
        _itemsUpdatedForCurrentLayout.Add(item);
        return true;
    }

    private void ApplyModelSnapshotPreservingViews<TModel>(
        IEnumerable<TModel> models,
        SelectItemAdapter<TModel> adapter,
        bool animateRelayout,
        bool resetScroll,
        bool updateExistingViews)
    {
        CancelRelayoutAnimations(applyFinalPositions: false);
        CancelPendingMaterialization();
        Dictionary<string, Control> reusableViews = CaptureReusableItemViewsById();
        Dictionary<string, Vector2> previousPositions = CaptureItemPositionsById();
        Dictionary<Control, Vector2> relayoutStartPositions = new();

        _items.Clear();
        _visibleItems.Clear();
        ClearLayoutTracking();

        int index = 0;
        foreach (TModel model in models)
        {
            GenericSelectItem<TModel> item = new(model, adapter, index);
            if (reusableViews.Remove(item.Id, out Control? view) && GodotObject.IsInstanceValid(view))
            {
                SetItemView(item, view);
                if (animateRelayout && previousPositions.TryGetValue(item.Id, out Vector2 previousPosition))
                    relayoutStartPositions[view] = previousPosition;
            }

            _items.Add(item);
            index++;
        }

        foreach (Control staleView in reusableViews.Values)
        {
            if (!GodotObject.IsInstanceValid(staleView))
                continue;

            staleView.GetParent()?.RemoveChild(staleView);
            ClearActivationBinding(staleView);
            staleView.QueueFreeSafely();
        }

        HashSet<string> currentItemIds = _items.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        foreach (string staleSelectionId in _selectedAmounts.Keys.Where(id => !currentItemIds.Contains(id)).ToList())
            _selectedAmounts.Remove(staleSelectionId);

        if (animateRelayout)
            LockRelayoutPositions(relayoutStartPositions);

        _configuredLocaleLanguage = GetCurrentLocaleLanguage();
        RebuildCurrentLayout(resetScroll, updateExistingViews);

        if (animateRelayout)
            AnimateRelayoutFrom(relayoutStartPositions);
    }

    private void ApplySingleItemRemoval<TModel>(
        IReadOnlyList<TModel> modelSnapshot,
        SelectItemAdapter<TModel> adapter,
        IGenericSelectItem removedItem,
        bool animateRelayout,
        bool updateExistingViews)
    {
        CancelRelayoutAnimations(applyFinalPositions: false);
        CancelPendingMaterialization();
        CancelPendingSearchRefresh();

        Dictionary<string, Control> reusableViews = CaptureReusableItemViewsById();
        Dictionary<string, Vector2> previousPositions = CaptureVisibleItemPositionsById();
        Dictionary<Control, Vector2> relayoutStartPositions = new();

        reusableViews.Remove(removedItem.Id);
        AnimateAndRemoveItemView(removedItem);

        _items.Clear();
        _visibleItems.Clear();

        int index = 0;
        foreach (TModel model in modelSnapshot)
        {
            GenericSelectItem<TModel> item = new(model, adapter, index);
            if (reusableViews.Remove(item.Id, out Control? view) && GodotObject.IsInstanceValid(view))
            {
                SetItemView(item, view);
                if (animateRelayout && previousPositions.TryGetValue(item.Id, out Vector2 previousPosition))
                    relayoutStartPositions[view] = previousPosition;
            }

            _items.Add(item);
            index++;
        }

        HashSet<string> currentItemIds = _items.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        foreach (string staleSelectionId in _selectedAmounts.Keys.Where(id => !currentItemIds.Contains(id)).ToList())
            _selectedAmounts.Remove(staleSelectionId);

        if (animateRelayout)
            LockRelayoutPositions(relayoutStartPositions);

        _configuredLocaleLanguage = GetCurrentLocaleLanguage();
        RebuildCurrentLayoutAfterSingleRemoval(updateExistingViews);

        if (animateRelayout)
            AnimateRelayoutFrom(relayoutStartPositions);
    }

    public void RequestDeferredVisibleRefresh()
    {
        ScheduleDeferredVisibleRefresh();
    }

    public void RefreshCurrentItemStates()
    {
        RefreshVisibleItemStates();
    }

    public void ForEachVisibleItemView(Action<IGenericSelectItem, Control> action, bool materializeMissing = false)
    {
        if (materializeMissing)
        {
            foreach (IGenericSelectItem item in _itemLayoutOrder.ToList())
            {
                if (_itemLayouts.TryGetValue(item, out SelectItemLayout layout))
                    MaterializeItemView(item, layout, updateExistingView: true);
            }
        }

        foreach (IGenericSelectItem item in _visibleItems.ToList())
        {
            if (item.View is null || !GodotObject.IsInstanceValid(item.View))
                continue;

            action(item, item.View);
        }

        if (materializeMissing)
            UpdateViewportCulling(force: true);
    }

    public void SetCustomVisibilityPredicate(Func<IGenericSelectItem, bool>? predicate)
    {
        _customVisibilityPredicate = predicate;
    }

    public SelectScreenUiState CaptureUiState()
    {
        return new SelectScreenUiState(
            _query,
            _filters
                .Select(filter => filter.GroupId)
                .Distinct(StringComparer.Ordinal)
                .ToDictionary(groupId => groupId, groupId => GetSelectedFilterIdForGroup(groupId) ?? AllFilterOptionId, StringComparer.Ordinal),
            new Dictionary<string, bool>(_toggleStates, StringComparer.Ordinal),
            _sortPriority.ToList(),
            _sorters.ToDictionary(sorter => sorter.Id, sorter => sorter.IsDescending, StringComparer.Ordinal),
            _targetScrollY);
    }

    public void RestoreUiState(SelectScreenUiState state)
    {
        _query = state.Query ?? string.Empty;
        if (_searchLineEdit is not null)
            _searchLineEdit.Text = _query;

        foreach ((string groupId, string selectedFilterId) in state.FilterSelections)
        {
            if (_filterGroupsById.ContainsKey(groupId))
                ApplyExclusiveFilterSelection(groupId, selectedFilterId == AllFilterOptionId ? null : selectedFilterId);
        }

        foreach ((string toggleId, bool isChecked) in state.ToggleStates)
        {
            if (_toggleStates.ContainsKey(toggleId))
                _toggleStates[toggleId] = isChecked;
        }

        foreach ((string sorterId, bool isDescending) in state.SortDescendingStates)
        {
            if (_sortersById.TryGetValue(sorterId, out SelectSorterDefinition? sorter))
                sorter.IsDescending = isDescending;
        }

        _sortPriority.Clear();
        foreach (string sorterId in state.SortPriority)
        {
            if (_sortersById.ContainsKey(sorterId) && !_sortPriority.Contains(sorterId))
                _sortPriority.Add(sorterId);
        }

        foreach (SelectSorterDefinition sorter in _sorters.Where(sorter => sorter.ActiveByDefault))
        {
            if (!_sortPriority.Contains(sorter.Id))
                _sortPriority.Add(sorter.Id);
        }

        RebuildSortButtons();
        RebuildActionButtons();
        RebuildToggleButtons();
        RebuildFilterButtons();
        RefreshNow(resetScroll: false);
        SetTargetScroll(state.ScrollY);
    }

    public int GetCurrentActivationMultiplier()
    {
        return GetCurrentInputMultiplier();
    }

    public static int GetCurrentInputMultiplier()
    {
        bool shift = Input.IsKeyPressed(Key.Shift);
        bool ctrl = Input.IsKeyPressed(Key.Ctrl);

        return (shift, ctrl) switch
        {
            (true, true) => 50,
            (true, false) => 10,
            (false, true) => 5,
            _ => 1
        };
    }

    public void ResetConfiguration(bool clearItemViews = true)
    {
        CancelRelayoutAnimations(applyFinalPositions: clearItemViews);
        CancelPendingMaterialization();
        if (clearItemViews)
            ClearItemViews();

        _items.Clear();
        _visibleItems.Clear();
        ClearLayoutTracking();
        _filters.Clear();
        _filtersById.Clear();
        _filterGroupsById.Clear();
        _filterGroupOrder.Clear();
        _sorters.Clear();
        _sortersById.Clear();
        _sortPriority.Clear();
        _actionButtons.Clear();
        _actionButtonsById.Clear();
        _toggles.Clear();
        _togglesById.Clear();
        _toggleStates.Clear();
        _groupsBySorterId.Clear();
        _selectedAmounts.Clear();
        ClearGeneratedGroupContainers();

        _options = new SelectScreenOptions();
        _layout = SelectLayoutDefinition.Default;
        _materializationMode = SelectMaterializationMode.Eager;
        _customVisibilityPredicate = null;
        _query = string.Empty;
        _isConfigured = false;
        _lastMeasuredItemWidth = -1f;

        if (_searchLineEdit is not null)
            _searchLineEdit.Text = string.Empty;

        RebuildSortButtons();
        RebuildActionButtons();
        ClearCustomSidebarControls();
        RebuildToggleButtons();
        RebuildFilterButtons();
        UpdateConfirmButtonState();
    }

    public void SetOptions(SelectScreenOptions options)
    {
        _options = options;
        UpdateConfirmButtonState();
    }

    public void SetLayout(SelectLayoutDefinition layout)
    {
        _layout = layout;
        GridColumns = layout.Columns;
        ItemHorizontalGap = layout.HorizontalGap;
        ItemVerticalGap = layout.VerticalGap;
        FallbackItemWidth = Mathf.CeilToInt(layout.ItemSize.X);
        FallbackItemHeight = Mathf.CeilToInt(layout.ItemSize.Y);
        ApplyLayoutSettings();
    }

    public void SetMaterializationMode(SelectMaterializationMode mode)
    {
        _materializationMode = mode;
    }

    public void AddFilterGroup(SelectFilterGroupDefinition group)
    {
        if (_filterGroupsById.ContainsKey(group.Id))
            return;

        _filterGroupsById[group.Id] = group;
        _filterGroupOrder.Add(group.Id);
        RebuildFilterButtons();
    }

    public void AddFilter(SelectFilterDefinition filter)
    {
        if (_filtersById.ContainsKey(filter.Id))
            throw new InvalidOperationException($"Filter id '{filter.Id}' already exists.");

        EnsureFilterGroupExists(filter.GroupId);

        _filters.Add(filter);
        _filtersById[filter.Id] = filter;

        if (filter.Enabled)
            EnforceSingleFilterSelection(filter.GroupId, filter.Id);
        else
            EnsureFilterSelectionIsValid(filter.GroupId);

        RebuildFilterButtons();
    }

    public void AddSorter(SelectSorterDefinition sorter)
    {
        if (_sortersById.ContainsKey(sorter.Id))
            throw new InvalidOperationException($"Sorter id '{sorter.Id}' already exists.");

        _sorters.Add(sorter);
        _sortersById[sorter.Id] = sorter;

        if (sorter.ActiveByDefault)
        {
            _sortPriority.Remove(sorter.Id);
            _sortPriority.Add(sorter.Id);
        }

        RebuildSortButtons();
    }

    public void AddGroupDefinition(SelectGroupDefinition group)
    {
        _groupsBySorterId[group.SorterId] = group;
    }

    public void AddActionButton(SelectActionButtonDefinition actionButton)
    {
        if (_actionButtonsById.ContainsKey(actionButton.Id))
            throw new InvalidOperationException($"Action button id '{actionButton.Id}' already exists.");

        _actionButtons.Add(actionButton);
        _actionButtonsById[actionButton.Id] = actionButton;
        RebuildActionButtons();
    }

    public void AddToggle(SelectToggleDefinition toggle)
    {
        if (_togglesById.ContainsKey(toggle.Id))
            throw new InvalidOperationException($"Toggle id '{toggle.Id}' already exists.");

        _toggles.Add(toggle);
        _togglesById[toggle.Id] = toggle;
        _toggleStates[toggle.Id] = toggle.CheckedByDefault;
        RebuildToggleButtons();
    }

    public void AddCustomSidebarControl(Control control)
    {
        if (_customControlsContainer is null)
        {
            _pendingCustomSidebarControls.Add(control);
            return;
        }

        EnsureCustomControlsSpacerIfNeeded();
        _customControlsContainer.AddChild(control);
    }

    public void ClearCustomSidebarControls()
    {
        foreach (Control pendingControl in _pendingCustomSidebarControls)
        {
            if (GodotObject.IsInstanceValid(pendingControl) && pendingControl.GetParent() is null)
                pendingControl.QueueFree();
        }

        _pendingCustomSidebarControls.Clear();

        if (_customControlsContainer is null)
            return;

        foreach (Node child in _customControlsContainer.GetChildren())
        {
            _customControlsContainer.RemoveChild(child);
            child.QueueFree();
        }
    }

    public void RefreshNow(bool resetScroll = false)
    {
        if (!_isConfigured || _itemGrid is null)
            return;

        RebuildCurrentLayout(resetScroll, updateExistingViews: true);
    }

    private void RebuildCurrentLayout(bool resetScroll, bool updateExistingViews)
    {
        if (!_isConfigured || _itemGrid is null)
            return;

        CancelPendingMaterialization();
        CancelPendingSearchRefresh();

        _visibleItems.Clear();
        _visibleItems.AddRange(_items.Where(PassesSearchAndFilters));
        _visibleItems.Sort(CompareItems);
        ClearLayoutTracking();

        foreach (IGenericSelectItem item in _items)
        {
            if (item.View is not null && GodotObject.IsInstanceValid(item.View))
                item.View.Visible = false;
        }

        float measuredWidth = GetActiveGroupDefinition() is { } group
            ? RefreshGroupedItems(group)
            : RefreshFlatItems();

        _lastMeasuredItemWidth = measuredWidth > 0f ? measuredWidth : FallbackItemWidth;
        ApplyLayoutSettings();
        UpdateConfirmButtonState();

        UpdateScrollBounds();

        if (resetScroll)
            ScrollToTop();

        if (_materializationMode == SelectMaterializationMode.Lazy)
            MaterializeViewportItemViews(InitialMaterializeBudget, updateExistingViews);
        else
            StartThreadedPreloadThenMaterializeAll();

        UpdateViewportCulling(force: true);
    }

    private void RebuildCurrentLayoutAfterSingleRemoval(bool updateExistingViews)
    {
        if (!_isConfigured || _itemGrid is null)
            return;

        _visibleItems.Clear();
        _visibleItems.AddRange(_items.Where(PassesSearchAndFilters));
        _visibleItems.Sort(CompareItems);
        ClearLayoutTracking();

        float measuredWidth = GetActiveGroupDefinition() is { } group
            ? RefreshGroupedItems(group)
            : RefreshFlatItems();

        _lastMeasuredItemWidth = measuredWidth > 0f ? measuredWidth : FallbackItemWidth;
        ApplyLayoutSettings();
        UpdateConfirmButtonState();
        UpdateScrollBounds();
        ClampScrollToBounds();

        ApplyLayoutToExistingItemViews(updateExistingViews);

        if (_materializationMode == SelectMaterializationMode.Lazy)
            MaterializeViewportItemViews(RemovalMaterializeBudget, updateExistingViews);
        else
            MaterializeAllItemViews(updateExistingViews);

        UpdateViewportCulling(force: true);
    }

    private Dictionary<string, Control> CaptureReusableItemViewsById()
    {
        Dictionary<string, Control> views = new(StringComparer.Ordinal);
        foreach (IGenericSelectItem item in _items)
        {
            if (item.View is null || !GodotObject.IsInstanceValid(item.View) || views.ContainsKey(item.Id))
                continue;

            views[item.Id] = item.View;
        }

        return views;
    }

    private Dictionary<string, Vector2> CaptureItemPositionsById()
    {
        Dictionary<string, Vector2> positions = new(StringComparer.Ordinal);
        foreach (IGenericSelectItem item in _items)
        {
            if (item.View is null || !GodotObject.IsInstanceValid(item.View) || positions.ContainsKey(item.Id))
                continue;

            positions[item.Id] = item.View.Position;
        }

        return positions;
    }

    private Dictionary<string, Vector2> CaptureVisibleItemPositionsById()
    {
        Dictionary<string, Vector2> positions = new(StringComparer.Ordinal);
        foreach (IGenericSelectItem item in _items)
        {
            if (item.View is null
                || !GodotObject.IsInstanceValid(item.View)
                || !item.View.Visible
                || positions.ContainsKey(item.Id))
            {
                continue;
            }

            positions[item.Id] = item.View.Position;
        }

        return positions;
    }

    private void LockRelayoutPositions(IReadOnlyDictionary<Control, Vector2> startPositions)
    {
        foreach ((Control view, Vector2 startPosition) in startPositions)
        {
            if (!GodotObject.IsInstanceValid(view))
                continue;

            _relayoutPositionLockedViews.Add(view);
            view.Position = startPosition;
        }
    }

    private void AnimateRelayoutFrom(IReadOnlyDictionary<Control, Vector2> startPositions)
    {
        if (startPositions.Count == 0)
            return;

        foreach (IGenericSelectItem item in _items)
        {
            if (item.View is null || !GodotObject.IsInstanceValid(item.View))
                continue;

            Control view = item.View;
            if (!startPositions.TryGetValue(view, out Vector2 previousPosition))
                continue;

            if (!_itemLayouts.TryGetValue(item, out SelectItemLayout layout))
            {
                UnlockRelayoutPosition(view);
                continue;
            }

            Vector2 targetPosition = layout.Position;
            if (!IsLayoutNearViewport(layout) || previousPosition.DistanceTo(targetPosition) <= RelayoutTweenMinDistance)
            {
                view.Position = targetPosition;
                UnlockRelayoutPosition(view);
                continue;
            }

            _relayoutTargets[view] = targetPosition;
            view.Position = previousPosition;
            Tween tween = view.CreateTween();
            _relayoutTweens[view] = tween;
            tween.TweenProperty(view, "position", targetPosition, RelayoutTweenSeconds)
                .SetEase(Tween.EaseType.Out)
                .SetTrans(Tween.TransitionType.Cubic);
            tween.Finished += () =>
            {
                if (GodotObject.IsInstanceValid(view))
                    view.Position = targetPosition;

                UnlockRelayoutPosition(view);
            };
        }
    }

    private bool IsRelayoutPositionLocked(Control view)
    {
        return _relayoutPositionLockedViews.Contains(view);
    }

    private void UnlockRelayoutPosition(Control view)
    {
        _relayoutPositionLockedViews.Remove(view);
        _relayoutTargets.Remove(view);
        _relayoutTweens.Remove(view);
    }

    private void CancelRelayoutAnimations(bool applyFinalPositions)
    {
        if (_relayoutPositionLockedViews.Count == 0 && _relayoutTweens.Count == 0)
            return;

        foreach ((Control view, Tween tween) in _relayoutTweens.ToList())
        {
            if (GodotObject.IsInstanceValid(tween))
                tween.Kill();

            if (applyFinalPositions
                && GodotObject.IsInstanceValid(view)
                && _relayoutTargets.TryGetValue(view, out Vector2 targetPosition))
            {
                view.Position = targetPosition;
            }
        }

        _relayoutTweens.Clear();
        _relayoutTargets.Clear();
        _relayoutPositionLockedViews.Clear();
    }

    public void ClearSelection()
    {
        _selectedAmounts.Clear();
        RefreshVisibleItemStates();
        UpdateConfirmButtonState();
    }

    public void SelectItem(string itemId, int amount = 1)
    {
        if (amount <= 0)
        {
            DeselectItem(itemId);
            return;
        }

        if (_options.SelectionMode == SelectSelectionMode.None)
            return;

        if (_options.SelectionMode == SelectSelectionMode.Single)
            _selectedAmounts.Clear();

        _selectedAmounts[itemId] = Math.Clamp(amount, 1, _options.MaxCopiesPerItem);
        RefreshVisibleItemStates();
        UpdateConfirmButtonState();
    }

    public void DeselectItem(string itemId)
    {
        if (_selectedAmounts.Remove(itemId))
        {
            RefreshVisibleItemStates();
            UpdateConfirmButtonState();
        }
    }

    public void ConfirmSelection()
    {
        if (!IsConfirmAllowed())
            return;

        CloseOpenDropdowns();

        List<IGenericSelectItem> selected = _items
            .Where(item => _selectedAmounts.GetValueOrDefault(item.Id) > 0)
            .ToList();

        Confirmed?.Invoke(selected);
    }

    public void CancelSelection()
    {
        // GD.Print($"NGenericSelectScreen CancelSelection fired. Visible={Visible}, IsVisibleInTree={IsVisibleInTree()}, Path={GetPath()}");

        CloseOpenDropdowns();
        Cancelled?.Invoke();
    }

    public bool SetExclusiveFilterSelection(string groupId, string? selectedFilterId, bool resetScroll = true)
    {
        if (!ApplyExclusiveFilterSelection(groupId, selectedFilterId))
            return false;

        RefreshNow(resetScroll);
        return true;
    }

    private void BindSceneNodes()
    {
        _searchLineEdit = GetNodeOrNull<LineEdit>(SearchLineEditPath);
        _clearSearchButton = GetNodeOrNull<BaseButton>(ClearSearchButtonPath);
        _clearSearchNButton = GetNodeOrNull<NButton>(ClearSearchButtonPath);
        _clearSearchClickable = GetNodeOrNull<NClickableControl>(ClearSearchButtonPath);
        _clearSearchControl = GetNodeOrNull<Control>(ClearSearchButtonPath);
        _filterControls = GetNodeOrNull<VBoxContainer>(FilterControlsPath);
        _itemGrid = GetNodeOrNull<Control>(ItemGridPath);
        _scrollMask = _itemGrid?.GetParent() as Control;
        _scrollContainer = GetNodeOrNull<Control>(ScrollContainerPath);
        _scrollbar = _scrollContainer?.GetNodeOrNull<NScrollbar>("Scrollbar");
        _confirmButton = GetNodeOrNull<BaseButton>(ConfirmButtonPath);
        _confirmClickable = GetNodeOrNull<NClickableControl>(ConfirmButtonPath);
        _cancelButton = GetNodeOrNull<BaseButton>(CancelButtonPath);
        _cancelClickable = GetNodeOrNull<NClickableControl>(CancelButtonPath);
        _selectedCountLabel = GetNodeOrNull<Control>(SelectedCountLabelPath);

        if (_searchLineEdit is null)
            GD.PushWarning($"{nameof(NGenericSelectScreen)}: missing search line edit at '{SearchLineEditPath}'. Search will be disabled.");

        if (_filterControls is null)
            GD.PushWarning($"{nameof(NGenericSelectScreen)}: missing filter controls at '{FilterControlsPath}'. Filters will still work, but no UI will be built.");

        if (_itemGrid is null)
            GD.PushError($"{nameof(NGenericSelectScreen)}: missing item grid at '{ItemGridPath}'. The select screen cannot render items.");

        ApplyLocalizedSceneText();
    }

    private void ApplyLocalizedSceneText()
    {
        if (_searchLineEdit is not null)
            _searchLineEdit.PlaceholderText = GameLoc("main_menu_ui", "CARD_LIBRARY_SEARCH", SelectScreenLoc.Text("SEARCH", "Search"));
    }

    private static string GameLoc(string table, string key, string fallback)
    {
        try
        {
            return LocString.Exists(table, key)
                ? new LocString(table, key).GetFormattedText()
                : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private void SubscribeToLocaleChanges()
    {
        if (_isSubscribedToLocaleChanges)
            return;

        LocString.SubscribeToLocaleChange(OnLocaleChanged);
        _isSubscribedToLocaleChanges = true;
    }

    private void UnsubscribeFromLocaleChanges()
    {
        if (!_isSubscribedToLocaleChanges)
            return;

        LocString.UnsubscribeToLocaleChange(OnLocaleChanged);
        _isSubscribedToLocaleChanges = false;
    }

    private void OnLocaleChanged()
    {
        if (!IsConfiguredForCurrentLocale)
            LocaleChanged?.Invoke();

        RefreshLocaleSensitiveText();
    }

    private static string GetCurrentLocaleLanguage()
    {
        return LocManager.Instance?.Language ?? string.Empty;
    }

    private void RefreshLocaleSensitiveText()
    {
        ApplyLocalizedSceneText();
        RebuildSortButtons();
        RebuildToggleButtons();
        RebuildFilterButtons();
        UpdateConfirmButtonState();
        RefreshVisibleItemStates();
    }

    private void BuildUtilityContainersIfMissing()
    {
        if (_filterControls is null)
            return;

        _sortButtonsContainer = _filterControls.GetNodeOrNull<Container>("SortButtons");
        if (_sortButtonsContainer is null)
        {
            _sortButtonsContainer = new VBoxContainer
            {
                Name = "SortButtons",
                SizeFlagsHorizontal = SizeFlags.ExpandFill
            };
            _filterControls.AddChild(_sortButtonsContainer);
        }

        _customControlsContainer = _filterControls.GetNodeOrNull<VBoxContainer>("CustomControls");
        if (_customControlsContainer is null)
        {
            _customControlsContainer = new VBoxContainer
            {
                Name = "CustomControls",
                SizeFlagsHorizontal = SizeFlags.ExpandFill
            };
            _filterControls.AddChild(_customControlsContainer);
        }

        _filtersContainer = _filterControls.GetNodeOrNull<VBoxContainer>("FilterGroups");
        if (_filtersContainer is null)
        {
            _filtersContainer = new VBoxContainer
            {
                Name = "FilterGroups",
                SizeFlagsHorizontal = SizeFlags.ExpandFill
            };
            _filterControls.AddChild(_filtersContainer);
        }

        _actionButtonsContainer = _filterControls.GetNodeOrNull<VBoxContainer>("ActionButtons");
        if (_actionButtonsContainer is null)
        {
            _actionButtonsContainer = new VBoxContainer
            {
                Name = "ActionButtons",
                SizeFlagsHorizontal = SizeFlags.ExpandFill
            };
            _filterControls.AddChild(_actionButtonsContainer);
        }

        _togglesContainer = _filterControls.GetNodeOrNull<VBoxContainer>("Toggles");
        if (_togglesContainer is null)
        {
            _togglesContainer = new VBoxContainer
            {
                Name = "Toggles",
                SizeFlagsHorizontal = SizeFlags.ExpandFill
            };
            _filterControls.AddChild(_togglesContainer);
        }

        _sortButtonsContainer.SizeFlagsVertical = SizeFlags.ShrinkBegin;
        _filtersContainer.SizeFlagsVertical = SizeFlags.ShrinkBegin;
        _actionButtonsContainer.SizeFlagsVertical = SizeFlags.ShrinkBegin;
        _customControlsContainer.SizeFlagsVertical = SizeFlags.ShrinkBegin;
        _togglesContainer.SizeFlagsVertical = SizeFlags.ShrinkBegin;

        if (_sortButtonsContainer.GetParent() == _filterControls && _filtersContainer.GetParent() == _filterControls)
            _filterControls.MoveChild(_filtersContainer, _sortButtonsContainer.GetIndex() + 1);

        if (_filtersContainer.GetParent() == _filterControls && _actionButtonsContainer.GetParent() == _filterControls)
            _filterControls.MoveChild(_actionButtonsContainer, _filtersContainer.GetIndex() + 1);

        if (_actionButtonsContainer.GetParent() == _filterControls && _customControlsContainer.GetParent() == _filterControls)
            _filterControls.MoveChild(_customControlsContainer, _actionButtonsContainer.GetIndex() + 1);

        if (_customControlsContainer.GetParent() == _filterControls && _togglesContainer.GetParent() == _filterControls)
            _filterControls.MoveChild(_togglesContainer, _customControlsContainer.GetIndex() + 1);
    }

    private void EnsureGameScrollbar()
    {
        if (_scrollContainer is null)
            return;

        _scrollbar ??= _scrollContainer.GetNodeOrNull<NScrollbar>("Scrollbar");
        if (_scrollbar is null)
        {
            _scrollbar = CreateGameScrollbar();
            _scrollContainer.AddChild(_scrollbar);
        }

        _scrollbar.Name = "Scrollbar";
        _scrollbar.MinValue = 0;
        _scrollbar.MaxValue = 100;
        _scrollbar.Step = 1;
        _scrollbar.Visible = false;
        _scrollbar.MouseFilter = MouseFilterEnum.Stop;
        _scrollbar.SetAnchorsPreset(LayoutPreset.RightWide);
        _scrollbar.OffsetLeft = -58f;
        _scrollbar.OffsetTop = 130f;
        _scrollbar.OffsetRight = -10f;
        _scrollbar.OffsetBottom = -130f;

        _scrollbar.Connect(NScrollbar.SignalName.MousePressed, Callable.From<InputEvent>(_ => _scrollbarPressed = true));
        _scrollbar.Connect(NScrollbar.SignalName.MouseReleased, Callable.From<InputEvent>(_ => _scrollbarPressed = false));
    }

    private void EnsureActionButtons()
    {
        if (_cancelClickable is null)
        {
            NBackButton backButton = NLoadoutBackButtonFactory.Create();
            backButton.Name = "BackButton";
            AddChild(backButton);
            _cancelClickable = backButton;
        }

        if (_confirmClickable is null)
        {
            NConfirmButton confirmButton = CreateConfirmButton();
            confirmButton.Name = "ConfirmButton";
            confirmButton.Visible = false;
            AddChild(confirmButton);
            _confirmClickable = confirmButton;
        }

        _cancelButton = GetNodeOrNull<BaseButton>(CancelButtonPath);
        _confirmButton = GetNodeOrNull<BaseButton>(ConfirmButtonPath);
        _confirmClickable = GetNodeOrNull<NClickableControl>(ConfirmButtonPath);
    }

    private void BindSceneSignals()
    {
        if (_searchLineEdit is not null)
        {
            _query = _searchLineEdit.Text;
            _searchLineEdit.TextChanged += OnSearchTextChanged;
            _searchLineEdit.TextSubmitted += OnSearchTextSubmitted;
        }

        if (_clearSearchNButton is not null)
            _clearSearchNButton.Connect(NClickableControl.SignalName.Released, Callable.From((Action<NButton>)(_ => ClearSearch())));
        else if (_clearSearchButton is not null)
            _clearSearchButton.Pressed += ClearSearch;
        else if (_clearSearchClickable is not null)
            _clearSearchClickable.Connect(NClickableControl.SignalName.Released, Callable.From<NClickableControl>(_ => ClearSearch()));
        else if (_clearSearchControl is not null)
            _clearSearchControl.GuiInput += OnClearSearchControlGuiInput;

        if (_confirmButton is not null)
            _confirmButton.Pressed += ConfirmSelection;

        if (_confirmClickable is not null)
            _confirmClickable.Connect(NClickableControl.SignalName.Released, Callable.From<NClickableControl>(_ => ConfirmSelection()));

        if (_cancelButton is not null)
            _cancelButton.Pressed += CancelSelection;

        if (_cancelClickable is not null)
            _cancelClickable.Connect(NClickableControl.SignalName.Released, Callable.From<NClickableControl>(_ =>
            {
                NLoadoutBackButtonFactory.ResetVisualState(_cancelClickable);
                CancelSelection();
            }));
    }

    private void OnSearchTextChanged(string text)
    {
        _query = text ?? string.Empty;

        if (SearchDelayMsec <= 0)
        {
            RefreshNow();
            return;
        }

        _searchDelayCts?.Cancel();
        _searchDelayCts?.Dispose();
        _searchDelayCts = new System.Threading.CancellationTokenSource();
        _ = RefreshAfterSearchDelayAsync(_searchDelayCts.Token);
    }

    private async System.Threading.Tasks.Task RefreshAfterSearchDelayAsync(System.Threading.CancellationToken token)
    {
        try
        {
            await System.Threading.Tasks.Task.Delay(SearchDelayMsec, token);
        }
        catch (System.Threading.Tasks.TaskCanceledException)
        {
            return;
        }

        if (!token.IsCancellationRequested)
            CallDeferred(nameof(RefreshNow), false);
    }

    private void OnSearchTextSubmitted(string text)
    {
        _query = text ?? string.Empty;
        RefreshNow();
    }

    private void ClearSearch()
    {
        if (_searchLineEdit is not null)
            _searchLineEdit.Text = string.Empty;

        _query = string.Empty;
        RefreshNow(resetScroll: true);
    }

    private void OnClearSearchControlGuiInput(InputEvent inputEvent)
    {
        if (inputEvent is not InputEventMouseButton { ButtonIndex: MouseButton.Left } mouseButton)
            return;

        if (mouseButton.Pressed)
        {
            _clearSearchPressStarted = true;
            return;
        }

        if (!_clearSearchPressStarted)
            return;

        _clearSearchPressStarted = false;
        ClearSearch();
        _clearSearchControl?.AcceptEvent();
    }

    private void CancelPendingSearchRefresh()
    {
        _searchDelayCts?.Cancel();
        _searchDelayCts?.Dispose();
        _searchDelayCts = null;
    }

    private void CancelPendingMaterialization()
    {
        _layoutGeneration++;

        CancellationTokenSource? preloadCts = _preloadCts;
        _preloadCts = null;
        if (preloadCts is not null)
        {
            preloadCts.Cancel();
            preloadCts.Dispose();
        }

        Interlocked.Exchange(ref _completedPreloadGeneration, -1);
        while (_preloadWarnings.TryDequeue(out _))
        {
            // discard stale warnings from cancelled generations
        }
    }

    private void ClearLayoutTracking()
    {
        _visibleLayoutNodes.Clear();
        _visibleLayoutNodeSet.Clear();
        _itemLayouts.Clear();
        _itemLayoutOrder.Clear();
        _itemsUpdatedForCurrentLayout.Clear();
        _lastCullScrollY = float.NaN;
        _lastCullNodeCount = -1;
    }

    private void RebuildCustomControls()
    {
        if (_customControlsContainer is null)
            return;

        _customControlsContainer.SizeFlagsHorizontal = SizeFlags.ExpandFill;

        if (_pendingCustomSidebarControls.Count > 0)
            EnsureCustomControlsSpacerIfNeeded();

        foreach (Control control in _pendingCustomSidebarControls.ToList())
        {
            if (!GodotObject.IsInstanceValid(control))
                continue;

            control.GetParent()?.RemoveChild(control);
            _customControlsContainer.AddChild(control);
        }

        _pendingCustomSidebarControls.Clear();
    }

    private void EnsureCustomControlsSpacerIfNeeded()
    {
        if (_customControlsContainer is null)
            return;

        if (_actionButtons.Count > 0)
        {
            RemoveSpacer(_customControlsContainer, "CustomControlsTopSpacer");
            return;
        }

        if (_customControlsContainer.GetChildren().OfType<Control>().Any(child => child.Name == "CustomControlsTopSpacer" && !child.IsQueuedForDeletion()))
            return;

        Control spacer = CreateSidebarSpacer("CustomControlsTopSpacer");
        _customControlsContainer.AddChild(spacer);
        _customControlsContainer.MoveChild(spacer, 0);
    }

    private static Control CreateSidebarSpacer(string name)
    {
        return new Control
        {
            Name = name,
            MouseFilter = MouseFilterEnum.Ignore,
            CustomMinimumSize = new Vector2(0f, 52f),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ShrinkBegin
        };
    }

    private static void RemoveSpacer(Container container, string spacerName)
    {
        foreach (Control spacer in container.GetChildren().OfType<Control>().Where(child => child.Name == spacerName).ToList())
        {
            container.RemoveChild(spacer);
            spacer.QueueFree();
        }
    }

    private void RebuildSortButtons()
    {
        if (_sortButtonsContainer is null)
            return;

        foreach (Node child in _sortButtonsContainer.GetChildren())
            child.QueueFree();

        _sortButtonsById.Clear();

        foreach (SelectSorterDefinition sorter in _sorters)
        {
            string sorterId = sorter.Id;
            NCardViewSortButton button = SceneHelper.Instantiate<NCardViewSortButton>(SelectSortButtonInnerPath);
            button.Name = MakeSafeNodeName($"{sorterId}SortButton");
            button.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            button.CustomMinimumSize = new Vector2(0f, 42f);
            button.Connect(NClickableControl.SignalName.Released, Callable.From<NClickableControl>(_ => OnSorterPressed(sorterId)));
            _sortButtonsContainer.AddChild(button);
            button.SetLabel(sorter.Label);
            button.IsDescending = sorter.IsDescending;
            _sortButtonsById[sorterId] = button;
        }

        UpdateSortButtonLabels();
    }

    private void RebuildActionButtons()
    {
        if (_actionButtonsContainer is null)
            return;

        foreach (Node child in _actionButtonsContainer.GetChildren())
            child.QueueFree();

        _actionButtonNodesById.Clear();

        if (_actionButtons.Count == 0)
            return;

        _actionButtonsContainer.AddChild(CreateSidebarSpacer("ActionButtonsTopSpacer"));

        foreach (SelectActionButtonDefinition actionButton in _actionButtons)
        {
            string actionButtonId = actionButton.Id;
            NLoadoutActionButton button = new();
            button.Name = MakeSafeNodeName($"{actionButtonId}ActionButton");
            button.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            button.CustomMinimumSize = new Vector2(0f, 42f);
            button.Init(actionButtonId, actionButton.Label, actionButton.Icon);
            button.Connect(NClickableControl.SignalName.Released, Callable.From<NClickableControl>(_ => OnActionButtonPressed(actionButtonId)));
            _actionButtonsContainer.AddChild(button);
            _actionButtonNodesById[actionButtonId] = button;
        }

        EnsureCustomControlsSpacerIfNeeded();
    }

    private void OnActionButtonPressed(string actionButtonId)
    {
        if (!_actionButtonsById.TryGetValue(actionButtonId, out SelectActionButtonDefinition? actionButton))
            return;

        try
        {
            actionButton.OnPressed(this);
        }
        catch (Exception exception)
        {
            GD.PushError($"Select screen action button '{actionButtonId}' failed: {exception}");
        }
    }

    private void OnSorterPressed(string sorterId)
    {
        if (!_sortersById.TryGetValue(sorterId, out SelectSorterDefinition? sorter))
            return;

        bool wasActive = _sortPriority.Contains(sorterId);
        if (wasActive)
            sorter.IsDescending = !sorter.IsDescending;
        else
            sorter.IsDescending = sorter.DescendingByDefault;

        _sortPriority.Remove(sorterId);
        _sortPriority.Insert(0, sorterId);

        UpdateSortButtonLabels();
        RefreshNow();
    }

    private void UpdateSortButtonLabels()
    {
        foreach (SelectSorterDefinition sorter in _sorters)
        {
            if (!_sortButtonsById.TryGetValue(sorter.Id, out NCardViewSortButton? button))
                continue;

            button.SetLabel(sorter.Label);
            button.IsDescending = sorter.IsDescending;
        }
    }

    private void RebuildToggleButtons()
    {
        if (_togglesContainer is null)
            return;

        foreach (Node child in _togglesContainer.GetChildren())
            child.QueueFree();

        _toggleButtonsById.Clear();

        foreach (SelectToggleDefinition toggle in _toggles)
        {
            bool isChecked = _toggleStates.GetValueOrDefault(toggle.Id, toggle.CheckedByDefault);
            NLoadoutToggle button = new();
            button.Name = MakeSafeNodeName($"{toggle.Id}Toggle");
            button.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            button.Init(toggle.Id, toggle.Label, isChecked);
            button.Connect(NLoadoutToggle.SignalName.Toggled, Callable.From<NLoadoutToggle>(OnToggleChanged));
            _togglesContainer.AddChild(button);
            _toggleButtonsById[toggle.Id] = button;
        }
    }

    private void OnToggleChanged(NLoadoutToggle toggle)
    {
        if (!_togglesById.ContainsKey(toggle.ToggleId))
            return;

        _toggleStates[toggle.ToggleId] = toggle.IsChecked;
        RefreshVisibleItemStates();
    }

    private void RebuildFilterButtons()
    {
        if (_filtersContainer is null)
            return;

        foreach (Node child in _filtersContainer.GetChildren())
            child.QueueFree();

        _filterDropdownsByGroupId.Clear();

        IEnumerable<string> groupOrder = _filterGroupOrder
            .Concat(_filters.Select(filter => filter.GroupId))
            .Distinct(StringComparer.Ordinal);

        foreach (string groupId in groupOrder)
        {
            List<SelectFilterDefinition> groupFilters = _filters
                .Where(filter => string.Equals(filter.GroupId, groupId, StringComparison.Ordinal))
                .ToList();

            if (groupFilters.Count == 0)
                continue;

            SelectFilterGroupDefinition group = _filterGroupsById.TryGetValue(groupId, out SelectFilterGroupDefinition? groupDef)
                ? groupDef
                : new SelectFilterGroupDefinition(groupId, groupId);

            EnsureFilterSelectionIsValid(groupId);

            List<LoadoutDropdownOption> options = new();
            options.Add(new LoadoutDropdownOption(AllFilterOptionId, SelectScreenLoc.Text("ALL", "All")));

            foreach (SelectFilterDefinition filter in groupFilters)
                options.Add(new LoadoutDropdownOption(filter.Id, filter.Label));

            string selectedOptionId = GetSelectedFilterIdForGroup(groupId) ?? AllFilterOptionId;
            NLoadoutDropdown dropdown = new();
            dropdown.Name = MakeSafeNodeName($"{groupId}FilterDropdown");
            dropdown.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            dropdown.CustomMinimumSize = new Vector2(256f, 52f);
            dropdown.SelectedItemChanged += selectedId => OnFilterDropdownSelected(groupId, selectedId);
            _filtersContainer.AddChild(dropdown);
            dropdown.SetItems(group.Label, options, selectedOptionId);
            _filterDropdownsByGroupId[groupId] = dropdown;
        }
    }

    private void OnFilterDropdownSelected(string groupId, string selectedOptionId)
    {
        if (_isSyncingFilterDropdowns)
            return;

        if (selectedOptionId == AllFilterOptionId)
        {
            SetExclusiveFilterSelection(groupId, null);
            return;
        }

        if (_filtersById.TryGetValue(selectedOptionId, out SelectFilterDefinition? filter))
            SetExclusiveFilterSelection(filter.GroupId, filter.Id);
    }

    private void EnsureFilterGroupExists(string groupId)
    {
        if (_filterGroupsById.ContainsKey(groupId))
            return;

        _filterGroupsById[groupId] = new SelectFilterGroupDefinition(groupId, groupId);
        _filterGroupOrder.Add(groupId);
    }

    private void EnforceSingleFilterSelection(string groupId, string selectedFilterId)
    {
        ApplyExclusiveFilterSelection(groupId, selectedFilterId);
    }

    private void EnsureFilterSelectionIsValid(string groupId)
    {
        List<SelectFilterDefinition> groupFilters = GetFiltersInGroup(groupId);
        if (groupFilters.Count == 0)
            return;

        SelectFilterDefinition? selectedFilter = groupFilters.FirstOrDefault(filter => filter.Enabled);
        if (selectedFilter is not null)
        {
            ApplyExclusiveFilterSelection(groupId, selectedFilter.Id);
            return;
        }

        if (GroupRequiresSelection(groupId))
        {
            ApplyExclusiveFilterSelection(groupId, groupFilters[0].Id);
            return;
        }

        SyncFilterDropdown(groupId);
    }

    private bool GroupRequiresSelection(string groupId)
    {
        return _filterGroupsById.TryGetValue(groupId, out SelectFilterGroupDefinition? group)
            && group.RequireSelection;
    }

    private List<SelectFilterDefinition> GetFiltersInGroup(string groupId)
    {
        return _filters
            .Where(filter => string.Equals(filter.GroupId, groupId, StringComparison.Ordinal))
            .ToList();
    }

    private string? GetSelectedFilterIdForGroup(string groupId)
    {
        return _filters
            .FirstOrDefault(filter => string.Equals(filter.GroupId, groupId, StringComparison.Ordinal) && filter.Enabled)
            ?.Id;
    }

    private bool ApplyExclusiveFilterSelection(string groupId, string? selectedFilterId)
    {
        List<SelectFilterDefinition> groupFilters = GetFiltersInGroup(groupId);
        if (groupFilters.Count == 0)
            return false;

        if (selectedFilterId is null && GroupRequiresSelection(groupId))
            selectedFilterId = groupFilters[0].Id;

        if (selectedFilterId is not null && groupFilters.All(filter => !string.Equals(filter.Id, selectedFilterId, StringComparison.Ordinal)))
            return false;

        bool changed = false;

        foreach (SelectFilterDefinition filter in groupFilters)
        {
            bool shouldEnable = selectedFilterId is not null && string.Equals(filter.Id, selectedFilterId, StringComparison.Ordinal);
            if (filter.Enabled != shouldEnable)
            {
                filter.Enabled = shouldEnable;
                changed = true;
            }
        }

        SyncFilterDropdown(groupId);
        return changed;
    }

    private void SyncFilterDropdown(string groupId)
    {
        if (!_filterDropdownsByGroupId.TryGetValue(groupId, out NLoadoutDropdown? dropdown))
            return;

        _isSyncingFilterDropdowns = true;
        dropdown.SetSelectedItem(GetSelectedFilterIdForGroup(groupId) ?? AllFilterOptionId);
        _isSyncingFilterDropdowns = false;
    }

    private bool PassesSearchAndFilters(IGenericSelectItem item)
    {
        if (!string.IsNullOrWhiteSpace(_query))
        {
            string normalizedQuery = SelectText.Normalize(_query);
            if (!item.MatchesSearch(normalizedQuery))
                return false;
        }

        if (_customVisibilityPredicate is not null && !_customVisibilityPredicate(item))
            return false;

        foreach (string groupId in _filters.Select(filter => filter.GroupId).Distinct(StringComparer.Ordinal))
        {
            List<SelectFilterDefinition> active = _filters
                .Where(filter => string.Equals(filter.GroupId, groupId, StringComparison.Ordinal) && filter.Enabled)
                .ToList();

            if (active.Count == 0)
            {
                if (GroupRequiresSelection(groupId))
                    return false;

                continue;
            }

            bool matchesGroup = active.Any(filter => filter.Predicate(item));
            if (!matchesGroup)
                return false;
        }

        return true;
    }

    private int CompareItems(IGenericSelectItem left, IGenericSelectItem right)
    {
        foreach (string sorterId in _sortPriority)
        {
            if (!_sortersById.TryGetValue(sorterId, out SelectSorterDefinition? sorter))
                continue;

            int result = sorter.Compare(left, right);
            if (result != 0)
                return result;
        }

        return left.OriginalIndex.CompareTo(right.OriginalIndex);
    }

    private SelectGroupDefinition? GetActiveGroupDefinition()
    {
        string? activeSorterId = _sortPriority.FirstOrDefault();
        if (activeSorterId is null)
            return null;

        return _groupsBySorterId.TryGetValue(activeSorterId, out SelectGroupDefinition? group)
            ? group
            : null;
    }

    private float RefreshFlatItems()
    {
        ClearGeneratedGroupContainers();

        float measuredWidth = 0f;
        Vector2 itemSize = ResolveConfiguredItemSize();
        int columns = ResolveColumnCount(itemSize, 0f);
        float contentWidth = CalculateGridWidth(columns, itemSize.X);
        float startX = CalculateCenteredStartX(contentWidth, 0f);
        float startY = _layout.PaddingTop;

        for (int i = 0; i < _visibleItems.Count; i++)
        {
            IGenericSelectItem item = _visibleItems[i];
            QueueItemLayout(item, new Vector2(
                startX + (i % columns) * (itemSize.X + ItemHorizontalGap),
                startY + (i / columns) * (itemSize.Y + ItemVerticalGap)), itemSize, i);

            measuredWidth = Math.Max(measuredWidth, itemSize.X);
        }

        int rows = _visibleItems.Count == 0 ? 0 : ((_visibleItems.Count - 1) / columns) + 1;
        float contentHeight = startY + rows * itemSize.Y + Math.Max(0, rows - 1) * ItemVerticalGap + _layout.PaddingBottom;
        SetContentSize(Math.Max(GetViewportContentWidth(), contentWidth + _layout.PaddingLeft + _layout.PaddingRight), contentHeight);
        return measuredWidth;
    }

    private float RefreshGroupedItems(SelectGroupDefinition group)
    {
        ClearGeneratedGroupContainers();

        Dictionary<string, List<IGenericSelectItem>> itemsByKey = new(StringComparer.Ordinal);
        foreach (IGenericSelectItem item in _visibleItems)
        {
            string key = group.KeySelector(item);
            if (!itemsByKey.TryGetValue(key, out List<IGenericSelectItem>? items))
            {
                items = new List<IGenericSelectItem>();
                itemsByKey[key] = items;
            }

            items.Add(item);
        }

        bool descending = _sortersById.TryGetValue(group.SorterId, out SelectSorterDefinition? sorter) && sorter.IsDescending;
        List<string> orderedKeys = group.GetGroupOrder(descending)
            .Concat(itemsByKey.Keys)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        float measuredWidth = 0f;
        int visibleIndex = 0;
        float y = _layout.PaddingTop;
        Vector2 itemSize = ResolveConfiguredItemSize();
        const float groupGridIndent = 48f;
        int columns = ResolveColumnCount(itemSize, groupGridIndent);
        float groupGridWidth = CalculateGridWidth(columns, itemSize.X);
        float headerWidth = Math.Max(groupGridWidth + groupGridIndent, Math.Min(GetViewportContentWidth(), 1020f));
        float groupStartX = CalculateCenteredStartX(headerWidth, 0f);
        float gridStartX = groupStartX + groupGridIndent;

        foreach (string key in orderedKeys)
        {
            List<IGenericSelectItem> groupItems = itemsByKey.GetValueOrDefault(key) ?? new List<IGenericSelectItem>();
            SelectGroupHeader header = group.HeaderSelector(key);
            bool hasVisibleChildGroup = header.ChildGroupPrefix is not null
                && itemsByKey.Any(pair => pair.Key.StartsWith(header.ChildGroupPrefix, StringComparison.Ordinal) && pair.Value.Count > 0);

            if (groupItems.Count == 0 && !header.ShowWhenEmpty && !hasVisibleChildGroup)
                continue;

            Control groupContainer = CreateGroupHeader(key, header);
            groupContainer.Position = new Vector2(groupStartX, y);
            groupContainer.Size = new Vector2(headerWidth, _layout.GroupHeaderHeight);
            _itemGrid!.AddChild(groupContainer);
            _generatedGroupContainers.Add(groupContainer);
            TrackVisibleLayoutNode(groupContainer);
            y += _layout.GroupHeaderHeight + _layout.GroupHeaderGap;

            if (groupItems.Count == 0)
            {
                if (!hasVisibleChildGroup)
                    y += _layout.GroupSectionGap;

                continue;
            }

            for (int i = 0; i < groupItems.Count; i++)
            {
                IGenericSelectItem item = groupItems[i];
                QueueItemLayout(item, new Vector2(
                    gridStartX + (i % columns) * (itemSize.X + ItemHorizontalGap),
                    y + (i / columns) * (itemSize.Y + ItemVerticalGap)), itemSize, visibleIndex);

                measuredWidth = Math.Max(measuredWidth, itemSize.X);
                visibleIndex++;
            }

            int rows = groupItems.Count == 0 ? 0 : ((groupItems.Count - 1) / columns) + 1;
            y += rows * itemSize.Y + Math.Max(0, rows - 1) * ItemVerticalGap + _layout.GroupSectionGap;
        }

        SetContentSize(Math.Max(GetViewportContentWidth(), headerWidth + _layout.PaddingLeft + _layout.PaddingRight), y + _layout.PaddingBottom);
        return measuredWidth;
    }

    private readonly record struct SelectItemLayout(Vector2 Position, Vector2 Size, int VisibleIndex);

    private void QueueItemLayout(IGenericSelectItem item, Vector2 position, Vector2 size, int visibleIndex)
    {
        _itemLayouts[item] = new SelectItemLayout(position, size, visibleIndex);
        _itemLayoutOrder.Add(item);
    }

    private void TrackVisibleLayoutNode(Control control)
    {
        if (_visibleLayoutNodeSet.Add(control))
            _visibleLayoutNodes.Add(control);
    }

    private int MaterializeViewportItemViews(int maxCount, bool updateExistingViews = false)
    {
        if (_itemGrid is null || maxCount <= 0)
            return 0;

        int materialized = MaterializeLayoutWindow(BuildViewportMaterializeRect(_scrollY, 0.5f, 0.75f), maxCount, updateExistingViews);
        if (materialized >= maxCount)
            return materialized;

        if (Mathf.Abs(_targetScrollY - _scrollY) > Math.Max(1f, ResolveConfiguredItemSize().Y + ItemVerticalGap) * 0.5f)
            materialized += MaterializeLayoutWindow(BuildViewportMaterializeRect(_targetScrollY, 0.5f, 1.5f), maxCount - materialized, updateExistingViews);

        if (materialized >= maxCount)
            return materialized;

        materialized += MaterializeLayoutWindow(BuildViewportMaterializeRect(_targetScrollY, MaterializeRowsBehind, MaterializeRowsAhead), maxCount - materialized, updateExistingViews);
        return materialized;
    }

    private void MaterializeAllItemViews(bool updateExistingViews = true)
    {
        if (_itemGrid is null)
            return;

        foreach (IGenericSelectItem item in _itemLayoutOrder)
        {
            if (!_itemLayouts.TryGetValue(item, out SelectItemLayout layout))
                continue;

            MaterializeItemView(item, layout, updateExistingViews);
        }
    }

    private void ApplyLayoutToExistingItemViews(bool updateExistingViews)
    {
        foreach (IGenericSelectItem item in _itemLayoutOrder)
        {
            if (item.View is null
                || !GodotObject.IsInstanceValid(item.View)
                || !_itemLayouts.TryGetValue(item, out SelectItemLayout layout))
            {
                continue;
            }

            MaterializeItemView(item, layout, updateExistingViews);
        }
    }

    private Rect2 BuildViewportMaterializeRect(float scrollY, float rowsBehind, float rowsAhead)
    {
        if (_itemGrid is null)
            return new Rect2();

        float viewportHeight = _scrollMask?.Size.Y ?? _itemGrid.GetParent<Control>()?.Size.Y ?? Size.Y;
        float rowHeight = Math.Max(1f, ResolveConfiguredItemSize().Y + ItemVerticalGap);
        float top = Mathf.Clamp(scrollY, 0f, _maxScrollY) - rowHeight * rowsBehind;
        float height = viewportHeight + rowHeight * (rowsBehind + rowsAhead);
        return new Rect2(new Vector2(0f, top), new Vector2(_itemGrid.Size.X, height));
    }

    private int MaterializeLayoutWindow(Rect2 materializeRect, int maxCount, bool updateExistingViews)
    {
        if (maxCount <= 0 || _itemLayoutOrder.Count == 0)
            return 0;

        int materialized = 0;
        float top = materializeRect.Position.Y;
        float bottom = materializeRect.End.Y;
        for (int i = FindFirstLayoutIndexAtOrAfter(top); i < _itemLayoutOrder.Count; i++)
        {
            IGenericSelectItem item = _itemLayoutOrder[i];
            if (!_itemLayouts.TryGetValue(item, out SelectItemLayout layout))
                continue;

            if (layout.Position.Y > bottom)
                break;

            if (layout.Position.Y + layout.Size.Y < top)
                continue;

            bool needsView = item.View is null || !GodotObject.IsInstanceValid(item.View);
            bool needsStateUpdate = updateExistingViews || !_itemsUpdatedForCurrentLayout.Contains(item);
            if (!needsView && !needsStateUpdate)
                continue;

            if (MaterializeItemView(item, layout, updateExistingViews) && needsView)
                materialized++;

            if (materialized >= maxCount)
                break;
        }

        return materialized;
    }

    private int FindFirstLayoutIndexAtOrAfter(float y)
    {
        int low = 0;
        int high = _itemLayoutOrder.Count - 1;
        int result = _itemLayoutOrder.Count;

        while (low <= high)
        {
            int middle = low + ((high - low) / 2);
            IGenericSelectItem item = _itemLayoutOrder[middle];
            if (!_itemLayouts.TryGetValue(item, out SelectItemLayout layout))
            {
                result = middle;
                high = middle - 1;
                continue;
            }

            if (layout.Position.Y + layout.Size.Y >= y)
            {
                result = middle;
                high = middle - 1;
            }
            else
            {
                low = middle + 1;
            }
        }

        return result;
    }

    private void FinalizeEagerMaterialization(bool warnOnMismatch)
    {
        if (_itemGrid is null)
            return;

        int visibleItemViews = 0;
        foreach (IGenericSelectItem item in _itemLayoutOrder)
        {
            if (!_itemLayouts.TryGetValue(item, out SelectItemLayout layout))
                continue;

            if (MaterializeItemView(item, layout, updateExistingView: true))
                visibleItemViews++;
        }

        if (!warnOnMismatch || visibleItemViews == _itemLayoutOrder.Count || _lastEagerMismatchWarningGeneration == _layoutGeneration)
            return;

        _lastEagerMismatchWarningGeneration = _layoutGeneration;
        GD.PushWarning(
            $"Loadout select screen '{Name}' eager materialization mismatch: " +
            $"layouts={_itemLayoutOrder.Count}, visibleItemViews={visibleItemViews}, layoutNodes={_visibleLayoutNodes.Count}.");
    }

    private void StartThreadedPreloadThenMaterializeAll()
    {
        if (_itemGrid is null)
            return;

        IGenericSelectItem[] preloadItems = _itemLayoutOrder
            .Where(item => item.HasPreloadResources)
            .ToArray();

        if (preloadItems.Length == 0)
        {
            FinalizeEagerMaterialization(warnOnMismatch: true);
            return;
        }

        ulong generation = _layoutGeneration;
        string screenName = Name;
        CancellationTokenSource preloadCts = new();
        _preloadCts = preloadCts;
        _ = ThreadedPreloadThenMaterializeAllAsync(generation, screenName, preloadItems, preloadCts.Token);
    }

    private async Task ThreadedPreloadThenMaterializeAllAsync(
        ulong generation,
        string screenName,
        IReadOnlyList<IGenericSelectItem> preloadItems,
        CancellationToken token)
    {
        int maxDegreeOfParallelism = Math.Max(1, Math.Min(preloadItems.Count, System.Environment.ProcessorCount - 1));

        try
        {
            await Parallel.ForEachAsync(
                preloadItems,
                new ParallelOptions
                {
                    CancellationToken = token,
                    MaxDegreeOfParallelism = maxDegreeOfParallelism
                },
                (item, cancellationToken) =>
                {
                    try
                    {
                        item.PreloadResources(cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        _preloadWarnings.Enqueue(
                            $"Loadout select screen '{screenName}' failed to preload item '{item.Id}' ({item.Name}): {exception}");
                    }

                    return ValueTask.CompletedTask;
                });
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!token.IsCancellationRequested)
            Interlocked.Exchange(ref _completedPreloadGeneration, unchecked((long)generation));
    }

    private void CompleteThreadedPreloadIfReady()
    {
        long completedGeneration = Interlocked.Read(ref _completedPreloadGeneration);
        if (completedGeneration < 0)
            return;

        if (completedGeneration != unchecked((long)_layoutGeneration))
        {
            Interlocked.CompareExchange(ref _completedPreloadGeneration, -1, completedGeneration);
            return;
        }

        Interlocked.Exchange(ref _completedPreloadGeneration, -1);

        _preloadCts?.Dispose();
        _preloadCts = null;

        FlushThreadedPreloadWarnings();

        if (!_isConfigured || _itemGrid is null || !IsInsideTree())
            return;

        FinalizeEagerMaterialization(warnOnMismatch: true);
        UpdateViewportCulling(force: true);
    }

    private void FlushThreadedPreloadWarnings()
    {
        while (_preloadWarnings.TryDequeue(out string? warning))
            GD.PushWarning(warning);
    }

    private void ScheduleDeferredVisibleRefresh()
    {
        if (!_isConfigured || !IsInsideTree())
            return;

        ulong generation = _layoutGeneration;
        if (_scheduledVisibleRefreshGeneration == generation)
            return;

        _scheduledVisibleRefreshGeneration = generation;
        _ = DeferredVisibleRefreshAsync(generation);
    }

    private async System.Threading.Tasks.Task DeferredVisibleRefreshAsync(ulong generation)
    {
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        if (generation != _layoutGeneration || !IsInsideTree() || !IsVisibleInTree())
            return;

        RefreshNow(resetScroll: false);
    }

    private void ScheduleDeferredEagerMaterializationRefresh()
    {
        if (_materializationMode != SelectMaterializationMode.Eager || !IsInsideTree())
            return;

        ulong generation = _layoutGeneration;
        if (_scheduledEagerRefreshGeneration == generation)
            return;

        _scheduledEagerRefreshGeneration = generation;
        _ = DeferredEagerMaterializationRefreshAsync(generation);
    }

    private async System.Threading.Tasks.Task DeferredEagerMaterializationRefreshAsync(ulong generation)
    {
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        if (generation != _layoutGeneration
            || _materializationMode != SelectMaterializationMode.Eager
            || !IsInsideTree()
            || !IsVisibleInTree())
        {
            return;
        }

        FinalizeEagerMaterialization(warnOnMismatch: true);
    }

    private bool MaterializeItemView(IGenericSelectItem item, SelectItemLayout layout, bool updateExistingView)
    {
        if (_itemGrid is null)
            return false;

        bool needsView = item.View is null || !GodotObject.IsInstanceValid(item.View);
        if (!TryMaterializeOrRecoverItemView(item, layout, out Control? materializedView) || materializedView is null)
            return false;

        Control view = materializedView;
        view.Size = layout.Size;
        if (!IsRelayoutPositionLocked(view))
            view.Position = layout.Position;
        view.ZIndex = 0;
        view.Visible = true;

        TrackVisibleLayoutNode(view);

        if (needsView || (updateExistingView && !_itemsUpdatedForCurrentLayout.Contains(item)))
        {
            try
            {
                item.UpdateView(BuildState(item, layout.VisibleIndex));
            }
            catch (Exception exception)
            {
                GD.PushWarning(
                    $"Loadout select screen '{Name}' failed to update item '{item.Id}' ({item.Name}): {exception}");
            }

            _itemsUpdatedForCurrentLayout.Add(item);
        }

        return true;
    }

    private bool TryMaterializeOrRecoverItemView(IGenericSelectItem item, SelectItemLayout layout, out Control? view)
    {
        view = null;

        try
        {
            view = EnsureViewInCanvas(item);
            return true;
        }
        catch (Exception exception)
        {
            GD.PushWarning(
                $"Loadout select screen '{Name}' failed to create item '{item.Id}' ({item.Name}); using fallback tile. {exception}");

            try
            {
                view = CreateFailedItemFallbackView(item, layout.Size);
                SetItemView(item, view);
                BindViewActivation(item, view);
                _itemGrid?.AddChild(view);
                return true;
            }
            catch (Exception fallbackException)
            {
                GD.PushError(
                    $"Loadout select screen '{Name}' failed to create fallback for item '{item.Id}' ({item.Name}): {fallbackException}");
                return false;
            }
        }
    }

    private Control CreateFailedItemFallbackView(IGenericSelectItem item, Vector2 size)
    {
        Button button = new()
        {
            Name = MakeSafeNodeName($"Fallback_{item.Id}"),
            CustomMinimumSize = size,
            Size = size,
            FocusMode = FocusModeEnum.All,
            MouseFilter = MouseFilterEnum.Stop,
            SizeFlagsHorizontal = SizeFlags.ShrinkBegin,
            SizeFlagsVertical = SizeFlags.ShrinkBegin,
            Text = $"{item.Name}\n{item.Id}"
        };
        button.AddThemeFontOverride("font", LoadGameFont("res://themes/kreon_regular_glyph_space_one.tres"));
        button.AddThemeFontSizeOverride("font_size", 18);
        button.AddThemeColorOverride("font_color", StsColors.cream);
        return button;
    }

    private void CloseOpenDropdowns()
    {
        CloseOpenDropdowns(this);
    }

    private static void CloseOpenDropdowns(Node root)
    {
        foreach (Node child in root.GetChildren())
        {
            if (child is NLoadoutDropdown dropdown)
                dropdown.CloseLoadoutDropdown(restoreFocus: false);

            CloseOpenDropdowns(child);
        }
    }

    private bool IsLayoutNearViewport(SelectItemLayout layout)
    {
        if (_itemGrid is null)
            return false;

        float viewportHeight = _scrollMask?.Size.Y ?? _itemGrid.GetParent<Control>()?.Size.Y ?? Size.Y;
        float rowHeight = Math.Max(1f, layout.Size.Y + ItemVerticalGap);
        float scrollTop = Math.Min(_scrollY, _targetScrollY);
        float scrollBottom = Math.Max(_scrollY, _targetScrollY);
        float behindBuffer = rowHeight * MaterializeRowsBehind;
        float aheadBuffer = rowHeight * MaterializeRowsAhead;
        Rect2 materializeRect = new(
            new Vector2(0f, scrollTop - behindBuffer),
            new Vector2(_itemGrid.Size.X, scrollBottom - scrollTop + viewportHeight + behindBuffer + aheadBuffer));
        return new Rect2(layout.Position, layout.Size).Intersects(materializeRect, includeBorders: true);
    }

    private Control CreateGroupHeader(string key, SelectGroupHeader header)
    {
        Control container = new()
        {
            Name = MakeSafeNodeName($"Group_{key}"),
            MouseFilter = MouseFilterEnum.Ignore
        };

        HBoxContainer headerRow = new()
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            MouseFilter = MouseFilterEnum.Ignore
        };
        headerRow.SetAnchorsPreset(LayoutPreset.FullRect);
        bool hasHeaderIcon = TryGetValidTexture(header.Icon, out Texture2D? headerIcon);
        headerRow.AddThemeConstantOverride("separation", hasHeaderIcon ? 12 : 0);
        container.AddChild(headerRow);

        if (hasHeaderIcon)
        {
            TextureRect icon = new()
            {
                Texture = headerIcon,
                CustomMinimumSize = new Vector2(48f, 48f),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                SizeFlagsHorizontal = SizeFlags.ShrinkCenter,
                SizeFlagsVertical = SizeFlags.ShrinkCenter,
                MouseFilter = MouseFilterEnum.Ignore
            };
            headerRow.AddChild(icon);
        }

        MegaRichTextLabel label = new()
        {
            Text = header.Text,
            BbcodeEnabled = true,
            FitContent = true,
            ScrollActive = false,
            AutowrapMode = TextServer.AutowrapMode.Off,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            MouseFilter = MouseFilterEnum.Ignore
        };
        ApplyGameRichTextTheme(label);
        headerRow.AddChild(label);

        return container;
    }

    private static bool TryGetValidTexture(Texture2D? texture, out Texture2D? validTexture)
    {
        validTexture = null;
        if (texture is null)
            return false;

        try
        {
            if (!GodotObject.IsInstanceValid(texture))
                return false;

            _ = texture.GetRid();
            validTexture = texture;
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private static void ApplyGameRichTextTheme(RichTextLabel label)
    {
        Font? regular = LoadGameFont("res://themes/kreon_regular_glyph_space_one.tres");
        Font? bold = LoadGameFont("res://themes/kreon_bold_glyph_space_one.tres");
        if (regular is not null)
        {
            label.AddThemeFontOverride("normal_font", regular);
            label.AddThemeFontOverride("italics_font", regular);
            label.AddThemeFontOverride("mono_font", regular);
        }

        if (bold is not null)
        {
            label.AddThemeFontOverride("bold_font", bold);
            label.AddThemeFontOverride("bold_italics_font", bold);
        }

        label.AddThemeFontSizeOverride("normal_font_size", SelectGroupHeader.CategoryDescriptionFontSize);
        label.AddThemeFontSizeOverride("bold_font_size", SelectGroupHeader.CategoryTitleFontSize);
        label.AddThemeColorOverride("default_color", StsColors.cream);
        label.AddThemeColorOverride("font_shadow_color", new Color(0f, 0f, 0f, 0.5f));
        label.AddThemeConstantOverride("shadow_offset_x", 3);
        label.AddThemeConstantOverride("shadow_offset_y", 2);
    }

    private void ClearGeneratedGroupContainers()
    {
        foreach (IGenericSelectItem item in _items)
        {
            if (item.View is null || !GodotObject.IsInstanceValid(item.View))
                continue;

            if (item.View.GetParent() is not null && item.View.GetParent() != _itemGrid)
            {
                item.View.GetParent()?.RemoveChild(item.View);
                _itemGrid?.AddChild(item.View);
            }
        }

        foreach (Control groupContainer in _generatedGroupContainers)
        {
            if (!GodotObject.IsInstanceValid(groupContainer))
                continue;

            groupContainer.GetParent()?.RemoveChild(groupContainer);
            groupContainer.QueueFree();
        }

        _generatedGroupContainers.Clear();
    }

    private Control EnsureViewInCanvas(IGenericSelectItem item)
    {
        if (_itemGrid is null)
            throw new InvalidOperationException("Item grid is not available.");

        if (item.View is null || !GodotObject.IsInstanceValid(item.View))
        {
            Control view = CreateLayoutView(item, BuildState(item, -1));
            SetItemView(item, view);
            NormalizeItemForGrid(view);
            BindViewActivation(item, view);
        }

        Control control = item.View!;
        if (control.GetParent() != _itemGrid)
        {
            control.GetParent()?.RemoveChild(control);
            _itemGrid.AddChild(control);
        }

        return control;
    }

    private Control CreateLayoutView(IGenericSelectItem item, SelectItemState state)
    {
        Control content = item.CreateView(state);
        if (!_layout.FixedSlots || content is NSelectItemSlot)
        {
            NotifyItemViewReady(item, content);
            return content;
        }

        NSelectItemSlot slot = new();
        slot.SetContent(content, _layout.ItemSize);
        slot.ContentReady = readyContent => NotifyItemViewReady(item, readyContent);
        return slot;
    }

    private static void NotifyItemViewReady(IGenericSelectItem item, Control view)
    {
        if (view.IsNodeReady())
        {
            item.NotifyViewReady(view);
            return;
        }

        view.Connect(Node.SignalName.Ready, Callable.From(() => item.NotifyViewReady(view)), (uint)ConnectFlags.OneShot);
    }

    private void BindViewActivation(IGenericSelectItem item, Control view)
    {
        if (!_activationBoundViews.Add(view))
            return;

        Action activate = () => ActivateCurrentItemForView(view);
        if (item.TryBindActivation(activate))
            return;

        if (view is NClickableControl clickableControl || TryFindDescendant(view, out clickableControl))
        {
            clickableControl.Connect(NClickableControl.SignalName.Released, Callable.From<NClickableControl>(_ => ActivateCurrentItemForView(view)));
            return;
        }

        if (view is BaseButton button || TryFindDescendant(view, out button))
            button.Pressed += () => ActivateCurrentItemForView(view);

        // For non-button views, the adapter should provide BindActivation.
    }

    private void SetItemView(IGenericSelectItem item, Control view)
    {
        if (item.View is Control previousView && previousView != view)
            _activationItemsByView.Remove(previousView);

        item.SetView(view);
        _activationItemsByView[view] = item;
    }

    private void ClearActivationBinding(Control view)
    {
        _activationItemsByView.Remove(view);
        _activationBoundViews.Remove(view);
    }

    public bool TryGetItemForView(Control view, out IGenericSelectItem item)
    {
        if (_activationItemsByView.TryGetValue(view, out IGenericSelectItem? mappedItem) && _items.Contains(mappedItem))
        {
            item = mappedItem;
            return true;
        }

        IGenericSelectItem? matchingItem = _items.FirstOrDefault(candidate => candidate.View == view);
        if (matchingItem is null)
        {
            item = null!;
            return false;
        }

        item = matchingItem;
        _activationItemsByView[view] = item;
        return true;
    }

    private void ActivateCurrentItemForView(Control view)
    {
        if (!TryGetItemForView(view, out IGenericSelectItem item))
            return;

        ActivateItem(item);
    }

    private static bool TryFindDescendant<TControl>(Node root, out TControl control)
        where TControl : class
    {
        foreach (Node child in root.GetChildren())
        {
            if (child is TControl direct)
            {
                control = direct;
                return true;
            }

            if (TryFindDescendant(child, out control))
                return true;
        }

        control = null!;
        return false;
    }

    private void ActivateItem(IGenericSelectItem item)
    {
        SelectItemState before = BuildState(item, _visibleItems.IndexOf(item));
        ItemActivated?.Invoke(item, before);

        if (_options.SelectionMode != SelectSelectionMode.None)
            ToggleSelection(item);

        SelectItemState after = BuildState(item, _visibleItems.IndexOf(item));
        item.UpdateView(after);
        ItemSelectionChanged?.Invoke(item, after);
        UpdateConfirmButtonState();
    }

    private void ToggleSelection(IGenericSelectItem item)
    {
        int current = _selectedAmounts.GetValueOrDefault(item.Id, 0);

        if (_options.SelectionMode == SelectSelectionMode.Single)
        {
            _selectedAmounts.Clear();
            if (current == 0)
                _selectedAmounts[item.Id] = 1;
            return;
        }

        if (_options.SelectionMode == SelectSelectionMode.Multi)
        {
            if (current <= 0)
            {
                _selectedAmounts[item.Id] = 1;
            }
            else if (current < _options.MaxCopiesPerItem)
            {
                _selectedAmounts[item.Id] = current + 1;
            }
            else
            {
                _selectedAmounts.Remove(item.Id);
            }
        }
    }

    private SelectItemState BuildState(IGenericSelectItem item, int visibleIndex)
    {
        int amount = _selectedAmounts.GetValueOrDefault(item.Id, 0);
        return new SelectItemState(
            originalIndex: item.OriginalIndex,
            visibleIndex: visibleIndex,
            selectionAmount: amount,
            isSelected: amount > 0,
            isEnabled: true,
            toggleStates: new Dictionary<string, bool>(_toggleStates, StringComparer.Ordinal));
    }

    private static string MakeSafeNodeName(string value)
    {
        string safeName = Regex.Replace(value, @"[^A-Za-z0-9_]", "_");
        return string.IsNullOrWhiteSpace(safeName) ? "GeneratedControl" : safeName;
    }

    private void RefreshVisibleItemStates()
    {
        for (int i = 0; i < _visibleItems.Count; i++)
        {
            IGenericSelectItem item = _visibleItems[i];
            if (item.View is null || !GodotObject.IsInstanceValid(item.View))
                continue;

            item.UpdateView(BuildState(item, i));
            _itemsUpdatedForCurrentLayout.Add(item);
        }
    }

    private void ClearItemViews()
    {
        foreach (IGenericSelectItem item in _items)
        {
            if (item.View is null || !GodotObject.IsInstanceValid(item.View))
                continue;

            RemoveItemView(item);
        }
    }

    private void RemoveItemView(IGenericSelectItem item)
    {
        if (item.View is null || !GodotObject.IsInstanceValid(item.View))
            return;

        Control view = item.View;
        view.GetParent()?.RemoveChild(view);
        ClearActivationBinding(view);
        view.QueueFreeSafely();
        item.SetView(null);
    }

    private void AnimateAndRemoveItemView(IGenericSelectItem item)
    {
        if (item.View is null || !GodotObject.IsInstanceValid(item.View))
            return;

        Control view = item.View;
        ClearActivationBinding(view);
        item.SetView(null);

        if (_itemGrid is null || !view.Visible || !view.IsInsideTree())
        {
            view.GetParent()?.RemoveChild(view);
            view.QueueFreeSafely();
            return;
        }

        Vector2 globalPosition = view.GlobalPosition;
        if (view.GetParent() != _itemGrid)
        {
            view.GetParent()?.RemoveChild(view);
            _itemGrid.AddChild(view);
            view.GlobalPosition = globalPosition;
        }

        view.MouseFilter = MouseFilterEnum.Ignore;
        view.ZIndex = Math.Max(view.ZIndex, 100);
        Vector2 startScale = view.Scale;
        Tween tween = view.CreateTween();
        tween.TweenProperty(view, "scale", new Vector2(startScale.X * 1.12f, startScale.Y * 0.02f), 0.18f)
            .SetEase(Tween.EaseType.In)
            .SetTrans(Tween.TransitionType.Cubic);
        tween.Parallel().TweenProperty(view, "modulate", Colors.Black, 0.14f)
            .SetEase(Tween.EaseType.In)
            .SetTrans(Tween.TransitionType.Cubic);
        tween.TweenCallback(Callable.From(view.QueueFreeSafely));
    }

    private void NormalizeItemForGrid(Control control)
    {
        Vector2 size = ResolveItemLayoutSize(control);
        Vector2 customMinimum = control.CustomMinimumSize;

        if (customMinimum.X <= 0f)
            customMinimum.X = size.X;

        if (customMinimum.Y <= 0f)
            customMinimum.Y = size.Y;

        control.CustomMinimumSize = customMinimum;
        control.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        control.SizeFlagsVertical = SizeFlags.ShrinkBegin;
    }

    private Vector2 ResolveItemLayoutSize(Control control)
    {
        Vector2 size = control.GetCombinedMinimumSize();

        if (size.X <= 0f)
            size.X = control.CustomMinimumSize.X;

        if (size.Y <= 0f)
            size.Y = control.CustomMinimumSize.Y;

        if (size.X <= 0f)
            size.X = control.Size.X;

        if (size.Y <= 0f)
            size.Y = control.Size.Y;

        if (size.X <= 0f)
            size.X = FallbackItemWidth;

        if (size.Y <= 0f)
            size.Y = FallbackItemHeight;

        return size;
    }

    private Vector2 ResolveConfiguredItemSize()
    {
        Vector2 size = _layout.ItemSize;

        if (size.X <= 0f)
            size.X = FallbackItemWidth;

        if (size.Y <= 0f)
            size.Y = FallbackItemHeight;

        return size;
    }

    private int ResolveColumnCount(Vector2 itemSize, float horizontalReservedSpace)
    {
        if (GridColumns > 0)
            return Math.Max(1, GridColumns);

        int requestedColumns = CalculateAutoColumns();
        int columns = Math.Max(1, requestedColumns);
        float availableWidth = Math.Max(1f, GetViewportContentWidth() - horizontalReservedSpace);

        while (columns > 1 && CalculateGridWidth(columns, itemSize.X) > availableWidth)
            columns--;

        return Math.Max(1, columns);
    }

    private float CalculateGridWidth(int columns, float itemWidth)
    {
        return columns * itemWidth + Math.Max(0, columns - 1) * ItemHorizontalGap;
    }

    private float CalculateCenteredStartX(float contentWidth, float horizontalReservedSpace)
    {
        if (!ShouldApplyCardSafeMargins())
            return _layout.PaddingLeft + Math.Max(0f, (GetViewportContentWidth() - horizontalReservedSpace - contentWidth) * 0.5f);

        float viewportWidth = GetRawViewportWidth() - _layout.PaddingLeft - _layout.PaddingRight - horizontalReservedSpace;
        float visualLaneWidth = Math.Max(1f, viewportWidth - CardScrollbarReserve);
        float visualContentWidth = contentWidth + CardVisualLeftOverhang;
        return _layout.PaddingLeft + CardVisualLeftOverhang + Math.Max(0f, (visualLaneWidth - visualContentWidth) * 0.5f);
    }

    private float GetViewportContentWidth()
    {
        float width = GetRawViewportWidth();
        float reservedWidth = ShouldApplyCardSafeMargins()
            ? CardVisualLeftOverhang + CardScrollbarReserve
            : 0f;
        return Math.Max(1f, width - _layout.PaddingLeft - _layout.PaddingRight - reservedWidth);
    }

    private float GetRawViewportWidth()
    {
        Control? viewport = _itemGrid?.GetParent<Control>();
        float width = viewport?.Size.X ?? 0f;

        if (width <= 0f && _itemGrid is not null)
            width = _itemGrid.Size.X;

        if (width <= 0f)
            width = Math.Max(1f, Size.X - SidebarWidth);

        return Math.Max(1f, width);
    }

    private void SetContentSize(float width, float height)
    {
        if (_itemGrid is null)
            return;

        Control? viewport = _itemGrid.GetParent<Control>();
        float viewportWidth = viewport?.Size.X ?? Size.X;
        float viewportHeight = viewport?.Size.Y ?? Size.Y;
        Vector2 contentSize = new(Math.Max(width, viewportWidth), Math.Max(height, viewportHeight));
        _itemGrid.CustomMinimumSize = contentSize;
        _itemGrid.Size = contentSize;
        UpdateScrollBounds();
    }

    private void UpdateViewportCulling(bool force = false)
    {
        if (_visibleLayoutNodes.Count == 0)
            return;

        if (_materializationMode == SelectMaterializationMode.Eager)
            return;

        float rowHeight = Math.Max(1f, ResolveConfiguredItemSize().Y + ItemVerticalGap);
        float cullThreshold = Math.Max(16f, rowHeight * CullUpdateRowsThreshold);
        if (!force
            && _lastCullNodeCount == _visibleLayoutNodes.Count
            && !float.IsNaN(_lastCullScrollY)
            && Math.Abs(_scrollY - _lastCullScrollY) < cullThreshold)
        {
            return;
        }

        _lastCullScrollY = _scrollY;
        _lastCullNodeCount = _visibleLayoutNodes.Count;

        float viewportHeight = _scrollMask?.Size.Y ?? _itemGrid?.GetParent<Control>()?.Size.Y ?? Size.Y;
        float cullTop = _scrollY - rowHeight * CullRetentionRows;
        float cullBottom = _scrollY + viewportHeight + rowHeight * CullRetentionRows;

        for (int i = _visibleLayoutNodes.Count - 1; i >= 0; i--)
        {
            Control control = _visibleLayoutNodes[i];
            if (!GodotObject.IsInstanceValid(control))
            {
                _visibleLayoutNodes.RemoveAt(i);
                _visibleLayoutNodeSet.Remove(control);
                continue;
            }

            Vector2 controlSize = control.Size;
            if (controlSize.Y <= 0f)
                controlSize = control.GetCombinedMinimumSize();

            float top = control.Position.Y;
            float bottom = top + Math.Max(controlSize.Y, control.CustomMinimumSize.Y);
            control.Visible = bottom >= cullTop && top <= cullBottom;
        }
    }

    private static void KeepHoverTipsAboveSelectScreen()
    {
        NLoadoutPanelRoot.Instance?.AdoptGameHoverTips();
    }

    private void UpdateMultiplierBadge()
    {
        int multiplier = GetCurrentInputMultiplier();
        if (multiplier <= 1 || !IsVisibleInTree())
        {
            if (_multiplierBadge is not null)
                _multiplierBadge.Visible = false;

            return;
        }

        EnsureMultiplierBadge();
        if (_multiplierBadge is null || _multiplierBadgeLabel is null)
            return;

        _multiplierBadge.Visible = true;
        _multiplierBadgeLabel.Text = $"x{multiplier}";

        Vector2 globalMouse = GetViewport().GetMousePosition();
        Vector2 localMouse = GetGlobalTransformWithCanvas().AffineInverse() * globalMouse;
        _multiplierBadge.Position = localMouse + new Vector2(22f, 18f);
        _multiplierBadge.MoveToFront();
    }

    private void EnsureMultiplierBadge()
    {
        if (_multiplierBadge is not null && GodotObject.IsInstanceValid(_multiplierBadge))
            return;

        Control badge = new()
        {
            Name = "MultiplierBadge",
            MouseFilter = MouseFilterEnum.Ignore,
            ZIndex = 200,
            Size = new Vector2(70f, 38f),
            CustomMinimumSize = new Vector2(70f, 38f),
            Visible = false
        };

        ColorRect background = new()
        {
            Name = "Background",
            MouseFilter = MouseFilterEnum.Ignore,
            Color = new Color(0.02f, 0.025f, 0.03f, 0.82f),
            Size = badge.Size
        };
        badge.AddChild(background);

        MegaLabel label = new()
        {
            Name = "Label",
            Text = "x5",
            AutoSizeEnabled = false,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore,
            Position = Vector2.Zero,
            Size = badge.Size
        };
        if (LoadGameFont("res://themes/kreon_bold_glyph_space_one.tres") is { } font)
            label.AddThemeFontOverride("font", font);

        label.AddThemeFontSizeOverride("font_size", 26);
        label.AddThemeColorOverride("font_color", StsColors.gold);
        label.AddThemeColorOverride("font_shadow_color", Colors.Black);
        label.AddThemeConstantOverride("shadow_offset_x", 3);
        label.AddThemeConstantOverride("shadow_offset_y", 2);
        badge.AddChild(label);

        AddChild(badge);
        _multiplierBadge = badge;
        _multiplierBadgeLabel = label;
    }

    private void ApplyLayoutSettings()
    {
        if (_itemGrid is null)
            return;

        if (_isConfigured)
            SetContentSize(_itemGrid.Size.X, _itemGrid.Size.Y);
    }

    private bool ShouldApplyCardSafeMargins()
    {
        return GridColumns == 5 && FallbackItemWidth >= 240f && FallbackItemHeight >= 300f;
    }

    private void UpdateSelectScroll(double delta)
    {
        if (_itemGrid is null)
            return;

        if (_scrollbarPressed && _scrollbar is not null && _maxScrollY > 0f)
        {
            _targetScrollY = Mathf.Clamp((float)_scrollbar.Value * 0.01f * _maxScrollY, 0f, _maxScrollY);
            _scrollY = _targetScrollY;
        }
        else
        {
            float scrollBlend = Mathf.Clamp((float)delta * ScrollSmoothingRate, 0f, 1f);
            _scrollY = Mathf.Lerp(_scrollY, _targetScrollY, scrollBlend);
        }

        if (Mathf.Abs(_scrollY - _targetScrollY) < 0.5f)
            _scrollY = _targetScrollY;

        _itemGrid.Position = new Vector2(_itemGrid.Position.X, -_scrollY);

        if (_scrollbar is not null && !_scrollbarPressed)
            _scrollbar.SetValueWithoutAnimation(_maxScrollY <= 0f ? 0 : Mathf.Clamp(_scrollY / _maxScrollY, 0f, 1f) * 100f);

        if (_materializationMode == SelectMaterializationMode.Lazy)
        {
            MaterializeViewportItemViews(ScrollMaterializeBudget);
            UpdateViewportCulling();
        }
    }

    private void SetTargetScroll(float value)
    {
        _targetScrollY = Mathf.Clamp(value, 0f, _maxScrollY);
    }

    private void ClampScrollToBounds()
    {
        _targetScrollY = Mathf.Clamp(_targetScrollY, 0f, _maxScrollY);
        _scrollY = Mathf.Clamp(_scrollY, 0f, _maxScrollY);
        if (_itemGrid is not null)
            _itemGrid.Position = new Vector2(_itemGrid.Position.X, -_scrollY);

        if (_scrollbar is not null && !_scrollbarPressed)
            _scrollbar.SetValueWithoutAnimation(_maxScrollY <= 0f ? 0 : Mathf.Clamp(_scrollY / _maxScrollY, 0f, 1f) * 100f);
    }

    private void ScrollToTop()
    {
        _scrollY = 0f;
        _targetScrollY = 0f;
        if (_itemGrid is not null)
            _itemGrid.Position = new Vector2(_itemGrid.Position.X, 0f);

        _scrollbar?.SetValueWithoutAnimation(0);
    }

    private void UpdateScrollBounds()
    {
        if (_itemGrid is null)
            return;

        Control? viewport = _itemGrid.GetParent<Control>();
        float viewportHeight = viewport?.Size.Y ?? Size.Y;
        _maxScrollY = Math.Max(0f, _itemGrid.Size.Y - viewportHeight);
        _targetScrollY = Mathf.Clamp(_targetScrollY, 0f, _maxScrollY);
        _scrollY = Mathf.Clamp(_scrollY, 0f, _maxScrollY);

        if (_scrollbar is not null)
        {
            _scrollbar.Visible = _maxScrollY > 1f;
            _scrollbar.MouseFilter = _scrollbar.Visible ? MouseFilterEnum.Stop : MouseFilterEnum.Ignore;
        }
    }

    private static NScrollbar CreateGameScrollbar()
    {
        NScrollbar scrollbar = new()
        {
            CustomMinimumSize = new Vector2(48f, 820f),
            MouseFilter = MouseFilterEnum.Stop
        };

        TextureRect trackBody = new()
        {
            Name = "TrackBody",
            Modulate = new Color(0.164706f, 0.290196f, 0.321569f, 1f),
            Texture = LoadGameTexture("res://images/atlases/ui_atlas.sprites/scrollbar_track_center.tres"),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            MouseFilter = MouseFilterEnum.Ignore
        };
        trackBody.SetAnchorsPreset(LayoutPreset.FullRect);
        scrollbar.AddChild(trackBody);

        TextureRect trackTop = new()
        {
            Name = "TrackTop",
            Modulate = trackBody.Modulate,
            Texture = LoadGameTexture("res://images/atlases/ui_atlas.sprites/scrollbar_track_edge2.tres"),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            MouseFilter = MouseFilterEnum.Ignore
        };
        trackTop.SetAnchorsPreset(LayoutPreset.TopWide);
        trackTop.OffsetTop = -48f;
        scrollbar.AddChild(trackTop);

        TextureRect trackBottom = new()
        {
            Name = "TrackBot",
            Modulate = trackBody.Modulate,
            Texture = trackTop.Texture,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            FlipV = true,
            MouseFilter = MouseFilterEnum.Ignore
        };
        trackBottom.SetAnchorsPreset(LayoutPreset.BottomWide);
        trackBottom.OffsetBottom = 48f;
        scrollbar.AddChild(trackBottom);

        TextureRect handle = new()
        {
            Name = "Handle",
            UniqueNameInOwner = true,
            Texture = LoadGameTexture("res://images/atlases/ui_atlas.sprites/scrollbar_train_large.tres"),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            PivotOffset = new Vector2(36f, 36f),
            MouseFilter = MouseFilterEnum.Ignore
        };
        handle.SetAnchorsPreset(LayoutPreset.BottomWide);
        handle.OffsetLeft = -12f;
        handle.OffsetTop = -847f;
        handle.OffsetRight = 12f;
        handle.OffsetBottom = -775f;
        scrollbar.AddChild(handle);

        AssignOwnerRecursive(scrollbar, scrollbar);
        return scrollbar;
    }

    private static NConfirmButton CreateConfirmButton()
    {
        NConfirmButton confirmButton = new()
        {
            FocusMode = FocusModeEnum.All,
            MouseFilter = MouseFilterEnum.Stop,
            PivotOffset = new Vector2(180f, 40f)
        };
        confirmButton.SetAnchorsPreset(LayoutPreset.BottomRight);
        confirmButton.OffsetLeft = -160f;
        confirmButton.OffsetTop = -354f;
        confirmButton.OffsetRight = 40f;
        confirmButton.OffsetBottom = -244f;

        TextureRect shadow = new()
        {
            Name = "Shadow",
            Modulate = new Color(0f, 0f, 0f, 0.25098f),
            Texture = LoadGameTexture("res://images/atlases/ui_atlas.sprites/confirm_button.tres"),
            ExpandMode = TextureRect.ExpandModeEnum.FitWidthProportional,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = MouseFilterEnum.Ignore
        };
        shadow.SetAnchorsPreset(LayoutPreset.FullRect);
        shadow.OffsetLeft = -41f;
        shadow.OffsetTop = -1f;
        shadow.OffsetRight = 26f;
        shadow.OffsetBottom = 39f;
        confirmButton.AddChild(shadow);

        TextureRect outline = new()
        {
            Name = "Outline",
            Modulate = new Color(0.941176f, 0.705882f, 0f, 0.752941f),
            Texture = LoadGameTexture("res://images/atlases/compressed.sprites/confirm_button_outline.tres"),
            ExpandMode = TextureRect.ExpandModeEnum.FitWidthProportional,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = MouseFilterEnum.Ignore
        };
        outline.SetAnchorsPreset(LayoutPreset.FullRect);
        outline.OffsetLeft = -56f;
        outline.OffsetTop = -16f;
        outline.OffsetRight = 17f;
        outline.OffsetBottom = 30f;
        confirmButton.AddChild(outline);

        TextureRect image = new()
        {
            Name = "Image",
            Texture = LoadGameTexture("res://images/atlases/ui_atlas.sprites/confirm_button.tres"),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = MouseFilterEnum.Ignore
        };
        image.SetAnchorsPreset(LayoutPreset.FullRect);
        image.OffsetLeft = -53f;
        image.OffsetTop = -13f;
        image.OffsetRight = 14f;
        image.OffsetBottom = 27f;
        confirmButton.AddChild(image);

        TextureRect icon = new()
        {
            Name = "Icon",
            Modulate = StsColors.cream,
            Texture = LoadGameTexture("res://images/atlases/compressed.sprites/confirm_button_tick.tres"),
            ExpandMode = TextureRect.ExpandModeEnum.FitWidthProportional,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = MouseFilterEnum.Ignore,
            Position = new Vector2(88f, 28f),
            Size = new Vector2(80f, 80f)
        };
        image.AddChild(icon);

        TextureRect controllerIcon = new()
        {
            Name = "ControllerIcon",
            UniqueNameInOwner = true,
            Visible = false,
            MouseFilter = MouseFilterEnum.Ignore
        };
        controllerIcon.SetAnchorsPreset(LayoutPreset.Center);
        controllerIcon.OffsetLeft = -142.5f;
        controllerIcon.OffsetTop = -64f;
        controllerIcon.OffsetRight = 97.5f;
        controllerIcon.OffsetBottom = 56f;
        controllerIcon.Scale = new Vector2(0.5f, 0.5f);
        controllerIcon.PivotOffset = new Vector2(256f, 128f);
        image.AddChild(controllerIcon);

        AssignOwnerRecursive(confirmButton, confirmButton);
        return confirmButton;
    }

    private static Texture2D? LoadGameTexture(string path)
    {
        string localPath = path
            .Replace("res://images/atlases/", "res://Loadout/images/atlases/")
            .Replace("res://images/packed/common_ui/", "res://Loadout/images/atlases/ui_atlas.sprites/");

        if (ResourceLoader.Exists(localPath))
            return GD.Load<Texture2D>(localPath);

        if (ResourceLoader.Exists(path))
            return GD.Load<Texture2D>(path);

        return null;
    }

    private static Font? LoadGameFont(string path)
    {
        string localPath = path.Replace("res://themes/", "res://Loadout/themes/default/");
        if (ResourceLoader.Exists(localPath))
            return GD.Load<Font>(localPath);

        return ResourceLoader.Exists(path) ? GD.Load<Font>(path) : null;
    }

    private static Material? LoadGameMaterial(string path)
    {
        string localPath = path.Replace("res://themes/", "res://Loadout/themes/default/");
        if (ResourceLoader.Exists(localPath))
            return GD.Load<Material>(localPath);

        return ResourceLoader.Exists(path) ? GD.Load<Material>(path) : null;
    }

    private static void ResetActionButtonVisualState(Node button)
    {
        if (button is Control control)
            control.Scale = Vector2.One;

        if (button.GetNodeOrNull<CanvasItem>("Image") is { } image)
            image.Modulate = Colors.White;

        if (button.GetNodeOrNull<CanvasItem>("Outline") is { } outline)
            outline.Modulate = Colors.Transparent;
    }

    private static void AssignOwnerRecursive(Node root, Node owner)
    {
        foreach (Node child in root.GetChildren())
        {
            child.Owner = owner;
            AssignOwnerRecursive(child, owner);
        }
    }

    private int CalculateAutoColumns()
    {
        if (_itemGrid is null)
            return 1;

        float availableWidth = GetViewportContentWidth();
        if (availableWidth <= 0f)
            return 1;

        float itemWidth = _lastMeasuredItemWidth > 0f ? _lastMeasuredItemWidth : FallbackItemWidth;
        int columns = (int)MathF.Floor((availableWidth + ItemHorizontalGap) / (itemWidth + ItemHorizontalGap));
        return Math.Max(1, columns);
    }

    private bool IsConfirmAllowed()
    {
        int selectedCount = _selectedAmounts.Values.Sum();
        return selectedCount >= _options.MinSelection
            && selectedCount <= _options.MaxTotalSelection;
    }

    private void UpdateConfirmButtonState()
    {
        bool usesSelection = _options.SelectionMode != SelectSelectionMode.None;
        int selectedCount = _selectedAmounts.Values.Sum();

        if (_selectedCountLabel is Label selectedCountLabel)
        {
            selectedCountLabel.Visible = usesSelection;
            selectedCountLabel.Text = SelectScreenLoc.Text("SELECTED_COUNT", "Selected: {0}", selectedCount);
        }
        else if (_selectedCountLabel is not null)
        {
            _selectedCountLabel.Visible = usesSelection;
        }

        if (_confirmButton is not null)
        {
            _confirmButton.Visible = usesSelection;
            _confirmButton.Disabled = !usesSelection || !IsConfirmAllowed();
        }

        if (_confirmClickable is not null)
        {
            _confirmClickable.Visible = usesSelection;
            _confirmClickable.SetEnabled(usesSelection && IsConfirmAllowed());
        }

        if (_cancelButton is not null)
            _cancelButton.Visible = true;

        if (_cancelClickable is not null)
        {
            _cancelClickable.Visible = true;
            _cancelClickable.SetEnabled(true);
            ResetActionButtonVisualState(_cancelClickable);
        }
    }
}

public static class SelectScreenLoc
{
    private const string Table = "loadout";

    public static string Text(string key, string fallback)
    {
        return LocMan.Loc(key, fallback);
    }

    public static string Text(string key, string fallback, params object[] args)
    {
        return LocMan.Loc(key, fallback, args);
    }
}

public sealed class SelectScreenBuilder<TModel>
{
    private readonly NGenericSelectScreen _screen;
    private readonly SelectItemAdapter<TModel> _adapter;

    internal SelectScreenBuilder(NGenericSelectScreen screen, SelectItemAdapter<TModel> adapter)
    {
        _screen = screen;
        _adapter = adapter;
    }

    public SelectScreenBuilder<TModel> Options(SelectScreenOptions options)
    {
        _screen.SetOptions(options);
        return this;
    }

    public SelectScreenBuilder<TModel> Materialization(SelectMaterializationMode mode)
    {
        _screen.SetMaterializationMode(mode);
        return this;
    }

    public SelectScreenBuilder<TModel> Layout(
        int columns,
        Vector2 itemSize,
        int horizontalGap,
        int verticalGap,
        bool fixedSlots = true,
        float paddingLeft = 0f,
        float paddingTop = 80f,
        float paddingRight = 0f,
        float paddingBottom = 180f)
    {
        _screen.SetLayout(new SelectLayoutDefinition(
            columns,
            itemSize,
            horizontalGap,
            verticalGap,
            fixedSlots,
            paddingLeft,
            paddingTop,
            paddingRight,
            paddingBottom));
        return this;
    }

    public SelectScreenBuilder<TModel> FilterGroup(
        string id,
        string label,
        SelectFilterGroupSelectionMode mode = SelectFilterGroupSelectionMode.Any,
        bool requireSelection = false)
    {
        _screen.AddFilterGroup(new SelectFilterGroupDefinition(id, label, mode, requireSelection));
        return this;
    }

    public SelectScreenBuilder<TModel> Filter(
        string id,
        string label,
        Func<TModel, bool> predicate,
        string groupId = "default",
        bool enabledByDefault = false)
    {
        _screen.AddFilter(new SelectFilterDefinition(
            id,
            label,
            groupId,
            item => item is GenericSelectItem<TModel> typed && predicate(typed.Model),
            enabledByDefault));

        return this;
    }

    public SelectScreenBuilder<TModel> Toggle(string id, string label, bool checkedByDefault = false)
    {
        _screen.AddToggle(new SelectToggleDefinition(id, label, checkedByDefault));
        return this;
    }

    public SelectScreenBuilder<TModel> ActionButton(
        string id,
        string label,
        Action<NGenericSelectScreen> onPressed,
        Texture2D? icon = null)
    {
        _screen.AddActionButton(new SelectActionButtonDefinition(id, label, onPressed, icon));
        return this;
    }

    public SelectScreenBuilder<TModel> CustomVisibilityPredicate(Func<TModel, bool> predicate)
    {
        _screen.SetCustomVisibilityPredicate(item =>
            item is GenericSelectItem<TModel> typed && predicate(typed.Model));
        return this;
    }

    public SelectScreenBuilder<TModel> Sorter(
        string id,
        string label,
        Comparison<TModel> ascendingComparison,
        Comparison<TModel>? descendingComparison = null,
        bool activeByDefault = false,
        bool descendingByDefault = false)
    {
        _screen.AddSorter(new SelectSorterDefinition(
            id,
            label,
            (left, right) =>
            {
                if (left is not GenericSelectItem<TModel> typedLeft || right is not GenericSelectItem<TModel> typedRight)
                    return 0;

                return ascendingComparison(typedLeft.Model, typedRight.Model);
            },
            descendingComparison is null
                ? null
                : (left, right) =>
                {
                    if (left is not GenericSelectItem<TModel> typedLeft || right is not GenericSelectItem<TModel> typedRight)
                        return 0;

                    return descendingComparison(typedLeft.Model, typedRight.Model);
                },
            activeByDefault,
            descendingByDefault));

        return this;
    }

    public SelectScreenBuilder<TModel> GroupBySorter(
        string sorterId,
        Func<TModel, string> keySelector,
        Func<string, SelectGroupHeader> headerSelector,
        IEnumerable<string> groupOrder,
        IEnumerable<string>? descendingGroupOrder = null)
    {
        _screen.AddGroupDefinition(new SelectGroupDefinition(
            sorterId,
            item => item is GenericSelectItem<TModel> typed ? keySelector(typed.Model) : string.Empty,
            headerSelector,
            groupOrder.ToList(),
            descendingGroupOrder?.ToList()));

        return this;
    }
}

public sealed class SelectItemAdapter<TModel>
{
    public required Func<TModel, string> GetId { get; init; }
    public required Func<TModel, string> GetName { get; init; }
    public Func<TModel, string>? GetSearchText { get; init; }
    public required Func<TModel, SelectItemState, Control> CreateView { get; init; }
    public Action<TModel, CancellationToken>? PreloadResources { get; init; }
    public Action<TModel, Control>? ViewReady { get; init; }
    public Action<TModel, Control, SelectItemState>? UpdateView { get; init; }
    public Func<TModel, string, bool>? MatchesSearch { get; init; }
    public Func<TModel, Control, Action, bool>? BindActivation { get; init; }
}

public interface IGenericSelectItem
{
    object UntypedModel { get; }
    string Id { get; }
    string Name { get; }
    string SearchText { get; }
    int OriginalIndex { get; }
    Control? View { get; }
    bool HasPreloadResources { get; }

    Control CreateView(SelectItemState state);
    void PreloadResources(CancellationToken token);
    void NotifyViewReady(Control renderedView);
    void UpdateView(SelectItemState state);
    bool MatchesSearch(string normalizedQuery);
    bool TryBindActivation(Action activate);
    void SetView(Control? view);
}

public sealed class GenericSelectItem<TModel> : IGenericSelectItem
{
    private readonly SelectItemAdapter<TModel> _adapter;

    public GenericSelectItem(TModel model, SelectItemAdapter<TModel> adapter, int originalIndex)
    {
        Model = model;
        _adapter = adapter;
        OriginalIndex = originalIndex;
        Id = adapter.GetId(model);
        Name = adapter.GetName(model);
        SearchText = adapter.GetSearchText?.Invoke(model) ?? Name;
    }

    public TModel Model { get; }
    public object UntypedModel => Model!;
    public string Id { get; }
    public string Name { get; }
    public string SearchText { get; }
    public int OriginalIndex { get; }
    public Control? View { get; private set; }
    public bool HasPreloadResources => _adapter.PreloadResources is not null;

    public Control CreateView(SelectItemState state)
    {
        return _adapter.CreateView(Model, state);
    }

    public void PreloadResources(CancellationToken token)
    {
        _adapter.PreloadResources?.Invoke(Model, token);
    }

    public void UpdateView(SelectItemState state)
    {
        if (View is not null)
            _adapter.UpdateView?.Invoke(Model, View, state);
    }

    public void NotifyViewReady(Control renderedView)
    {
        _adapter.ViewReady?.Invoke(Model, renderedView);
    }

    public bool MatchesSearch(string normalizedQuery)
    {
        if (_adapter.MatchesSearch is not null)
            return _adapter.MatchesSearch(Model, normalizedQuery);

        return SelectText.Normalize(SearchText).Contains(normalizedQuery, StringComparison.Ordinal);
    }

    public bool TryBindActivation(Action activate)
    {
        if (View is null || _adapter.BindActivation is null)
            return false;

        return _adapter.BindActivation(Model, View, activate);
    }

    public void SetView(Control? view)
    {
        View = view;
    }
}

public sealed class SelectScreenOptions
{
    public SelectSelectionMode SelectionMode { get; init; } = SelectSelectionMode.None;
    public int MinSelection { get; init; } = 0;
    public int MaxTotalSelection { get; init; } = int.MaxValue;
    public int MaxCopiesPerItem { get; init; } = 1;
}

public sealed class SelectScreenUiState
{
    public SelectScreenUiState(
        string query,
        IReadOnlyDictionary<string, string> filterSelections,
        IReadOnlyDictionary<string, bool> toggleStates,
        IReadOnlyList<string> sortPriority,
        IReadOnlyDictionary<string, bool> sortDescendingStates,
        float scrollY)
    {
        Query = query;
        FilterSelections = filterSelections;
        ToggleStates = toggleStates;
        SortPriority = sortPriority;
        SortDescendingStates = sortDescendingStates;
        ScrollY = scrollY;
    }

    public string Query { get; }
    public IReadOnlyDictionary<string, string> FilterSelections { get; }
    public IReadOnlyDictionary<string, bool> ToggleStates { get; }
    public IReadOnlyList<string> SortPriority { get; }
    public IReadOnlyDictionary<string, bool> SortDescendingStates { get; }
    public float ScrollY { get; }
}

public sealed class SelectLayoutDefinition
{
    public static SelectLayoutDefinition Default { get; } = new(0, new Vector2(220f, 300f), 12, 12, fixedSlots: false);

    public SelectLayoutDefinition(
        int columns,
        Vector2 itemSize,
        int horizontalGap,
        int verticalGap,
        bool fixedSlots,
        float paddingLeft = 0f,
        float paddingTop = 80f,
        float paddingRight = 0f,
        float paddingBottom = 180f)
    {
        Columns = columns;
        ItemSize = itemSize;
        HorizontalGap = horizontalGap;
        VerticalGap = verticalGap;
        FixedSlots = fixedSlots;
        PaddingLeft = paddingLeft;
        PaddingTop = paddingTop;
        PaddingRight = paddingRight;
        PaddingBottom = paddingBottom;
        GroupHeaderHeight = 48f;
        GroupHeaderGap = 16f;
        GroupSectionGap = 48f;
    }

    public int Columns { get; }
    public Vector2 ItemSize { get; }
    public int HorizontalGap { get; }
    public int VerticalGap { get; }
    public bool FixedSlots { get; }
    public float PaddingLeft { get; }
    public float PaddingTop { get; }
    public float PaddingRight { get; }
    public float PaddingBottom { get; }
    public float GroupHeaderHeight { get; }
    public float GroupHeaderGap { get; }
    public float GroupSectionGap { get; }
}

public sealed class SelectGroupDefinition
{
    public SelectGroupDefinition(
        string sorterId,
        Func<IGenericSelectItem, string> keySelector,
        Func<string, SelectGroupHeader> headerSelector,
        IReadOnlyList<string> groupOrder,
        IReadOnlyList<string>? descendingGroupOrder = null)
    {
        SorterId = sorterId;
        KeySelector = keySelector;
        HeaderSelector = headerSelector;
        GroupOrder = groupOrder;
        DescendingGroupOrder = descendingGroupOrder;
    }

    public string SorterId { get; }
    public Func<IGenericSelectItem, string> KeySelector { get; }
    public Func<string, SelectGroupHeader> HeaderSelector { get; }
    public IReadOnlyList<string> GroupOrder { get; }
    public IReadOnlyList<string>? DescendingGroupOrder { get; }

    public IEnumerable<string> GetGroupOrder(bool descending)
    {
        if (!descending)
            return GroupOrder;

        return DescendingGroupOrder ?? GroupOrder.Reverse();
    }
}

public sealed class SelectGroupHeader
{
    public const int CategoryTitleFontSize = 32;
    public const int CategoryDescriptionFontSize = 26;
    private static readonly Regex LocalizedCategoryPattern = new(
        @"^(?<title>\[(?<color>gold|blue)\]\[font_size=)\d+(?<titleTail>\]\[b\].+?\[/b\]\[/font_size\]\[/\k<color>\])(?<description>\s+.+)$",
        RegexOptions.CultureInvariant | RegexOptions.Singleline);
    private readonly Texture2D? _icon;
    private readonly Func<Texture2D?>? _iconProvider;

    public SelectGroupHeader(string text, Texture2D? icon = null, bool showWhenEmpty = false, string? childGroupPrefix = null)
    {
        Text = NormalizeCategoryText(text);
        _icon = icon;
        ShowWhenEmpty = showWhenEmpty;
        ChildGroupPrefix = childGroupPrefix;
    }

    public SelectGroupHeader(string text, Func<Texture2D?> iconProvider, bool showWhenEmpty = false, string? childGroupPrefix = null)
    {
        Text = NormalizeCategoryText(text);
        _iconProvider = iconProvider;
        ShowWhenEmpty = showWhenEmpty;
        ChildGroupPrefix = childGroupPrefix;
    }

    public static SelectGroupHeader Category(string title, string? description = null, Texture2D? icon = null, bool showWhenEmpty = false, string? childGroupPrefix = null)
    {
        return new SelectGroupHeader(FormatCategoryText(title, description), icon, showWhenEmpty, childGroupPrefix);
    }

    public static string FormatCategoryText(string title, string? description = null)
    {
        if (string.IsNullOrWhiteSpace(description))
            return $"[gold][font_size={CategoryTitleFontSize}][b]{title}[/b][/font_size][/gold]";

        return $"[gold][font_size={CategoryTitleFontSize}][b]{title}:[/b][/font_size][/gold] [font_size={CategoryDescriptionFontSize}]{description}[/font_size]";
    }

    private static string NormalizeCategoryText(string text)
    {
        Match match = LocalizedCategoryPattern.Match(text);
        if (!match.Success)
            return text;

        string description = match.Groups["description"].Value.Trim();
        if (description.StartsWith("[font_size=", StringComparison.OrdinalIgnoreCase))
            return text;

        return $"{match.Groups["title"].Value}{CategoryTitleFontSize}{match.Groups["titleTail"].Value} [font_size={CategoryDescriptionFontSize}]{description}[/font_size]";
    }

    public string Text { get; }
    public Texture2D? Icon => _iconProvider?.Invoke() ?? _icon;
    public bool ShowWhenEmpty { get; }
    public string? ChildGroupPrefix { get; }
}

public enum SelectSelectionMode
{
    None,
    Single,
    Multi
}

public enum SelectFilterGroupSelectionMode
{
    Any,
    Single
}

public enum SelectMaterializationMode
{
    Lazy,
    Eager
}

public sealed class SelectItemState
{
    public SelectItemState(
        int originalIndex,
        int visibleIndex,
        int selectionAmount,
        bool isSelected,
        bool isEnabled,
        IReadOnlyDictionary<string, bool>? toggleStates = null)
    {
        OriginalIndex = originalIndex;
        VisibleIndex = visibleIndex;
        SelectionAmount = selectionAmount;
        IsSelected = isSelected;
        IsEnabled = isEnabled;
        ToggleStates = toggleStates ?? new Dictionary<string, bool>(StringComparer.Ordinal);
    }

    public int OriginalIndex { get; }
    public int VisibleIndex { get; }
    public int SelectionAmount { get; }
    public bool IsSelected { get; }
    public bool IsEnabled { get; }
    public IReadOnlyDictionary<string, bool> ToggleStates { get; }

    public bool IsToggleEnabled(string id)
    {
        return ToggleStates.TryGetValue(id, out bool enabled) && enabled;
    }
}

public sealed class SelectToggleDefinition
{
    public SelectToggleDefinition(string id, string label, bool checkedByDefault = false)
    {
        Id = id;
        Label = label;
        CheckedByDefault = checkedByDefault;
    }

    public string Id { get; }
    public string Label { get; }
    public bool CheckedByDefault { get; }
}

public sealed class SelectActionButtonDefinition
{
    public SelectActionButtonDefinition(
        string id,
        string label,
        Action<NGenericSelectScreen> onPressed,
        Texture2D? icon = null)
    {
        Id = id;
        Label = label;
        OnPressed = onPressed;
        Icon = icon;
    }

    public string Id { get; }
    public string Label { get; }
    public Action<NGenericSelectScreen> OnPressed { get; }
    public Texture2D? Icon { get; }
}

public sealed class SelectFilterGroupDefinition
{
    public SelectFilterGroupDefinition(
        string id,
        string label,
        SelectFilterGroupSelectionMode selectionMode = SelectFilterGroupSelectionMode.Any,
        bool requireSelection = false)
    {
        Id = id;
        Label = label;
        SelectionMode = selectionMode;
        RequireSelection = requireSelection;
    }

    public string Id { get; }
    public string Label { get; }
    public SelectFilterGroupSelectionMode SelectionMode { get; }
    public bool RequireSelection { get; }
}

public sealed class SelectFilterDefinition
{
    public SelectFilterDefinition(
        string id,
        string label,
        string groupId,
        Func<IGenericSelectItem, bool> predicate,
        bool enabled = false)
    {
        Id = id;
        Label = label;
        GroupId = groupId;
        Predicate = predicate;
        Enabled = enabled;
    }

    public string Id { get; }
    public string Label { get; }
    public string GroupId { get; }
    public Func<IGenericSelectItem, bool> Predicate { get; }
    public bool Enabled { get; set; }
}

public sealed class SelectSorterDefinition
{
    public SelectSorterDefinition(
        string id,
        string label,
        Comparison<IGenericSelectItem> ascendingComparison,
        Comparison<IGenericSelectItem>? descendingComparison = null,
        bool activeByDefault = false,
        bool descendingByDefault = false)
    {
        Id = id;
        Label = label;
        AscendingComparison = ascendingComparison;
        DescendingComparison = descendingComparison;
        ActiveByDefault = activeByDefault;
        DescendingByDefault = descendingByDefault;
        IsDescending = descendingByDefault;
    }

    public string Id { get; }
    public string Label { get; }
    public Comparison<IGenericSelectItem> AscendingComparison { get; }
    public Comparison<IGenericSelectItem>? DescendingComparison { get; }
    public bool ActiveByDefault { get; }
    public bool DescendingByDefault { get; }
    public bool IsDescending { get; set; }

    public int Compare(IGenericSelectItem left, IGenericSelectItem right)
    {
        if (!IsDescending)
            return AscendingComparison(left, right);

        return DescendingComparison?.Invoke(left, right) ?? -AscendingComparison(left, right);
    }
}

public static class SelectText
{
    private static readonly Regex BbCodeRegex = new(@"\[[^\]]+\]", RegexOptions.Compiled);
    private static readonly Regex HtmlRegex = new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        string withoutTags = HtmlRegex.Replace(BbCodeRegex.Replace(text, " "), " ");
        string collapsed = WhitespaceRegex.Replace(withoutTags, " ");
        return collapsed.Trim().ToLowerInvariant();
    }
}
