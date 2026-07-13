#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
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
using Loadout.Services.Actions;
using Loadout.Services.CardModification;
using Loadout.Services.LastActions;
using Loadout.Services.Loadouts;
using Loadout.Services.PowerGiver;
using Loadout.Services.RelicModification;
using Loadout.Services.Targets;
using Loadout.UI.Managers;

namespace  Loadout.UI;

public partial class NLoadoutPanel : Panel
{
	private const int MaxLoadoutItemInitAttempts = 120;
	private static bool DebugForceShownExpanded = false;
	private const string LoadoutBagTargetDropdownName = "LoadoutBagTargetDropdown";
	private const string LoadoutCauldronTargetKey = "loadout_cauldron";
	private const string LoadoutCauldronTargetDropdownName = "LoadoutCauldronTargetDropdown";
	private const string RemoveRelicTargetKey = "remove_relic";
	private const string RemoveRelicTargetDropdownName = "RemoveRelicTargetDropdown";
	private const string RemoveCardTargetKey = "remove_card";
	private const string RemoveCardTargetDropdownName = "RemoveCardTargetDropdown";


	[Export]
	public bool Shown = true;

	[Export]
	public new bool Hidden = true;

	[Export]
	public float SlideSpeed = 12f;
	
	private PanelContainer _panelContainer = null!;
	private MarginContainer _marginContainer = null!;
	private Control _itemsContainer = null!;
	private Control _toggleButton = null!;

	public static Control ItemsContainer = null!;

	private static NLoadoutPanel? _instance;
	private static bool _configPreviewVisible;

	private int _loadoutItemInitAttempts;
	private bool _loadoutItemsAdded;
	private string? _lastLoadoutItemInitError;
	private bool _runStartedConnected;
	private bool _isReady;
	private Control? _layoutParent;
	private NGenericSelectScreen? _loadoutBagRelicScreen;
	private readonly HashSet<string> _pendingPermanentRelicRefreshIds = new(StringComparer.Ordinal);
	
	public event Action? VisibilityStateChanged;

	public override void _EnterTree()
	{
		InitializePanelState();
	}

	// Called when the node enters the scene tree for the first time.
	public override void _Ready()
	{
		_instance = this;
		_toggleButton = GetNode<Control>("LoadoutPanelButton");
		_panelContainer = GetNode<PanelContainer>("PanelContainer");
		_marginContainer = GetNode<MarginContainer>("PanelContainer/MarginContainer");
		_itemsContainer = GetNode<Control>("PanelContainer/MarginContainer/VBoxContainer");
		_itemsContainer.AddThemeConstantOverride("separation", 0);
		ItemsContainer = _itemsContainer;
		
		BindRunHooks();
		LoadoutPanelAccessService.AccessChanged += OnLoadoutPanelAccessChanged;
		RelicModificationStateService.PermanentRelicDisplayChanged += OnPermanentRelicDisplayChanged;

		_layoutParent = GetParent<Control>();
		if (_layoutParent is not null)
			_layoutParent.Resized += OnPanelLayoutChanged;
		Resized += OnPanelLayoutChanged;

		_isReady = true;
		SnapToTargetPosition();
	}

	public override void _ExitTree()
	{
		UnbindRunHooks();
		LoadoutPanelAccessService.AccessChanged -= OnLoadoutPanelAccessChanged;
		RelicModificationStateService.PermanentRelicDisplayChanged -= OnPermanentRelicDisplayChanged;

		if (_layoutParent is not null && IsInstanceValid(_layoutParent))
			_layoutParent.Resized -= OnPanelLayoutChanged;
		Resized -= OnPanelLayoutChanged;
		_layoutParent = null;
		_isReady = false;
		SetProcess(false);

		if (_instance == this)
			_instance = null;
	}

	public void ToggleShown()
	{
		if (Hidden || (!_configPreviewVisible && !LoadoutPanelAccessService.CanLocalPlayerUsePanel()))
			return;

		SetPanelState(Hidden, !Shown);
	}

	public override void _Process(double delta)
	{
		UpdatePosition(delta);
	}
	
