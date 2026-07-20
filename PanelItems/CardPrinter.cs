using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Loadout.Helpers;
using Loadout.Services.Actions;
using Loadout.Services.CardModification;
using Loadout.Patches.Cards.CardModification;
using Loadout.Services.LastActions;
using Loadout.Services.Targets;
using Loadout.UI;
using Loadout.UI.Managers;
using Loadout.UI.Screens;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.UI;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Runs;

namespace Loadout.PanelItems;

public class CardPrinter
{
	private const string ViewUpgradesToggleId = "view_upgrades";
	private const string PreviewUpgradeMetaKey = "loadout_preview_upgrade";
	private const string TargetDropdownName = "CardPrinterTargetDropdown";
	private static string _currentCardFilterId;
	
    public static void Initialize()
    {
	    IReadOnlyList<CardModel> allCards = ModelDb.AllCards.ToList();
	    NGenericSelectScreen activeScreen = null;
	    HashSet<string> pendingCardRefreshIds = new(StringComparer.Ordinal);
	    SelectItemAdapter<CardModel> cardPrinterAdapter = new()
	    {
		    GetId = card => card.Id.ToString(),
		    GetName = card => FormatCardTitle(card),
		    GetSearchText = card => $"{card.Id} {FormatCardTitle(card)} {card.GetDescriptionForPile(PileType.None)}",
		    CreateView = CreateCardGridItem,
		    ViewReady = (card, view) => RefreshCardVisuals(view, card),
		    UpdateView = (card, view, state) =>
		    {
			    ForceRefreshCardVisuals(view, card);
			    UpdateCardGridItem(view, state);
		    },
		    BindActivationWithCleanup = (_, view, activate) => BindCardActivationWithCleanup(view, activate)
	    };

	    void BuildCardPrinterScreen(SelectScreenBuilder<CardModel> builder)
	    {
		    IReadOnlyList<CardModel> effectiveCards = allCards
			    .Select(CardModificationRuntime.GetPermanentCardForDisplay)
			    .ToList();

		    builder.Options(new SelectScreenOptions { SelectionMode = SelectSelectionMode.None });
		    builder.Materialization(SelectMaterializationMode.Lazy);
		    builder.Layout(5, NCard.defaultSize * NCardHolder.smallScale, 32, 40, paddingLeft: 0f, paddingTop: 200f, paddingRight: 0f);
		    builder.FilterGroup("class", LocMan.Loc("FILTER_GROUP_CLASS", "Class"));
		    AddCardPoolFilters(builder);
		    builder.FilterGroup("type", LocMan.GameLoc("gameplay_ui", "SORT_TYPE", LocMan.Loc("FILTER_GROUP_TYPE", "Type")));
		    AddCardTypeFilters(builder, effectiveCards);
		    builder.FilterGroup("rarity", LocMan.GameLoc("main_menu_ui", "CARD_LIBRARY_RARITY", LocMan.Loc("FILTER_GROUP_RARITY", "Rarity")));
		    AddCardRarityFilters(builder, effectiveCards);
		    AddCardCostFilterGroup(builder, effectiveCards);
		    AddCardKeywordFilterGroup(builder, effectiveCards);
		    AddCardTagFilterGroup(builder, effectiveCards);
		    CommonHelpers.AddModFilters(builder, allCards);
		    builder.Toggle(
			    ViewUpgradesToggleId,
			    LocMan.GameLoc("card_library", "VIEW_UPGRADES", LocMan.GameLoc("gameplay_ui", "VIEW_UPGRADES", "View Upgrades")),
			    checkedByDefault: false,
			    section: SelectSidebarSection.Bottom);
		    IReadOnlyList<CardPoolModel> librarySortPools = BuildOrderedCardPools();
		    builder.Sorter("library", LocMan.Loc("SORT_LIBRARY", "Library"), (a, b) => CompareCardLibraryOrder(a, b, librarySortPools), activeByDefault: true);
		    builder.Sorter("name", LocMan.GameLoc("gameplay_ui", "SORT_ALPHABET", LocMan.Loc("SORT_NAME", "Name")), (a, b) => string.Compare(FormatCardTitle(a), FormatCardTitle(b), StringComparison.Ordinal));
		    builder.Sorter("id", LocMan.Loc("SORT_ID", "ID"), (a, b) => string.Compare(a.Id.Entry, b.Id.Entry, StringComparison.Ordinal));
		    builder.Sorter("cost", LocMan.GameLoc("gameplay_ui", "SORT_COST", LocMan.Loc("SORT_COST", "Cost")), CompareEffectiveCardCost);
	    }

	    void BindCardPrinterSidebar(NGenericSelectScreen screen, bool applyDefaultClassFilter = true)
	    {
		    LoadoutTargetService.UpsertTargetDropdown(
			    screen,
			    TargetDropdownName,
			    LastActionService.CardPrinterKey,
			    LoadoutTargetMode.AllPlayersAndPlayers);

		    if (applyDefaultClassFilter)
			    ApplyCurrentCardClassFilter(screen);
	    }

	    void ConfigureCardPrinterSidebar(NGenericSelectScreen screen, bool applyDefaultClassFilter = true)
	    {
		    activeScreen = screen;
		    BindCardPrinterSidebar(screen, applyDefaultClassFilter);

		    if (pendingCardRefreshIds.Count > 0)
		    {
			    string[] cardIds = pendingCardRefreshIds.ToArray();
			    pendingCardRefreshIds.Clear();
			    RefreshVisibleCardPrinterCards(screen, cardIds);
		    }
	    }

	    void RefreshCardPrinterCard(ModelId cardId)
	    {
		    string cardKey = cardId.ToString();
		    if (string.IsNullOrWhiteSpace(cardKey))
			    return;

		    if (activeScreen is null || !GodotObject.IsInstanceValid(activeScreen))
		    {
			    pendingCardRefreshIds.Add(cardKey);
			    return;
		    }

		    if (!activeScreen.IsInsideTree() || !activeScreen.IsVisibleInTree())
		    {
			    pendingCardRefreshIds.Add(cardKey);
			    return;
		    }

		    RefreshVisibleCardPrinterCards(activeScreen, new[] { cardKey });
	    }

	    void RefreshVisibleCardPrinterCards(NGenericSelectScreen screen, IReadOnlyCollection<string> cardIds)
	    {
		    if (cardIds.Count == 0)
			    return;

		    Callable.From(() =>
		    {
			    if (!GodotObject.IsInstanceValid(screen) || !screen.IsInsideTree())
				    return;

			    if (!screen.IsVisibleInTree())
			    {
				    foreach (string cardId in cardIds)
					    pendingCardRefreshIds.Add(cardId);
				    return;
			    }

			    if (screen is NCardSelectScreen cardScreen)
			    {
				    bool layoutDirty = false;
				    foreach (string cardId in cardIds)
				    {
					    layoutDirty |= cardScreen.RefreshItemById(
						    cardId,
						    (item, view) =>
						    {
							    if (item.UntypedModel is CardModel card)
								    ForceRefreshCardVisuals(view, card);
						    },
						    refreshMetadata: true,
						    refreshLayout: false);
				    }

				    if (layoutDirty)
					    cardScreen.RefreshLayout(resetScroll: false, updateExistingViews: false);
				    return;
			    }

			    screen.ForEachVisibleItemView((item, view) =>
			    {
				    if (item.UntypedModel is CardModel card && MatchesCardRefreshId(card, cardIds))
					    ForceRefreshCardVisuals(view, card);
			    });
		    }).CallDeferred();
	    }

	    CardModificationRuntime.PermanentCardDisplayChanged += RefreshCardPrinterCard;

		CommonHelpers.CreateAndAddLoadoutItem(
			allCards,
			cardPrinterAdapter,
			BuildCardPrinterScreen,
			screen => ConfigureCardPrinterSidebar(screen),
			"CardPrinter.png",
			LocMan.Loc("CARDPRINTER_TITLE", "Card Printer"),
			LocMan.Loc("CARDPRINTER_DESC", "Right-click this relic to obtain any card you want; use during combat will also add it to your hand. Ctrl + right click to repeat the last action."),
			HandleAddCardActivatedAsync,
			LastActionService.CardPrinterKey,
			ReplayCardPrinterLastActionAsync,
			selectScreenScenePath: CommonHelpers.CardSelectScreenScenePath);
    }

