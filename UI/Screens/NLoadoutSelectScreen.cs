#nullable enable

namespace Loadout.UI.Screens;

using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

public enum SelectFilterGroupSelectionMode
{
	Any,
	Single
}

public sealed class SelectFilterGroup
{
	public SelectFilterGroup(
		string id,
		string? label = null,
		SelectFilterGroupSelectionMode selectionMode = SelectFilterGroupSelectionMode.Any,
		bool requireSelection = false)
	{
		Id = id;
		Label = label ?? id;
		SelectionMode = selectionMode;
		RequireSelection = requireSelection;
	}

	public string Id { get; }
	public string Label { get; }
	public SelectFilterGroupSelectionMode SelectionMode { get; }
	public bool RequireSelection { get; }
}

public sealed class SelectFilter<TModel>
{
	public SelectFilter(
		string id,
		string label,
		Func<TModel, bool> predicate,
		string groupId = "default",
		bool enabledByDefault = false)
	{
		Id = id;
		Label = label;
		GroupId = groupId;
		Predicate = predicate;
		EnabledByDefault = enabledByDefault;
	}

	public string Id { get; }
	public string Label { get; }
	public string GroupId { get; }
	public bool EnabledByDefault { get; }
	public Func<TModel, bool> Predicate { get; }
}

public sealed class SelectSorter<TModel>
{
	public SelectSorter(
		string id,
		string label,
		Comparison<TModel> ascendingComparison,
		Comparison<TModel>? descendingComparison = null,
		bool descendingByDefault = false)
	{
		Id = id;
		Label = label;
		AscendingComparison = ascendingComparison;
		DescendingComparison = descendingComparison;
		DescendingByDefault = descendingByDefault;
	}

	public string Id { get; }
	public string Label { get; }
	public Comparison<TModel> AscendingComparison { get; }
	public Comparison<TModel>? DescendingComparison { get; }
	public bool DescendingByDefault { get; }
}

public partial class NLoadoutSelectScreen : Control
{
	[Export]
	public NodePath FilterControlsPath = "PanelContainer/HBoxContainer/SideBar/MarginContainer/FilterControls";

	[Export]
	public NodePath SearchLineEditPath = "PanelContainer/HBoxContainer/SideBar/MarginContainer/FilterControls/SearchBar/LineEdit";

	[Export]
	public NodePath ClearSearchButtonPath = "PanelContainer/HBoxContainer/SideBar/MarginContainer/FilterControls/SearchBar/TextureButton";

	[Export]
	public NodePath ItemGridPath = "PanelContainer/HBoxContainer/ScrollContainer/ItemGrid";

	[Export(PropertyHint.Range, "0,1000,1")]
	public int DelayAfterTextFilterChangedMsec = 160;

	[Export(PropertyHint.Range, "0,20,1")]
	public int GridColumns = 0;

	[Export(PropertyHint.Range, "0,64,1")]
	public int ItemHorizontalGap = 12;

	[Export(PropertyHint.Range, "0,64,1")]
	public int ItemVerticalGap = 12;

	[Export(PropertyHint.Range, "0,256,1")]
	public int GridMarginLeft = 8;

	[Export(PropertyHint.Range, "0,256,1")]
	public int GridMarginTop = 8;

	[Export(PropertyHint.Range, "0,256,1")]
	public int GridMarginRight = 8;

	[Export(PropertyHint.Range, "0,256,1")]
	public int GridMarginBottom = 8;

	[Export(PropertyHint.Range, "1,2048,1")]
	public int FallbackItemWidth = 220;

	[Export(PropertyHint.Range, "1,2048,1")]
	public int FallbackItemHeight = 300;

	public event Action<object?>? ItemActivated;

	private readonly List<ModelEntry> _entries = new();
	private readonly List<FilterDefinition> _filters = new();
	private readonly Dictionary<string, FilterDefinition> _filtersById = new(StringComparer.Ordinal);
	private readonly List<string> _filterGroupOrder = new();
	private readonly Dictionary<string, SelectFilterGroup> _filterGroups = new(StringComparer.Ordinal);
	private readonly List<SorterDefinition> _sorters = new();
	private readonly Dictionary<string, SorterDefinition> _sortersById = new(StringComparer.Ordinal);
	private readonly List<string> _sortPriority = new();
	private readonly Dictionary<string, CheckButton> _filterButtonsById = new(StringComparer.Ordinal);
	private readonly Dictionary<string, Button> _sortButtonsById = new(StringComparer.Ordinal);