	private void InitializePanelState()
	{
		if (_configPreviewVisible)
		{
			SetPanelState(hidden: false, shown: true, notify: false);
			return;
		}

		if (!LoadoutPanelAccessService.CanLocalPlayerUsePanel())
		{
			SetPanelState(hidden: true, shown: false, notify: false);
			NLoadoutPanelRoot.Instance?.CloseAllScreens();
			return;
		}

		if (DebugForceShownExpanded)
		{
			SetPanelState(hidden: false, shown: true, notify: false);
			return;
		}

		SetPanelState(!IsPlayerInGame(), shown: false, notify: false);
	}

	private void BindRunHooks()
	{
		if (_runStartedConnected)
			return;

		RunManager.Instance.RunStarted += OnRunStarted;
		_runStartedConnected = true;
	}

	private void UnbindRunHooks()
	{
		if (!_runStartedConnected)
			return;

		RunManager.Instance.RunStarted -= OnRunStarted;
		_runStartedConnected = false;
	}

	private void OnRunStarted(RunState _)
	{
		if (!LoadoutPanelAccessService.CanLocalPlayerUsePanel())
		{
			SetPanelState(hidden: true, shown: false);
			NLoadoutPanelRoot.Instance?.CloseAllScreens();
			return;
		}

		if (DebugForceShownExpanded)
			return;

		SetPanelState(hidden: false, shown: false);
	}

	private void OnLoadoutPanelAccessChanged()
	{
		if (_configPreviewVisible)
		{
			InitializePanelState();
			SnapToTargetPosition();
			return;
		}

		if (!LoadoutPanelAccessService.CanLocalPlayerUsePanel())
		{
			SetPanelState(hidden: true, shown: false);
			NLoadoutPanelRoot.Instance?.CloseAllScreens();
			return;
		}

		InitializePanelState();
		SnapToTargetPosition();
	}

	private void OnRunCleanedUp()
	{
		if (DebugForceShownExpanded || _configPreviewVisible)
			return;

		SetPanelState(hidden: true, shown: false);
		NLoadoutPanelRoot.Instance?.CloseAllScreens();
	}

	public static void NotifyRunCleanedUp()
	{
		if (_instance is null || !IsInstanceValid(_instance))
			return;

		_instance.OnRunCleanedUp();
	}

	public static void SetConfigPreviewVisible(bool visible)
	{
		_configPreviewVisible = visible;
		if (_instance is null || !IsInstanceValid(_instance))
			return;

		_instance.InitializePanelState();
		_instance.SnapToTargetPosition();
	}

	private void SetPanelState(bool hidden, bool shown, bool notify = true)
	{
		if (Hidden == hidden && Shown == shown)
			return;

		Hidden = hidden;
		Shown = shown;

		if (_isReady)
			StartSlideAnimation();

		if (notify)
			VisibilityStateChanged?.Invoke();
	}

	private static bool IsPlayerInGame()
	{
		try
		{
			return RunManager.Instance.IsInProgress;
		}
		catch (Exception exception)
		{
			GD.PushWarning($"LoadoutPanel: could not resolve run state for panel visibility. {exception.Message}");
			return false;
		}
	}

	private void UpdatePosition(double delta)
	{
		Vector2 target = GetTargetPosition();
		if (SlideSpeed <= 0f || Position.DistanceSquaredTo(target) <= 0.25f)
		{
			Position = target;
			SetProcess(false);
			return;
		}

		float weight = Mathf.Clamp((float)(SlideSpeed * delta), 0f, 1f);
		Position = Position.Lerp(target, weight);
	}

	private void StartSlideAnimation()
	{
		if (!_isReady || !IsInsideTree())
			return;

		Vector2 target = GetTargetPosition();
		if (Position.DistanceSquaredTo(target) <= 0.25f)
		{
			Position = target;
			SetProcess(false);
			return;
		}

		SetProcess(true);
	}

	private void SnapToTargetPosition()
	{
		Position = GetTargetPosition();
		SetProcess(false);
	}

	private void OnPanelLayoutChanged()
	{
		if (!_isReady)
			return;

		if (IsProcessing())
			StartSlideAnimation();
		else
			SnapToTargetPosition();
	}

	private Vector2 GetTargetPosition()
	{
		Vector2 target = Position;
		target.Y = (GetParent<Control>().Size.Y - Size.Y) / 2f;
		target.X = Hidden ? GetHiddenXOffset() : Shown ? 0 : -Size.X;
		return target;
	}

