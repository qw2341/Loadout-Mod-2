#nullable enable

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
    private const string SelectSortButtonInnerPath = "screens/card_library/library_sort_button";
    private const float SidebarWidth = 288f;
    private const float CardVisualLeftOverhang = 96f;
    private const float CardScrollbarReserve = 48f;
    private const int InitialMaterializeBudget = 96;
    private const int ScrollMaterializeBudget = 48;
    private const int DeferredMaterializeBatchSize = 24;
    private const float MaterializeRowsAhead = 3f;
    private const float MaterializeRowsBehind = 2f;
    private const float CullRetentionRows = 0.75f;

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
    private readonly List<Control> _visibleLayoutNodes = new();
    private readonly Dictionary<IGenericSelectItem, SelectItemLayout> _itemLayouts = new();
    private readonly List<IGenericSelectItem> _itemLayoutOrder = new();

    private readonly Dictionary<string, NLoadoutDropdown> _filterDropdownsByGroupId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NCardViewSortButton> _sortButtonsById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _selectedAmounts = new(StringComparer.Ordinal);

    private LineEdit? _searchLineEdit;
    private BaseButton? _clearSearchButton;
    private NClickableControl? _clearSearchClickable;
    private VBoxContainer? _filterControls;
    private Container? _sortButtonsContainer;
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
    private float _scrollY;
    private float _targetScrollY;
    private float _maxScrollY;
    private bool _scrollbarPressed;
    private System.Threading.CancellationTokenSource? _searchDelayCts;
    private System.Threading.CancellationTokenSource? _materializeCts;
    private ulong _layoutGeneration;

    public IReadOnlyList<IGenericSelectItem> Items => _items;
    public IReadOnlyList<IGenericSelectItem> VisibleItems => _visibleItems;
    public IReadOnlyDictionary<string, int> SelectedAmounts => _selectedAmounts;

    public override void _Ready()
    {
        BindSceneNodes();
        BuildUtilityContainersIfMissing();
        EnsureGameScrollbar();
        EnsureActionButtons();
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
        CancelPendingMaterialization();
    }

    public override void _Process(double delta)
    {
        UpdateSelectScroll(delta);
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

        float drag = ScrollHelper.GetDragForScrollEvent(@event);
        if (!Mathf.IsZeroApprox(drag))
            SetTargetScroll(_targetScrollY - drag);

        GetViewport().SetInputAsHandled();
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
        CancelPendingMaterialization();
        ClearItemViews();

        _items.Clear();
        _visibleItems.Clear();
        _itemLayouts.Clear();
        _itemLayoutOrder.Clear();
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

        CancelPendingMaterialization();
        CancelPendingSearchRefresh();

        _visibleItems.Clear();
        _visibleItems.AddRange(_items.Where(PassesSearchAndFilters));
        _visibleItems.Sort(CompareItems);
        _visibleLayoutNodes.Clear();
        _itemLayouts.Clear();
        _itemLayoutOrder.Clear();

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

        MaterializeViewportItemViews(InitialMaterializeBudget);
        StartDeferredItemMaterialization();
        UpdateViewportCulling();
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
        _clearSearchClickable = GetNodeOrNull<NClickableControl>(ClearSearchButtonPath);
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
        if (GetNodeOrNull<Label>("Sidebar/MarginContainer/TopVBox/TitleLabel") is { } titleLabel)
            titleLabel.Text = SelectScreenLoc.Text("TITLE_SELECT", "Select");

        if (GetNodeOrNull<Label>("Sidebar/MarginContainer/TopVBox/SortLabel") is { } sortLabel)
            sortLabel.Text = SelectScreenLoc.Text("SORT", "Sort");

        if (GetNodeOrNull<Label>("Sidebar/MarginContainer/TopVBox/FilterLabel") is { } filterLabel)
            filterLabel.Text = SelectScreenLoc.Text("FILTERS", "Filters");

        if (_searchLineEdit is not null)
            _searchLineEdit.PlaceholderText = SelectScreenLoc.Text("SEARCH", "Search");
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
            NBackButton backButton = CreateBackButton();
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

        if (_clearSearchButton is not null)
            _clearSearchButton.Pressed += ClearSearch;

        if (_clearSearchClickable is not null)
            _clearSearchClickable.Connect(NClickableControl.SignalName.Released, Callable.From<NClickableControl>(_ => ClearSearch()));

        if (_confirmButton is not null)
            _confirmButton.Pressed += ConfirmSelection;

        if (_confirmClickable is not null)
            _confirmClickable.Connect(NClickableControl.SignalName.Released, Callable.From<NClickableControl>(_ => ConfirmSelection()));

        if (_cancelButton is not null)
            _cancelButton.Pressed += CancelSelection;

        if (_cancelClickable is not null)
            _cancelClickable.Connect(NClickableControl.SignalName.Released, Callable.From<NClickableControl>(_ => CancelSelection()));
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

    private void CancelPendingMaterialization()
    {
        _layoutGeneration++;
        _materializeCts?.Cancel();
        _materializeCts?.Dispose();
        _materializeCts = null;
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

        List<string> orderedKeys = group.GroupOrder
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
            if (groupItems.Count == 0 && !header.ShowWhenEmpty)
                continue;

            Control groupContainer = CreateGroupHeader(key, header);
            groupContainer.Position = new Vector2(groupStartX, y);
            groupContainer.Size = new Vector2(headerWidth, _layout.GroupHeaderHeight);
            _itemGrid!.AddChild(groupContainer);
            _generatedGroupContainers.Add(groupContainer);
            _visibleLayoutNodes.Add(groupContainer);
            y += _layout.GroupHeaderHeight + _layout.GroupHeaderGap;

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

    private void MaterializeViewportItemViews(int maxCount)
    {
        if (_itemGrid is null || maxCount <= 0)
            return;

        int materialized = 0;
        foreach (IGenericSelectItem item in _itemLayoutOrder)
        {
            if (materialized >= maxCount)
                break;

            if (!_itemLayouts.TryGetValue(item, out SelectItemLayout layout) || !IsLayoutNearViewport(layout))
                continue;

            bool needsView = item.View is null || !GodotObject.IsInstanceValid(item.View);
            if (MaterializeItemView(item, layout) && needsView)
                materialized++;
        }
    }

    private bool MaterializeItemView(IGenericSelectItem item, SelectItemLayout layout)
    {
        if (_itemGrid is null)
            return false;

        Control view = EnsureViewInCanvas(item);
        view.Size = layout.Size;
        view.Position = layout.Position;
        view.ZIndex = 0;
        view.Visible = true;

        if (!_visibleLayoutNodes.Contains(view))
            _visibleLayoutNodes.Add(view);

        item.UpdateView(BuildState(item, layout.VisibleIndex));
        return true;
    }

    private void StartDeferredItemMaterialization()
    {
        if (!IsInsideTree() || _itemLayoutOrder.Count == 0)
            return;

        _materializeCts = new System.Threading.CancellationTokenSource();
        ulong generation = _layoutGeneration;
        _ = MaterializeRemainingItemViewsAsync(_itemLayoutOrder.ToList(), generation, _materializeCts.Token);
    }

    private async System.Threading.Tasks.Task MaterializeRemainingItemViewsAsync(
        IReadOnlyList<IGenericSelectItem> itemOrder,
        ulong generation,
        System.Threading.CancellationToken token)
    {
        int index = 0;
        while (!token.IsCancellationRequested && generation == _layoutGeneration && IsInsideTree() && index < itemOrder.Count)
        {
            int materializedThisFrame = 0;
            while (index < itemOrder.Count && materializedThisFrame < DeferredMaterializeBatchSize)
            {
                IGenericSelectItem item = itemOrder[index++];
                if (!_itemLayouts.TryGetValue(item, out SelectItemLayout layout))
                    continue;

                if (item.View is not null && GodotObject.IsInstanceValid(item.View))
                    continue;

                if (MaterializeItemView(item, layout))
                    materializedThisFrame++;
            }

            UpdateViewportCulling();

            if (index < itemOrder.Count)
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
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
        headerRow.AddThemeConstantOverride("separation", header.Icon is null ? 0 : 12);
        container.AddChild(headerRow);

        if (TryGetValidTexture(header.Icon, out Texture2D? headerIcon))
        {
            TextureRect icon = new()
            {
                Texture = headerIcon,
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

    private void UpdateViewportCulling()
    {
        if (_visibleLayoutNodes.Count == 0)
            return;

        Rect2 viewportRect = _scrollMask?.GetGlobalRect() ?? new Rect2(Vector2.Zero, GetViewportRect().Size);
        float rowHeight = Math.Max(1f, ResolveConfiguredItemSize().Y + ItemVerticalGap);
        Rect2 cullRect = viewportRect.Grow(rowHeight * CullRetentionRows);

        foreach (Control control in _visibleLayoutNodes)
        {
            if (!GodotObject.IsInstanceValid(control))
                continue;

            Rect2 rect = control.GetGlobalRect();
            control.Visible = rect.Intersects(cullRect, includeBorders: true);
        }
    }

    private static void KeepHoverTipsAboveSelectScreen()
    {
        NLoadoutPanelRoot.Instance?.AdoptGameHoverTips();
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
            _targetScrollY = Mathf.Clamp((float)_scrollbar.Value * 0.01f * _maxScrollY, 0f, _maxScrollY);

        _scrollY = Mathf.Lerp(_scrollY, _targetScrollY, (float)delta * 15f);
        if (Mathf.Abs(_scrollY - _targetScrollY) < 0.5f)
            _scrollY = _targetScrollY;

        _itemGrid.Position = new Vector2(_itemGrid.Position.X, -_scrollY);

        if (_scrollbar is not null && !_scrollbarPressed)
            _scrollbar.SetValueWithoutAnimation(_maxScrollY <= 0f ? 0 : Mathf.Clamp(_scrollY / _maxScrollY, 0f, 1f) * 100f);

        MaterializeViewportItemViews(ScrollMaterializeBudget);
        UpdateViewportCulling();
    }

    private void SetTargetScroll(float value)
    {
        _targetScrollY = Mathf.Clamp(value, 0f, _maxScrollY);
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

    private static NBackButton CreateBackButton()
    {
        NBackButton backButton = new()
        {
            FocusMode = FocusModeEnum.All,
            MouseFilter = MouseFilterEnum.Stop,
            PivotOffset = new Vector2(20f, 40f)
        };
        backButton.SetAnchorsPreset(LayoutPreset.BottomLeft);
        backButton.OffsetLeft = -40f;
        backButton.OffsetTop = -354f;
        backButton.OffsetRight = 160f;
        backButton.OffsetBottom = -244f;

        TextureRect shadow = new()
        {
            Name = "Shadow",
            Modulate = new Color(0f, 0f, 0f, 0.25098f),
            Texture = LoadGameTexture("res://images/atlases/ui_atlas.sprites/back_button.tres"),
            ExpandMode = TextureRect.ExpandModeEnum.FitWidthProportional,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = MouseFilterEnum.Ignore
        };
        shadow.SetAnchorsPreset(LayoutPreset.FullRect);
        shadow.OffsetLeft = -9f;
        shadow.OffsetTop = -1f;
        shadow.OffsetRight = 58f;
        shadow.OffsetBottom = 39f;
        backButton.AddChild(shadow);

        TextureRect outline = new()
        {
            Name = "Outline",
            Modulate = StsColors.gold,
            Texture = LoadGameTexture("res://images/atlases/compressed.sprites/back_button_outline.tres"),
            ExpandMode = TextureRect.ExpandModeEnum.FitWidthProportional,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = MouseFilterEnum.Ignore
        };
        outline.SetAnchorsPreset(LayoutPreset.FullRect);
        outline.OffsetLeft = -24f;
        outline.OffsetTop = -16f;
        outline.OffsetRight = 49f;
        outline.OffsetBottom = 30f;
        backButton.AddChild(outline);

        TextureRect image = new()
        {
            Name = "Image",
            Texture = LoadGameTexture("res://images/atlases/ui_atlas.sprites/back_button.tres"),
            ExpandMode = TextureRect.ExpandModeEnum.FitWidthProportional,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = MouseFilterEnum.Ignore
        };
        image.SetAnchorsPreset(LayoutPreset.FullRect);
        image.OffsetLeft = -21f;
        image.OffsetTop = -13f;
        image.OffsetRight = 46f;
        image.OffsetBottom = 27f;
        backButton.AddChild(image);

        TextureRect icon = new()
        {
            Name = "Icon",
            Modulate = StsColors.cream,
            Texture = LoadGameTexture("res://images/atlases/compressed.sprites/back_button_arrow.tres"),
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
        controllerIcon.OffsetLeft = -219f;
        controllerIcon.OffsetTop = -48f;
        controllerIcon.OffsetRight = 21f;
        controllerIcon.OffsetBottom = 72f;
        controllerIcon.Scale = new Vector2(0.5f, 0.5f);
        controllerIcon.PivotOffset = new Vector2(256f, 128f);
        backButton.AddChild(controllerIcon);

        AssignOwnerRecursive(backButton, backButton);
        return backButton;
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
        }
    }
}

public static class SelectScreenLoc
{
    private const string Table = "loadout";

    public static string Text(string key, string fallback)
    {
        try
        {
            return LocString.Exists(Table, key)
                ? new LocString(Table, key).GetFormattedText()
                : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    public static string Text(string key, string fallback, params object[] args)
    {
        string format = Text(key, fallback);
        try
        {
            return string.Format(format, args);
        }
        catch
        {
            return fallback;
        }
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
    public SelectSelectionMode SelectionMode { get; init; } = SelectSelectionMode.None;
    public int MinSelection { get; init; } = 0;
    public int MaxTotalSelection { get; init; } = int.MaxValue;
    public int MaxCopiesPerItem { get; init; } = 1;
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