    private static Task<IReadOnlyList<LastActionEntry>> HandleAddCardActivatedAsync(NGenericSelectScreen screen, IGenericSelectItem selectItem)
    {
	    if (selectItem.UntypedModel is not CardModel canonicalCard)
		    return Task.FromResult<IReadOnlyList<LastActionEntry>>(Array.Empty<LastActionEntry>());

	    int multiplier = screen.GetCurrentActivationMultiplier();
	    int upgradeCount = screen.IsToggleEnabled(ViewUpgradesToggleId) && canonicalCard.IsUpgradable ? 1 : 0;
	    LoadoutTargetSelection target = LoadoutTargetService.GetSelected(LastActionService.CardPrinterKey, LoadoutTargetMode.AllPlayersAndPlayers);
	    if (!LoadoutImmediateMutationService.RequestAddCard(canonicalCard.Id, multiplier, target, upgradeCount))
		    return Task.FromResult<IReadOnlyList<LastActionEntry>>(Array.Empty<LastActionEntry>());

	    LastActionEntry entry = new()
	    {
		    Kind = LastActionService.AddCardKind,
		    ContentId = canonicalCard.Id.ToString(),
		    Amount = multiplier,
		    UpgradeCount = upgradeCount
	    };
	    entry.SetTargetSelection(target);
	    return Task.FromResult<IReadOnlyList<LastActionEntry>>(new[] { entry });
    }