	private float GetHiddenXOffset()
	{
		float rightEdge = Size.X;
		if (_toggleButton is not null && IsInstanceValid(_toggleButton))
			rightEdge = Mathf.Max(rightEdge, _toggleButton.Position.X + _toggleButton.Size.X);

		return -rightEdge;
	}

	private void AddLoadoutItems()
	{
		
		//01 - LOADOUT BAG
		NLoadoutPanelItem loadoutBagItem = CommonHelpers.CreateAndAddLoadoutItem(
			ModelDb.AllRelics,
			new SelectItemAdapter<RelicModel>
			{
				GetId = relic => relic.Id.ToString(),
				GetName = relic => CommonHelpers.FormatRelicTitle(relic),
				GetSearchText = relic => $"{relic.Id} {CommonHelpers.FormatRelicTitle(relic)} {relic.DynamicDescription}",
				CreateView = (relic, _) => CreateRelicGridItem(relic),
				ViewReady = (relic, view) => RefreshRelicGridItem(view, relic),
				UpdateView = (relic, view, _) => RefreshRelicGridItem(view, relic),
				BindActivationWithCleanup = (_, view, activate) => BindRelicActivationWithCleanup(view, activate)
			}, builder =>
			{
				builder.Options(new SelectScreenOptions { SelectionMode = SelectSelectionMode.None });
				builder.Materialization(SelectMaterializationMode.Lazy);
				builder.Layout(10, new Vector2(68f, 68f), 32, 32);
				builder.FilterGroup("class", LocMan.Loc("FILTER_GROUP_CLASS", "Class"));
				AddRelicPoolFilters(builder);
				builder.FilterGroup("rarity", LocMan.Loc("FILTER_GROUP_RARITY", "Rarity"));
				CommonHelpers.AddEnumFilters(builder, "rarity", (RelicModel relic) => relic.Rarity, RelicRarity.None);
				CommonHelpers.AddModFilters(builder, ModelDb.AllRelics);
				builder.Sorter("name", LocMan.Loc("SORT_NAME", "Name"), (a, b) => string.Compare(CommonHelpers.FormatRelicTitle(a), CommonHelpers.FormatRelicTitle(b), StringComparison.Ordinal));
				builder.Sorter("id", LocMan.Loc("SORT_ID", "ID"), (a, b) => string.Compare(a.Id.Entry, b.Id.Entry, StringComparison.Ordinal));
				builder.Sorter("rarity", LocMan.GameLoc("gameplay_ui", "SORT_RARITY", LocMan.Loc("SORT_RARITY", "Rarity")), CompareRelicRarity, activeByDefault: true);
				RelicGroupingData relicGroupingData = BuildRelicGroupingData();
				builder.GroupBySorter(
					"rarity",
					relic => GetRelicGroupKey(relic, relicGroupingData),
					key => GetRelicGroupHeader(key, relicGroupingData),
					relicGroupingData.GroupOrder,
					relicGroupingData.DescendingGroupOrder);
			}, screen =>
			{
				LoadoutTargetService.UpsertTargetDropdown(
					screen,
					LoadoutBagTargetDropdownName,
					LastActionService.LoadoutBagKey,
					LoadoutTargetMode.AllPlayersAndPlayers);
				RefreshPendingPermanentRelics(screen);
			},
			"LoadoutBag.png",
			LocMan.Loc("LOADOUTBAG_TITLE", "Loadout Bag"),
			LocMan.Loc("LOADOUTBAG_DESC", "Right-click this relic to obtain any relic you want. Ctrl x5, Shift x10. Ctrl + right click to repeat the last action."),
			HandleAddRelicActivatedAsync,
			LastActionService.LoadoutBagKey,
			ReplayLoadoutBagLastActionAsync);
		_loadoutBagRelicScreen = loadoutBagItem.BoundScreen;
		_loadoutBagRelicScreen.VisibilityChanged += () =>
		{
			if (_loadoutBagRelicScreen?.IsVisibleInTree() == true)
				RefreshPendingPermanentRelics(_loadoutBagRelicScreen);
		};
		//02 - TRASH BIN
		CommonHelpers.CreateAndAddDynamicLoadoutItem(GetSelectedTargetRelics,
			new SelectItemAdapter<LoadoutOwnedItem<RelicModel>>
			{
				GetId = item => CommonHelpers.OwnedItemId(item),
				GetName = item => CommonHelpers.FormatRelicTitle(item.Model),
				GetSearchText = item => $"{item.Model.Id} {CommonHelpers.FormatRelicTitle(item.Model)} {item.Model.DynamicDescription}",
				CreateView = (item, _) => CreateOwnedRelicGridItem(item.Model),
				BindActivationWithCleanup = (_, view, activate) => BindRelicActivationWithCleanup(view, activate)
			},
			builder =>
			{
				builder.Options(new SelectScreenOptions { SelectionMode = SelectSelectionMode.None });
				builder.Materialization(SelectMaterializationMode.Lazy);
				builder.Layout(10, new Vector2(68f, 68f), 32, 32);
				builder.ActionButton(
					"remove_all_relics",
					LocMan.Loc("REMOVE_ALL_RELICS", "Remove All Relics"),
					HandleRemoveAllRelics,
					CommonHelpers.LoadActionButtonIcon("TrashBin.png"));
			},
			HandleRemoveRelicActivatedAsync,
			"TrashBin.png",
			LocMan.Loc("THEBIN_TITLE", "The Bin"),
			LocMan.Loc("THEBIN_DESC", "Right-click this relic to obliterate any relic you want."),
			(screen, refresh) => LoadoutTargetService.UpsertTargetDropdown(
				screen,
				RemoveRelicTargetDropdownName,
				RemoveRelicTargetKey,
				LoadoutTargetMode.PlayersOnly,
				refresh));
		//03 - LOADOUT CAULDRON
		CommonHelpers.CreateAndAddLoadoutItem(
			ModelDb.AllPotions,
			new SelectItemAdapter<PotionModel>
			{
				GetId = potion => potion.Id.ToString(),
				GetName = potion => CommonHelpers.FormatPotionTitle(potion),
				GetSearchText = potion => $"{potion.Id} {CommonHelpers.FormatPotionTitle(potion)} {potion.DynamicDescription}",
				CreateView = (potion, _) => CreatePotionGridItem(potion),
				BindActivationWithCleanup = (_, view, activate) => CommonHelpers.BindGuiReleaseActivationWithCleanup(view, activate)
			}, builder =>
			{
				builder.Options(new SelectScreenOptions { SelectionMode = SelectSelectionMode.None });
				builder.Materialization(SelectMaterializationMode.Lazy);
				builder.Layout(10, new Vector2(60f, 60f), 32, 32);
				builder.FilterGroup("class", LocMan.Loc("FILTER_GROUP_CLASS", "Class"));
				AddPotionPoolFilters(builder);
				builder.FilterGroup("rarity", LocMan.Loc("FILTER_GROUP_RARITY", "Rarity"));
				CommonHelpers.AddEnumFilters(builder, "rarity", (PotionModel potion) => potion.Rarity, PotionRarity.None);
				CommonHelpers.AddModFilters(builder, ModelDb.AllPotions);
				builder.Sorter("name", LocMan.Loc("SORT_NAME", "Name"), (a, b) => string.Compare(CommonHelpers.FormatPotionTitle(a), CommonHelpers.FormatPotionTitle(b), StringComparison.Ordinal));
				builder.Sorter("id", LocMan.Loc("SORT_ID", "ID"), (a, b) => string.Compare(a.Id.Entry, b.Id.Entry, StringComparison.Ordinal));
				builder.Sorter("rarity", LocMan.GameLoc("gameplay_ui", "SORT_RARITY", LocMan.Loc("SORT_RARITY", "Rarity")), ComparePotionRarity, activeByDefault: true);
				builder.GroupBySorter(
					"rarity",
					GetPotionGroupKey,
					GetPotionGroupHeader,
					PotionGroupOrder,
					PotionGroupOrder.Reverse());
			}, screen => LoadoutTargetService.UpsertTargetDropdown(
				screen,
				LoadoutCauldronTargetDropdownName,
				LoadoutCauldronTargetKey,
				LoadoutTargetMode.AllPlayersAndPlayers),
			"LoadoutCauldron.png",
			LocMan.Loc("LOADOUTCAULDRON_TITLE", "The Cauldron"),
			LocMan.Loc("LOADOUTCAULDRON_DESC", "Right-click this relic to obtain any potion you want. Ctrl + right click to repeat the last action."),
			HandleAddPotionActivatedAsync);

		//04 - CARD PRINTER
		CardPrinter.Initialize();
		//05 - CARD SHREDDER
		CommonHelpers.CreateAndAddDynamicLoadoutItem(GetSelectedTargetDeckCardsForRemoval,
			new SelectItemAdapter<LoadoutOwnedItem<CardModel>>
			{
				GetId = item => CommonHelpers.OwnedItemId(item),
				GetName = item => CardPrinter.FormatCardTitle(item.Model),
				GetSearchText = item => $"{item.Model.Id} {CardPrinter.FormatCardTitle(item.Model)} {item.Model.TitleLocString} {item.Model.Description}",
				CreateView = (item, state) => CardPrinter.CreateCardGridItem(item.Model, state),
				ViewReady = (item, view) => CardPrinter.RefreshCardVisuals(view, item.Model),
				UpdateView = (item, view, state) =>
				{
					CardPrinter.ForceRefreshCardVisuals(view, item.Model);
					CardPrinter.UpdateCardGridItem(view, state);
				},
				BindActivationWithCleanup = (_, view, activate) => CardPrinter.BindCardActivationWithCleanup(view, activate)
			},
			builder =>
			{
				builder.Options(new SelectScreenOptions { SelectionMode = SelectSelectionMode.None });
				builder.Materialization(SelectMaterializationMode.Lazy);
				builder.Layout(5, NCard.defaultSize * NCardHolder.smallScale, 32, 40, paddingLeft: 0f, paddingTop: 200f, paddingRight: 0f);
				builder.ActionButton(
					"remove_all_cards",
					LocMan.Loc("REMOVE_ALL_CARDS", "Remove All Cards"),
					HandleRemoveAllCards,
					CommonHelpers.LoadActionButtonIcon("CardShredder.png"));
			},
			HandleRemoveCardActivatedAsync,
			"CardShredder.png",
			LocMan.Loc("CARDSHREDDER_TITLE", "Card Shredder"),
			LocMan.Loc("CARDSHREDDER_DESC", "Right-click this relic to obliterate any card you want; use during combat will also remove it from combat."),
			(screen, refresh) => LoadoutTargetService.UpsertTargetDropdown(
				screen,
				RemoveCardTargetDropdownName,
				RemoveCardTargetKey,
				LoadoutTargetMode.PlayersOnly,
				refresh),
			(_, _) => { });
		//06 - CARD MODIFIER
		CardModifier.Initialize();
		//07 - RELIC MODIFIER
		RelicModifier.Initialize();
				
		//08 - EVENTFUL COMPASS
		EventfulCompass.Initialize();
		//09 - POWER GIVER
		PowerGiver.Initialize();
		//10 - TILDE KEY
		TildeKey.Initialize();
		//11 - BOTTLE MONSTER
		BottledMonster.Initialize();
	}