	private Type? _modelType;
	private Func<object?, string> _searchTextSelector = DefaultSearchTextSelector;
	private Func<string, string, bool> _searchMatcher = DefaultSearchMatcher;
	private Func<object?, Control>? _itemRenderer;
	private CancellationTokenSource? _displayCardsShortDelayCancelToken;
	private string _query = string.Empty;
	private bool _synchronizingFilterButtons;

	private VBoxContainer? _filterControls;
	private LineEdit? _searchLineEdit;
	private BaseButton? _clearSearchButton;
	private GridContainer? _itemGrid;
	private MarginContainer? _itemGridMarginContainer;
	private Control? _layoutSizeHost;
	private HFlowContainer? _sortButtonsContainer;
	private VBoxContainer? _filtersContainer;
	private float _lastMeasuredItemWidth = -1f;

	public override void _Ready()
	{
		_filterControls = GetNodeOrNull<VBoxContainer>(FilterControlsPath);
		_searchLineEdit = GetNodeOrNull<LineEdit>(SearchLineEditPath);
		_clearSearchButton = GetNodeOrNull<BaseButton>(ClearSearchButtonPath);
		_itemGrid = GetNodeOrNull<GridContainer>(ItemGridPath);

		if (_filterControls is null || _searchLineEdit is null || _itemGrid is null)
		{
			GD.PushError($"{nameof(NLoadoutSelectScreen)} is missing required nodes.");
			return;
		}

		EnsureItemGridMarginContainer();
		_layoutSizeHost = _itemGridMarginContainer ?? _itemGrid.GetParentOrNull<Control>();
		if (_layoutSizeHost is not null)
		{
			_layoutSizeHost.Resized += OnLayoutSizeHostResized;
		}

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

		_query = _searchLineEdit.Text;

		_searchLineEdit.TextChanged += SearchBarQueryChanged;
		_searchLineEdit.TextSubmitted += SearchBarQuerySubmitted;

		if (_clearSearchButton is not null)
		{
			_clearSearchButton.Pressed += ClearSearchQuery;
		}

		RebuildSortButtons();
		RebuildFilterButtons();
		ApplyLayoutFromCurrentContent();
		RefreshVisibleItems();
	}

	public override void _ExitTree()
	{
		if (_searchLineEdit is not null)
		{
			_searchLineEdit.TextChanged -= SearchBarQueryChanged;
			_searchLineEdit.TextSubmitted -= SearchBarQuerySubmitted;
		}

		if (_clearSearchButton is not null)
		{
			_clearSearchButton.Pressed -= ClearSearchQuery;
		}

		if (_layoutSizeHost is not null)
		{
			_layoutSizeHost.Resized -= OnLayoutSizeHostResized;
			_layoutSizeHost = null;
		}

		_displayCardsShortDelayCancelToken?.Cancel();
		_displayCardsShortDelayCancelToken?.Dispose();
		_displayCardsShortDelayCancelToken = null;
	}

	public void ResetConfiguration()
	{
		_entries.Clear();
		_filters.Clear();
		_filtersById.Clear();
		_filterGroups.Clear();
		_filterGroupOrder.Clear();
		_sorters.Clear();
		_sortersById.Clear();
		_sortPriority.Clear();
		_modelType = null;
		_query = string.Empty;
		_searchTextSelector = DefaultSearchTextSelector;
		_searchMatcher = DefaultSearchMatcher;
		_itemRenderer = null;

		RebuildSortButtons();
		RebuildFilterButtons();
		RefreshVisibleItems();
	}

	public void SetItems<TModel>(IEnumerable<TModel> items, Func<TModel, string>? searchTextSelector = null)
	{
		EnsureModelType<TModel>();

		if (searchTextSelector is not null)
		{
			_searchTextSelector = model => searchTextSelector((TModel)model!);
		}

		_entries.Clear();

		int index = 0;
		foreach (TModel model in items)
		{
			string searchableText = _searchTextSelector(model);
			_entries.Add(new ModelEntry(model, searchableText, index));
			index++;
		}

		RefreshVisibleItems();
	}

