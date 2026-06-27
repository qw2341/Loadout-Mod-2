#nullable enable

namespace Loadout.UI.Screens;

using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

/// <summary>
/// Generic, reusable select-screen root for Godot/STS2 mods.
///
/// The Godot node itself is intentionally non-generic because Godot C# scene scripts
/// should be concrete node types. Type safety is provided by SelectItemAdapter<TModel>
/// and the typed AddFilter/AddSorter overloads.
/// </summary>
public partial class NGenericSelectScreen : Control
{
    [Export]
    public NodePath SearchLineEditPath = "PanelContainer/HBoxContainer/SideBar/MarginContainer/FilterControls/SearchBar/LineEdit";

    [Export]
    public NodePath ClearSearchButtonPath = "PanelContainer/HBoxContainer/SideBar/MarginContainer/FilterControls/SearchBar/TextureButton";

    [Export]
    public NodePath FilterControlsPath = "PanelContainer/HBoxContainer/SideBar/MarginContainer/FilterControls";

    [Export]
    public NodePath ItemGridPath = "PanelContainer/HBoxContainer/ScrollContainer/ItemGrid";

    [Export]
    public NodePath ScrollContainerPath = "PanelContainer/HBoxContainer/ScrollContainer";

    [Export]
    public NodePath ConfirmButtonPath = "PanelContainer/VBoxContainer/ConfirmButton";

    [Export]
    public NodePath CancelButtonPath = "PanelContainer/VBoxContainer/CancelButton";

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
    public event Action<IGenericSelectItem, SelectItemState>? ItemActivated;
    public event Action<IGenericSelectItem, SelectItemState>? ItemSelectionChanged;

    private readonly List<IGenericSelectItem> _items = new();
    private readonly List<IGenericSelectItem> _visibleItems = new();

    private readonly List<SelectFilterDefinition> _filters = new();
    private readonly Dictionary<string, SelectFilterDefinition> _filtersById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SelectFilterGroupDefinition> _filterGroupsById = new(StringComparer.Ordinal);
    private readonly List<string> _filterGroupOrder = new();

    private readonly List<SelectSorterDefinition> _sorters = new();
    private readonly Dictionary<string, SelectSorterDefinition> _sortersById = new(StringComparer.Ordinal);
    private readonly List<string> _sortPriority = new();

    private readonly Dictionary<string, CheckButton> _filterButtonsById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Button> _sortButtonsById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _selectedAmounts = new(StringComparer.Ordinal);

    private LineEdit? _searchLineEdit;
    private BaseButton? _clearSearchButton;
    private VBoxContainer? _filterControls;
    private HFlowContainer? _sortButtonsContainer;
    private VBoxContainer? _filtersContainer;
    private GridContainer? _itemGrid;
    private ScrollContainer? _scrollContainer;
    private BaseButton? _confirmButton;
    private BaseButton? _cancelButton;

    private SelectScreenOptions _options = new();
    private string _query = string.Empty;
    private bool _isSyncingFilterButtons;
    private bool _isConfigured;
    private float _lastMeasuredItemWidth = -1f;
    private System.Threading.CancellationTokenSource? _searchDelayCts;

    public IReadOnlyList<IGenericSelectItem> Items => _items;
    public IReadOnlyList<IGenericSelectItem> VisibleItems => _visibleItems;
    public IReadOnlyDictionary<string, int> SelectedAmounts => _selectedAmounts;

    public override void _Ready()
    {
        BindSceneNodes();
        BuildUtilityContainersIfMissing();
        BindSceneSignals();
        ApplyLayoutSettings();
    }

    public override void _ExitTree()
    {
        _searchDelayCts?.Cancel();
        _searchDelayCts?.Dispose();
        _searchDelayCts = null;
    }

    /// <summary>
    /// The main entry point. Build wrappers from any model type, then configure filters/sorters/options with a typed builder.
    /// </summary>
    public void Configure<TModel>(
        IEnumerable<TModel> models,
        SelectItemAdapter<TModel> adapter,
        Action<SelectScreenBuilder<TModel>>? build = null)
    {
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
        RefreshNow(resetScroll: true);
    }