	public bool TryInitializeLoadoutItems()
	{
		if (_loadoutItemsAdded)
			return true;

		if (_loadoutItemInitAttempts >= MaxLoadoutItemInitAttempts)
			return false;

		_loadoutItemInitAttempts++;

		try
		{
			VerifyStaticModelCollectionsAreReady();
			AddLoadoutItems();
			_loadoutItemsAdded = true;
			_lastLoadoutItemInitError = null;
			UpdatePanelHeight();
			return true;
		}
		catch (KeyNotFoundException exception)
		{
			_lastLoadoutItemInitError = exception.Message;

			if (_loadoutItemInitAttempts == 1)
			{
				GD.PushWarning(
					$"LoadoutPanel: ModelDb is not ready yet; startup preloader will retry. Missing key: {exception.Message}");
			}

			if (_loadoutItemInitAttempts >= MaxLoadoutItemInitAttempts)
			{
				GD.PushError(
					$"LoadoutPanel: failed to initialize loadout items after {_loadoutItemInitAttempts} frames. " +
					$"Last missing key: {exception.Message}");
			}

			return false;
		}
	}

	public bool LoadoutItemsInitialized => _loadoutItemsAdded;
	public int LoadoutItemInitAttempts => _loadoutItemInitAttempts;
	public bool LoadoutItemInitializationExhausted => _loadoutItemInitAttempts >= MaxLoadoutItemInitAttempts;
	public string? LastLoadoutItemInitError => _lastLoadoutItemInitError;