	public void SetItemRenderer<TModel>(Func<TModel, Control> itemRenderer)
	{
		EnsureModelType<TModel>();
		_itemRenderer = model => itemRenderer((TModel)model!);
		RefreshVisibleItems();
	}

	public void SetSearchMatcher(Func<string, string, bool> searchMatcher)
	{
		_searchMatcher = searchMatcher;
		RefreshVisibleItems();
	}

	public void SetFilterGroups(IEnumerable<SelectFilterGroup> groups)
	{
		_filterGroups.Clear();
		_filterGroupOrder.Clear();

		foreach (SelectFilterGroup group in groups)
		{
			_filterGroups[group.Id] = group;
			_filterGroupOrder.Add(group.Id);
		}

		RebuildFilterButtons();
		RefreshVisibleItems();
	}

	public void AddFilter<TModel>(SelectFilter<TModel> filter)
	{
		EnsureModelType<TModel>();

		if (_filtersById.ContainsKey(filter.Id))
		{
			throw new InvalidOperationException($"Filter id '{filter.Id}' already exists.");
		}

		EnsureFilterGroupExists(filter.GroupId);

		FilterDefinition def = new(
			filter.Id,
			filter.GroupId,
			filter.Label,
			filter.EnabledByDefault,
			model => filter.Predicate((TModel)model!));

		_filters.Add(def);
		_filtersById[def.Id] = def;

		if (def.Enabled)
		{
			EnforceSingleSelection(def.GroupId, def.Id);
		}

		RebuildFilterButtons();
		RefreshVisibleItems();
	}

	public void ClearFilters()
	{
		_filters.Clear();
		_filtersById.Clear();
		_filterGroups.Clear();
		_filterGroupOrder.Clear();
		RebuildFilterButtons();
		RefreshVisibleItems();
	}

	public void AddSorter<TModel>(SelectSorter<TModel> sorter)
	{
		EnsureModelType<TModel>();

		if (_sortersById.ContainsKey(sorter.Id))
		{
			throw new InvalidOperationException($"Sorter id '{sorter.Id}' already exists.");
		}

		SorterDefinition def = new(
			sorter.Id,
			sorter.Label,
			sorter.DescendingByDefault,
			(a, b) => sorter.AscendingComparison((TModel)a!, (TModel)b!),
			sorter.DescendingComparison is null
				? null
				: (a, b) => sorter.DescendingComparison((TModel)a!, (TModel)b!));

		_sorters.Add(def);
		_sortersById[def.Id] = def;
		RebuildSortButtons();
		RefreshVisibleItems();
	}

	public void ClearSorters()
	{
		_sorters.Clear();
		_sortersById.Clear();
		_sortPriority.Clear();
		RebuildSortButtons();
		RefreshVisibleItems();
	}

	public void RefreshNow()
	{
		RefreshVisibleItems();
	}

	public void SetLayoutColumns(int columns)
	{
		GridColumns = Math.Max(0, columns);
		ApplyLayoutFromCurrentContent();
	}

	public void SetItemGaps(int horizontalGap, int verticalGap)
	{
		ItemHorizontalGap = Math.Max(0, horizontalGap);
		ItemVerticalGap = Math.Max(0, verticalGap);
		ApplyLayoutFromCurrentContent();
	}

	public void SetGridMargins(int left, int top, int right, int bottom)
	{
		GridMarginLeft = Math.Max(0, left);
		GridMarginTop = Math.Max(0, top);
		GridMarginRight = Math.Max(0, right);
		GridMarginBottom = Math.Max(0, bottom);
		ApplyLayoutFromCurrentContent();
	}

	private void EnsureItemGridMarginContainer()
	{
		if (_itemGrid is null)
		{
			return;
		}

		if (_itemGrid.GetParent() is MarginContainer existingMargin &&
		    string.Equals(existingMargin.Name, "ItemGridMargins", StringComparison.Ordinal))
		{
			_itemGridMarginContainer = existingMargin;
			return;
		}

		if (_itemGrid.GetParent() is not Control parentControl)
		{
			return;
		}

		int previousIndex = _itemGrid.GetIndex();
		_itemGridMarginContainer = new MarginContainer
		{
			Name = "ItemGridMargins",
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
			SizeFlagsVertical = SizeFlags.ExpandFill
		};

		parentControl.RemoveChild(_itemGrid);
		parentControl.AddChild(_itemGridMarginContainer);
		parentControl.MoveChild(_itemGridMarginContainer, previousIndex);
		_itemGridMarginContainer.AddChild(_itemGrid);
		_itemGrid.SizeFlagsHorizontal = SizeFlags.ExpandFill;
		_itemGrid.SizeFlagsVertical = SizeFlags.ShrinkBegin;
	}

