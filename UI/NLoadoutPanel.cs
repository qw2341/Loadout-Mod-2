#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Loadout.UI.Screens;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Entities.UI;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.CardPools;
using MegaCrit.Sts2.Core.Models.PotionPools;
using MegaCrit.Sts2.Core.Models.RelicPools;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.PotionLab;
using MegaCrit.Sts2.Core.Nodes.Screens.RelicCollection;
using MegaCrit.Sts2.Core.Runs;
using System.Text.RegularExpressions;

namespace  Loadout.UI;

public partial class NLoadoutPanel : Panel
{
	private const int MaxLoadoutItemInitAttempts = 120;

	[Export]
	public bool Shown = true;

	[Export]
	public float SlideSpeed = 12f;
	
	private PanelContainer _panelContainer = null!;
	private MarginContainer _marginContainer = null!;
	private Control _itemsContainer = null!;
	private int _loadoutItemInitAttempts;
	private bool _loadoutItemsAdded;
	private bool _loadoutItemRetryScheduled;

	// Called when the node enters the scene tree for the first time.
	public override void _Ready()
	{
		_panelContainer = GetNode<PanelContainer>("PanelContainer");
		_marginContainer = GetNode<MarginContainer>("PanelContainer/MarginContainer");
		_itemsContainer = GetNode<Control>("PanelContainer/MarginContainer/VBoxContainer");
		
		
		
		TryAddLoadoutItems();

		// Recompute whenever the VBox minimum size changes
		// _marginContainer.MinimumSizeChanged += UpdatePanelHeight;
		// _itemsContainer.MinimumSizeChanged += UpdatePanelHeight;
	}

	public void ToggleShown()
	{
		Shown = !Shown;
	}

	public override void _Process(double delta)
	{
		UpdatePosition(delta);
	}
	
	private void UpdatePosition(double delta)
	{
		Vector2 target = Position;
		target.Y = (GetParent<Control>().Size.Y - Size.Y) / 2f;
		target.X = Shown ? 0 : -Size.X;
		
		float weight = Mathf.Clamp((float)(SlideSpeed * delta), 0f, 1f);
		Position = Position.Lerp(target, weight);
	}