	public IReadOnlyList<SelectScreenPreloadEntry> GetSelectScreensForPreload()
	{
		if (!_loadoutItemsAdded || !IsInstanceValid(_itemsContainer))
			return Array.Empty<SelectScreenPreloadEntry>();

		List<SelectScreenPreloadEntry> screens = [];
		int itemIndex = 0;

		foreach (Node child in _itemsContainer.GetChildren())
		{
			if (child is not NLoadoutPanelItem item)
				continue;

			string fileName = Path.GetFileNameWithoutExtension(item.TextureFileName);
			string safeName = CommonHelpers.MakeSafeNodeName(
				string.IsNullOrWhiteSpace(fileName) ? $"Item{itemIndex}" : fileName);

			AddSelectScreenPreloadEntry(
				screens,
				item.BoundScreen,
				$"Loadout_{itemIndex:D2}_{safeName}_Primary");
			AddSelectScreenPreloadEntry(
				screens,
				item.AlternateBoundScreen,
				$"Loadout_{itemIndex:D2}_{safeName}_Alternate");

			itemIndex++;
		}

		return screens;
	}

	private static void AddSelectScreenPreloadEntry(
		ICollection<SelectScreenPreloadEntry> screens,
		NGenericSelectScreen? screen,
		string screenName)
	{
		if (screen is null || !GodotObject.IsInstanceValid(screen))
			return;

		screen.Name = new StringName(screenName);
		screens.Add(new SelectScreenPreloadEntry(screen.Name, screen));
	}