    private static Task ReplayCardPrinterLastActionAsync()
    {
	    LoadoutTargetSelection fallbackTarget = LoadoutTargetService.GetSelected(LastActionService.CardPrinterKey, LoadoutTargetMode.AllPlayersAndPlayers);
	    foreach (LastActionEntry entry in LastActionService.GetAction(LastActionService.CardPrinterKey))
	    {
		    if (entry.Kind != LastActionService.AddCardKind || entry.Amount <= 0)
			    continue;

		    CardModel card = ResolveCanonicalCard(entry.ContentId);
		    if (card is null)
		    {
			    GD.PushWarning($"LoadoutPanel: cannot replay card action for unknown card '{entry.ContentId}'.");
			    continue;
		    }

		    if (!LoadoutImmediateMutationService.RequestAddCard(
			        card.Id,
			        entry.Amount,
			        entry.GetTargetSelection(fallbackTarget),
			        entry.UpgradeCount))
		    {
			    GD.PushWarning($"LoadoutPanel: could not replay card action for '{entry.ContentId}'.");
		    }
	    }

	    return Task.CompletedTask;
    }

    public static Control CreateCardGridItem(CardModel model, SelectItemState state)
    {
	    CardModel displayModel = CardModificationRuntime.GetPermanentCardForDisplay(model);
	    var card = NCard.Create(displayModel);
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

	    holder.MouseFilter = Control.MouseFilterEnum.Pass;
	    holder.Scale = holder.SmallScale;
	    holder.CustomMinimumSize = NCard.defaultSize * holder.SmallScale;
	    ApplyCardUpgradePreview(holder, state);
	    return holder;
    }

    private static void AddCardPoolFilters(SelectScreenBuilder<CardModel> builder)
    {
	    IReadOnlyList<CardPoolModel> pools = BuildOrderedCardPools();

	    foreach (CardPoolModel pool in pools)
	    {
		    CardPoolModel localPool = pool;
			    builder.Filter(
			    CommonHelpers.PoolFilterId("card", localPool),
			    CommonHelpers.GetPoolLabel(localPool),
			    card => CommonHelpers.SamePool(CardModificationRuntime.GetPermanentCardForDisplay(card).Pool, localPool),
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
			    CommonHelpers.EnumFilterId("card_type", localType), GetCardTypeLabel(localType),
			    card => CardModificationRuntime.GetPermanentCardForDisplay(card).Type == localType,
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
			    CommonHelpers.EnumFilterId("card_rarity", localRarity), GetCardRarityLabel(localRarity),
			    card => CardModificationRuntime.GetPermanentCardForDisplay(card).Rarity == localRarity,
			    "rarity");
	    }
    }