	private void OnLayoutSizeHostResized()
	{
		ApplyLayoutFromCurrentContent();
	}

	private void ApplyLayoutFromCurrentContent()
	{
		if (_itemGrid is null)
		{
			return;
		}

		ApplyLayoutSettings(_lastMeasuredItemWidth);
	}

	private void ApplyLayoutSettings(float measuredItemWidth)
	{
		if (_itemGrid is null)
		{
			return;
		}

		_itemGrid.AddThemeConstantOverride("h_separation", ItemHorizontalGap);
		_itemGrid.AddThemeConstantOverride("v_separation", ItemVerticalGap);

		if (_itemGridMarginContainer is not null)
		{
			_itemGridMarginContainer.AddThemeConstantOverride("margin_left", GridMarginLeft);
			_itemGridMarginContainer.AddThemeConstantOverride("margin_top", GridMarginTop);
			_itemGridMarginContainer.AddThemeConstantOverride("margin_right", GridMarginRight);
			_itemGridMarginContainer.AddThemeConstantOverride("margin_bottom", GridMarginBottom);
		}

		int columns = GridColumns;
		if (columns <= 0)
		{
			columns = CalculateAutoColumns(measuredItemWidth);
		}

		_itemGrid.Columns = Math.Max(1, columns);
	}

	private int CalculateAutoColumns(float measuredItemWidth)
	{
		if (_itemGrid is null)
		{
			return 1;
		}

		float availableWidth = 0f;
		if (_itemGridMarginContainer is not null && _itemGridMarginContainer.Size.X > 0f)
		{
			availableWidth = _itemGridMarginContainer.Size.X;
		}
		else if (_layoutSizeHost is not null && _layoutSizeHost.Size.X > 0f)
		{
			availableWidth = _layoutSizeHost.Size.X;
		}
		else if (_itemGrid.Size.X > 0f)
		{
			availableWidth = _itemGrid.Size.X;
		}

		if (availableWidth <= 0f)
		{
			return Math.Max(1, _itemGrid.Columns);
		}

		float targetItemWidth = measuredItemWidth > 0f ? measuredItemWidth : EstimateItemWidthFromCurrentChildren();
		targetItemWidth = Math.Max(1f, targetItemWidth);

		int columns = (int)MathF.Floor((availableWidth + ItemHorizontalGap) / (targetItemWidth + ItemHorizontalGap));
		return Math.Max(1, columns);
	}

	private float EstimateItemWidthFromCurrentChildren()
	{
		if (_itemGrid is null)
		{
			return FallbackItemWidth;
		}

		float maxWidth = 0f;
		foreach (Control child in _itemGrid.GetChildren().OfType<Control>())
		{
			Vector2 size = ResolveItemLayoutSize(child);
			if (size.X > maxWidth)
			{
				maxWidth = size.X;
			}
		}

		return maxWidth > 0f ? maxWidth : FallbackItemWidth;
	}

	private void NormalizeItemForGrid(Control control)
	{
		Vector2 size = ResolveItemLayoutSize(control);
		Vector2 customMinimum = control.CustomMinimumSize;
		if (customMinimum.X <= 0f)
		{
			customMinimum.X = size.X;
		}

		if (customMinimum.Y <= 0f)
		{
			customMinimum.Y = size.Y;
		}

		control.CustomMinimumSize = customMinimum;
		control.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
		control.SizeFlagsVertical = SizeFlags.ShrinkBegin;
	}

	private Vector2 ResolveItemLayoutSize(Control control)
	{
		Vector2 size = control.GetCombinedMinimumSize();
		if (size.X <= 0f)
		{
			size.X = control.CustomMinimumSize.X;
		}

		if (size.Y <= 0f)
		{
			size.Y = control.CustomMinimumSize.Y;
		}

		if (size.X <= 0f)
		{
			size.X = control.Size.X;
		}

		if (size.Y <= 0f)
		{
			size.Y = control.Size.Y;
		}

		if (size.X <= 0f)
		{
			size.X = FallbackItemWidth;
		}

		if (size.Y <= 0f)
		{
			size.Y = FallbackItemHeight;
		}

		return size;
	}