    public void ResetConfiguration()
    {
        ClearItemViews();

        _items.Clear();
        _visibleItems.Clear();
        _filters.Clear();
        _filtersById.Clear();
        _filterGroupsById.Clear();
        _filterGroupOrder.Clear();
        _sorters.Clear();
        _sortersById.Clear();
        _sortPriority.Clear();
        _selectedAmounts.Clear();

        _options = new SelectScreenOptions();
        _query = string.Empty;
        _isConfigured = false;
        _lastMeasuredItemWidth = -1f;

        if (_searchLineEdit is not null)
            _searchLineEdit.Text = string.Empty;

        RebuildSortButtons();
        RebuildFilterButtons();
        UpdateConfirmButtonState();
    }

    public void SetOptions(SelectScreenOptions options)
    {
        _options = options;
        UpdateConfirmButtonState();
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

        RebuildFilterButtons();
    }

    public void AddSorter(SelectSorterDefinition sorter)
    {
        if (_sortersById.ContainsKey(sorter.Id))
            throw new InvalidOperationException($"Sorter id '{sorter.Id}' already exists.");

        _sorters.Add(sorter);
        _sortersById[sorter.Id] = sorter;

        if (sorter.ActiveByDefault)
            _sortPriority.Add(sorter.Id);

        RebuildSortButtons();
    }

    public void RefreshNow(bool resetScroll = false)
    {
        if (!_isConfigured || _itemGrid is null)
            return;

        CancelPendingSearchRefresh();

        _visibleItems.Clear();
        _visibleItems.AddRange(_items.Where(PassesSearchAndFilters));
        _visibleItems.Sort(CompareItems);

        foreach (IGenericSelectItem item in _items)
        {
            if (item.View is not null && GodotObject.IsInstanceValid(item.View))
                item.View.Visible = false;
        }

        float measuredWidth = 0f;
        for (int i = 0; i < _visibleItems.Count; i++)
        {
            IGenericSelectItem item = _visibleItems[i];
            Control view = EnsureViewInGrid(item);
            view.Visible = true;
            _itemGrid.MoveChild(view, i);

            SelectItemState state = BuildState(item, i);
            item.UpdateView(state);

            Vector2 size = ResolveItemLayoutSize(view);
            measuredWidth = Math.Max(measuredWidth, size.X);
        }

        _lastMeasuredItemWidth = measuredWidth > 0f ? measuredWidth : FallbackItemWidth;
        ApplyLayoutSettings();
        UpdateConfirmButtonState();

        if (resetScroll && _scrollContainer is not null)
            _scrollContainer.SetDeferred(ScrollContainer.PropertyName.ScrollVertical, 0);
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

        List<IGenericSelectItem> selected = _items
            .Where(item => _selectedAmounts.GetValueOrDefault(item.Id) > 0)
            .ToList();

        Confirmed?.Invoke(selected);
    }

    public void CancelSelection()
    {
        Cancelled?.Invoke();
    }

    private void BindSceneNodes()
    {
        _searchLineEdit = GetNodeOrNull<LineEdit>(SearchLineEditPath);
        _clearSearchButton = GetNodeOrNull<BaseButton>(ClearSearchButtonPath);
        _filterControls = GetNodeOrNull<VBoxContainer>(FilterControlsPath);
        _itemGrid = GetNodeOrNull<GridContainer>(ItemGridPath);
        _scrollContainer = GetNodeOrNull<ScrollContainer>(ScrollContainerPath);
        _confirmButton = GetNodeOrNull<BaseButton>(ConfirmButtonPath);
        _cancelButton = GetNodeOrNull<BaseButton>(CancelButtonPath);

        if (_searchLineEdit is null)
            GD.PushWarning($"{nameof(NGenericSelectScreen)}: missing search line edit at '{SearchLineEditPath}'. Search will be disabled.");

        if (_filterControls is null)
            GD.PushWarning($"{nameof(NGenericSelectScreen)}: missing filter controls at '{FilterControlsPath}'. Filters will still work, but no UI will be built.");

        if (_itemGrid is null)
            GD.PushError($"{nameof(NGenericSelectScreen)}: missing item grid at '{ItemGridPath}'. The select screen cannot render items.");

        if (_scrollContainer is not null)
            _scrollContainer.FollowFocus = true;
    }

