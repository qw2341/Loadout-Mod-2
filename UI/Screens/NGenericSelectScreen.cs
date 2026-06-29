#nullable enable

namespace Loadout.UI.Screens;

using Godot;
using Loadout.UI.Screens.Controls;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
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
    private const string AllFilterOptionId = "__all__";
    private const string SelectSortButtonInnerPath = "screens/deck_view_screen/deck_view_sort_button";
    private const string SelectFilterDropdownScenePath = "res://UI/Screens/Controls/SelectFilterDropdown.tscn";

    [Export]
    public NodePath SearchLineEditPath = "Sidebar/MarginContainer/MainVBox/TopVBox/SearchBar/TextArea";

    [Export]
    public NodePath ClearSearchButtonPath = "Sidebar/MarginContainer/MainVBox/TopVBox/SearchBar/ClearButton";

    [Export]
    public NodePath FilterControlsPath = "Sidebar/MarginContainer/MainVBox/TopVBox";

    [Export]
    public NodePath ItemGridPath = "CardGrid/ScrollContainer/GridMargin/ItemGrid";

    [Export]
    public NodePath ScrollContainerPath = "CardGrid/ScrollContainer";

    [Export]
    public NodePath ConfirmButtonPath = "Sidebar/MarginContainer/MainVBox/BottomVBox/ConfirmButton";

    [Export]
    public NodePath CancelButtonPath = "Sidebar/MarginContainer/MainVBox/BottomVBox/CancelButton";

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
    private readonly Dictionary<string, SelectGroupDefinition> _groupsBySorterId = new(StringComparer.Ordinal);
    private readonly List<Control> _generatedGroupContainers = new();

    private readonly Dictionary<string, NSelectFilterDropdown> _filterDropdownsByGroupId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NCardViewSortButton> _sortButtonsById = new(StringComparer.Ordinal);
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
    private SelectLayoutDefinition _layout = SelectLayoutDefinition.Default;
    private string _query = string.Empty;
    private bool _isSyncingFilterDropdowns;
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
        RebuildSortButtons();
        RebuildFilterButtons();
        ApplyLayoutSettings();

        if (_isConfigured)
            RefreshNow(resetScroll: true);
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
        _groupsBySorterId.Clear();
        _selectedAmounts.Clear();
        ClearGeneratedGroupContainers();

        _options = new SelectScreenOptions();
        _layout = SelectLayoutDefinition.Default;
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

        float measuredWidth = GetActiveGroupDefinition() is { } group
            ? RefreshGroupedItems(group)
            : RefreshFlatItems();

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
            NCardViewSortButton button = SceneHelper.Instantiate<NCardViewSortButton>(SelectSortButtonInnerPath);
            button.Name = MakeSafeNodeName($"{sorterId}SortButton");
            button.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            button.Connect(NClickableControl.SignalName.Released, Callable.From<NClickableControl>(_ => OnSorterPressed(sorterId)));
            _sortButtonsContainer.AddChild(button);
            button.SetLabel(sorter.Label);
            button.IsDescending = sorter.IsDescending;
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
            if (!_sortButtonsById.TryGetValue(sorter.Id, out NCardViewSortButton? button))
                continue;

            button.SetLabel(sorter.Label);
            button.IsDescending = sorter.IsDescending;
        }
    }

    private void RebuildFilterButtons()
    {
        if (_filtersContainer is null)
            return;

        foreach (Node child in _filtersContainer.GetChildren())
            child.QueueFree();

        _filterDropdownsByGroupId.Clear();
        PackedScene? filterDropdownScene = GD.Load<PackedScene>(SelectFilterDropdownScenePath);
        if (filterDropdownScene is null)
        {
            GD.PushError($"{nameof(NGenericSelectScreen)}: missing filter dropdown scene at '{SelectFilterDropdownScenePath}'.");
            return;
        }

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

            List<SelectDropdownOption> options = new();
            if (!group.RequireSelection)
                options.Add(new SelectDropdownOption(AllFilterOptionId, "All"));

            foreach (SelectFilterDefinition filter in groupFilters)
                options.Add(new SelectDropdownOption(filter.Id, filter.Label));

            string selectedOptionId = GetSelectedFilterIdForGroup(groupId) ?? AllFilterOptionId;
            NSelectFilterDropdown dropdown = filterDropdownScene.Instantiate<NSelectFilterDropdown>(PackedScene.GenEditState.Disabled);
            dropdown.Name = MakeSafeNodeName($"{groupId}FilterDropdown");
            dropdown.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            dropdown.OptionSelected += selectedId => OnFilterDropdownSelected(groupId, selectedId);
            _filtersContainer.AddChild(dropdown);
            dropdown.SetOptions(group.Label, options, selectedOptionId);
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
            RefreshNow(resetScroll: true);
            return;
        }

        if (_filtersById.TryGetValue(selectedOptionId, out SelectFilterDefinition? filter))
            SetExclusiveFilterSelection(filter.GroupId, filter.Id);

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
        SetExclusiveFilterSelection(groupId, selectedFilterId);
    }

    private void EnsureFilterSelectionIsValid(string groupId)
    {
        List<SelectFilterDefinition> groupFilters = GetFiltersInGroup(groupId);
        if (groupFilters.Count == 0)
            return;

        SelectFilterDefinition? selectedFilter = groupFilters.FirstOrDefault(filter => filter.Enabled);
        if (selectedFilter is not null)
        {
            SetExclusiveFilterSelection(groupId, selectedFilter.Id);
            return;
        }

        if (GroupRequiresSelection(groupId))
        {
            SetExclusiveFilterSelection(groupId, groupFilters[0].Id);
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

    private void SetExclusiveFilterSelection(string groupId, string? selectedFilterId)
    {
        List<SelectFilterDefinition> groupFilters = GetFiltersInGroup(groupId);
        if (groupFilters.Count == 0)
            return;

        if (selectedFilterId is null && GroupRequiresSelection(groupId))
            selectedFilterId = groupFilters[0].Id;

        foreach (SelectFilterDefinition filter in groupFilters)
            filter.Enabled = selectedFilterId is not null && string.Equals(filter.Id, selectedFilterId, StringComparison.Ordinal);

        SyncFilterDropdown(groupId);
    }

    private void SyncFilterDropdown(string groupId)
    {
        if (!_filterDropdownsByGroupId.TryGetValue(groupId, out NSelectFilterDropdown? dropdown))
            return;

        _isSyncingFilterDropdowns = true;
        dropdown.SetSelectedOption(GetSelectedFilterIdForGroup(groupId) ?? AllFilterOptionId);
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
        ApplyLayoutToGrid(_itemGrid!, GridColumns > 0 ? GridColumns : CalculateAutoColumns());

        float measuredWidth = 0f;
        for (int i = 0; i < _visibleItems.Count; i++)
        {
            IGenericSelectItem item = _visibleItems[i];
            Control view = EnsureViewInGrid(item, _itemGrid!);
            view.Visible = true;
            _itemGrid!.MoveChild(view, i);

            SelectItemState state = BuildState(item, i);
            item.UpdateView(state);

            Vector2 size = ResolveItemLayoutSize(view);
            measuredWidth = Math.Max(measuredWidth, size.X);
        }

        return measuredWidth;
    }

    private float RefreshGroupedItems(SelectGroupDefinition group)
    {
        ClearGeneratedGroupContainers();
        ApplyLayoutToGrid(_itemGrid!, 1);

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

        List<string> orderedKeys = group.GroupOrder
            .Concat(itemsByKey.Keys)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        float measuredWidth = 0f;
        int visibleIndex = 0;
        foreach (string key in orderedKeys)
        {
            List<IGenericSelectItem> groupItems = itemsByKey.GetValueOrDefault(key) ?? new List<IGenericSelectItem>();
            SelectGroupHeader header = group.HeaderSelector(key);
            if (groupItems.Count == 0 && !header.ShowWhenEmpty)
                continue;

            VBoxContainer groupContainer = CreateGroupContainer(key, header);
            _itemGrid!.AddChild(groupContainer);
            _generatedGroupContainers.Add(groupContainer);

            GridContainer? groupGrid = null;
            if (groupItems.Count > 0)
            {
                groupGrid = CreateGroupGrid(key);
                groupContainer.AddChild(groupGrid);
            }

            foreach (IGenericSelectItem item in groupItems)
            {
                Control view = EnsureViewInGrid(item, groupGrid!);
                view.Visible = true;

                SelectItemState state = BuildState(item, visibleIndex);
                item.UpdateView(state);

                Vector2 size = ResolveItemLayoutSize(view);
                measuredWidth = Math.Max(measuredWidth, size.X);
                visibleIndex++;
            }
        }

        return measuredWidth;
    }

    private VBoxContainer CreateGroupContainer(string key, SelectGroupHeader header)
    {
        VBoxContainer container = new()
        {
            Name = MakeSafeNodeName($"Group_{key}"),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            MouseFilter = MouseFilterEnum.Ignore
        };
        container.AddThemeConstantOverride("separation", 16);

        HBoxContainer headerRow = new()
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            MouseFilter = MouseFilterEnum.Ignore
        };
        headerRow.AddThemeConstantOverride("separation", header.Icon is null ? 0 : 12);
        container.AddChild(headerRow);

        if (header.Icon is not null)
        {
            TextureRect icon = new()
            {
                Texture = header.Icon,
                CustomMinimumSize = new Vector2(48f, 48f),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
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

    private GridContainer CreateGroupGrid(string key)
    {
        GridContainer grid = new()
        {
            Name = MakeSafeNodeName($"GroupGrid_{key}"),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            MouseFilter = MouseFilterEnum.Ignore
        };
        ApplyLayoutToGrid(grid, Math.Max(1, _layout.Columns));
        return grid;
    }

    private static void ApplyGameRichTextTheme(RichTextLabel label)
    {
        Font? regular = GD.Load<Font>("res://themes/kreon_regular_glyph_space_one.tres");
        Font? bold = GD.Load<Font>("res://themes/kreon_bold_glyph_space_one.tres");
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

        label.AddThemeFontSizeOverride("normal_font_size", 24);
        label.AddThemeFontSizeOverride("bold_font_size", 28);
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
                item.View.GetParent()?.RemoveChild(item.View);
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

    private Control EnsureViewInGrid(IGenericSelectItem item, GridContainer parentGrid)
    {
        if (parentGrid is null)
            throw new InvalidOperationException("Item grid is not available.");

        if (item.View is null || !GodotObject.IsInstanceValid(item.View))
        {
            Control view = CreateLayoutView(item, BuildState(item, -1));
            item.SetView(view);
            NormalizeItemForGrid(view);
            BindViewActivation(item, view);
        }

        Control control = item.View!;
        if (control.GetParent() != parentGrid)
        {
            control.GetParent()?.RemoveChild(control);
            parentGrid.AddChild(control);
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
        if (item.TryBindActivation(() => ActivateItem(item)))
            return;

        if (view is NClickableControl clickableControl || TryFindDescendant(view, out clickableControl))
        {
            clickableControl.Connect(NClickableControl.SignalName.Released, Callable.From<NClickableControl>(_ => ActivateItem(item)));
            return;
        }

        if (view is BaseButton button || TryFindDescendant(view, out button))
            button.Pressed += () => ActivateItem(item);

        // For non-button views, the adapter should provide BindActivation.
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
            isEnabled: true);
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

        int columns = GridColumns > 0 ? GridColumns : CalculateAutoColumns();
        ApplyLayoutToGrid(_itemGrid, Math.Max(1, columns));
    }

    private void ApplyLayoutToGrid(GridContainer grid, int columns)
    {
        grid.AddThemeConstantOverride("h_separation", ItemHorizontalGap);
        grid.AddThemeConstantOverride("v_separation", ItemVerticalGap);
        grid.Columns = Math.Max(1, columns);
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

    public SelectScreenBuilder<TModel> Layout(
        int columns,
        Vector2 itemSize,
        int horizontalGap,
        int verticalGap,
        bool fixedSlots = true)
    {
        _screen.SetLayout(new SelectLayoutDefinition(columns, itemSize, horizontalGap, verticalGap, fixedSlots));
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

    public SelectScreenBuilder<TModel> GroupBySorter(
        string sorterId,
        Func<TModel, string> keySelector,
        Func<string, SelectGroupHeader> headerSelector,
        IEnumerable<string> groupOrder)
    {
        _screen.AddGroupDefinition(new SelectGroupDefinition(
            sorterId,
            item => item is GenericSelectItem<TModel> typed ? keySelector(typed.Model) : string.Empty,
            headerSelector,
            groupOrder.ToList()));

        return this;
    }
}

public sealed class SelectItemAdapter<TModel>
{
    public required Func<TModel, string> GetId { get; init; }
    public required Func<TModel, string> GetName { get; init; }
    public Func<TModel, string>? GetSearchText { get; init; }
    public required Func<TModel, SelectItemState, Control> CreateView { get; init; }
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

    Control CreateView(SelectItemState state);
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

    public Control CreateView(SelectItemState state)
    {
        return _adapter.CreateView(Model, state);
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
    public SelectSelectionMode SelectionMode { get; init; } = SelectSelectionMode.Multi;
    public int MinSelection { get; init; } = 0;
    public int MaxTotalSelection { get; init; } = int.MaxValue;
    public int MaxCopiesPerItem { get; init; } = 1;
}

public sealed class SelectLayoutDefinition
{
    public static SelectLayoutDefinition Default { get; } = new(0, new Vector2(220f, 300f), 12, 12, fixedSlots: false);

    public SelectLayoutDefinition(int columns, Vector2 itemSize, int horizontalGap, int verticalGap, bool fixedSlots)
    {
        Columns = columns;
        ItemSize = itemSize;
        HorizontalGap = horizontalGap;
        VerticalGap = verticalGap;
        FixedSlots = fixedSlots;
    }

    public int Columns { get; }
    public Vector2 ItemSize { get; }
    public int HorizontalGap { get; }
    public int VerticalGap { get; }
    public bool FixedSlots { get; }
}

public sealed class SelectGroupDefinition
{
    public SelectGroupDefinition(
        string sorterId,
        Func<IGenericSelectItem, string> keySelector,
        Func<string, SelectGroupHeader> headerSelector,
        IReadOnlyList<string> groupOrder)
    {
        SorterId = sorterId;
        KeySelector = keySelector;
        HeaderSelector = headerSelector;
        GroupOrder = groupOrder;
    }

    public string SorterId { get; }
    public Func<IGenericSelectItem, string> KeySelector { get; }
    public Func<string, SelectGroupHeader> HeaderSelector { get; }
    public IReadOnlyList<string> GroupOrder { get; }
}

public sealed class SelectGroupHeader
{
    public SelectGroupHeader(string text, Texture2D? icon = null, bool showWhenEmpty = false)
    {
        Text = text;
        Icon = icon;
        ShowWhenEmpty = showWhenEmpty;
    }

    public string Text { get; }
    public Texture2D? Icon { get; }
    public bool ShowWhenEmpty { get; }
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
