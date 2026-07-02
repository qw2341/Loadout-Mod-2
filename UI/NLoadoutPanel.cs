#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Godot;
using Loadout.UI.Screens;
using Loadout.UI.Screens.Controls;
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
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Nodes.Relics;
using MegaCrit.Sts2.Core.Nodes.Screens.Capstones;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Nodes.Screens.PotionLab;
using MegaCrit.Sts2.Core.Nodes.Screens.RelicCollection;
using MegaCrit.Sts2.Core.Nodes.Vfx;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using System.Text.RegularExpressions;
using HarmonyLib;
using Loadout.PanelItems;
using Loadout.Services.LastActions;
using Loadout.Services.PowerGiver;
using Loadout.UI.Managers;

namespace  Loadout.UI;

public partial class NLoadoutPanel : Panel
{
	public delegate Task<IReadOnlyList<LastActionEntry>> SelectActivationHandler(NGenericSelectScreen screen, IGenericSelectItem selectItem);

	private const int MaxLoadoutItemInitAttempts = 120;
	private const string PowerGiverFavoriteModeAll = "all";
	private const string PowerGiverFavoriteModeFavorites = "favorites";

	[Export]
	public bool Shown = true;

	[Export]
	public float SlideSpeed = 12f;
	
	private PanelContainer _panelContainer = null!;
	private MarginContainer _marginContainer = null!;
	private Control _itemsContainer = null!;

	public static Control ItemsContainer = null!;

	private int _loadoutItemInitAttempts;
	private bool _loadoutItemsAdded;
	private bool _loadoutItemRetryScheduled;
	