    private void BuildUtilityContainersIfMissing()
    {
        if (_filterControls is null)
            return;

        _sortButtonsContainer = _filterControls.GetNodeOrNull<HFlowContainer>("SortButtons");
        if (_sortButtonsContainer is null)
        {
            _sortButtonsContainer = new HFlowContainer
            {
                Name = "SortButtons",
                SizeFlagsHorizontal = SizeFlags.ExpandFill
            };
            _filterControls.AddChild(_sortButtonsContainer);
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
    }

    private void BindSceneSignals()
    {
        if (_searchLineEdit is not null)
        {
            _query = _searchLineEdit.Text;
            _searchLineEdit.TextChanged += OnSearchTextChanged;
            _searchLineEdit.TextSubmitted += OnSearchTextSubmitted;
        }

        if (_clearSearchButton is not null)
            _clearSearchButton.Pressed += ClearSearch;

        if (_confirmButton is not null)
            _confirmButton.Pressed += ConfirmSelection;

        if (_cancelButton is not null)
            _cancelButton.Pressed += CancelSelection;
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

    private void CancelPendingSearchRefresh()
    {
        _searchDelayCts?.Cancel();
        _searchDelayCts?.Dispose();
        _searchDelayCts = null;
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
            Button button = new()
            {
                Text = sorter.Label,
                SizeFlagsHorizontal = SizeFlags.ExpandFill
            };

            button.Pressed += () => OnSorterPressed(sorterId);
            _sortButtonsContainer.AddChild(button);
            _sortButtonsById[sorterId] = button;
        }

        UpdateSortButtonLabels();
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
            if (!_sortButtonsById.TryGetValue(sorter.Id, out Button? button))
                continue;

            int priority = _sortPriority.IndexOf(sorter.Id);
            if (priority < 0)
            {
                button.Text = sorter.Label;
                continue;
            }

            string direction = sorter.IsDescending ? "↓" : "↑";
            button.Text = $"{sorter.Label} {direction} {priority + 1}";
        }
    }