	private void EnsureFilterGroupExists(string groupId)
	{
		if (_filterGroups.ContainsKey(groupId))
		{
			return;
		}

		_filterGroups[groupId] = new SelectFilterGroup(groupId);
		_filterGroupOrder.Add(groupId);
	}

	private void EnsureModelType<TModel>()
	{
		Type requested = typeof(TModel);
		if (_modelType is null)
		{
			_modelType = requested;
			return;
		}

		if (_modelType != requested)
		{
			throw new InvalidOperationException(
				$"Screen is configured for model type '{_modelType.Name}'. Call ResetConfiguration() before switching to '{requested.Name}'.");
		}
	}

	private static string DefaultSearchTextSelector(object? model) => model?.ToString() ?? string.Empty;

	private static bool DefaultSearchMatcher(string haystack, string query)
		=> haystack.Contains(query, StringComparison.OrdinalIgnoreCase);

	private void SearchBarQueryChanged(string text)
	{
		_query = text ?? string.Empty;
		DisplayCardsAfterShortDelay();
	}

	private void SearchBarQuerySubmitted(string text)
	{
		_query = text ?? string.Empty;
		RefreshVisibleItems();
	}

	private void ClearSearchQuery()
	{
		if (_searchLineEdit is not null)
		{
			_searchLineEdit.Text = string.Empty;
		}

		_query = string.Empty;
		RefreshVisibleItems();
	}

	private void DisplayCardsAfterShortDelay()
	{
		if (DelayAfterTextFilterChangedMsec <= 0)
		{
			RefreshVisibleItems();
			return;
		}

		_displayCardsShortDelayCancelToken?.Cancel();
		_displayCardsShortDelayCancelToken?.Dispose();
		_displayCardsShortDelayCancelToken = new CancellationTokenSource();
		_ = DisplayCardsAfterShortDelayAsync(_displayCardsShortDelayCancelToken.Token);
	}

	private async Task DisplayCardsAfterShortDelayAsync(CancellationToken cancellationToken)
	{
		try
		{
			await Task.Delay(DelayAfterTextFilterChangedMsec, cancellationToken);
		}
		catch (TaskCanceledException)
		{
			return;
		}

		if (cancellationToken.IsCancellationRequested)
		{
			return;
		}

		CallDeferred(MethodName.RefreshVisibleItems);
	}

	private void RebuildSortButtons()
	{
		if (_sortButtonsContainer is null)
		{
			return;
		}

		foreach (Node child in _sortButtonsContainer.GetChildren())
		{
			child.QueueFree();
		}

		_sortButtonsById.Clear();

		foreach (SorterDefinition sorter in _sorters)
		{
			Button button = new()
			{
				Text = sorter.Label,
				SizeFlagsHorizontal = SizeFlags.ExpandFill
			};

			string sorterId = sorter.Id;
			button.Pressed += () => OnSorterPressed(sorterId);
			_sortButtonsContainer.AddChild(button);
			_sortButtonsById[sorterId] = button;
		}

		UpdateSortButtonLabels();
	}

	private void OnSorterPressed(string sorterId)
	{
		if (!_sortersById.TryGetValue(sorterId, out SorterDefinition? sorter))
		{
			return;
		}

		bool wasActive = _sortPriority.Contains(sorterId);
		if (wasActive)
		{
			sorter.IsDescending = !sorter.IsDescending;
		}
		else
		{
			sorter.IsDescending = sorter.DescendingByDefault;
		}

		_sortPriority.Remove(sorterId);
		_sortPriority.Insert(0, sorterId);

		UpdateSortButtonLabels();
		RefreshVisibleItems();
	}

	private void UpdateSortButtonLabels()
	{
		foreach (SorterDefinition sorter in _sorters)
		{
			if (!_sortButtonsById.TryGetValue(sorter.Id, out Button? button))
			{
				continue;
			}

			int priority = _sortPriority.IndexOf(sorter.Id);
			if (priority < 0)
			{
				button.Text = sorter.Label;
				continue;
			}

			string direction = sorter.IsDescending ? "desc" : "asc";
			button.Text = $"{sorter.Label} ({direction} #{priority + 1})";
		}
	}