	private void AddLoadoutItems()
	{
		CardPoolModel? currentCardPool = GetCurrentCharacterCardPool();
		RelicGroupingData relicGroupingData = BuildRelicGroupingData();

		CreateAndAddLoadoutItem(
			ModelDb.AllCards,
			new SelectItemAdapter<CardModel>
			{
				GetId = card => card.Id.ToString(),
				GetName = card => FormatCardTitle(card),
				GetSearchText = card => $"{card.Id} {FormatCardTitle(card)} {card.TitleLocString} {card.Description}",
				CreateView = (card, _) => CreateCardGridItem(card),
				ViewReady = (_, view) => RefreshCardVisuals(view),
				BindActivation = (_, view, activate) => BindCardActivation(view, activate)
			}, builder =>
			{
				builder.Layout(5, NCard.defaultSize * NCardHolder.smallScale, 32, 40, paddingLeft: 0f, paddingRight: 0f);
				builder.FilterGroup("class", L("FILTER_GROUP_CLASS", "Class"));
				AddCardPoolFilters(builder, currentCardPool);
				builder.FilterGroup("type", L("FILTER_GROUP_TYPE", "Type"));
				builder.Filter("attack", L("CARD_TYPE_ATTACK", "Attack"), card => card.Type == CardType.Attack, "type");
				builder.Filter("skill", L("CARD_TYPE_SKILL", "Skill"), card => card.Type == CardType.Skill, "type");
				builder.Filter("power", L("CARD_TYPE_POWER", "Power"), card => card.Type == CardType.Power, "type");
				builder.FilterGroup("rarity", L("FILTER_GROUP_RARITY", "Rarity"));
				builder.Filter("basic", L("RARITY_BASIC", "Basic"), card => card.Rarity == CardRarity.Basic, "rarity");
				builder.Filter("common", L("RARITY_COMMON", "Common"), card => card.Rarity == CardRarity.Common, "rarity");
				builder.Filter("uncommon", L("RARITY_UNCOMMON", "Uncommon"), card => card.Rarity == CardRarity.Uncommon, "rarity");
				builder.Filter("rare", L("RARITY_RARE", "Rare"), card => card.Rarity == CardRarity.Rare, "rarity");
				builder.Sorter("name", L("SORT_NAME", "Name"), (a, b) => string.Compare(FormatCardTitle(a), FormatCardTitle(b), StringComparison.Ordinal), activeByDefault: true);
				builder.Sorter("id", L("SORT_ID", "ID"), (a, b) => string.Compare(a.Id.Entry, b.Id.Entry, StringComparison.Ordinal));
				builder.Sorter("cost", L("SORT_COST", "Cost"), (a, b) => a.EnergyCost.Canonical.CompareTo(b.EnergyCost.Canonical));
			});

		CreateAndAddLoadoutItem(
			ModelDb.AllPotions,
			new SelectItemAdapter<PotionModel>
			{
				GetId = potion => potion.Id.ToString(),
				GetName = potion => FormatPotionTitle(potion),
				GetSearchText = potion => $"{potion.Id} {FormatPotionTitle(potion)} {potion.DynamicDescription}",
				CreateView = (potion, _) => CreatePotionGridItem(potion),
				BindActivation = (_, view, activate) => BindGuiReleaseActivation(view, activate)
			}, builder =>
			{
				builder.Layout(10, new Vector2(60f, 60f), 32, 32);
				builder.FilterGroup("class", L("FILTER_GROUP_CLASS", "Class"));
				AddPotionPoolFilters(builder);
				builder.FilterGroup("rarity", L("FILTER_GROUP_RARITY", "Rarity"));
				AddEnumFilters(builder, "rarity", (PotionModel potion) => potion.Rarity, PotionRarity.None);
				builder.Sorter("name", L("SORT_NAME", "Name"), (a, b) => string.Compare(FormatPotionTitle(a), FormatPotionTitle(b), StringComparison.Ordinal));
				builder.Sorter("id", L("SORT_ID", "ID"), (a, b) => string.Compare(a.Id.Entry, b.Id.Entry, StringComparison.Ordinal));
				builder.Sorter("rarity", L("SORT_RARITY", "Rarity"), ComparePotionRarity, activeByDefault: true);
				builder.GroupBySorter(
					"rarity",
					GetPotionGroupKey,
					GetPotionGroupHeader,
					PotionGroupOrder);
			});

		CreateAndAddLoadoutItem(
			ModelDb.AllRelics,
			new SelectItemAdapter<RelicModel>
			{
				GetId = relic => relic.Id.ToString(),
				GetName = relic => FormatRelicTitle(relic),
				GetSearchText = relic => $"{relic.Id} {FormatRelicTitle(relic)} {relic.DynamicDescription}",
				CreateView = (relic, _) => CreateRelicGridItem(relic),
				BindActivation = (_, view, activate) => BindRelicActivation(view, activate)
			}, builder =>
			{
				builder.Layout(10, new Vector2(68f, 68f), 32, 32);
				builder.FilterGroup("class", L("FILTER_GROUP_CLASS", "Class"));
				AddRelicPoolFilters(builder);
				builder.FilterGroup("rarity", L("FILTER_GROUP_RARITY", "Rarity"));
				AddEnumFilters(builder, "rarity", (RelicModel relic) => relic.Rarity, RelicRarity.None);
				builder.Sorter("name", L("SORT_NAME", "Name"), (a, b) => string.Compare(FormatRelicTitle(a), FormatRelicTitle(b), StringComparison.Ordinal));
				builder.Sorter("id", L("SORT_ID", "ID"), (a, b) => string.Compare(a.Id.Entry, b.Id.Entry, StringComparison.Ordinal));
				builder.Sorter("rarity", L("SORT_RARITY", "Rarity"), CompareRelicRarity, activeByDefault: true);
				builder.GroupBySorter(
					"rarity",
					relic => GetRelicGroupKey(relic, relicGroupingData),
					key => GetRelicGroupHeader(key, relicGroupingData),
					relicGroupingData.GroupOrder);
			});

		CreateAndAddLoadoutItem(
			ModelDb.AllEvents,
			new SelectItemAdapter<EventModel>
			{
				GetId = eventModel => eventModel.Id.ToString(),
				GetName = eventModel => FormatEventTitle(eventModel),
				GetSearchText = eventModel => $"{eventModel.Id} {FormatEventTitle(eventModel)} {eventModel.InitialDescription}",
				CreateView = (eventModel, _) => CreateTextModelGridItem(eventModel, FormatEventTitle(eventModel), eventModel.Id.Entry, L("CATEGORY_EVENT", "Event"))
			}, builder =>
			{
				builder.Layout(4, new Vector2(220f, 120f), 24, 24);
				builder.FilterGroup("layout", L("FILTER_GROUP_LAYOUT", "Layout"));
				builder.Filter("default", L("LAYOUT_DEFAULT", "Default"), eventModel => eventModel.LayoutType == EventLayoutType.Default, "layout");
				builder.Filter("combat", L("LAYOUT_COMBAT", "Combat"), eventModel => eventModel.LayoutType == EventLayoutType.Combat, "layout");
				builder.Filter("ancient", L("LAYOUT_ANCIENT", "Ancient"), eventModel => eventModel.LayoutType == EventLayoutType.Ancient, "layout");
				builder.FilterGroup("sharing", L("FILTER_GROUP_SCOPE", "Scope"));
				builder.Filter("shared", L("SCOPE_SHARED", "Shared"), eventModel => eventModel.IsShared, "sharing");
				builder.Filter("solo", L("SCOPE_SOLO", "Solo"), eventModel => !eventModel.IsShared, "sharing");
				builder.Sorter("name", L("SORT_NAME", "Name"), (a, b) => string.Compare(FormatEventTitle(a), FormatEventTitle(b), StringComparison.Ordinal), activeByDefault: true);
				builder.Sorter("id", L("SORT_ID", "ID"), (a, b) => string.Compare(a.Id.Entry, b.Id.Entry, StringComparison.Ordinal));
			});

		CreateAndAddLoadoutItem(
			ModelDb.AllPowers,
			new SelectItemAdapter<PowerModel>
			{
				GetId = power => power.Id.ToString(),
				GetName = power => FormatPowerTitle(power),
				GetSearchText = power => $"{power.Id} {FormatPowerTitle(power)} {power.Description}",
				CreateView = (power, _) => CreatePowerGridItem(power)
			}, builder =>
			{
				builder.Layout(5, new Vector2(220f, 104f), 24, 24);
				builder.FilterGroup("type", L("FILTER_GROUP_TYPE", "Type"));
				builder.Filter("buff", L("POWER_TYPE_BUFF", "Buff"), power => power.Type == PowerType.Buff, "type");
				builder.Filter("debuff", L("POWER_TYPE_DEBUFF", "Debuff"), power => power.Type == PowerType.Debuff, "type");
				builder.Filter("type_none", L("NONE", "None"), power => power.Type == PowerType.None, "type");
				builder.FilterGroup("stack", L("FILTER_GROUP_STACK", "Stack"));
				builder.Filter("stack_none", L("NONE", "None"), power => power.StackType == PowerStackType.None, "stack");
				builder.Filter("counter", L("POWER_STACK_COUNTER", "Counter"), power => power.StackType == PowerStackType.Counter, "stack");
				builder.Filter("single", L("POWER_STACK_SINGLE", "Single"), power => power.StackType == PowerStackType.Single, "stack");
				builder.Sorter("name", L("SORT_NAME", "Name"), (a, b) => string.Compare(FormatPowerTitle(a), FormatPowerTitle(b), StringComparison.Ordinal), activeByDefault: true);
				builder.Sorter("id", L("SORT_ID", "ID"), (a, b) => string.Compare(a.Id.Entry, b.Id.Entry, StringComparison.Ordinal));
				builder.Sorter("type", L("SORT_TYPE", "Type"), (a, b) => a.Type.CompareTo(b.Type));
			});
	}