	private static void VerifyStaticModelCollectionsAreReady()
	{
		_ = ModelDb.AllRelics;
		_ = ModelDb.AllPotions;
		_ = ModelDb.AllCards;
		_ = ModelDb.AllEvents;
		_ = ModelDb.AllAncients;
		_ = ModelDb.AllPowers;
		_ = ModelDb.Monsters;
		_ = ModelDb.AllCharacters;
	}

	public readonly struct SelectScreenPreloadEntry
	{
		public SelectScreenPreloadEntry(StringName name, NGenericSelectScreen screen)
		{
			Name = name;
			Screen = screen;
		}

		public StringName Name { get; }
		public NGenericSelectScreen Screen { get; }
	}

	private static IReadOnlyList<LoadoutOwnedItem<RelicModel>> GetSelectedTargetRelics()
	{
		LoadoutTargetSelection target = LoadoutTargetService.GetSelected(RemoveRelicTargetKey, LoadoutTargetMode.PlayersOnly);
		return LoadoutTargetService.BuildOwnedItems(target, player => player.Relics.ToList());
	}

	private static IReadOnlyList<LoadoutOwnedItem<CardModel>> GetSelectedTargetDeckCardsForRemoval()
	{
		LoadoutTargetSelection target = LoadoutTargetService.GetSelected(RemoveCardTargetKey, LoadoutTargetMode.PlayersOnly);
		return LoadoutTargetService.BuildOwnedItems(target, player => player.Deck.Cards.ToList());
	}