	private void RebuildFilterButtons()
	{
		if (_filtersContainer is null)
		{
			return;
		}

		foreach (Node child in _filtersContainer.GetChildren())
		{
			child.QueueFree();
		}

		_filterButtonsById.Clear();

		if (_filters.Count == 0)
		{
			return;
		}

		IEnumerable<string> groupOrder = _filterGroupOrder.Concat(_filters.Select(static f => f.GroupId))
			.Distinct(StringComparer.Ordinal);

		foreach (string groupId in groupOrder)
		{
			List<FilterDefinition> groupFilters = _filters
				.Where(f => string.Equals(f.GroupId, groupId, StringComparison.Ordinal))
				.ToList();

			if (groupFilters.Count == 0)
			{
				continue;
			}

			SelectFilterGroup group = _filterGroups.TryGetValue(groupId, out SelectFilterGroup? value)
				? value
				: new SelectFilterGroup(groupId);

			Label groupLabel = new()
			{
				Text = group.Label
			};
			_filtersContainer.AddChild(groupLabel);

			VBoxContainer groupBox = new()
			{
				SizeFlagsHorizontal = SizeFlags.ExpandFill
			};
			_filtersContainer.AddChild(groupBox);

			foreach (FilterDefinition filter in groupFilters)
			{
				CheckButton button = new()
				{
					Text = filter.Label,
					ButtonPressed = filter.Enabled,
					SizeFlagsHorizontal = SizeFlags.ExpandFill
				};

				string filterId = filter.Id;
				button.Toggled += enabled => OnFilterToggled(filterId, enabled);
				groupBox.AddChild(button);
				_filterButtonsById[filterId] = button;
			}
		}
	}

	private void OnFilterToggled(string filterId, bool enabled)
	{
		if (_synchronizingFilterButtons)
		{
			return;
		}

		if (!_filtersById.TryGetValue(filterId, out FilterDefinition? filter))
		{
			return;
		}

		filter.Enabled = enabled;

		if (!enabled)
		{
			if (GroupRequiresSelection(filter.GroupId) && !HasAnyEnabledFilter(filter.GroupId))
			{
				SetFilterState(filter.Id, true);
				return;
			}

			RefreshVisibleItems();
			return;
		}

		EnforceSingleSelection(filter.GroupId, filter.Id);
		RefreshVisibleItems();
	}

	private void EnforceSingleSelection(string groupId, string selectedFilterId)
	{
		if (!_filterGroups.TryGetValue(groupId, out SelectFilterGroup? group) ||
		    group.SelectionMode != SelectFilterGroupSelectionMode.Single)
		{
			return;
		}

		foreach (FilterDefinition other in _filters.Where(f => string.Equals(f.GroupId, groupId, StringComparison.Ordinal) &&
		                                                       !string.Equals(f.Id, selectedFilterId, StringComparison.Ordinal)))
		{
			if (!other.Enabled)
			{
				continue;
			}

			SetFilterState(other.Id, false);
		}
	}

	private bool GroupRequiresSelection(string groupId)
	{
		return _filterGroups.TryGetValue(groupId, out SelectFilterGroup? group) && group.RequireSelection;
	}

	private bool HasAnyEnabledFilter(string groupId)
	{
		return _filters.Any(filter => string.Equals(filter.GroupId, groupId, StringComparison.Ordinal) && filter.Enabled);
	}

	private void SetFilterState(string filterId, bool enabled)
	{
		if (!_filtersById.TryGetValue(filterId, out FilterDefinition? filter))
		{
			return;
		}

		filter.Enabled = enabled;

		if (_filterButtonsById.TryGetValue(filterId, out CheckButton? button))
		{
			_synchronizingFilterButtons = true;
			button.ButtonPressed = enabled;
			_synchronizingFilterButtons = false;
		}
	}