	private void TryAddLoadoutItems()
	{
		if (_loadoutItemsAdded)
			return;

		try
		{
			AddLoadoutItems();
			_loadoutItemsAdded = true;
			UpdatePanelHeight();
		}
		catch (KeyNotFoundException exception)
		{
			ScheduleLoadoutItemRetry(exception);
		}
	}

	private async void ScheduleLoadoutItemRetry(KeyNotFoundException exception)
	{
		if (_loadoutItemRetryScheduled)
			return;

		if (_loadoutItemInitAttempts >= MaxLoadoutItemInitAttempts)
		{
			GD.PushError($"LoadoutPanel: failed to initialize loadout items after {_loadoutItemInitAttempts} frames. Last missing key: {exception.Message}");
			return;
		}

		_loadoutItemInitAttempts++;
		_loadoutItemRetryScheduled = true;

		if (_loadoutItemInitAttempts == 1)
			GD.PushWarning($"LoadoutPanel: ModelDb is not ready yet; retrying loadout item initialization. Missing key: {exception.Message}");

		await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

		_loadoutItemRetryScheduled = false;
		if (IsInsideTree())
			TryAddLoadoutItems();
	}

	private void CreateAndAddLoadoutItem<TModel>(IEnumerable<TModel> models, SelectItemAdapter<TModel> adapter,  Action<SelectScreenBuilder<TModel>> builder)
	{
		var item = new NLoadoutPanelItem();
		var scene = GD.Load<PackedScene>("res://UI/Screens/GenericSelectScreen.tscn");
		var screen = scene.Instantiate<NGenericSelectScreen>();
		screen.Configure(models, adapter, builder);
		screen.Cancelled += CloseTopLoadoutScreen;
		screen.Confirmed += _ => CloseTopLoadoutScreen();
		
		item.BoundScreen = screen;
		_itemsContainer.AddChild(item);
	}