    private static void AddCardKeywordFilterGroup(SelectScreenBuilder<CardModel> builder, IEnumerable<CardModel> cards)
    {
	    IReadOnlyList<CardKeyword> keywords = cards
		    .SelectMany(GetLocalCardKeywords)
		    .Where<CardKeyword>(keyword => keyword != CardKeyword.None)
		    .Distinct()
		    .OrderBy(keyword => Convert.ToInt32(keyword))
		    .ToList();

	    if (keywords.Count == 0)
		    return;

	    builder.FilterGroup("keyword", LocMan.Loc("FILTER_GROUP_KEYWORD", "Keyword"));
	    foreach (CardKeyword keyword in keywords)
	    {
		    CardKeyword localKeyword = keyword;
		    builder.Filter(
			    CommonHelpers.EnumFilterId("card_keyword", localKeyword), GetCardKeywordLabel(localKeyword),
			    card => Enumerable.Contains(GetLocalCardKeywords(CardModificationRuntime.GetPermanentCardForDisplay(card)), localKeyword),
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

	    builder.FilterGroup("tag", LocMan.Loc("FILTER_GROUP_TAG", "Tag"));
	    foreach (CardTag tag in tags)
	    {
		    CardTag localTag = tag;
		    builder.Filter(
			    CommonHelpers.EnumFilterId("card_tag", localTag), GetCardTagLabel(localTag),
			    card => CardModificationRuntime.GetPermanentCardForDisplay(card).Tags.Contains(localTag),
			    "tag");
	    }
    }
    
    private static void AddCardCostFilterGroup(SelectScreenBuilder<CardModel> builder, IEnumerable<CardModel> cards)
    {

	    builder.FilterGroup("energy_cost", LocMan.Loc("FILTER_GROUP_COST", "Cost"));
	    builder.Filter(
		    $"card_energy_cost_0", "0",
		    card => CardModificationRuntime.GetPermanentCardForDisplay(card).EnergyCost.Canonical == 0 && !CardModificationRuntime.GetPermanentCardForDisplay(card).EnergyCost.CostsX,
		    "energy_cost");
	    for (int i = 1; i < 3; i++)
	    {
		    int localI = i;
		    builder.Filter(
			    $"card_energy_cost_{localI}", localI.ToString(),
			    card => CardModificationRuntime.GetPermanentCardForDisplay(card).EnergyCost.Canonical == localI,
			    "energy_cost");
	    }
	    builder.Filter(
		    $"card_energy_cost_3+", "3+",
		    card => CardModificationRuntime.GetPermanentCardForDisplay(card).EnergyCost.Canonical >= 3,
		    "energy_cost");
	    builder.Filter(
		    $"card_energy_cost_X", "X",
		    card => CardModificationRuntime.GetPermanentCardForDisplay(card).EnergyCost.CostsX,
		    "energy_cost");
	    builder.Filter(
		    $"card_energy_cost_unplayable", CommonLoc.Unplayable,
		    card => CardModificationRuntime.GetPermanentCardForDisplay(card).EnergyCost.Canonical < 0,
		    "energy_cost");
    }

    public static IReadOnlyList<CardPoolModel> BuildOrderedCardPools()
    {
	    return CommonHelpers.BuildOrderedPools(
		    ModelDb.AllCards
			    .Select(CardModificationRuntime.GetPermanentCardForDisplay)
			    .Select(card => card.Pool),
		    ModelDb.AllCharacters.Where(character => character.IsPlayable).Select(character => character.CardPool),
		    pool => !CommonHelpers.IsInternalPool(pool));
    }

    public static string GetCardTypeLabel(CardType type)
    {
	    try
	    {
		    return type.ToLocString().GetFormattedText();
	    }
	    catch
	    {
		    return CommonHelpers.PrettifyEnumValue(type);
	    }
    }

    public static string GetCardRarityLabel(CardRarity rarity)
    {
	    try
	    {
		    return rarity.ToLocString().GetFormattedText();
	    }
	    catch
	    {
		    return CommonHelpers.PrettifyEnumValue(rarity);
	    }
    }

    public static string GetCardKeywordLabel(CardKeyword keyword)
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

	    return CommonHelpers.PrettifyEnumValue(keyword);
    }

    public static string GetCardTagLabel(CardTag tag)
    {
	    return CommonHelpers.PrettifyEnumValue(tag);
    }

    public static string FormatCardTitle(CardModel card)
    {
	    return card.Title;
    }

    public static void RefreshCardVisuals(Control view, CardModel sourceModel = null)
    {
	    if (!TryFindLiveCardHolder(view, out NGridCardHolder holder) || holder.CardNode is null || !GodotObject.IsInstanceValid(holder.CardNode))
		    return;

	    RefreshHolderModel(holder, sourceModel, forceReassign: false);

	    if (holder.CardModel is not null
	        && holder.CardModel.IsUpgradable
	        && holder.GetMeta(PreviewUpgradeMetaKey, false).AsBool())
	    {
		    holder.SetIsPreviewingUpgrade(true);
	    }
    }

    public static void ForceRefreshCardVisuals(Control view, CardModel sourceModel = null)
    {
	    if (!TryFindLiveCardHolder(view, out NGridCardHolder holder) || holder.CardNode is null || !GodotObject.IsInstanceValid(holder.CardNode))
		    return;

	    CardModel model = ResolveDisplayModel(sourceModel ?? holder.CardModel);
	    if (model is null)
		    return;

	    bool shouldPreviewUpgrade = model.IsUpgradable && holder.GetMeta(PreviewUpgradeMetaKey, false).AsBool();
	    RefreshHolderModel(holder, model, forceReassign: true);
	    holder.SetIsPreviewingUpgrade(shouldPreviewUpgrade);
    }

    public static void ReloadCardVisuals(Control view, CardModel sourceModel = null)
    {
	    if (!TryFindLiveCardHolder(view, out NGridCardHolder holder) || holder.CardNode is null || !GodotObject.IsInstanceValid(holder.CardNode))
		    return;

	    CardModel model = ResolveDisplayModel(sourceModel ?? holder.CardModel);
	    if (model is null)
		    return;

	    bool shouldPreviewUpgrade = model.IsUpgradable && holder.GetMeta(PreviewUpgradeMetaKey, false).AsBool();
	    holder.CardNode.Model = null;
	    holder.ReassignToCard(model, PileType.None, null, ModelVisibility.Visible);
	    holder.SetIsPreviewingUpgrade(shouldPreviewUpgrade);
    }

    public static void UpdateCardGridItem(Control view, SelectItemState state)
    {
	    if (!TryFindLiveCardHolder(view, out NGridCardHolder holder))
		    return;

	    ApplyCardUpgradePreview(holder, state);
    }

    private static void RefreshHolderModel(NGridCardHolder holder, CardModel sourceModel, bool forceReassign)
    {
	    CardModel model = ResolveDisplayModel(sourceModel ?? holder.CardModel);
	    if (model is null)
		    return;

	    if (forceReassign || !ReferenceEquals(holder.CardModel, model))
	    {
		    holder.ReassignToCard(model, PileType.None, null, ModelVisibility.Visible);
		    return;
	    }

	    holder.SetIsPreviewingUpgrade(false);
	    holder.CardNode?.UpdateVisuals(PileType.None, CardPreviewMode.Normal);
    }

    private static CardModel ResolveDisplayModel(CardModel model)
    {
	    return model is null
		    ? null
		    : CardModificationRuntime.GetPermanentCardForDisplay(model);
    }

    public static Action? BindCardActivationWithCleanup(Control view, Action activate)
    {
	    if (!TryFindLiveCardHolder(view, out NGridCardHolder holder))
		    return null;

	    Callable pressedCallable = Callable.From<NCardHolder>(_ => activate());
	    holder.Connect(NCardHolder.SignalName.Pressed, pressedCallable);
	    return () =>
	    {
		    if (GodotObject.IsInstanceValid(holder)
		        && holder.IsConnected(NCardHolder.SignalName.Pressed, pressedCallable))
		    {
			    holder.Disconnect(NCardHolder.SignalName.Pressed, pressedCallable);
		    }
	    };
    }

    public static Action? BindCardActivationWithCleanup(
	    Control view,
	    Action activate,
	    Action alternateActivate)
    {
	    if (!TryFindLiveCardHolder(view, out NGridCardHolder holder))
		    return null;

	    Callable pressedCallable = Callable.From<NCardHolder>(_ => activate());
	    Callable alternatePressedCallable = Callable.From<NCardHolder>(_ => alternateActivate());
	    holder.Connect(NCardHolder.SignalName.Pressed, pressedCallable);
	    holder.Connect(NCardHolder.SignalName.AltPressed, alternatePressedCallable);
	    return () =>
	    {
		    if (!GodotObject.IsInstanceValid(holder))
			    return;

		    if (holder.IsConnected(NCardHolder.SignalName.Pressed, pressedCallable))
			    holder.Disconnect(NCardHolder.SignalName.Pressed, pressedCallable);
		    if (holder.IsConnected(NCardHolder.SignalName.AltPressed, alternatePressedCallable))
			    holder.Disconnect(NCardHolder.SignalName.AltPressed, alternatePressedCallable);
	    };
    }

    public static bool BindCardActivation(Control view, Action activate)
    {
	    return BindCardActivationWithCleanup(view, activate) is not null;
    }

    public static bool BindCardActivation(Control view, Action activate, Action alternateActivate)
    {
	    return BindCardActivationWithCleanup(view, activate, alternateActivate) is not null;
    }

    private static bool TryFindLiveCardHolder(Control view, out NGridCardHolder holder)
    {
	    holder = null;
	    if (view is null || !GodotObject.IsInstanceValid(view))
		    return false;

	    return CommonHelpers.TryFindDescendantOrSelf(view, out holder)
	           && holder is not null
	           && GodotObject.IsInstanceValid(holder);
    }

    
    private static void ApplyCurrentCardClassFilter(NGenericSelectScreen screen)
    {
	    CardPoolModel currentCardPool = GetCurrentCharacterCardPool();
	    if (currentCardPool is null)
		    return;

	    string filterId = CommonHelpers.PoolFilterId("card", currentCardPool);
	    if (string.Equals(_currentCardFilterId, filterId, StringComparison.Ordinal))
		    return;

	    if (screen.SetExclusiveFilterSelection("class", filterId, resetScroll: true))
		    _currentCardFilterId = filterId;
    }

    private static bool MatchesCardRefreshId(CardModel card, IReadOnlyCollection<string> cardIds)
    {
	    return cardIds.Contains(card.Id.ToString())
	           || cardIds.Contains(card.Id.Entry);
    }
    
    private static int CompareCardLibraryOrder(CardModel left, CardModel right, IReadOnlyList<CardPoolModel> orderedPools)
    {
	    left = CardModificationRuntime.GetPermanentCardForDisplay(left);
	    right = CardModificationRuntime.GetPermanentCardForDisplay(right);

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

    private static int CompareEffectiveCardCost(CardModel left, CardModel right)
    {
	    left = CardModificationRuntime.GetPermanentCardForDisplay(left);
	    right = CardModificationRuntime.GetPermanentCardForDisplay(right);
	    int cost = left.EnergyCost.GetResolved().CompareTo(right.EnergyCost.GetResolved());
	    return cost != 0 ? cost : string.Compare(left.Id.Entry, right.Id.Entry, StringComparison.Ordinal);
    }

    private static CardPoolModel GetCurrentCharacterCardPool()
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

    private static void ApplyCardUpgradePreview(NGridCardHolder holder, SelectItemState state)
    {
	    bool shouldPreviewUpgrade = holder.CardModel is not null
	                                && holder.CardModel.IsUpgradable
	                                && state.IsToggleEnabled(CardPrinter.ViewUpgradesToggleId);

	    holder.SetMeta(CardPrinter.PreviewUpgradeMetaKey, shouldPreviewUpgrade);

	    if (holder.CardModel is null || !holder.CardModel.IsUpgradable)
		    return;

	    holder.SetIsPreviewingUpgrade(shouldPreviewUpgrade);
    }

    private static int GetCardPoolSortIndex(CardPoolModel pool, IReadOnlyList<CardPoolModel> orderedPools)
    {
	    for (int i = 0; i < orderedPools.Count; i++)
	    {
		    if (CommonHelpers.SamePool(pool, orderedPools[i]))
			    return i;
	    }

	    return orderedPools.Count;
    }

    public static int GetCardRaritySortValue(CardRarity rarity)
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

    private static CardModel ResolveCanonicalCard(string cardId)
    {
	    return ModelDb.AllCards.FirstOrDefault(card => CommonHelpers.ModelIdMatches(card, cardId));
    }
}