	// Called when the node enters the scene tree for the first time.
	public override void _Ready()
	{
		_panelContainer = GetNode<PanelContainer>("PanelContainer");
		_marginContainer = GetNode<MarginContainer>("PanelContainer/MarginContainer");
		_itemsContainer = GetNode<Control>("PanelContainer/MarginContainer/VBoxContainer");
		ItemsContainer = _itemsContainer;
		
		
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
		
		//01 - LOADOUT BAG
		CommonHelpers.CreateAndAddLoadoutItem(
			ModelDb.AllRelics,
			new SelectItemAdapter<RelicModel>
			{
				GetId = relic => relic.Id.ToString(),
				GetName = relic => CommonHelpers.FormatRelicTitle(relic),
				GetSearchText = relic => $"{relic.Id} {CommonHelpers.FormatRelicTitle(relic)} {relic.DynamicDescription}",
				CreateView = (relic, _) => CreateRelicGridItem(relic),
				BindActivation = (_, view, activate) => BindRelicActivation(view, activate)
			}, builder =>
			{
				builder.Options(new SelectScreenOptions { SelectionMode = SelectSelectionMode.None });
				builder.Materialization(SelectMaterializationMode.Eager);
				builder.Layout(10, new Vector2(68f, 68f), 32, 32);
				builder.FilterGroup("class", LocMan.SScreenLoc("FILTER_GROUP_CLASS", "Class"));
				AddRelicPoolFilters(builder);
				builder.FilterGroup("rarity", LocMan.SScreenLoc("FILTER_GROUP_RARITY", "Rarity"));
				CommonHelpers.AddEnumFilters(builder, "rarity", (RelicModel relic) => relic.Rarity, RelicRarity.None);
				builder.Sorter("name", LocMan.SScreenLoc("SORT_NAME", "Name"), (a, b) => string.Compare(CommonHelpers.FormatRelicTitle(a), CommonHelpers.FormatRelicTitle(b), StringComparison.Ordinal));
				builder.Sorter("id", LocMan.SScreenLoc("SORT_ID", "ID"), (a, b) => string.Compare(a.Id.Entry, b.Id.Entry, StringComparison.Ordinal));
				builder.Sorter("rarity", LocMan.GameLoc("gameplay_ui", "SORT_RARITY", LocMan.SScreenLoc("SORT_RARITY", "Rarity")), CompareRelicRarity, activeByDefault: true);
				RelicGroupingData relicGroupingData = BuildRelicGroupingData();
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
			HandleAddRelicActivatedAsync,
			LastActionService.LoadoutBagKey,
			ReplayLoadoutBagLastActionAsync);
		//02 - TRASH BIN
		CreateAndAddDynamicLoadoutItem(CommonHelpers.GetLocalRelics,
			new SelectItemAdapter<RelicModel>
			{
				GetId = RuntimeItemId,
				GetName = relic => CommonHelpers.FormatRelicTitle(relic),
				GetSearchText = relic => $"{relic.Id} {CommonHelpers.FormatRelicTitle(relic)} {relic.DynamicDescription}",
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
		CommonHelpers.CreateAndAddLoadoutItem(
			ModelDb.AllPotions,
			new SelectItemAdapter<PotionModel>
			{
				GetId = potion => potion.Id.ToString(),
				GetName = potion => CommonHelpers.FormatPotionTitle(potion),
				GetSearchText = potion => $"{potion.Id} {CommonHelpers.FormatPotionTitle(potion)} {potion.DynamicDescription}",
				CreateView = (potion, _) => CreatePotionGridItem(potion),
				BindActivation = (_, view, activate) => CommonHelpers.BindGuiReleaseActivation(view, activate)
			}, builder =>
			{
				builder.Options(new SelectScreenOptions { SelectionMode = SelectSelectionMode.None });
				builder.Materialization(SelectMaterializationMode.Eager);
				builder.Layout(10, new Vector2(60f, 60f), 32, 32);
				builder.FilterGroup("class", LocMan.SScreenLoc("FILTER_GROUP_CLASS", "Class"));
				AddPotionPoolFilters(builder);
				builder.FilterGroup("rarity", LocMan.SScreenLoc("FILTER_GROUP_RARITY", "Rarity"));
				CommonHelpers.AddEnumFilters(builder, "rarity", (PotionModel potion) => potion.Rarity, PotionRarity.None);
				builder.Sorter("name", LocMan.SScreenLoc("SORT_NAME", "Name"), (a, b) => string.Compare(CommonHelpers.FormatPotionTitle(a), CommonHelpers.FormatPotionTitle(b), StringComparison.Ordinal));
				builder.Sorter("id", LocMan.SScreenLoc("SORT_ID", "ID"), (a, b) => string.Compare(a.Id.Entry, b.Id.Entry, StringComparison.Ordinal));
				builder.Sorter("rarity", LocMan.GameLoc("gameplay_ui", "SORT_RARITY", LocMan.SScreenLoc("SORT_RARITY", "Rarity")), ComparePotionRarity, activeByDefault: true);
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
		CardPrinter.Initialize();
		//05 - CARD SHREDDER
		CreateAndAddDynamicLoadoutItem(CommonHelpers.GetLocalDeckCards,
			new SelectItemAdapter<CardModel>
			{
				GetId = RuntimeItemId,
				GetName = card => CardPrinter.FormatCardTitle(card),
				GetSearchText = card => $"{card.Id} {CardPrinter.FormatCardTitle(card)} {card.TitleLocString} {card.Description}",
				CreateView = CardPrinter.CreateCardGridItem,
				ViewReady = (_, view) => CardPrinter.RefreshCardVisuals(view),
				UpdateView = (_, view, state) => CardPrinter.UpdateCardGridItem(view, state),
				BindActivation = (_, view, activate) => CardPrinter.BindCardActivation(view, activate)
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
		//06 - CARD MODIFIER
		SelectItemAdapter<CardModel> cardModifierAdapter = new()
		{
			GetId = RuntimeItemId,
			GetName = card => CardPrinter.FormatCardTitle(card),
			GetSearchText = card => $"{card.Id} {CardPrinter.FormatCardTitle(card)} {card.TitleLocString} {card.Description}",
			CreateView = CardPrinter.CreateCardGridItem,
			ViewReady = (_, view) => CardPrinter.RefreshCardVisuals(view),
			UpdateView = (_, view, state) => CardPrinter.UpdateCardGridItem(view, state),
			BindActivation = (_, view, activate) => CardPrinter.BindCardActivation(view, activate)
		};

		void BuildCardModifierScreen(SelectScreenBuilder<CardModel> builder)
		{
			builder.Options(new SelectScreenOptions { SelectionMode = SelectSelectionMode.None });
			builder.Materialization(SelectMaterializationMode.Lazy);
			builder.Layout(5, NCard.defaultSize * NCardHolder.smallScale, 32, 40, paddingLeft: 0f, paddingTop: 200f, paddingRight: 0f);
			builder.ActionButton(
				"upgrade_all", LocMan.SScreenLoc("UPGRADE_ALL", "Upgrade All"),
				screen =>
				{
					HandleUpgradeAllDeckCards(screen);
					screen.RefreshItemsPreservingViews(CommonHelpers.GetLocalDeckCards(), cardModifierAdapter, animateRelayout: true);
				}, CommonHelpers.LoadActionButtonIcon("CardModifier.png"));
		}

		CreateAndAddDynamicLoadoutItem(CommonHelpers.GetLocalDeckCards,
			cardModifierAdapter,
			BuildCardModifierScreen,
			HandleUpgradeCardActivatedAsync,
			"CardModifier.png",
			"Card Modifier",
			"Modifies cards in your current deck.");
				
		//07 - EVENTFUL COMPASS
		EventfulCompass.Initialize();
		//08 - POWER GIVER
		CreateAndAddPowerGiverItem(
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

	private void CreateAndAddPowerGiverItem(
		string textureFileName,
		string title,
		string description)
	{
		var item = new NLoadoutPanelItem(textureFileName, title, description);
		var scene = GD.Load<PackedScene>("res://UI/Screens/GenericSelectScreen.tscn");
		var screen = scene.Instantiate<NGenericSelectScreen>();
		bool showPowerGiverFavoritesOnly = PowerGiverStateService.HasFavorites();
		CommonHelpers.LastActionCaptureSession? captureSession = null;

		SelectItemAdapter<PowerModel> adapter = new()
		{
			GetId = PowerId,
			GetName = CommonHelpers.FormatPowerTitle,
			GetSearchText = power => $"{power.Id} {CommonHelpers.FormatPowerTitle(power)} {power.Description}",
			CreateView = (power, _) => CreatePowerGridItem(
				power,
				PowerGiverStateService.GetCounter(PowerId(power)),
				PowerGiverStateService.IsFavorite(PowerId(power)) && !showPowerGiverFavoritesOnly),
			UpdateView = (power, view, _) => UpdatePowerGridItem(view, power, showPowerGiverFavoritesOnly),
			BindActivation = (power, view, _) => BindPowerGiverActivation(
				screen,
				power,
				view,
				entry => captureSession?.Add([entry]))
		};

		void ConfigurePowerGiverScreen(NGenericSelectScreen target, bool resetFavoriteMode = true)
		{
			PowerGiverStateService.EnsureLoaded();
			if (resetFavoriteMode)
				showPowerGiverFavoritesOnly = PowerGiverStateService.HasFavorites();

			target.Configure(ModelDb.AllPowers, adapter, builder =>
			{
				builder.Options(new SelectScreenOptions { SelectionMode = SelectSelectionMode.None });
				builder.Materialization(SelectMaterializationMode.Eager);
				builder.Layout(5, new Vector2(220f, 104f), 24, 24, fixedSlots: false);
				builder.CustomVisibilityPredicate(power => !showPowerGiverFavoritesOnly || PowerGiverStateService.IsFavorite(PowerId(power)));
				builder.FilterGroup("type", LocMan.SScreenLoc("FILTER_GROUP_TYPE", "Type"));
				builder.Filter("buff", LocMan.SScreenLoc("POWER_TYPE_BUFF", "Buff"), power => power.Type == PowerType.Buff, "type");
				builder.Filter("debuff", LocMan.SScreenLoc("POWER_TYPE_DEBUFF", "Debuff"), power => power.Type == PowerType.Debuff, "type");
				builder.Filter("type_none", LocMan.SScreenLoc("NONE", "None"), power => power.Type == PowerType.None, "type");
				builder.FilterGroup("stack", LocMan.SScreenLoc("FILTER_GROUP_STACK", "Stack"));
				builder.Filter("stack_none", LocMan.SScreenLoc("NONE", "None"), power => power.StackType == PowerStackType.None, "stack");
				builder.Filter("counter", LocMan.SScreenLoc("POWER_STACK_COUNTER", "Counter"), power => power.StackType == PowerStackType.Counter, "stack");
				builder.Filter("single", LocMan.SScreenLoc("POWER_STACK_SINGLE", "Single"), power => power.StackType == PowerStackType.Single, "stack");
				builder.Sorter("name", LocMan.SScreenLoc("SORT_NAME", "Name"), (a, b) => string.Compare(CommonHelpers.FormatPowerTitle(a), CommonHelpers.FormatPowerTitle(b), StringComparison.Ordinal), activeByDefault: true);
				builder.Sorter("id", LocMan.SScreenLoc("SORT_ID", "ID"), (a, b) => string.Compare(a.Id.Entry, b.Id.Entry, StringComparison.Ordinal));
				builder.Sorter("type", LocMan.GameLoc("gameplay_ui", "SORT_TYPE", LocMan.SScreenLoc("SORT_TYPE", "Type")), (a, b) => a.Type.CompareTo(b.Type));
			});
			AddPowerGiverSidebarDropdowns(
				target,
				() => showPowerGiverFavoritesOnly,
				value => showPowerGiverFavoritesOnly = value);
		}

		void RefreshPowerGiverScreenForOpen(NGenericSelectScreen target)
		{
			if (!target.IsConfiguredForCurrentLocale)
			{
				ConfigurePowerGiverScreen(target, resetFavoriteMode: false);
				return;
			}

			PowerGiverStateService.EnsureLoaded();
			target.SetCustomVisibilityPredicate(item =>
				item.UntypedModel is PowerModel power
				&& (!showPowerGiverFavoritesOnly || PowerGiverStateService.IsFavorite(PowerId(power))));
			target.GetNodeOrNull<NLoadoutDropdown>("Sidebar/MarginContainer/TopVBox/CustomControls/PowerGiverFavoritesDropdown")
				?.SetSelectedItem(showPowerGiverFavoritesOnly ? PowerGiverFavoriteModeFavorites : PowerGiverFavoriteModeAll);
			target.RefreshNow(resetScroll: true);
			target.RefreshCurrentItemStates();
		}

		ConfigurePowerGiverScreen(screen);
		screen.LocaleChanged += () =>
		{
			SelectScreenUiState state = screen.CaptureUiState();
			ConfigurePowerGiverScreen(screen, resetFavoriteMode: false);
			screen.RestoreUiState(state);
		};
		screen.Cancelled += NLoadoutPanelRoot.CloseTopLoadoutScreen;
		screen.Confirmed += _ => NLoadoutPanelRoot.CloseTopLoadoutScreen();
		screen.ScreenClosed += () =>
		{
			captureSession?.Commit();
			captureSession = null;
		};
		item.BoundScreen = screen;
		item.QuickAction = ReplayPowerGiverLastActionAsync;
		item.AfterOpen = _ => captureSession = new CommonHelpers.LastActionCaptureSession(LastActionService.PowerGiverKey);
		item.BeforeOpen = target =>
		{
			RefreshPowerGiverScreenForOpen(target);
		};
		_itemsContainer.AddChild(item);
	}

	private void CreateAndAddDynamicLoadoutItem<TModel>(
		Func<IReadOnlyList<TModel>> getModels,
		SelectItemAdapter<TModel> adapter,
		Action<SelectScreenBuilder<TModel>> builder,
		SelectActivationHandler onActivated,
		string textureFileName,
		string title,
		string description)
	{
		var item = new NLoadoutPanelItem(textureFileName, title, description);
		var scene = GD.Load<PackedScene>("res://UI/Screens/GenericSelectScreen.tscn");
		var screen = scene.Instantiate<NGenericSelectScreen>();
		bool activationInFlight = false;
		object? configuredRunState = null;

		void ConfigureCurrentModels(NGenericSelectScreen target, bool preserveViews = false)
		{
			object? currentRunState = GetCurrentDynamicRunStateIdentity();
			IReadOnlyList<TModel> models = getModels();
			// if (models.Count == 0)
			// 	LogEmptyDynamicScreen(title);

			if (preserveViews)
				target.ConfigurePreservingViews(models, adapter, builder, animateRelayout: true);
			else
				target.Configure(models, adapter, builder);

			if (!preserveViews)
				target.RequestDeferredVisibleRefresh();

			configuredRunState = currentRunState;
		}

		void RefreshCurrentModels(NGenericSelectScreen target, bool animateRelayout = false, bool resetScroll = false)
		{
			object? currentRunState = GetCurrentDynamicRunStateIdentity();
			target.RefreshItemsPreservingViews(getModels(), adapter, animateRelayout, resetScroll);
			configuredRunState = currentRunState;
		}

		void RefreshDynamicScreenForOpen(NGenericSelectScreen target)
		{
			object? currentRunState = GetCurrentDynamicRunStateIdentity();
			if (!target.IsConfiguredForCurrentLocale || !ReferenceEquals(configuredRunState, currentRunState))
			{
				ConfigureCurrentModels(target);
				return;
			}

			RefreshCurrentModels(target, resetScroll: true);
		}

		ConfigureCurrentModels(screen);
		screen.LocaleChanged += () =>
		{
			SelectScreenUiState state = screen.CaptureUiState();
			ConfigureCurrentModels(screen, preserveViews: false);
			screen.RestoreUiState(state);
		};
		screen.Cancelled += NLoadoutPanelRoot.CloseTopLoadoutScreen;
		screen.Confirmed += _ => NLoadoutPanelRoot.CloseTopLoadoutScreen();
		screen.ItemActivated += (selectItem, state) =>
		{
			if (activationInFlight)
				return;

			activationInFlight = true;
			_ = HandleDynamicItemActivatedAsync(
				screen,
				selectItem,
				onActivated,
				target => RefreshCurrentModels(target, animateRelayout: true),
				() => activationInFlight = false);
		};

		item.BoundScreen = screen;
		item.BeforeOpen = RefreshDynamicScreenForOpen;

		_itemsContainer.AddChild(item);
	}

	private static object? GetCurrentDynamicRunStateIdentity()
	{
		try
		{
			return RunManager.Instance.IsInProgress
				? RunManager.Instance.DebugOnlyGetState()
				: null;
		}
		catch (Exception exception)
		{
			GD.PushWarning($"LoadoutPanel: could not resolve current run state. {exception.Message}");
			return null;
		}
	}

	private static async Task HandleDynamicItemActivatedAsync(
		NGenericSelectScreen screen,
		IGenericSelectItem selectItem,
		SelectActivationHandler onActivated,
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

	private static string RuntimeItemId(AbstractModel model)
	{
		return $"{model.Id}:{RuntimeHelpers.GetHashCode(model)}";
	}

	private static async Task<IReadOnlyList<LastActionEntry>> HandleAddRelicActivatedAsync(NGenericSelectScreen screen, IGenericSelectItem selectItem)
	{
		if (selectItem.UntypedModel is not RelicModel canonicalRelic)
			return [];

		int obtained = await ObtainRelicCopiesAsync(canonicalRelic, screen.GetCurrentActivationMultiplier(), selectItem.Id);

		return obtained > 0
			?
			[
				new LastActionEntry
				{
					Kind = LastActionService.AddRelicKind,
					ContentId = canonicalRelic.Id.ToString(),
					Amount = obtained
				}
			]
			: [];
	}

	private static async Task<int> ObtainRelicCopiesAsync(RelicModel canonicalRelic, int amount, string logId)
	{
		Player? localPlayer = CommonHelpers.GetLocalRunPlayer();
		if (localPlayer is null || amount <= 0)
			return 0;

		int obtained = 0;
		for (int i = 0; i < amount; i++)
		{
			try
			{
				await RelicCmd.Obtain(canonicalRelic.ToMutable(), localPlayer);
				obtained++;
			}
			catch (Exception exception)
			{
				GD.PushWarning($"LoadoutPanel: stopped adding relic '{logId}' after {obtained}/{amount} copies. {exception.Message}");
				break;
			}
		}

		return obtained;
	}

	private static async Task<IReadOnlyList<LastActionEntry>> HandleAddPotionActivatedAsync(NGenericSelectScreen screen, IGenericSelectItem selectItem)
	{
		if (selectItem.UntypedModel is not PotionModel canonicalPotion)
			return [];

		Player? localPlayer = CommonHelpers.GetLocalRunPlayer();
		if (localPlayer is null)
			return [];

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

		return [];
	}

	private static async Task<IReadOnlyList<LastActionEntry>> HandleRemoveCardActivatedAsync(NGenericSelectScreen _, IGenericSelectItem selectItem)
	{
		if (selectItem.UntypedModel is not CardModel card)
			return [];

		Player? localPlayer = CommonHelpers.GetLocalRunPlayer();
		if (localPlayer is null
		    || !LocalContext.IsMine(card)
		    || card.Pile?.Type != PileType.Deck
		    || !localPlayer.Deck.Cards.Contains(card))
		{
			return [];
		}

		await CardPileCmd.RemoveFromDeck(card);
		return [];
	}

	private static Task<IReadOnlyList<LastActionEntry>> HandleUpgradeCardActivatedAsync(NGenericSelectScreen screen, IGenericSelectItem selectItem)
	{
		if (selectItem.UntypedModel is not CardModel card)
			return Task.FromResult<IReadOnlyList<LastActionEntry>>([]);

		Player? localPlayer = CommonHelpers.GetLocalRunPlayer();
		if (localPlayer is null)
			return Task.FromResult<IReadOnlyList<LastActionEntry>>([]);

		
		int multiplier = screen.GetCurrentActivationMultiplier();
		for (int i = 0; i < multiplier; i++)
		{
			CardCmd.Upgrade(card, CardPreviewStyle.None);
		}

		if (selectItem.View is Control view)
		{
			PlayCardSmithFeedback(view);
			CardPrinter.RefreshCardVisuals(view);
		}

		return Task.FromResult<IReadOnlyList<LastActionEntry>>([]);
	}

	private static void HandleUpgradeAllDeckCards(NGenericSelectScreen _)
	{
		Player? localPlayer = CommonHelpers.GetLocalRunPlayer();
		if (localPlayer is null)
			return;
		int i = 0;
		foreach (CardModel card in localPlayer.Deck.Cards)
		{
			CardCmd.Upgrade(card, CardPreviewStyle.None);
			if (_.Items[i++].View is not Control view) continue;
			PlayCardSmithFeedback(view);
			CardPrinter.RefreshCardVisuals(view);
		}
		
	}

	private static async Task<IReadOnlyList<LastActionEntry>> HandleRemoveRelicActivatedAsync(NGenericSelectScreen _, IGenericSelectItem selectItem)
	{
		if (selectItem.UntypedModel is not RelicModel relic)
			return [];

		Player? localPlayer = CommonHelpers.GetLocalRunPlayer();
		if (localPlayer is null
		    || !LocalContext.IsMine(relic)
		    || !localPlayer.Relics.Contains(relic))
		{
			return [];
		}

		await RelicCmd.Remove(relic);
		return [];
	}

	private static async Task ReplayLoadoutBagLastActionAsync()
	{
		foreach (LastActionEntry entry in LastActionService.GetAction(LastActionService.LoadoutBagKey))
		{
			if (entry.Kind != LastActionService.AddRelicKind || entry.Amount <= 0)
				continue;

			RelicModel? relic = ResolveCanonicalRelic(entry.ContentId);
			if (relic is null)
			{
				GD.PushWarning($"LoadoutPanel: cannot replay relic action for unknown relic '{entry.ContentId}'.");
				continue;
			}

			await ObtainRelicCopiesAsync(relic, entry.Amount, entry.ContentId);
		}
	}

	private static Task ReplayPowerGiverLastActionAsync()
	{
		foreach (LastActionEntry entry in LastActionService.GetAction(LastActionService.PowerGiverKey))
		{
			if (entry.Kind != LastActionService.AdjustPowerKind || entry.Amount == 0)
				continue;

			PowerGiverTarget target = entry.Target ?? PowerGiverTarget.Player;
			if (!PowerGiverStateService.AdjustCounter(entry.ContentId, entry.Amount, target))
				GD.PushWarning($"LoadoutPanel: could not replay power action for '{entry.ContentId}'.");
		}

		return Task.CompletedTask;
	}

	private static RelicModel? ResolveCanonicalRelic(string relicId)
	{
		return ModelDb.AllRelics.FirstOrDefault(relic => CommonHelpers.ModelIdMatches(relic, relicId));
	}


	private static Control CreatePotionGridItem(PotionModel model)
	{
		NLabPotionHolder? holder = NLabPotionHolder.Create(model.ToMutable(), ModelVisibility.Visible);
		if (holder is null)
			return CommonHelpers.CreateTextModelGridItem(model, CommonHelpers.FormatPotionTitle(model), model.Id.Entry, LocMan.SScreenLoc("CATEGORY_POTION", "Potion"));

		holder.MouseFilter = MouseFilterEnum.Pass;
		holder.CustomMinimumSize = new Vector2(60f, 60f);
		return holder;
	}

	private static Control CreateRelicGridItem(RelicModel model)
	{
		NRelicCollectionEntry? holder = NRelicCollectionEntry.Create(model, ModelVisibility.Visible);
		if (holder is null)
			return CommonHelpers.CreateTextModelGridItem(model, CommonHelpers.FormatRelicTitle(model), model.Id.Entry, LocMan.SScreenLoc("CATEGORY_RELIC", "Relic"));

		holder.MouseFilter = MouseFilterEnum.Pass;
		holder.CustomMinimumSize = new Vector2(68f, 68f);
		return holder;
	}

	private static Control CreateOwnedRelicGridItem(RelicModel model)
	{
		NRelicBasicHolder? holder = NRelicBasicHolder.Create(model);
		if (holder is null)
			return CommonHelpers.CreateTextModelGridItem(model, CommonHelpers.FormatRelicTitle(model), model.Id.Entry, LocMan.SScreenLoc("CATEGORY_RELIC", "Relic"));

		holder.MouseFilter = MouseFilterEnum.Pass;
		holder.CustomMinimumSize = new Vector2(68f, 68f);
		return holder;
	}

	private static Control CreatePowerGridItem(PowerModel model, int selectedAmount = 0, bool isFavorite = false)
	{
		Texture2D? icon = null;
		if (ResourceLoader.Exists(model.IconPath))
			icon = model.Icon;

		Button button = CommonHelpers.CreateModelButton(new Vector2(220f, 104f));
		button.ClipContents = false;
		Panel favoriteGlow = CreateFavoriteGlow(button.CustomMinimumSize, isFavorite);
		button.AddChild(favoriteGlow);

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

		MegaLabel nameLabel = CommonHelpers.CreateButtonLabel(
			"PowerName", CommonHelpers.FormatPowerTitle(model),
			new Vector2(82f, 8f),
			new Vector2(126f, 78f),
			18,
			HorizontalAlignment.Center,
			StsColors.cream);
		ConfigureWrappingPowerName(nameLabel);
		button.AddChild(nameLabel);

		MegaLabel amountLabel = CreatePowerAmountLabel(model, selectedAmount);
		button.AddChild(amountLabel);

		CommonHelpers.AttachHoverTips(button, model.HoverTips);
		return button;
	}

	private static Panel CreateFavoriteGlow(Vector2 size, bool visible)
	{
		StyleBoxFlat style = new()
		{
			BgColor = new Color(1f, 0.78f, 0.08f, 0.09f),
			BorderColor = new Color(1f, 0.82f, 0.08f, 0.92f),
			CornerRadiusTopLeft = 4,
			CornerRadiusTopRight = 4,
			CornerRadiusBottomLeft = 4,
			CornerRadiusBottomRight = 4,
			BorderWidthLeft = 3,
			BorderWidthTop = 3,
			BorderWidthRight = 3,
			BorderWidthBottom = 3
		};

		Panel panel = new()
		{
			Name = "FavoriteGlow",
			Visible = visible,
			MouseFilter = MouseFilterEnum.Ignore,
			Position = new Vector2(-4f, -4f),
			Size = size + new Vector2(8f, 8f),
			CustomMinimumSize = size + new Vector2(8f, 8f)
		};
		panel.AddThemeStyleboxOverride("panel", style);
		return panel;
	}

	private static MegaLabel CreatePowerAmountLabel(PowerModel model, int selectedAmount)
	{
		MegaLabel amountLabel = CommonHelpers.CreateButtonLabel(
			"PowerAmount",
			selectedAmount != 0 ? selectedAmount.ToString() : string.Empty,
			new Vector2(160f, 72f),
			new Vector2(50f, 26f),
			22,
			HorizontalAlignment.Right,
			model.AmountLabelColor);
		amountLabel.Visible = selectedAmount != 0;
		return amountLabel;
	}

	private static void UpdatePowerGridItem(Control view, PowerModel model, bool favoritesOnly)
	{
		string powerId = PowerId(model);
		int selectedAmount = PowerGiverStateService.GetCounter(powerId);
		if (view.GetNodeOrNull<MegaLabel>("PowerAmount") is { } amountLabel)
		{
			amountLabel.Text = selectedAmount != 0 ? selectedAmount.ToString() : string.Empty;
			amountLabel.Visible = selectedAmount != 0;
		}

		if (view.GetNodeOrNull<CanvasItem>("FavoriteGlow") is { } favoriteGlow)
			favoriteGlow.Visible = !favoritesOnly && PowerGiverStateService.IsFavorite(powerId);
	}

	private static void ConfigureWrappingPowerName(MegaLabel label)
	{
		label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
		label.TextOverrunBehavior = TextServer.OverrunBehavior.NoTrimming;
		label.AutoSizeEnabled = true;
		label.MinFontSize = 13;
		label.MaxFontSize = 18;
		label.AddThemeFontSizeOverride("font_size", label.MaxFontSize);
	}

	private static void PlayCardSmithFeedback(Control view)
	{
		if (!GodotObject.IsInstanceValid(view) || !CommonHelpers.TryFindDescendantOrSelf(view, out NGridCardHolder? holder))
			return;

		if (holder!.CardNode is not NCard cardNode || !GodotObject.IsInstanceValid(cardNode))
			return;

		NCardSmithVfx? smithVfx = NCardSmithVfx.Create(cardNode);
		if (smithVfx is null)
			return;

		Node? host = NLoadoutPanelRoot.Instance?.HoverTipLayer;
		if (host is null)
		{
			smithVfx.QueueFree();
			return;
		}

		host.AddChildSafely(smithVfx);
	}

	private static bool BindRelicActivation(Control view, Action activate)
	{
		if (CommonHelpers.TryFindDescendantOrSelf(view, out NRelicCollectionEntry? collectionEntry))
		{
			collectionEntry!.Connect(NClickableControl.SignalName.Released, Callable.From<NRelicCollectionEntry>(_ => activate()));
			return true;
		}

		if (CommonHelpers.TryFindDescendantOrSelf(view, out NRelicBasicHolder? basicHolder))
		{
			basicHolder!.Connect(NClickableControl.SignalName.Released, Callable.From<NRelicBasicHolder>(_ => activate()));
			return true;
		}

		return false;
	}

	private static bool BindPowerGiverActivation(
		NGenericSelectScreen screen,
		PowerModel power,
		Control view,
		Action<LastActionEntry>? recordLastAction = null)
	{
		string powerId = PowerId(power);
		view.GuiInput += input =>
		{
			if (input is not InputEventMouseButton mouseButton || mouseButton.Pressed)
				return;

			if (mouseButton.ButtonIndex != MouseButton.Left && mouseButton.ButtonIndex != MouseButton.Right)
				return;

			if (mouseButton.AltPressed || Input.IsKeyPressed(Key.Alt))
			{
				PowerGiverStateService.ToggleFavorite(powerId);
				screen.RefreshNow();
				view.AcceptEvent();
				return;
			}

			int multiplier = screen.GetCurrentActivationMultiplier();
			int delta = mouseButton.ButtonIndex == MouseButton.Right ? -multiplier : multiplier;
			PowerGiverTarget target = PowerGiverStateService.SelectedTarget;
			if (PowerGiverStateService.AdjustCounter(powerId, delta, target))
			{
				recordLastAction?.Invoke(new LastActionEntry
				{
					Kind = LastActionService.AdjustPowerKind,
					ContentId = powerId,
					Amount = delta,
					Target = target
				});
			}

			screen.RefreshCurrentItemStates();
			view.AcceptEvent();
		};

		return true;
	}

	private static void AddPowerGiverSidebarDropdowns(
		NGenericSelectScreen screen,
		Func<bool> getFavoritesOnly,
		Action<bool> setFavoritesOnly)
	{
		AddFavoritesModeDropdown(screen, "PowerGiverFavoritesDropdown", getFavoritesOnly, setFavoritesOnly);
		AddPowerGiverTargetDropdown(screen);
	}

	private static void AddFavoritesModeDropdown(
		NGenericSelectScreen screen,
		string name,
		Func<bool> getFavoritesOnly,
		Action<bool> setFavoritesOnly)
	{
		NLoadoutDropdown favoritesDropdown = new()
		{
			Name = name,
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
			CustomMinimumSize = new Vector2(256f, 52f)
		};
		favoritesDropdown.SetItems(LocMan.SScreenLoc("FILTER_GROUP_FAVORITES", "Favorites"),
			[
				new LoadoutDropdownOption(PowerGiverFavoriteModeAll, LocMan.SScreenLoc("ALL", "All")),
				new LoadoutDropdownOption(PowerGiverFavoriteModeFavorites, LocMan.SScreenLoc("FAVORITES_ONLY", "Favorites"))
			],
			getFavoritesOnly() ? PowerGiverFavoriteModeFavorites : PowerGiverFavoriteModeAll);
		favoritesDropdown.SelectedItemChanged += selectedId =>
		{
			setFavoritesOnly(selectedId == PowerGiverFavoriteModeFavorites);
			screen.RefreshNow(resetScroll: true);
		};

		screen.AddCustomSidebarControl(favoritesDropdown);
	}

	private static void AddPowerGiverTargetDropdown(NGenericSelectScreen screen)
	{
		NLoadoutDropdown dropdown = new()
		{
			Name = "PowerGiverTargetDropdown",
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
			CustomMinimumSize = new Vector2(256f, 52f)
		};
		dropdown.SetItems(LocMan.SScreenLoc("POWER_GIVER_TARGET", "Target"),
			[
				new LoadoutDropdownOption(PowerGiverTarget.Player.ToString(), LocMan.SScreenLoc("POWER_GIVER_TARGET_PLAYER", "Player")),
				new LoadoutDropdownOption(PowerGiverTarget.Monsters.ToString(), LocMan.SScreenLoc("POWER_GIVER_TARGET_MONSTERS", "Monsters"))
			],
			PowerGiverStateService.SelectedTarget.ToString());
		dropdown.SelectedItemChanged += selectedId =>
		{
			if (Enum.TryParse(selectedId, ignoreCase: true, out PowerGiverTarget target))
			{
				PowerGiverStateService.SetSelectedTarget(target);
				screen.RefreshCurrentItemStates();
			}
		};

		screen.AddCustomSidebarControl(dropdown);
	}

	private static void AddPotionPoolFilters(SelectScreenBuilder<PotionModel> builder)
	{
		IReadOnlyList<PotionPoolModel> pools = CommonHelpers.BuildOrderedPools(
			ModelDb.AllPotions.Select(potion => potion.Pool),
			ModelDb.AllCharacters.Where(character => character.IsPlayable).Select(character => character.PotionPool),
			pool => CommonHelpers.IsSharedPool(pool) && !CommonHelpers.IsInternalPool(pool));

		foreach (PotionPoolModel pool in pools)
		{
			PotionPoolModel localPool = pool;
			builder.Filter(CommonHelpers.PoolFilterId("potion", localPool), CommonHelpers.GetPoolLabel(localPool),
				potion => CommonHelpers.SamePool(potion.Pool, localPool),
				"class");
		}
	}

	private static void AddRelicPoolFilters(SelectScreenBuilder<RelicModel> builder)
	{
		IReadOnlyList<RelicPoolModel> pools = CommonHelpers.BuildOrderedPools(
			ModelDb.AllRelics.Select(relic => relic.Pool),
			ModelDb.AllCharacters.Where(character => character.IsPlayable).Select(character => character.RelicPool),
			pool => CommonHelpers.IsSharedPool(pool) && !CommonHelpers.IsInternalPool(pool));

		foreach (RelicPoolModel pool in pools)
		{
			RelicPoolModel localPool = pool;
			builder.Filter(CommonHelpers.PoolFilterId("relic", localPool), CommonHelpers.GetPoolLabel(localPool),
				relic => CommonHelpers.SamePool(relic.Pool, localPool),
				"class");
		}
	}


	private static int ComparePotionRarity(PotionModel left, PotionModel right)
	{
		int rarity = GetPotionRaritySortValue(left.Rarity).CompareTo(GetPotionRaritySortValue(right.Rarity));
		return rarity != 0 ? rarity : string.Compare(CommonHelpers.FormatPotionTitle(left), CommonHelpers.FormatPotionTitle(right), StringComparison.Ordinal);
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
		return rarity != 0 ? rarity : string.Compare(CommonHelpers.FormatRelicTitle(left), CommonHelpers.FormatRelicTitle(right), StringComparison.Ordinal);
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
			_ => SelectGroupHeader.Category(LocMan.SScreenLoc("OTHER", "Other"))
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
			AncientEventModel headerAncient = ancient;
			headers[groupKey] = new SelectGroupHeader(headerText.GetFormattedText(), () => CommonHelpers.TryGetValidTexture(headerAncient.RunHistoryIcon));
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
			: SelectGroupHeader.Category(LocMan.SScreenLoc("OTHER", "Other"));
	}

	private static string PowerId(PowerModel power)
	{
		return power.Id.ToString();
	}

	private static string FormatPowerCategory(PowerType type)
	{
		return type switch
		{
			PowerType.Buff => LocMan.SScreenLoc("POWER_TYPE_BUFF", "Buff"),
			PowerType.Debuff => LocMan.SScreenLoc("POWER_TYPE_DEBUFF", "Debuff"),
			_ => LocMan.SScreenLoc("NONE", "None")
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
