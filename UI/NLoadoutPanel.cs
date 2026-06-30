#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Godot;
using Loadout.UI.Screens;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Entities.UI;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.CardPools;
using MegaCrit.Sts2.Core.Models.PotionPools;
using MegaCrit.Sts2.Core.Models.RelicPools;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Nodes.Relics;
using MegaCrit.Sts2.Core.Nodes.Screens.PotionLab;
using MegaCrit.Sts2.Core.Nodes.Screens.RelicCollection;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using System.Text.RegularExpressions;
using HarmonyLib;

namespace  Loadout.UI;

public partial class NLoadoutPanel : Panel
{
	private const int MaxLoadoutItemInitAttempts = 120;
	private const string ViewUpgradesToggleId = "view_upgrades";
	private const string PreviewUpgradeMetaKey = "loadout_preview_upgrade";
	private static readonly Vector2 EventTileSize = new(220f, 120f);
	private static readonly Vector2I AncientPreviewTextureSize = new(360, 196);
	private static readonly Dictionary<string, Texture2D> AncientPreviewTextures = new(StringComparer.Ordinal);
	private static readonly Dictionary<string, SubViewport> AncientPreviewViewports = new(StringComparer.Ordinal);

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
	private string? _currentCardFilterId;

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
		RelicGroupingData relicGroupingData = BuildRelicGroupingData();
		IReadOnlyList<CardModel> allCards = ModelDb.AllCards.ToList();
		//01 - LOADOUT BAG
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
				builder.Options(new SelectScreenOptions { SelectionMode = SelectSelectionMode.None });
				builder.Materialization(SelectMaterializationMode.Eager);
				builder.Layout(10, new Vector2(68f, 68f), 32, 32);
				builder.FilterGroup("class", SScreenLoc("FILTER_GROUP_CLASS", "Class"));
				AddRelicPoolFilters(builder);
				builder.FilterGroup("rarity", SScreenLoc("FILTER_GROUP_RARITY", "Rarity"));
				AddEnumFilters(builder, "rarity", (RelicModel relic) => relic.Rarity, RelicRarity.None);
				builder.Sorter("name", SScreenLoc("SORT_NAME", "Name"), (a, b) => string.Compare(FormatRelicTitle(a), FormatRelicTitle(b), StringComparison.Ordinal));
				builder.Sorter("id", SScreenLoc("SORT_ID", "ID"), (a, b) => string.Compare(a.Id.Entry, b.Id.Entry, StringComparison.Ordinal));
				builder.Sorter("rarity", GameLoc("gameplay_ui", "SORT_RARITY", SScreenLoc("SORT_RARITY", "Rarity")), CompareRelicRarity, activeByDefault: true);
				builder.GroupBySorter(
					"rarity",
					relic => GetRelicGroupKey(relic, relicGroupingData),
					key => GetRelicGroupHeader(key, relicGroupingData),
					relicGroupingData.GroupOrder,
					relicGroupingData.DescendingGroupOrder);
			},null,
			"LoadoutBag.png",
			"Loadout Bag",
			"A bag that contains everthing.",
			HandleAddRelicActivatedAsync);
		//02 - TRASH BIN
		CreateAndAddDynamicLoadoutItem(
			GetLocalRelics,
			new SelectItemAdapter<RelicModel>
			{
				GetId = RuntimeItemId,
				GetName = relic => FormatRelicTitle(relic),
				GetSearchText = relic => $"{relic.Id} {FormatRelicTitle(relic)} {relic.DynamicDescription}",
				CreateView = (relic, _) => CreateOwnedRelicGridItem(relic),
				BindActivation = (_, view, activate) => BindRelicActivation(view, activate)
			},
			builder =>
			{
				builder.Options(new SelectScreenOptions { SelectionMode = SelectSelectionMode.None });
				builder.Materialization(SelectMaterializationMode.Eager);
				builder.Layout(10, new Vector2(68f, 68f), 32, 32);
			},
			HandleRemoveRelicActivatedAsync,
			"TrashBin.png",
			"Remove Relic",
			"Remove one of your current relics.");
		//03 - LOADOUT CAULDRON
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
				builder.Options(new SelectScreenOptions { SelectionMode = SelectSelectionMode.None });
				builder.Materialization(SelectMaterializationMode.Eager);
				builder.Layout(10, new Vector2(60f, 60f), 32, 32);
				builder.FilterGroup("class", SScreenLoc("FILTER_GROUP_CLASS", "Class"));
				AddPotionPoolFilters(builder);
				builder.FilterGroup("rarity", SScreenLoc("FILTER_GROUP_RARITY", "Rarity"));
				AddEnumFilters(builder, "rarity", (PotionModel potion) => potion.Rarity, PotionRarity.None);
				builder.Sorter("name", SScreenLoc("SORT_NAME", "Name"), (a, b) => string.Compare(FormatPotionTitle(a), FormatPotionTitle(b), StringComparison.Ordinal));
				builder.Sorter("id", SScreenLoc("SORT_ID", "ID"), (a, b) => string.Compare(a.Id.Entry, b.Id.Entry, StringComparison.Ordinal));
				builder.Sorter("rarity", GameLoc("gameplay_ui", "SORT_RARITY", SScreenLoc("SORT_RARITY", "Rarity")), ComparePotionRarity, activeByDefault: true);
				builder.GroupBySorter(
					"rarity",
					GetPotionGroupKey,
					GetPotionGroupHeader,
					PotionGroupOrder,
					PotionGroupOrder.Reverse());
			}, null,
			"LoadoutCauldron.png",
			"Loadout Cauldron",
			"A cauldron that creates any potion.",
			HandleAddPotionActivatedAsync);

		//04 - CARD PRINTER
		CreateAndAddLoadoutItem(
			allCards,
			new SelectItemAdapter<CardModel>
			{
				GetId = card => card.Id.ToString(),
				GetName = card => FormatCardTitle(card),
				GetSearchText = card => $"{card.Id} {FormatCardTitle(card)} {card.TitleLocString} {card.Description}",
				CreateView = CreateCardGridItem,
				ViewReady = (_, view) => RefreshCardVisuals(view),
				UpdateView = (_, view, state) => UpdateCardGridItem(view, state),
				BindActivation = (_, view, activate) => BindCardActivation(view, activate)
			}, builder =>
			{
				builder.Options(new SelectScreenOptions { SelectionMode = SelectSelectionMode.None });
				builder.Materialization(SelectMaterializationMode.Lazy);
				builder.Layout(5, NCard.defaultSize * NCardHolder.smallScale, 32, 40, paddingLeft: 0f, paddingTop: 200f, paddingRight: 0f);
				builder.FilterGroup("class", SScreenLoc("FILTER_GROUP_CLASS", "Class"));
				AddCardPoolFilters(builder);
				builder.FilterGroup("type", GameLoc("gameplay_ui", "SORT_TYPE", SScreenLoc("FILTER_GROUP_TYPE", "Type")));
				AddCardTypeFilters(builder, allCards);
				builder.FilterGroup("rarity", GameLoc("main_menu_ui", "CARD_LIBRARY_RARITY", SScreenLoc("FILTER_GROUP_RARITY", "Rarity")));
				AddCardRarityFilters(builder, allCards);
				AddCardKeywordFilterGroup(builder, allCards);
				AddCardTagFilterGroup(builder, allCards);
				builder.Toggle(ViewUpgradesToggleId, GameLoc("card_library", "VIEW_UPGRADES", GameLoc("gameplay_ui", "VIEW_UPGRADES", "View Upgrades")), checkedByDefault: false);
				IReadOnlyList<CardPoolModel> librarySortPools = BuildOrderedCardPools();
				builder.Sorter("library", SScreenLoc("SORT_LIBRARY", "Library"), (a, b) => CompareCardLibraryOrder(a, b, librarySortPools), activeByDefault: true);
				builder.Sorter("name", GameLoc("gameplay_ui", "SORT_ALPHABET", SScreenLoc("SORT_NAME", "Name")), (a, b) => string.Compare(FormatCardTitle(a), FormatCardTitle(b), StringComparison.Ordinal));
				builder.Sorter("id", SScreenLoc("SORT_ID", "ID"), (a, b) => string.Compare(a.Id.Entry, b.Id.Entry, StringComparison.Ordinal));
				builder.Sorter("cost", GameLoc("gameplay_ui", "SORT_COST", SScreenLoc("SORT_COST", "Cost")), (a, b) => a.EnergyCost.Canonical.CompareTo(b.EnergyCost.Canonical));
			},
			ApplyCurrentCardClassFilter, 
			"CardPrinter.png",
			"Card Printer",
			"It prints any cards you want.",
			HandleAddCardActivatedAsync);
		//05 - CARD SHREDDER
		CreateAndAddDynamicLoadoutItem(
			GetLocalDeckCards,
			new SelectItemAdapter<CardModel>
			{
				GetId = RuntimeItemId,
				GetName = card => FormatCardTitle(card),
				GetSearchText = card => $"{card.Id} {FormatCardTitle(card)} {card.TitleLocString} {card.Description}",
				CreateView = CreateCardGridItem,
				ViewReady = (_, view) => RefreshCardVisuals(view),
				UpdateView = (_, view, state) => UpdateCardGridItem(view, state),
				BindActivation = (_, view, activate) => BindCardActivation(view, activate)
			},
			builder =>
			{
				builder.Options(new SelectScreenOptions { SelectionMode = SelectSelectionMode.None });
				builder.Materialization(SelectMaterializationMode.Lazy);
				builder.Layout(5, NCard.defaultSize * NCardHolder.smallScale, 32, 40, paddingLeft: 0f, paddingTop: 200f, paddingRight: 0f);
			},
			HandleRemoveCardActivatedAsync,
			"CardShredder.png",
			"Remove Card",
			"Remove a card from your current deck.");
				
		//07 - EVENTFUL COMPASS
		CreateAndAddLoadoutItem(
			ModelDb.AllEvents.Concat(ModelDb.AllAncients).Distinct(),
			new SelectItemAdapter<EventModel>
			{
				GetId = eventModel => eventModel.Id.ToString(),
				GetName = eventModel => FormatEventTitle(eventModel),
				GetSearchText = eventModel => $"{eventModel.Id} {FormatEventTitle(eventModel)} {eventModel.InitialDescription}",
				CreateView = (eventModel, _) => CreateEventGridItem(eventModel),
				BindActivation = (_, view, activate) => BindGuiReleaseActivation(view, activate)
			}, builder =>
			{
				builder.Options(new SelectScreenOptions { SelectionMode = SelectSelectionMode.None });
				builder.Materialization(SelectMaterializationMode.Eager);
				builder.Layout(4, EventTileSize, 24, 24);
				builder.FilterGroup("layout", SScreenLoc("FILTER_GROUP_LAYOUT", "Layout"));
				builder.Filter("default", SScreenLoc("LAYOUT_DEFAULT", "Default"), eventModel => eventModel.LayoutType == EventLayoutType.Default, "layout");
				builder.Filter("combat", SScreenLoc("LAYOUT_COMBAT", "Combat"), eventModel => eventModel.LayoutType == EventLayoutType.Combat, "layout");
				builder.Filter("ancient", SScreenLoc("LAYOUT_ANCIENT", "Ancient"), eventModel => eventModel.LayoutType == EventLayoutType.Ancient, "layout");
				builder.FilterGroup("sharing", SScreenLoc("FILTER_GROUP_SCOPE", "Scope"));
				builder.Filter("shared", SScreenLoc("SCOPE_SHARED", "Shared"), eventModel => eventModel.IsShared, "sharing");
				builder.Filter("solo", SScreenLoc("SCOPE_SOLO", "Solo"), eventModel => !eventModel.IsShared, "sharing");
				builder.Sorter("name", SScreenLoc("SORT_NAME", "Name"), (a, b) => string.Compare(FormatEventTitle(a), FormatEventTitle(b), StringComparison.Ordinal), activeByDefault: true);
				builder.Sorter("id", SScreenLoc("SORT_ID", "ID"), (a, b) => string.Compare(a.Id.Entry, b.Id.Entry, StringComparison.Ordinal));
			},null,
			"EventfulCompass.png",
			"Eventful Compass",
			"A compass that leads to your heart's desire.",
			HandleEnterEventActivatedAsync);
		//08 - POWER GIVER
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
				builder.Materialization(SelectMaterializationMode.Eager);
				builder.Layout(5, new Vector2(220f, 104f), 24, 24, fixedSlots: false);
				builder.FilterGroup("type", SScreenLoc("FILTER_GROUP_TYPE", "Type"));
				builder.Filter("buff", SScreenLoc("POWER_TYPE_BUFF", "Buff"), power => power.Type == PowerType.Buff, "type");
				builder.Filter("debuff", SScreenLoc("POWER_TYPE_DEBUFF", "Debuff"), power => power.Type == PowerType.Debuff, "type");
				builder.Filter("type_none", SScreenLoc("NONE", "None"), power => power.Type == PowerType.None, "type");
				builder.FilterGroup("stack", SScreenLoc("FILTER_GROUP_STACK", "Stack"));
				builder.Filter("stack_none", SScreenLoc("NONE", "None"), power => power.StackType == PowerStackType.None, "stack");
				builder.Filter("counter", SScreenLoc("POWER_STACK_COUNTER", "Counter"), power => power.StackType == PowerStackType.Counter, "stack");
				builder.Filter("single", SScreenLoc("POWER_STACK_SINGLE", "Single"), power => power.StackType == PowerStackType.Single, "stack");
				builder.Sorter("name", SScreenLoc("SORT_NAME", "Name"), (a, b) => string.Compare(FormatPowerTitle(a), FormatPowerTitle(b), StringComparison.Ordinal), activeByDefault: true);
				builder.Sorter("id", SScreenLoc("SORT_ID", "ID"), (a, b) => string.Compare(a.Id.Entry, b.Id.Entry, StringComparison.Ordinal));
				builder.Sorter("type", GameLoc("gameplay_ui", "SORT_TYPE", SScreenLoc("SORT_TYPE", "Type")), (a, b) => a.Type.CompareTo(b.Type));
			},
			null,
			"PowerGiver.png",
			"Power Giver",
			"Potion that gives power.");
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

	private void CreateAndAddLoadoutItem<TModel>(
		IEnumerable<TModel> models,
		SelectItemAdapter<TModel> adapter,
		Action<SelectScreenBuilder<TModel>> builder,
		Action<NGenericSelectScreen>? beforeOpen,
		string textureFileName,
		string title,
		string description,
		Func<NGenericSelectScreen, IGenericSelectItem, Task>? onActivated = null)
	{
		var item = new NLoadoutPanelItem(textureFileName, title, description);
		var scene = GD.Load<PackedScene>("res://UI/Screens/GenericSelectScreen.tscn");
		var screen = scene.Instantiate<NGenericSelectScreen>();
		bool activationInFlight = false;

		screen.Configure(models, adapter, builder);
		screen.Cancelled += CloseTopLoadoutScreen;
		screen.Confirmed += _ => CloseTopLoadoutScreen();
		if (onActivated is not null)
		{
			screen.ItemActivated += (selectItem, state) =>
			{
				if (activationInFlight)
					return;

				activationInFlight = true;
				_ = HandleStaticItemActivatedAsync(
					screen,
					selectItem,
					onActivated,
					() => activationInFlight = false);
			};
		}

		item.BoundScreen = screen;
		if (beforeOpen is not null)
			item.BeforeOpen = beforeOpen;

		_itemsContainer.AddChild(item);
	}

	private static async Task HandleStaticItemActivatedAsync(
		NGenericSelectScreen screen,
		IGenericSelectItem selectItem,
		Func<NGenericSelectScreen, IGenericSelectItem, Task> onActivated,
		Action clearActivation)
	{
		try
		{
			await onActivated(screen, selectItem);
		}
		catch (Exception exception)
		{
			GD.PushError($"LoadoutPanel: item activation failed for '{selectItem.Id}' ({selectItem.Name}): {exception}");
		}
		finally
		{
			clearActivation();
		}
	}

	private void CreateAndAddDynamicLoadoutItem<TModel>(
		Func<IReadOnlyList<TModel>> getModels,
		SelectItemAdapter<TModel> adapter,
		Action<SelectScreenBuilder<TModel>> builder,
		Func<NGenericSelectScreen, IGenericSelectItem, Task> onActivated,
		string textureFileName,
		string title,
		string description)
	{
		var item = new NLoadoutPanelItem(textureFileName, title, description);
		var scene = GD.Load<PackedScene>("res://UI/Screens/GenericSelectScreen.tscn");
		var screen = scene.Instantiate<NGenericSelectScreen>();
		bool activationInFlight = false;

		void ConfigureCurrentModels(NGenericSelectScreen target, bool preserveViews = false)
		{
			IReadOnlyList<TModel> models = getModels();
			if (models.Count == 0)
				LogEmptyDynamicScreen(title);

			if (preserveViews)
				target.ConfigurePreservingViews(models, adapter, builder, animateRelayout: true);
			else
				target.Configure(models, adapter, builder);

			if (!preserveViews)
				target.RequestDeferredVisibleRefresh();
		}

		ConfigureCurrentModels(screen);
		screen.Cancelled += CloseTopLoadoutScreen;
		screen.Confirmed += _ => CloseTopLoadoutScreen();
		screen.ItemActivated += (selectItem, state) =>
		{
			if (activationInFlight)
				return;

			activationInFlight = true;
			_ = HandleDynamicItemActivatedAsync(
				screen,
				selectItem,
				onActivated,
				target => ConfigureCurrentModels(target, preserveViews: true),
				() => activationInFlight = false);
		};

		item.BoundScreen = screen;
		item.BeforeOpen = target => ConfigureCurrentModels(target);

		_itemsContainer.AddChild(item);
	}

	private static void LogEmptyDynamicScreen(string title)
	{
		bool isInProgress = RunManager.Instance.IsInProgress;
		Player? localPlayer = GetLocalRunPlayer();
		int deckCount = localPlayer?.Deck.Cards.Count ?? -1;
		int relicCount = localPlayer?.Relics.Count ?? -1;
		GD.PushWarning(
			$"LoadoutPanel: dynamic screen '{title}' has no items. " +
			$"isInProgress={isInProgress}, localPlayerResolved={localPlayer is not null}, deckCount={deckCount}, relicCount={relicCount}.");
	}

	private static async Task HandleDynamicItemActivatedAsync(
		NGenericSelectScreen screen,
		IGenericSelectItem selectItem,
		Func<NGenericSelectScreen, IGenericSelectItem, Task> onActivated,
		Action<NGenericSelectScreen> refresh,
		Action clearActivation)
	{
		try
		{
			await onActivated(screen, selectItem);
		}
		catch (Exception exception)
		{
			GD.PushError($"LoadoutPanel: dynamic item activation failed for '{selectItem.Id}' ({selectItem.Name}): {exception}");
		}
		finally
		{
			refresh(screen);
			clearActivation();
		}
	}

	private static IReadOnlyList<CardModel> GetLocalDeckCards()
	{
		Player? localPlayer = GetLocalRunPlayer();
		return localPlayer is null ? Array.Empty<CardModel>() : localPlayer.Deck.Cards.ToList();
	}

	private static IReadOnlyList<RelicModel> GetLocalRelics()
	{
		Player? localPlayer = GetLocalRunPlayer();
		return localPlayer is null ? Array.Empty<RelicModel>() : localPlayer.Relics.ToList();
	}

	private static Player? GetLocalRunPlayer()
	{
		try
		{
			if (!RunManager.Instance.IsInProgress)
				return null;

			return LocalContext.GetMe(RunManager.Instance.DebugOnlyGetState());
		}
		catch (Exception exception)
		{
			GD.PushWarning($"LoadoutPanel: could not resolve local run player. {exception.Message}");
			return null;
		}
	}

	private static string RuntimeItemId(AbstractModel model)
	{
		return $"{model.Id}:{RuntimeHelpers.GetHashCode(model)}";
	}

	private static async Task HandleAddCardActivatedAsync(NGenericSelectScreen screen, IGenericSelectItem selectItem)
	{
		if (selectItem.UntypedModel is not CardModel canonicalCard)
			return;

		Player? localPlayer = GetLocalRunPlayer();
		if (localPlayer is null)
			return;

		int multiplier = screen.GetCurrentActivationMultiplier();
		List<CardPileAddResult> results = new();
		for (int i = 0; i < multiplier; i++)
		{
			try
			{
				CardModel card = localPlayer.RunState.CreateCard(canonicalCard, localPlayer);
				CardPileAddResult result = await CardPileCmd.Add(card, PileType.Deck);
				if (!result.success)
					break;

				results.Add(result);
			}
			catch (Exception exception)
			{
				GD.PushWarning($"LoadoutPanel: stopped adding card '{selectItem.Id}' after {results.Count}/{multiplier} copies. {exception.Message}");
				break;
			}
		}

		if (results.Count == 0)
			return;

		CardPreviewStyle previewStyle = results.Count > 5 ? CardPreviewStyle.MessyLayout : CardPreviewStyle.HorizontalLayout;
		CardCmd.PreviewCardPileAdd(results, 2f, previewStyle);
	}

	private static async Task HandleAddRelicActivatedAsync(NGenericSelectScreen screen, IGenericSelectItem selectItem)
	{
		if (selectItem.UntypedModel is not RelicModel canonicalRelic)
			return;

		Player? localPlayer = GetLocalRunPlayer();
		if (localPlayer is null)
			return;

		int multiplier = screen.GetCurrentActivationMultiplier();
		for (int i = 0; i < multiplier; i++)
		{
			try
			{
				await RelicCmd.Obtain(canonicalRelic.ToMutable(), localPlayer);
			}
			catch (Exception exception)
			{
				GD.PushWarning($"LoadoutPanel: stopped adding relic '{selectItem.Id}' after {i}/{multiplier} copies. {exception.Message}");
				break;
			}
		}
	}

	private static async Task HandleAddPotionActivatedAsync(NGenericSelectScreen screen, IGenericSelectItem selectItem)
	{
		if (selectItem.UntypedModel is not PotionModel canonicalPotion)
			return;

		Player? localPlayer = GetLocalRunPlayer();
		if (localPlayer is null)
			return;

		int multiplier = screen.GetCurrentActivationMultiplier();
		for (int i = 0; i < multiplier; i++)
		{
			try
			{
				PotionProcureResult result = await PotionCmd.TryToProcure(canonicalPotion.ToMutable(), localPlayer);
				if (!result.success)
					break;
			}
			catch (Exception exception)
			{
				GD.PushWarning($"LoadoutPanel: stopped adding potion '{selectItem.Id}' after {i}/{multiplier} copies. {exception.Message}");
				break;
			}
		}
	}

	private static async Task HandleRemoveCardActivatedAsync(NGenericSelectScreen _, IGenericSelectItem selectItem)
	{
		if (selectItem.UntypedModel is not CardModel card)
			return;

		Player? localPlayer = GetLocalRunPlayer();
		if (localPlayer is null
		    || !LocalContext.IsMine(card)
		    || card.Pile?.Type != PileType.Deck
		    || !localPlayer.Deck.Cards.Contains(card))
		{
			return;
		}

		await CardPileCmd.RemoveFromDeck(card);
	}

	private static async Task HandleRemoveRelicActivatedAsync(NGenericSelectScreen _, IGenericSelectItem selectItem)
	{
		if (selectItem.UntypedModel is not RelicModel relic)
			return;

		Player? localPlayer = GetLocalRunPlayer();
		if (localPlayer is null
		    || !LocalContext.IsMine(relic)
		    || !localPlayer.Relics.Contains(relic))
		{
			return;
		}

		await RelicCmd.Remove(relic);
	}

	private static async Task HandleEnterEventActivatedAsync(NGenericSelectScreen _, IGenericSelectItem selectItem)
	{
		if (selectItem.UntypedModel is not EventModel eventModel)
			return;

		Player? localPlayer = GetLocalRunPlayer();
		if (localPlayer is null || !RunManager.Instance.IsInProgress)
		{
			GD.PushWarning($"LoadoutPanel: cannot enter event '{selectItem.Id}' because no local run player was resolved.");
			return;
		}

		try
		{
			if (!RunManager.Instance.IsSingleplayerOrFakeMultiplayer)
			{
				string command = $"event {eventModel.Id.Entry}";
				RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(
					new ConsoleCmdGameAction(localPlayer, command, CombatManager.Instance.IsInProgress));
				CloseTopLoadoutScreen();
				return;
			}

			MapPointType mapPointType = eventModel is AncientEventModel
				? MapPointType.Ancient
				: MapPointType.Unknown;
			localPlayer.RunState.AppendToMapPointHistory(mapPointType, RoomType.Event, eventModel.Id);
			await RunManager.Instance.EnterRoom(new EventRoom(eventModel));
			CloseTopLoadoutScreen();
		}
		catch (Exception exception)
		{
			GD.PushError($"LoadoutPanel: failed to enter event '{eventModel.Id}': {exception}");
		}
	}

	private void ApplyCurrentCardClassFilter(NGenericSelectScreen screen)
	{
		CardPoolModel? currentCardPool = GetCurrentCharacterCardPool();
		if (currentCardPool is null)
			return;

		string filterId = PoolFilterId("card", currentCardPool);
		if (string.Equals(_currentCardFilterId, filterId, StringComparison.Ordinal))
			return;

		if (screen.SetExclusiveFilterSelection("class", filterId, resetScroll: true))
			_currentCardFilterId = filterId;
	}

	private static void CloseTopLoadoutScreen()
	{
		NLoadoutPanelRoot.Instance?.CloseTopScreen();
	}

	private static Control CreateCardGridItem(CardModel model, SelectItemState state)
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
		ApplyCardUpgradePreview(holder, state);
		return holder;
	}

	private static Control CreatePotionGridItem(PotionModel model)
	{
		NLabPotionHolder? holder = NLabPotionHolder.Create(model.ToMutable(), ModelVisibility.Visible);
		if (holder is null)
			return CreateTextModelGridItem(model, FormatPotionTitle(model), model.Id.Entry, SScreenLoc("CATEGORY_POTION", "Potion"));

		holder.MouseFilter = MouseFilterEnum.Pass;
		holder.CustomMinimumSize = new Vector2(60f, 60f);
		return holder;
	}

	private static Control CreateRelicGridItem(RelicModel model)
	{
		NRelicCollectionEntry? holder = NRelicCollectionEntry.Create(model, ModelVisibility.Visible);
		if (holder is null)
			return CreateTextModelGridItem(model, FormatRelicTitle(model), model.Id.Entry, SScreenLoc("CATEGORY_RELIC", "Relic"));

		holder.MouseFilter = MouseFilterEnum.Pass;
		holder.CustomMinimumSize = new Vector2(68f, 68f);
		return holder;
	}

	private static Control CreateOwnedRelicGridItem(RelicModel model)
	{
		NRelicBasicHolder? holder = NRelicBasicHolder.Create(model);
		if (holder is null)
			return CreateTextModelGridItem(model, FormatRelicTitle(model), model.Id.Entry, SScreenLoc("CATEGORY_RELIC", "Relic"));

		holder.MouseFilter = MouseFilterEnum.Pass;
		holder.CustomMinimumSize = new Vector2(68f, 68f);
		return holder;
	}

	private static Control CreatePowerGridItem(PowerModel model)
	{
		Texture2D? icon = null;
		if (ResourceLoader.Exists(model.IconPath))
			icon = model.Icon;

		Button button = CreateModelButton(new Vector2(220f, 104f));

		if (icon is not null)
		{
			TextureRect iconRect = new()
			{
				Texture = icon,
				StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
				MouseFilter = MouseFilterEnum.Ignore,
				Position = new Vector2(18f, 22f),
				Size = new Vector2(62f, 62f)
			};
			button.AddChild(iconRect);
		}

		MegaLabel nameLabel = CreateButtonLabel(
			"PowerName",
			FormatPowerTitle(model),
			new Vector2(82f, 8f),
			new Vector2(126f, 78f),
			18,
			HorizontalAlignment.Center,
			StsColors.cream);
		button.AddChild(nameLabel);

		if (model.StackType == PowerStackType.Counter)
		{
			MegaLabel amountLabel = CreateButtonLabel(
				"PowerAmount",
				model.DisplayAmount.ToString(),
				new Vector2(160f, 72f),
				new Vector2(50f, 26f),
				22,
				HorizontalAlignment.Right,
				model.AmountLabelColor);
			button.AddChild(amountLabel);
		}

		AttachHoverTips(button, model.HoverTips);
		return button;
	}

	private static Control CreateEventGridItem(EventModel model)
	{
		Button button = CreateModelButton(EventTileSize);
		button.ClipContents = true;

		TextureRect? background = model is AncientEventModel ancient
			? CreateAncientEventTileBackground(ancient)
			: CreateRegularEventTileBackground(model);
		if (background is not null)
			button.AddChild(background);

		ColorRect shade = new()
		{
			Color = model is AncientEventModel ? new Color(0f, 0f, 0f, 0.42f) : new Color(0f, 0f, 0f, 0.35f),
			MouseFilter = MouseFilterEnum.Ignore,
			Position = Vector2.Zero,
			Size = EventTileSize
		};
		button.AddChild(shade);

		MegaLabel titleLabel = CreateButtonLabel(
			"EventTitle",
			FormatEventTitle(model),
			model is AncientEventModel ? new Vector2(12f, 30f) : new Vector2(12f, 16f),
			model is AncientEventModel ? new Vector2(196f, 36f) : new Vector2(196f, 88f),
			model is AncientEventModel ? 26 : 22,
			HorizontalAlignment.Center,
			StsColors.cream);
		if (model is AncientEventModel)
			titleLabel.AddThemeFontOverride("font", LoadGameFont("res://themes/ancient_name_banner.tres"));
		button.AddChild(titleLabel);

		if (model is AncientEventModel ancientEvent)
		{
			MegaLabel epithetLabel = CreateButtonLabel(
				"AncientEpithet",
				ancientEvent.Epithet.GetFormattedText(),
				new Vector2(12f, 64f),
				new Vector2(196f, 34f),
				15,
				HorizontalAlignment.Center,
				new Color(StsColors.cream, 0.72f));
			epithetLabel.AddThemeFontOverride("font", LoadGameFont("res://themes/bitter_medium_italic_glyph_space_one.tres"));
			button.AddChild(epithetLabel);
		}

		AttachHoverTips(button, CreateEventHoverTips(model));
		return button;
	}

	private static TextureRect? CreateRegularEventTileBackground(EventModel model)
	{
		try
		{
			Texture2D portrait = model.CreateInitialPortrait();
			return CreateTileBackground(portrait);
		}
		catch (Exception exception)
		{
			GD.PushWarning($"LoadoutPanel: could not load event portrait for '{model.Id}'. {exception.Message}");
			return null;
		}
	}

	private static TextureRect? CreateAncientEventTileBackground(AncientEventModel model)
	{
		Texture2D? texture = GetAncientPreviewTexture(model);
		return texture is null ? null : CreateTileBackground(texture);
	}

	private static TextureRect CreateTileBackground(Texture2D texture)
	{
		return new TextureRect
		{
			Texture = texture,
			ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
			StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
			MouseFilter = MouseFilterEnum.Ignore,
			Modulate = new Color(1f, 1f, 1f, 0.5f),
			Position = Vector2.Zero,
			Size = EventTileSize
		};
	}

	private static Texture2D? GetAncientPreviewTexture(AncientEventModel model)
	{
		string id = model.Id.ToString();
		if (AncientPreviewTextures.TryGetValue(id, out Texture2D? cachedTexture))
			return cachedTexture;

		try
		{
			SubViewport viewport = new()
			{
				Name = $"LoadoutAncientPreview_{MakeSafeNodeName(id)}",
				Size = AncientPreviewTextureSize,
				TransparentBg = true,
				Disable3D = true,
				RenderTargetUpdateMode = SubViewport.UpdateMode.Once
			};
			Control backgroundScene = model.CreateBackgroundScene().Instantiate<Control>(PackedScene.GenEditState.Disabled);
			backgroundScene.MouseFilter = MouseFilterEnum.Ignore;
			backgroundScene.Position = Vector2.Zero;
			backgroundScene.Size = new Vector2(1920f, 1080f);
			float scale = Math.Max(
				AncientPreviewTextureSize.X / Math.Max(1f, backgroundScene.Size.X),
				AncientPreviewTextureSize.Y / Math.Max(1f, backgroundScene.Size.Y));
			backgroundScene.Scale = Vector2.One * scale;
			viewport.AddChild(backgroundScene);

			NLoadoutPanelRoot.Instance?.AddChild(viewport);
			Texture2D texture = viewport.GetTexture();
			AncientPreviewViewports[id] = viewport;
			AncientPreviewTextures[id] = texture;
			return texture;
		}
		catch (Exception exception)
		{
			GD.PushWarning($"LoadoutPanel: could not create ancient preview for '{model.Id}'. {exception.Message}");
			return null;
		}
	}

	private static Button CreateModelButton(Vector2 size)
	{
		Button button = new()
		{
			CustomMinimumSize = size,
			MouseFilter = MouseFilterEnum.Stop,
			FocusMode = FocusModeEnum.All,
			SizeFlagsHorizontal = SizeFlags.ShrinkBegin,
			SizeFlagsVertical = SizeFlags.ShrinkBegin,
			Text = string.Empty
		};
		button.AddThemeFontOverride("font", LoadGameFont());
		button.AddThemeFontSizeOverride("font_size", 18);
		button.AddThemeColorOverride("font_color", StsColors.cream);
		return button;
	}

	private static MegaLabel CreateButtonLabel(
		string name,
		string text,
		Vector2 position,
		Vector2 size,
		int fontSize,
		HorizontalAlignment horizontalAlignment,
		Color color)
	{
		MegaLabel label = new()
		{
			Name = name,
			Text = text,
			AutoSizeEnabled = false,
			HorizontalAlignment = horizontalAlignment,
			VerticalAlignment = VerticalAlignment.Center,
			MouseFilter = MouseFilterEnum.Ignore,
			Position = position,
			Size = size
		};
		label.AddThemeFontOverride("font", LoadGameFont());
		label.AddThemeFontSizeOverride("font_size", fontSize);
		label.AddThemeColorOverride("font_color", color);
		label.AddThemeColorOverride("font_shadow_color", new Color(0f, 0f, 0f, 0.5f));
		label.AddThemeConstantOverride("shadow_offset_x", 3);
		label.AddThemeConstantOverride("shadow_offset_y", 2);
		return label;
	}

	private static void AttachHoverTips(Control owner, IEnumerable<IHoverTip> hoverTips)
	{
		IReadOnlyList<IHoverTip> tips = hoverTips.Where(tip => tip is not null).ToList();
		if (tips.Count == 0)
			return;

		owner.MouseEntered += () => ShowHoverTips(owner, tips);
		owner.MouseExited += () => NHoverTipSet.Remove(owner);
		owner.TreeExiting += () => NHoverTipSet.Remove(owner);
	}

	private static void ShowHoverTips(Control owner, IReadOnlyList<IHoverTip> tips)
	{
		NHoverTipSet.Remove(owner);
		NHoverTipSet.CreateAndShow(owner, tips, HoverTip.GetHoverTipAlignment(owner))?.SetFollowOwner();
		NLoadoutPanelRoot.Instance?.AdoptGameHoverTips();
	}

	private static IReadOnlyList<IHoverTip> CreateEventHoverTips(EventModel model)
	{
		string description = GetFirstEventDescriptionParagraph(model.InitialDescription);
		string idLine = $"[color=#9a9a9a]{model.Id}[/color]";
		string hoverDescription = string.IsNullOrWhiteSpace(description)
			? idLine
			: $"{description}\n\n{idLine}";

		return [new HoverTip(model.Title, hoverDescription)];
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

		if (holder.CardModel is not null
		    && holder.CardModel.IsUpgradable
		    && holder.GetMeta(PreviewUpgradeMetaKey, false).AsBool())
		{
			holder.SetIsPreviewingUpgrade(true);
		}
	}

	private static void UpdateCardGridItem(Control view, SelectItemState state)
	{
		if (!TryFindDescendantOrSelf(view, out NGridCardHolder? holder))
			return;

		ApplyCardUpgradePreview(holder!, state);
	}

	private static void ApplyCardUpgradePreview(NGridCardHolder holder, SelectItemState state)
	{
		bool shouldPreviewUpgrade = holder.CardModel is not null
			&& holder.CardModel.IsUpgradable
			&& state.IsToggleEnabled(ViewUpgradesToggleId);

		holder.SetMeta(PreviewUpgradeMetaKey, shouldPreviewUpgrade);

		if (holder.CardModel is null || !holder.CardModel.IsUpgradable)
			return;

		holder.SetIsPreviewingUpgrade(shouldPreviewUpgrade);
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
		if (TryFindDescendantOrSelf(view, out NRelicCollectionEntry? collectionEntry))
		{
			collectionEntry!.Connect(NClickableControl.SignalName.Released, Callable.From<NRelicCollectionEntry>(_ => activate()));
			return true;
		}

		if (TryFindDescendantOrSelf(view, out NRelicBasicHolder? basicHolder))
		{
			basicHolder!.Connect(NClickableControl.SignalName.Released, Callable.From<NRelicBasicHolder>(_ => activate()));
			return true;
		}

		return false;
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

	private static void AddCardPoolFilters(SelectScreenBuilder<CardModel> builder)
	{
		IReadOnlyList<CardPoolModel> pools = BuildOrderedCardPools();

		foreach (CardPoolModel pool in pools)
		{
			CardPoolModel localPool = pool;
			builder.Filter(
				PoolFilterId("card", localPool),
				GetPoolLabel(localPool),
				card => SamePool(card.Pool, localPool),
				"class");
		}
	}

	private static void AddCardTypeFilters(SelectScreenBuilder<CardModel> builder, IEnumerable<CardModel> cards)
	{
		foreach (CardType type in cards
			         .Select(card => card.Type)
			         .Distinct()
			         .OrderBy(type => Convert.ToInt32(type)))
		{
			CardType localType = type;
			builder.Filter(
				EnumFilterId("card_type", localType),
				GetCardTypeLabel(localType),
				card => card.Type == localType,
				"type");
		}
	}

	private static void AddCardRarityFilters(SelectScreenBuilder<CardModel> builder, IEnumerable<CardModel> cards)
	{
		foreach (CardRarity rarity in cards
			         .Select(card => card.Rarity)
			         .Distinct()
			         .Where(rarity => rarity != CardRarity.None)
			         .OrderBy(GetCardRaritySortValue)
			         .ThenBy(rarity => Convert.ToInt32(rarity)))
		{
			CardRarity localRarity = rarity;
			builder.Filter(
				EnumFilterId("card_rarity", localRarity),
				GetCardRarityLabel(localRarity),
				card => card.Rarity == localRarity,
				"rarity");
		}
	}

	private static void AddCardKeywordFilterGroup(SelectScreenBuilder<CardModel> builder, IEnumerable<CardModel> cards)
	{
		IReadOnlyList<CardKeyword> keywords = cards
			.SelectMany(GetLocalCardKeywords)
			.Where(keyword => keyword != CardKeyword.None)
			.Distinct()
			.OrderBy(keyword => Convert.ToInt32(keyword))
			.ToList();

		if (keywords.Count == 0)
			return;

		builder.FilterGroup("keyword", SScreenLoc("FILTER_GROUP_KEYWORD", "Keyword"));
		foreach (CardKeyword keyword in keywords)
		{
			CardKeyword localKeyword = keyword;
			builder.Filter(
				EnumFilterId("card_keyword", localKeyword),
				GetCardKeywordLabel(localKeyword),
				card => GetLocalCardKeywords(card).Contains(localKeyword),
				"keyword");
		}
	}

	private static IEnumerable<CardKeyword> GetLocalCardKeywords(CardModel card)
	{
		return card.GetKeywordsWithSources(KeywordSources.Local);
	}

	private static void AddCardTagFilterGroup(SelectScreenBuilder<CardModel> builder, IEnumerable<CardModel> cards)
	{
		IReadOnlyList<CardTag> tags = cards
			.SelectMany(card => card.Tags)
			.Where(tag => tag != CardTag.None)
			.Distinct()
			.OrderBy(tag => Convert.ToInt32(tag))
			.ToList();

		if (tags.Count == 0)
			return;

		builder.FilterGroup("tag", SScreenLoc("FILTER_GROUP_TAG", "Tag"));
		foreach (CardTag tag in tags)
		{
			CardTag localTag = tag;
			builder.Filter(
				EnumFilterId("card_tag", localTag),
				GetCardTagLabel(localTag),
				card => card.Tags.Contains(localTag),
				"tag");
		}
	}

	private static IReadOnlyList<CardPoolModel> BuildOrderedCardPools()
	{
		return BuildOrderedPools(
			ModelDb.AllCards.Select(card => card.Pool),
			ModelDb.AllCharacters.Where(character => character.IsPlayable).Select(character => character.CardPool),
			pool => !IsInternalPool(pool));
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
			return SScreenLoc("POOL_SHARED", "Shared");

		if (typeName.StartsWith("Colorless", StringComparison.Ordinal))
			return SScreenLoc("POOL_COLORLESS", "Colorless");

		if (typeName.StartsWith("Event", StringComparison.Ordinal))
			return SScreenLoc("POOL_EVENT", "Event");

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

	private static int CompareCardLibraryOrder(CardModel left, CardModel right, IReadOnlyList<CardPoolModel> orderedPools)
	{
		int pool = GetCardPoolSortIndex(left.Pool, orderedPools).CompareTo(GetCardPoolSortIndex(right.Pool, orderedPools));
		if (pool != 0)
			return pool;

		int rarity = GetCardRaritySortValue(left.Rarity).CompareTo(GetCardRaritySortValue(right.Rarity));
		if (rarity != 0)
			return rarity;

		int type = left.Type.CompareTo(right.Type);
		if (type != 0)
			return type;

		int cost = left.EnergyCost.GetResolved().CompareTo(right.EnergyCost.GetResolved());
		if (cost != 0)
			return cost;

		return string.Compare(left.Id.Entry, right.Id.Entry, StringComparison.Ordinal);
	}

	private static int GetCardPoolSortIndex(CardPoolModel pool, IReadOnlyList<CardPoolModel> orderedPools)
	{
		for (int i = 0; i < orderedPools.Count; i++)
		{
			if (SamePool(pool, orderedPools[i]))
				return i;
		}

		return orderedPools.Count;
	}

	private static int GetCardRaritySortValue(CardRarity rarity)
	{
		if (rarity <= CardRarity.Ancient)
			return (int)rarity;

		return rarity switch
		{
			CardRarity.Status => 6,
			CardRarity.Curse => 7,
			CardRarity.Event => 8,
			CardRarity.Quest => 9,
			CardRarity.Token => 10,
			_ => (int)rarity
		};
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
			_ => SelectGroupHeader.Category(SScreenLoc("OTHER", "Other"))
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
			["relic:ancient"] = new(new LocString("relic_collection", "ANCIENT").GetFormattedText(), childGroupPrefix: "relic:ancient:"),
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
		List<string> ancientGroupOrder = new();

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
			ancientGroupOrder.Add(groupKey);

			foreach (RelicModel relic in ancientRelics)
				keyByRelicId[relic.Id.ToString()] = groupKey;
		}

		groupOrder.Add("relic:event");

		List<string> descendingGroupOrder = new()
		{
			"relic:event",
			"relic:ancient"
		};
		descendingGroupOrder.AddRange(ancientGroupOrder);
		descendingGroupOrder.AddRange(new[]
		{
			"relic:shop",
			"relic:rare",
			"relic:uncommon",
			"relic:common",
			"relic:starter"
		});

		return new RelicGroupingData(keyByRelicId, headers, groupOrder, descendingGroupOrder);
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
			: SelectGroupHeader.Category(SScreenLoc("OTHER", "Other"));
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

			string label = GetEnumLabel(value);
			builder.Filter(EnumFilterId(typeof(TEnum).Name, value), label, model => EqualityComparer<TEnum>.Default.Equals(getValue(model), value), groupId);
		}
	}

	private static string SScreenLoc(string key, string fallback)
	{
		return SelectScreenLoc.Text(key, fallback);
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

	private static string GetCardTypeLabel(CardType type)
	{
		try
		{
			return type.ToLocString().GetFormattedText();
		}
		catch
		{
			return PrettifyEnumValue(type);
		}
	}

	private static string GetCardRarityLabel(CardRarity rarity)
	{
		try
		{
			return rarity.ToLocString().GetFormattedText();
		}
		catch
		{
			return PrettifyEnumValue(rarity);
		}
	}

	private static string GetCardKeywordLabel(CardKeyword keyword)
	{
		try
		{
			if (HoverTipFactory.FromKeyword(keyword) is HoverTip hoverTip && !string.IsNullOrWhiteSpace(hoverTip.Title))
				return hoverTip.Title;
		}
		catch
		{
			// Fall back below for unknown or modded keyword values.
		}

		return PrettifyEnumValue(keyword);
	}

	private static string GetCardTagLabel(CardTag tag)
	{
		return PrettifyEnumValue(tag);
	}

	private static string GetEnumLabel<TEnum>(TEnum value)
		where TEnum : struct, Enum
	{
		try
		{
			return value switch
			{
				PotionRarity potionRarity => potionRarity.ToLocString().GetFormattedText(),
				RelicRarity relicRarity => GameLoc("gameplay_ui", $"RELIC_RARITY.{relicRarity.ToString().ToUpperInvariant()}", PrettifyEnumValue(relicRarity)),
				_ => PrettifyEnumValue(value)
			};
		}
		catch
		{
			return PrettifyEnumValue(value);
		}
	}

	private static string EnumFilterId<TEnum>(string prefix, TEnum value)
		where TEnum : struct, Enum
	{
		string raw = $"{prefix}_{value}_{Convert.ToInt64(value)}";
		return Regex.Replace(raw.ToLowerInvariant(), "[^a-z0-9_]+", "_");
	}

	private static string PrettifyEnumValue<TEnum>(TEnum value)
		where TEnum : struct, Enum
	{
		return Regex.Replace(value.ToString(), "([a-z])([A-Z])", "$1 $2");
	}

	private static Font LoadGameFont()
	{
		const string localPath = "res://Loadout/themes/default/kreon_bold_glyph_space_one.tres";
		if (ResourceLoader.Exists(localPath))
			return GD.Load<Font>(localPath);

		return GD.Load<Font>("res://themes/kreon_bold_glyph_space_one.tres");
	}

	private static Font LoadGameFont(string path)
	{
		if (ResourceLoader.Exists(path))
			return GD.Load<Font>(path);

		return LoadGameFont();
	}

	private static string MakeSafeNodeName(string value)
	{
		string safeName = Regex.Replace(value, @"[^A-Za-z0-9_]", "_");
		return string.IsNullOrWhiteSpace(safeName) ? "GeneratedControl" : safeName;
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

	private static string GetFirstEventDescriptionParagraph(LocString description)
	{
		string text = description.GetFormattedText()
			.Replace("[p]", "\n\n", StringComparison.OrdinalIgnoreCase);

		foreach (string paragraph in Regex.Split(text, @"(?:\r?\n){2,}"))
		{
			string cleaned = StripUiMarkup(paragraph);
			if (!string.IsNullOrWhiteSpace(cleaned))
				return cleaned;
		}

		return string.Empty;
	}

	private static string StripUiMarkup(string text)
	{
		string withoutBbcode = Regex.Replace(text, @"\[[^\]]+\]", " ");
		string withoutTags = Regex.Replace(withoutBbcode, @"<[^>]+>", " ");
		return Regex.Replace(withoutTags, @"\s+", " ").Trim();
	}

	private static string FormatPowerTitle(PowerModel power)
	{
		return power.Title.GetFormattedText();
	}

	private static string FormatPowerCategory(PowerType type)
	{
		return type switch
		{
			PowerType.Buff => SScreenLoc("POWER_TYPE_BUFF", "Buff"),
			PowerType.Debuff => SScreenLoc("POWER_TYPE_DEBUFF", "Debuff"),
			_ => SScreenLoc("NONE", "None")
		};
	}

	private sealed class RelicGroupingData
	{
		public RelicGroupingData(
			IReadOnlyDictionary<string, string> ancientGroupKeyByRelicId,
			IReadOnlyDictionary<string, SelectGroupHeader> headersByKey,
			IReadOnlyList<string> groupOrder,
			IReadOnlyList<string> descendingGroupOrder)
		{
			AncientGroupKeyByRelicId = ancientGroupKeyByRelicId;
			HeadersByKey = headersByKey;
			GroupOrder = groupOrder;
			DescendingGroupOrder = descendingGroupOrder;
		}

		public IReadOnlyDictionary<string, string> AncientGroupKeyByRelicId { get; }
		public IReadOnlyDictionary<string, SelectGroupHeader> HeadersByKey { get; }
		public IReadOnlyList<string> GroupOrder { get; }
		public IReadOnlyList<string> DescendingGroupOrder { get; }
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