	private static void CloseTopLoadoutScreen()
	{
		NLoadoutPanelRoot.Instance?.CloseTopScreen();
	}

	private static Control CreateCardGridItem(CardModel model)
	{
		var card = NCard.Create(model);
		if (card is null)
		{
			return new Control
			{
				CustomMinimumSize = NCard.defaultSize
			};
		}

		var holder = NGridCardHolder.Create(card);
		if (holder is null)
		{
			card.CustomMinimumSize = card.GetCurrentSize();
			return card;
		}

		holder.MouseFilter = MouseFilterEnum.Pass;
		holder.Scale = holder.SmallScale;
		holder.CustomMinimumSize = NCard.defaultSize * holder.SmallScale;
		return holder;
	}

	private static Control CreatePotionGridItem(PotionModel model)
	{
		NLabPotionHolder? holder = NLabPotionHolder.Create(model.ToMutable(), ModelVisibility.Visible);
		if (holder is null)
			return CreateTextModelGridItem(model, FormatPotionTitle(model), model.Id.Entry, L("CATEGORY_POTION", "Potion"));

		holder.MouseFilter = MouseFilterEnum.Pass;
		holder.CustomMinimumSize = new Vector2(60f, 60f);
		return holder;
	}

	private static Control CreateRelicGridItem(RelicModel model)
	{
		NRelicCollectionEntry? holder = NRelicCollectionEntry.Create(model, ModelVisibility.Visible);
		if (holder is null)
			return CreateTextModelGridItem(model, FormatRelicTitle(model), model.Id.Entry, L("CATEGORY_RELIC", "Relic"));

		holder.MouseFilter = MouseFilterEnum.Pass;
		holder.CustomMinimumSize = new Vector2(68f, 68f);
		return holder;
	}

	private static Control CreatePowerGridItem(PowerModel model)
	{
		Texture2D? icon = null;
		if (ResourceLoader.Exists(model.IconPath))
			icon = model.Icon;

		return CreateTextModelGridItem(model, FormatPowerTitle(model), model.Id.Entry, FormatPowerCategory(model.Type), icon, new Vector2(220f, 104f));
	}

	private static Button CreateTextModelGridItem(
		AbstractModel model,
		string title,
		string subtitle,
		string category,
		Texture2D? icon = null,
		Vector2? itemSize = null)
	{
		Vector2 size = itemSize ?? new Vector2(220f, icon is null ? 120f : 148f);
		Button button = new()
		{
			CustomMinimumSize = size,
			MouseFilter = MouseFilterEnum.Stop,
			FocusMode = FocusModeEnum.All,
			SizeFlagsHorizontal = SizeFlags.ShrinkBegin,
			SizeFlagsVertical = SizeFlags.ShrinkBegin
		};
		button.AddThemeFontOverride("font", LoadGameFont());
		button.AddThemeFontSizeOverride("font_size", 18);
		button.AddThemeColorOverride("font_color", StsColors.cream);
		button.Text = icon is null
			? $"{title}\n{category}\n{subtitle}"
			: $"{title}\n{category}";
		button.TooltipText = $"{title}\n{model.Id}";

		if (icon is not null)
		{
			TextureRect iconRect = new()
			{
				Texture = icon,
				CustomMinimumSize = new Vector2(64f, 64f),
				StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
				MouseFilter = MouseFilterEnum.Ignore,
				Position = new Vector2(Mathf.Max(0f, (size.X - 64f) * 0.5f), Mathf.Max(0f, size.Y - 70f)),
				Size = new Vector2(64f, 64f)
			};
			button.AddChild(iconRect);
		}

		return button;
	}