	private static Task<IReadOnlyList<LastActionEntry>> HandleAddRelicActivatedAsync(NGenericSelectScreen screen, IGenericSelectItem selectItem)
	{
		if (selectItem.UntypedModel is not RelicModel canonicalRelic)
			return Task.FromResult<IReadOnlyList<LastActionEntry>>(Array.Empty<LastActionEntry>());

		int amount = screen.GetCurrentActivationMultiplier();
		LoadoutTargetSelection target = LoadoutTargetService.GetSelected(LastActionService.LoadoutBagKey, LoadoutTargetMode.AllPlayersAndPlayers);
		if (!LoadoutImmediateMutationService.RequestAddRelic(canonicalRelic.Id, amount, target))
			return Task.FromResult<IReadOnlyList<LastActionEntry>>(Array.Empty<LastActionEntry>());

		LastActionEntry entry = new()
		{
			Kind = LastActionService.AddRelicKind,
			ContentId = canonicalRelic.Id.ToString(),
			Amount = amount
		};
		entry.SetTargetSelection(target);
		return Task.FromResult<IReadOnlyList<LastActionEntry>>(new[] { entry });
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

	private static Task<IReadOnlyList<LastActionEntry>> HandleAddPotionActivatedAsync(NGenericSelectScreen screen, IGenericSelectItem selectItem)
	{
		if (selectItem.UntypedModel is not PotionModel canonicalPotion)
			return Task.FromResult<IReadOnlyList<LastActionEntry>>(Array.Empty<LastActionEntry>());

		LoadoutImmediateMutationService.RequestAddPotion(
			canonicalPotion.Id,
			screen.GetCurrentActivationMultiplier(),
			LoadoutTargetService.GetSelected(LoadoutCauldronTargetKey, LoadoutTargetMode.AllPlayersAndPlayers));

		return Task.FromResult<IReadOnlyList<LastActionEntry>>(Array.Empty<LastActionEntry>());
	}

	public static Task<IReadOnlyList<LastActionEntry>> HandleRemoveCardActivatedAsync(NGenericSelectScreen screen, IGenericSelectItem selectItem)
	{
		if (selectItem.UntypedModel is not LoadoutOwnedItem<CardModel> item)
			return Task.FromResult<IReadOnlyList<LastActionEntry>>(Array.Empty<LastActionEntry>());

		bool removed = LoadoutImmediateMutationService.RequestRemoveCard(item);

		if (!removed)
		{
			GD.PushWarning($"LoadoutPanel: failed to remove card '{item.Model.Id}' at index {item.Index}.");
			return Task.FromResult<IReadOnlyList<LastActionEntry>>(Array.Empty<LastActionEntry>());
		}

		return Task.FromResult<IReadOnlyList<LastActionEntry>>(Array.Empty<LastActionEntry>());
	}

	public static Task<IReadOnlyList<LastActionEntry>> HandleRemoveRelicActivatedAsync(
		NGenericSelectScreen _,
		IGenericSelectItem selectItem)
	{
		if (selectItem.UntypedModel is not LoadoutOwnedItem<RelicModel> item)
			return Task.FromResult<IReadOnlyList<LastActionEntry>>(Array.Empty<LastActionEntry>());

		if (!LoadoutImmediateMutationService.RequestRemoveRelic(item))
		{
			GD.PushWarning($"LoadoutPanel: failed to request relic removal for '{item.Model.Id}' at index {item.Index}.");
			return Task.FromResult<IReadOnlyList<LastActionEntry>>(Array.Empty<LastActionEntry>());
		}

		return Task.FromResult<IReadOnlyList<LastActionEntry>>(Array.Empty<LastActionEntry>());
	}

	private static void HandleRemoveAllCards(NGenericSelectScreen _)
	{
		LoadoutTargetSelection target = LoadoutTargetService.GetSelected(RemoveCardTargetKey, LoadoutTargetMode.PlayersOnly);
		if (!LoadoutImmediateMutationService.RequestRemoveAllCards(target))
			GD.PushWarning("LoadoutPanel: failed to request removal of all cards.");
	}

	private static void HandleRemoveAllRelics(NGenericSelectScreen _)
	{
		LoadoutTargetSelection target = LoadoutTargetService.GetSelected(RemoveRelicTargetKey, LoadoutTargetMode.PlayersOnly);
		if (!LoadoutImmediateMutationService.RequestRemoveAllRelics(target))
			GD.PushWarning("LoadoutPanel: failed to request removal of all relics.");
	}

	private static Task ReplayLoadoutBagLastActionAsync()
	{
		LoadoutTargetSelection fallbackTarget = LoadoutTargetService.GetSelected(LastActionService.LoadoutBagKey, LoadoutTargetMode.AllPlayersAndPlayers);
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

			if (!LoadoutImmediateMutationService.RequestAddRelic(
				    relic.Id,
				    entry.Amount,
				    entry.GetTargetSelection(fallbackTarget)))
			{
			    GD.PushWarning($"LoadoutPanel: could not replay relic action for '{entry.ContentId}'.");
		    }
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
			return CommonHelpers.CreateTextModelGridItem(model, CommonHelpers.FormatPotionTitle(model), model.Id.Entry, LocMan.Loc("CATEGORY_POTION", "Potion"));

		holder.MouseFilter = MouseFilterEnum.Pass;
		holder.CustomMinimumSize = new Vector2(60f, 60f);
		return holder;
	}

	private static Control CreateRelicGridItem(RelicModel model)
	{
		NRelicCollectionEntry? holder = NRelicCollectionEntry.Create(model, ModelVisibility.Visible);
		if (holder is null)
			return CommonHelpers.CreateTextModelGridItem(model, CommonHelpers.FormatRelicTitle(model), model.Id.Entry, LocMan.Loc("CATEGORY_RELIC", "Relic"));

		holder.MouseFilter = MouseFilterEnum.Pass;
		holder.CustomMinimumSize = new Vector2(68f, 68f);
		return holder;
	}

	private static void RefreshRelicGridItem(Control view, RelicModel model)
	{
		if (CommonHelpers.TryFindDescendantOrSelf(view, out NRelicCollectionEntry collectionEntry))
			collectionEntry.relic = model;

		if (!CommonHelpers.TryFindDescendantOrSelf(view, out NRelic relicView))
			return;

		relicView.Model = RelicModificationStateService.GetEffectivePermanentRelicForDisplay(model);
	}

	private void OnPermanentRelicDisplayChanged(ModelId relicId)
	{
		string key = relicId.ToString();
		if (string.IsNullOrWhiteSpace(key))
			return;

		_pendingPermanentRelicRefreshIds.Add(key);
		NGenericSelectScreen? screen = _loadoutBagRelicScreen;
		if (screen is null || !IsInstanceValid(screen) || !screen.IsInsideTree())
			return;

		Callable.From(() =>
		{
			if (!IsInstanceValid(screen) || !screen.IsInsideTree())
				return;

			screen.ForEachVisibleItemView((item, view) =>
			{
				if (item.UntypedModel is RelicModel relic && relic.Id.Equals(relicId))
					RefreshRelicGridItem(view, relic);
			});

			if (screen.IsVisibleInTree())
				RefreshPendingPermanentRelics(screen);
		}).CallDeferred();
	}

	private void RefreshPendingPermanentRelics(NGenericSelectScreen screen)
	{
		if (_pendingPermanentRelicRefreshIds.Count == 0
		    || !IsInstanceValid(screen)
		    || !screen.IsInsideTree()
		    || !screen.IsVisibleInTree())
		{
			return;
		}

		_pendingPermanentRelicRefreshIds.Clear();
		// Rarity is both presentation and layout state in this catalog, so a permanent
		// change must rerun the existing filters/grouping rather than only swap the icon.
		screen.RefreshNow(resetScroll: false);
	}

	internal static Control CreateOwnedRelicGridItem(RelicModel model)
	{
		NRelicBasicHolder? holder = NRelicBasicHolder.Create(model);
		if (holder is null)
			return CommonHelpers.CreateTextModelGridItem(model, CommonHelpers.FormatRelicTitle(model), model.Id.Entry, LocMan.Loc("CATEGORY_RELIC", "Relic"));

		holder.MouseFilter = MouseFilterEnum.Pass;
		holder.CustomMinimumSize = new Vector2(68f, 68f);
		return holder;
	}

	internal static Action? BindRelicActivationWithCleanup(Control view, Action activate)
	{
		if (CommonHelpers.TryFindDescendantOrSelf(view, out NRelicCollectionEntry collectionEntry))
		{
			Callable releasedCallable = Callable.From<NRelicCollectionEntry>(_ => activate());
			collectionEntry.Connect(NClickableControl.SignalName.Released, releasedCallable);
			return () =>
			{
				if (GodotObject.IsInstanceValid(collectionEntry)
				    && collectionEntry.IsConnected(NClickableControl.SignalName.Released, releasedCallable))
				{
					collectionEntry.Disconnect(NClickableControl.SignalName.Released, releasedCallable);
				}
			};
		}

		if (CommonHelpers.TryFindDescendantOrSelf(view, out NRelicBasicHolder basicHolder))
		{
			Callable releasedCallable = Callable.From<NRelicBasicHolder>(_ => activate());
			basicHolder.Connect(NClickableControl.SignalName.Released, releasedCallable);
			return () =>
			{
				if (GodotObject.IsInstanceValid(basicHolder)
				    && basicHolder.IsConnected(NClickableControl.SignalName.Released, releasedCallable))
				{
					basicHolder.Disconnect(NClickableControl.SignalName.Released, releasedCallable);
				}
			};
		}

		return CommonHelpers.BindGuiReleaseActivationWithCleanup(view, activate);
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
			_ => SelectGroupHeader.Category(LocMan.Loc("OTHER", "Other"))
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
			: SelectGroupHeader.Category(LocMan.Loc("OTHER", "Other"));
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
		SnapToTargetPosition();
	}
}