	private void RefreshVisibleItems()
	{
		if (_itemGrid is null)
		{
			return;
		}

		_displayCardsShortDelayCancelToken?.Cancel();
		_displayCardsShortDelayCancelToken?.Dispose();
		_displayCardsShortDelayCancelToken = null;

		List<ModelEntry> filtered = _entries.Where(PassesSearchAndFilters).ToList();
		filtered.Sort(CompareEntries);

		foreach (Node child in _itemGrid.GetChildren())
		{
			child.QueueFree();
		}

		float measuredItemWidth = 0f;
		foreach (ModelEntry entry in filtered)
		{
			Control control = _itemRenderer?.Invoke(entry.Model) ?? CreateDefaultItem(entry);
			if (control.GetParent() is not null)
			{
				control.GetParent().RemoveChild(control);
			}

			NormalizeItemForGrid(control);
			float itemWidth = ResolveItemLayoutSize(control).X;
			if (itemWidth > measuredItemWidth)
			{
				measuredItemWidth = itemWidth;
			}

			if (control is BaseButton button)
			{
				object? model = entry.Model;
				button.Pressed += () => ItemActivated?.Invoke(model);
			}

			_itemGrid.AddChild(control);
		}

		_lastMeasuredItemWidth = measuredItemWidth > 0f ? measuredItemWidth : FallbackItemWidth;
		ApplyLayoutSettings(_lastMeasuredItemWidth);
		CallDeferred(nameof(ApplyLayoutFromCurrentContent));
	}

	private bool PassesSearchAndFilters(ModelEntry entry)
	{
		if (!string.IsNullOrWhiteSpace(_query))
		{
			if (!_searchMatcher(entry.SearchText, _query))
			{
				return false;
			}
		}

		foreach (string groupId in _filters.Select(static x => x.GroupId).Distinct(StringComparer.Ordinal))
		{
			List<FilterDefinition> activeGroup = _filters
				.Where(filter => string.Equals(filter.GroupId, groupId, StringComparison.Ordinal) && filter.Enabled)
				.ToList();

			if (activeGroup.Count == 0)
			{
				if (GroupRequiresSelection(groupId))
				{
					return false;
				}

				continue;
			}

			bool matchesGroup = activeGroup.Any(filter => filter.Predicate(entry.Model));
			if (!matchesGroup)
			{
				return false;
			}
		}

		return true;
	}

	private int CompareEntries(ModelEntry left, ModelEntry right)
	{
		foreach (string sorterId in _sortPriority)
		{
			if (!_sortersById.TryGetValue(sorterId, out SorterDefinition? sorter))
			{
				continue;
			}

			int comparison = sorter.Compare(left.Model, right.Model);
			if (comparison != 0)
			{
				return comparison;
			}
		}

		return left.OriginalIndex.CompareTo(right.OriginalIndex);
	}

	private static Control CreateDefaultItem(ModelEntry entry)
	{
		return new Button
		{
			Text = entry.SearchText,
			SizeFlagsHorizontal = SizeFlags.ExpandFill
		};
	}

	private sealed class ModelEntry
	{
		public ModelEntry(object? model, string searchText, int originalIndex)
		{
			Model = model;
			SearchText = searchText ?? string.Empty;
			OriginalIndex = originalIndex;
		}

		public object? Model { get; }
		public string SearchText { get; }
		public int OriginalIndex { get; }
	}

	private sealed class FilterDefinition
	{
		public FilterDefinition(string id, string groupId, string label, bool enabled, Func<object?, bool> predicate)
		{
			Id = id;
			GroupId = groupId;
			Label = label;
			Enabled = enabled;
			Predicate = predicate;
		}

		public string Id { get; }
		public string GroupId { get; }
		public string Label { get; }
		public bool Enabled { get; set; }
		public Func<object?, bool> Predicate { get; }
	}

	private sealed class SorterDefinition
	{
		public SorterDefinition(
			string id,
			string label,
			bool descendingByDefault,
			Comparison<object?> ascendingComparison,
			Comparison<object?>? descendingComparison)
		{
			Id = id;
			Label = label;
			DescendingByDefault = descendingByDefault;
			AscendingComparison = ascendingComparison;
			DescendingComparison = descendingComparison;
			IsDescending = descendingByDefault;
		}

		public string Id { get; }
		public string Label { get; }
		public bool DescendingByDefault { get; }
		public bool IsDescending { get; set; }
		public Comparison<object?> AscendingComparison { get; }
		public Comparison<object?>? DescendingComparison { get; }

		public int Compare(object? left, object? right)
		{
			if (!IsDescending)
			{
				return AscendingComparison(left, right);
			}

			if (DescendingComparison is not null)
			{
				return DescendingComparison(left, right);
			}

			return -AscendingComparison(left, right);
		}
	}
}