	private static void RefreshCardVisuals(Control view)
	{
		if (!TryFindDescendantOrSelf(view, out NGridCardHolder? holder) || holder!.CardNode is null)
			return;

		holder.CardNode.UpdateVisuals(PileType.None, CardPreviewMode.Normal);
	}

	private static bool BindCardActivation(Control view, Action activate)
	{
		if (!TryFindDescendantOrSelf(view, out NGridCardHolder? holder))
			return false;

		holder!.Connect(NCardHolder.SignalName.Pressed, Callable.From<NCardHolder>(_ => activate()));
		return true;
	}

	private static bool BindRelicActivation(Control view, Action activate)
	{
		if (!TryFindDescendantOrSelf(view, out NRelicCollectionEntry? clickable))
			return false;

		clickable!.Connect(NClickableControl.SignalName.Released, Callable.From<NRelicCollectionEntry>(_ => activate()));
		return true;
	}

	private static bool BindGuiReleaseActivation(Control view, Action activate)
	{
		Control control = TryFindDescendantOrSelf(view, out NLabPotionHolder? potionHolder)
			? potionHolder!
			: view;

		control.GuiInput += input =>
		{
			if (input is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false })
				activate();
		};

		return true;
	}

	private static bool TryFindDescendantOrSelf<TControl>(Node root, out TControl? control)
		where TControl : class
	{
		if (root is TControl direct)
		{
			control = direct;
			return true;
		}

		foreach (Node child in root.GetChildren())
		{
			if (TryFindDescendantOrSelf(child, out control))
				return true;
		}

		control = null;
		return false;
	}

	private static void AddCardPoolFilters(SelectScreenBuilder<CardModel> builder, CardPoolModel? currentCardPool)
	{
		IReadOnlyList<CardPoolModel> pools = BuildOrderedPools(
			ModelDb.AllCards.Select(card => card.Pool),
			ModelDb.AllCharacters.Where(character => character.IsPlayable).Select(character => character.CardPool),
			pool => pool.IsColorless && !IsInternalPool(pool));

		foreach (CardPoolModel pool in pools)
		{
			CardPoolModel localPool = pool;
			builder.Filter(
				PoolFilterId("card", localPool),
				GetPoolLabel(localPool),
				card => SamePool(card.Pool, localPool),
				"class",
				currentCardPool is not null && SamePool(currentCardPool, localPool));
		}
	}

	private static void AddPotionPoolFilters(SelectScreenBuilder<PotionModel> builder)
	{
		IReadOnlyList<PotionPoolModel> pools = BuildOrderedPools(
			ModelDb.AllPotions.Select(potion => potion.Pool),
			ModelDb.AllCharacters.Where(character => character.IsPlayable).Select(character => character.PotionPool),
			pool => IsSharedPool(pool) && !IsInternalPool(pool));

		foreach (PotionPoolModel pool in pools)
		{
			PotionPoolModel localPool = pool;
			builder.Filter(
				PoolFilterId("potion", localPool),
				GetPoolLabel(localPool),
				potion => SamePool(potion.Pool, localPool),
				"class");
		}
	}

	private static void AddRelicPoolFilters(SelectScreenBuilder<RelicModel> builder)
	{
		IReadOnlyList<RelicPoolModel> pools = BuildOrderedPools(
			ModelDb.AllRelics.Select(relic => relic.Pool),
			ModelDb.AllCharacters.Where(character => character.IsPlayable).Select(character => character.RelicPool),
			pool => IsSharedPool(pool) && !IsInternalPool(pool));

		foreach (RelicPoolModel pool in pools)
		{
			RelicPoolModel localPool = pool;
			builder.Filter(
				PoolFilterId("relic", localPool),
				GetPoolLabel(localPool),
				relic => SamePool(relic.Pool, localPool),
				"class");
		}
	}

	private static IReadOnlyList<TPool> BuildOrderedPools<TPool>(
		IEnumerable<TPool> usedPools,
		IEnumerable<TPool> characterPools,
		Func<TPool, bool> includeSharedPool)
		where TPool : AbstractModel
	{
		Dictionary<string, TPool> usedByKey = usedPools
			.GroupBy(PoolKey, StringComparer.Ordinal)
			.ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

		List<TPool> ordered = new();
		HashSet<string> added = new(StringComparer.Ordinal);

		foreach (TPool pool in characterPools)
		{
			string key = PoolKey(pool);
			if (usedByKey.ContainsKey(key) && added.Add(key))
				ordered.Add(pool);
		}

		foreach (TPool pool in usedByKey.Values.OrderBy(GetPoolLabel, StringComparer.Ordinal))
		{
			string key = PoolKey(pool);
			if (!added.Contains(key) && includeSharedPool(pool) && added.Add(key))
				ordered.Add(pool);
		}

		return ordered;
	}

	private static string GetPoolLabel(AbstractModel pool)
	{
		CharacterModel? character = ModelDb.AllCharacters
			.Where(candidate => candidate.IsPlayable)
			.FirstOrDefault(candidate =>
				SamePool(candidate.CardPool, pool)
				|| SamePool(candidate.PotionPool, pool)
				|| SamePool(candidate.RelicPool, pool));

		if (character is not null)
			return character.Title.GetFormattedText();

		if (pool is CardPoolModel cardPool && !string.IsNullOrWhiteSpace(cardPool.Title))
			return cardPool.Title;

		string typeName = pool.GetType().Name;
		if (typeName.StartsWith("Shared", StringComparison.Ordinal))
			return L("POOL_SHARED", "Shared");

		if (typeName.StartsWith("Colorless", StringComparison.Ordinal))
			return L("POOL_COLORLESS", "Colorless");

		if (typeName.StartsWith("Event", StringComparison.Ordinal))
			return L("POOL_EVENT", "Event");

		return PrettifyPoolTypeName(typeName);
	}

	private static string PoolFilterId(string prefix, AbstractModel pool)
	{
		return $"{prefix}_pool_{Regex.Replace(PoolKey(pool).ToLowerInvariant(), "[^a-z0-9_]+", "_")}";
	}

	private static string PoolKey(AbstractModel pool)
	{
		return $"{pool.GetType().FullName}:{pool.Id}";
	}

	private static bool SamePool(AbstractModel left, AbstractModel right)
	{
		return string.Equals(PoolKey(left), PoolKey(right), StringComparison.Ordinal);
	}

	private static bool IsSharedPool(AbstractModel pool)
	{
		string typeName = pool.GetType().Name;
		return typeName.StartsWith("Shared", StringComparison.Ordinal)
			|| typeName.StartsWith("Event", StringComparison.Ordinal)
			|| typeName.StartsWith("Colorless", StringComparison.Ordinal);
	}

	private static bool IsInternalPool(AbstractModel pool)
	{
		string typeName = pool.GetType().Name;
		return typeName.StartsWith("Deprecated", StringComparison.Ordinal)
			|| typeName.StartsWith("Mock", StringComparison.Ordinal)
			|| typeName.StartsWith("Token", StringComparison.Ordinal)
			|| typeName.StartsWith("Status", StringComparison.Ordinal)
			|| typeName.StartsWith("Fallback", StringComparison.Ordinal);
	}

	private static string PrettifyPoolTypeName(string typeName)
	{
		string name = typeName
			.Replace("CardPool", string.Empty, StringComparison.Ordinal)
			.Replace("PotionPool", string.Empty, StringComparison.Ordinal)
			.Replace("RelicPool", string.Empty, StringComparison.Ordinal);

		return Regex.Replace(name, "([a-z])([A-Z])", "$1 $2");
	}

	private static CardPoolModel? GetCurrentCharacterCardPool()
	{
		try
		{
			return LocalContext.GetMe(RunManager.Instance.DebugOnlyGetState())?.Character.CardPool;
		}
		catch (Exception exception)
		{
			GD.PushWarning($"LoadoutPanel: could not detect current character card pool; showing all cards by default. {exception.Message}");
			return null;
		}
	}

	private static int ComparePotionRarity(PotionModel left, PotionModel right)
	{
		int rarity = GetPotionRaritySortValue(left.Rarity).CompareTo(GetPotionRaritySortValue(right.Rarity));
		return rarity != 0 ? rarity : string.Compare(FormatPotionTitle(left), FormatPotionTitle(right), StringComparison.Ordinal);
	}

	private static int GetPotionRaritySortValue(PotionRarity rarity)
	{
		return rarity switch
		{
			PotionRarity.Common => 0,
			PotionRarity.Uncommon => 1,
			PotionRarity.Rare => 2,
			PotionRarity.Event => 3,
			PotionRarity.Token => 3,
			_ => 99
		};
	}

	private static int CompareRelicRarity(RelicModel left, RelicModel right)
	{
		int rarity = GetRelicRaritySortValue(left.Rarity).CompareTo(GetRelicRaritySortValue(right.Rarity));
		return rarity != 0 ? rarity : string.Compare(FormatRelicTitle(left), FormatRelicTitle(right), StringComparison.Ordinal);
	}

	private static int GetRelicRaritySortValue(RelicRarity rarity)
	{
		return rarity switch
		{
			RelicRarity.Starter => 0,
			RelicRarity.Common => 1,
			RelicRarity.Uncommon => 2,
			RelicRarity.Rare => 3,
			RelicRarity.Shop => 4,
			RelicRarity.Ancient => 5,
			RelicRarity.Event => 6,
			_ => 99
		};
	}

	private static readonly string[] PotionGroupOrder =
	{
		"potion:common",
		"potion:uncommon",
		"potion:rare",
		"potion:special"
	};

	private static string GetPotionGroupKey(PotionModel potion)
	{
		return potion.Rarity switch
		{
			PotionRarity.Common => "potion:common",
			PotionRarity.Uncommon => "potion:uncommon",
			PotionRarity.Rare => "potion:rare",
			PotionRarity.Event or PotionRarity.Token => "potion:special",
			_ => "potion:unknown"
		};
	}

	private static SelectGroupHeader GetPotionGroupHeader(string key)
	{
		return key switch
		{
			"potion:common" => new SelectGroupHeader(new LocString("potion_lab", "COMMON").GetFormattedText()),
			"potion:uncommon" => new SelectGroupHeader(new LocString("potion_lab", "UNCOMMON").GetFormattedText()),
			"potion:rare" => new SelectGroupHeader(new LocString("potion_lab", "RARE").GetFormattedText()),
			"potion:special" => new SelectGroupHeader(new LocString("potion_lab", "SPECIAL").GetFormattedText()),
			_ => new SelectGroupHeader(L("OTHER", "Other"))
		};
	}

	private static RelicGroupingData BuildRelicGroupingData()
	{
		Dictionary<string, string> keyByRelicId = new(StringComparer.Ordinal);
		Dictionary<string, RelicModel> ancientRelicsById = ModelDb.AllRelics
			.Where(relic => relic.Rarity == RelicRarity.Ancient)
			.GroupBy(relic => relic.Id.ToString(), StringComparer.Ordinal)
			.ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
		Dictionary<string, SelectGroupHeader> headers = new(StringComparer.Ordinal)
		{
			["relic:starter"] = new(new LocString("relic_collection", "STARTER").GetFormattedText()),
			["relic:common"] = new(new LocString("relic_collection", "COMMON").GetFormattedText()),
			["relic:uncommon"] = new(new LocString("relic_collection", "UNCOMMON").GetFormattedText()),
			["relic:rare"] = new(new LocString("relic_collection", "RARE").GetFormattedText()),
			["relic:shop"] = new(new LocString("relic_collection", "SHOP").GetFormattedText()),
			["relic:ancient"] = new(new LocString("relic_collection", "ANCIENT").GetFormattedText(), showWhenEmpty: true),
			["relic:event"] = new(new LocString("relic_collection", "EVENT").GetFormattedText())
		};
		List<string> groupOrder = new()
		{
			"relic:starter",
			"relic:common",
			"relic:uncommon",
			"relic:rare",
			"relic:shop",
			"relic:ancient"
		};

		foreach (AncientEventModel ancient in ModelDb.AllAncients.Distinct())
		{
			List<RelicModel> ancientRelics = ancient.AllPossibleOptions
				.Select(option => option.Relic?.CanonicalInstance)
				.OfType<RelicModel>()
				.Where(relic => ancientRelicsById.ContainsKey(relic.Id.ToString()))
				.DistinctBy(relic => relic.Id.ToString())
				.ToList();

			if (ancientRelics.Count == 0)
				continue;

			string groupKey = $"relic:ancient:{ancient.Id.Entry}";
			LocString headerText = new("relic_collection", "ANCIENT_SUBCATEGORY");
			headerText.Add("Ancient", ancient.Title);
			headers[groupKey] = new SelectGroupHeader(headerText.GetFormattedText(), TryGetValidTexture(ancient.RunHistoryIcon));
			groupOrder.Add(groupKey);

			foreach (RelicModel relic in ancientRelics)
				keyByRelicId[relic.Id.ToString()] = groupKey;
		}

		groupOrder.Add("relic:event");
		return new RelicGroupingData(keyByRelicId, headers, groupOrder);
	}

	private static string GetRelicGroupKey(RelicModel relic, RelicGroupingData groupingData)
	{
		if (relic.Rarity == RelicRarity.Ancient && groupingData.AncientGroupKeyByRelicId.TryGetValue(relic.Id.ToString(), out string? ancientKey))
			return ancientKey;

		return relic.Rarity switch
		{
			RelicRarity.Starter => "relic:starter",
			RelicRarity.Common => "relic:common",
			RelicRarity.Uncommon => "relic:uncommon",
			RelicRarity.Rare => "relic:rare",
			RelicRarity.Shop => "relic:shop",
			RelicRarity.Ancient => "relic:ancient",
			RelicRarity.Event => "relic:event",
			_ => "relic:unknown"
		};
	}

	private static SelectGroupHeader GetRelicGroupHeader(string key, RelicGroupingData groupingData)
	{
		return groupingData.HeadersByKey.TryGetValue(key, out SelectGroupHeader? header)
			? header
			: new SelectGroupHeader(L("OTHER", "Other"));
	}

	private static Texture2D? TryGetValidTexture(Texture2D? texture)
	{
		if (texture is null)
			return null;

		try
		{
			if (!GodotObject.IsInstanceValid(texture))
				return null;

			_ = texture.GetRid();
			return texture;
		}
		catch (ObjectDisposedException)
		{
			return null;
		}
	}

	private static void AddEnumFilters<TModel, TEnum>(
		SelectScreenBuilder<TModel> builder,
		string groupId,
		Func<TModel, TEnum> getValue,
		TEnum excludedValue)
		where TEnum : struct, Enum
	{
		foreach (TEnum value in Enum.GetValues<TEnum>())
		{
			if (EqualityComparer<TEnum>.Default.Equals(value, excludedValue))
				continue;

			string label = value.ToString();
			builder.Filter(label.ToLowerInvariant(), L($"ENUM_{typeof(TEnum).Name.ToUpperInvariant()}_{label.ToUpperInvariant()}", label), model => EqualityComparer<TEnum>.Default.Equals(getValue(model), value), groupId);
		}
	}

	private static string L(string key, string fallback)
	{
		return SelectScreenLoc.Text(key, fallback);
	}

	private static Font LoadGameFont()
	{
		const string localPath = "res://Loadout/themes/default/kreon_bold_glyph_space_one.tres";
		if (ResourceLoader.Exists(localPath))
			return GD.Load<Font>(localPath);

		return GD.Load<Font>("res://themes/kreon_bold_glyph_space_one.tres");
	}

	private static string FormatCardTitle(CardModel card)
	{
		return card.Title;
	}

	private static string FormatPotionTitle(PotionModel potion)
	{
		return potion.Title.GetFormattedText();
	}

	private static string FormatRelicTitle(RelicModel relic)
	{
		return relic.Title.GetFormattedText();
	}

	private static string FormatEventTitle(EventModel eventModel)
	{
		return eventModel.Title.GetFormattedText();
	}

	private static string FormatPowerTitle(PowerModel power)
	{
		return power.Title.GetFormattedText();
	}

	private static string FormatPowerCategory(PowerType type)
	{
		return type switch
		{
			PowerType.Buff => L("POWER_TYPE_BUFF", "Buff"),
			PowerType.Debuff => L("POWER_TYPE_DEBUFF", "Debuff"),
			_ => L("NONE", "None")
		};
	}

	private sealed class RelicGroupingData
	{
		public RelicGroupingData(
			IReadOnlyDictionary<string, string> ancientGroupKeyByRelicId,
			IReadOnlyDictionary<string, SelectGroupHeader> headersByKey,
			IReadOnlyList<string> groupOrder)
		{
			AncientGroupKeyByRelicId = ancientGroupKeyByRelicId;
			HeadersByKey = headersByKey;
			GroupOrder = groupOrder;
		}

		public IReadOnlyDictionary<string, string> AncientGroupKeyByRelicId { get; }
		public IReadOnlyDictionary<string, SelectGroupHeader> HeadersByKey { get; }
		public IReadOnlyList<string> GroupOrder { get; }
	}
	
	private void UpdatePanelHeight()
	{
		// Includes children/layout of the VBox.
		Vector2 contentMin = _panelContainer.GetCombinedMinimumSize();

		// Keep current width, only change height.
		Vector2 size = Size;
		size.Y = contentMin.Y;
		Size = size;
		//recenter it
		Vector2 pos = Position;
		pos.Y = (GetParent<Control>().Size.Y - Size.Y) / 2f;
		Position = pos;
	}
}