    private void RebuildFilterButtons()
    {
        if (_filtersContainer is null)
            return;

        foreach (Node child in _filtersContainer.GetChildren())
            child.QueueFree();

        _filterButtonsById.Clear();

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

            Label groupLabel = new()
            {
                Text = group.Label,
                SizeFlagsHorizontal = SizeFlags.ExpandFill
            };
            _filtersContainer.AddChild(groupLabel);

            VBoxContainer groupBox = new()
            {
                Name = $"{groupId}Options",
                SizeFlagsHorizontal = SizeFlags.ExpandFill
            };
            _filtersContainer.AddChild(groupBox);

            foreach (SelectFilterDefinition filter in groupFilters)
            {
                string filterId = filter.Id;
                CheckButton button = new()
                {
                    Text = filter.Label,
                    ButtonPressed = filter.Enabled,
                    SizeFlagsHorizontal = SizeFlags.ExpandFill
                };

                button.Toggled += enabled => OnFilterToggled(filterId, enabled);
                groupBox.AddChild(button);
                _filterButtonsById[filterId] = button;
            }
        }
    }

    private void OnFilterToggled(string filterId, bool enabled)
    {
        if (_isSyncingFilterButtons)
            return;

        if (!_filtersById.TryGetValue(filterId, out SelectFilterDefinition? filter))
            return;

        filter.Enabled = enabled;

        if (enabled)
        {
            EnforceSingleFilterSelection(filter.GroupId, filter.Id);
        }
        else if (GroupRequiresSelection(filter.GroupId) && !HasEnabledFilterInGroup(filter.GroupId))
        {
            SetFilterState(filter.Id, true);
            return;
        }

        RefreshNow(resetScroll: true);
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
        if (!_filterGroupsById.TryGetValue(groupId, out SelectFilterGroupDefinition? group))
            return;

        if (group.SelectionMode != SelectFilterGroupSelectionMode.Single)
            return;

        foreach (SelectFilterDefinition filter in _filters)
        {
            if (!string.Equals(filter.GroupId, groupId, StringComparison.Ordinal))
                continue;

            if (string.Equals(filter.Id, selectedFilterId, StringComparison.Ordinal))
                continue;

            if (filter.Enabled)
                SetFilterState(filter.Id, false);
        }
    }

    private bool GroupRequiresSelection(string groupId)
    {
        return _filterGroupsById.TryGetValue(groupId, out SelectFilterGroupDefinition? group)
            && group.RequireSelection;
    }

    private bool HasEnabledFilterInGroup(string groupId)
    {
        return _filters.Any(filter =>
            string.Equals(filter.GroupId, groupId, StringComparison.Ordinal) && filter.Enabled);
    }

    private void SetFilterState(string filterId, bool enabled)
    {
        if (!_filtersById.TryGetValue(filterId, out SelectFilterDefinition? filter))
            return;

        filter.Enabled = enabled;

        if (_filterButtonsById.TryGetValue(filterId, out CheckButton? button))
        {
            _isSyncingFilterButtons = true;
            button.ButtonPressed = enabled;
            _isSyncingFilterButtons = false;
        }
    }

    private bool PassesSearchAndFilters(IGenericSelectItem item)
    {
        if (!string.IsNullOrWhiteSpace(_query))
        {
            string normalizedQuery = SelectText.Normalize(_query);
            if (!item.MatchesSearch(normalizedQuery))
                return false;
        }

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

    private Control EnsureViewInGrid(IGenericSelectItem item)
    {
        if (_itemGrid is null)
            throw new InvalidOperationException("Item grid is not available.");

        if (item.View is null || !GodotObject.IsInstanceValid(item.View))
        {
            Control view = item.CreateView(BuildState(item, -1));
            item.SetView(view);
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

    private void BindViewActivation(IGenericSelectItem item, Control view)
    {
        if (item.TryBindActivation(() => ActivateItem(item)))
            return;

        if (view is BaseButton button)
            button.Pressed += () => ActivateItem(item);

        // For non-button views, the adapter should provide BindActivation.
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
            isEnabled: true);
    }

    private void RefreshVisibleItemStates()
    {
        for (int i = 0; i < _visibleItems.Count; i++)
        {
            IGenericSelectItem item = _visibleItems[i];
            if (item.View is null || !GodotObject.IsInstanceValid(item.View))
                continue;

            item.UpdateView(BuildState(item, i));
        }
    }

    private void ClearItemViews()
    {
        foreach (IGenericSelectItem item in _items)
        {
            if (item.View is null || !GodotObject.IsInstanceValid(item.View))
                continue;

            item.View.GetParent()?.RemoveChild(item.View);
            item.View.QueueFree();
            item.SetView(null);
        }
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

    private void ApplyLayoutSettings()
    {
        if (_itemGrid is null)
            return;

        _itemGrid.AddThemeConstantOverride("h_separation", ItemHorizontalGap);
        _itemGrid.AddThemeConstantOverride("v_separation", ItemVerticalGap);

        int columns = GridColumns > 0 ? GridColumns : CalculateAutoColumns();
        _itemGrid.Columns = Math.Max(1, columns);
    }

    private int CalculateAutoColumns()
    {
        if (_itemGrid is null)
            return 1;

        float availableWidth = _scrollContainer?.Size.X ?? _itemGrid.Size.X;
        if (availableWidth <= 0f)
            return Math.Max(1, _itemGrid.Columns);

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
        if (_confirmButton is null)
            return;

        _confirmButton.Disabled = !IsConfirmAllowed();
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
}

public sealed class SelectItemAdapter<TModel>
{
    
    
    public required Func<TModel, string> GetId { get; init; }
    public required Func<TModel, string> GetName { get; init; }
    public Func<TModel, string>? GetSearchText { get; init; }
    public required Func<TModel, SelectItemState, Control> CreateView { get; init; }
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

    Control CreateView(SelectItemState state);
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

    public Control CreateView(SelectItemState state)
    {
        return _adapter.CreateView(Model, state);
    }

    public void UpdateView(SelectItemState state)
    {
        if (View is not null)
            _adapter.UpdateView?.Invoke(Model, View, state);
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
    public SelectSelectionMode SelectionMode { get; init; } = SelectSelectionMode.Multi;
    public int MinSelection { get; init; } = 0;
    public int MaxTotalSelection { get; init; } = int.MaxValue;
    public int MaxCopiesPerItem { get; init; } = 1;
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

public sealed class SelectItemState
{
    public SelectItemState(int originalIndex, int visibleIndex, int selectionAmount, bool isSelected, bool isEnabled)
    {
        OriginalIndex = originalIndex;
        VisibleIndex = visibleIndex;
        SelectionAmount = selectionAmount;
        IsSelected = isSelected;
        IsEnabled = isEnabled;
    }

    public int OriginalIndex { get; }
    public int VisibleIndex { get; }
    public int SelectionAmount { get; }
    public bool IsSelected { get; }
    public bool IsEnabled { get; }
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
