using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Loadout.Services.Actions;
using Loadout.Services.Compatibility;
using Loadout.Services.LastActions;
using Loadout.Services.RelicModification;
using Loadout.Services.Targets;
using Loadout.UI;
using Loadout.UI.Managers;
using Loadout.UI.Screens;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Entities.UI;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Relics;
using MegaCrit.Sts2.Core.Nodes.Screens.RelicCollection;

namespace Loadout.PanelItems;

public class LoadoutBag
{
	public static NGenericSelectScreen? LoadoutBagRelicScreen;
    public static void Initialize()
    {
        NLoadoutPanelItem loadoutBagItem = CommonHelpers.CreateAndAddLoadoutItem(
			ModelDb.AllRelics,
			new SelectItemAdapter<RelicModel>
			{
				GetId = relic => relic.Id.ToString(),
				GetName = relic => CommonHelpers.FormatRelicTitle(relic),
				GetSearchText = relic => $"{relic.Id} {CommonHelpers.FormatRelicTitle(relic)} {relic.DynamicDescription.GetFormattedText()}",
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
				RelicGroupingData relicGroupingData = BuildRelicGroupingData();
				builder.Sorter(
					"rarity",
					LocMan.GameLoc("gameplay_ui", "SORT_RARITY", LocMan.Loc("SORT_RARITY", "Rarity")),
					(left, right) => CompareRelicRarity(left, right, relicGroupingData),
					activeByDefault: true);
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
				NLoadoutPanel.Instance?.RefreshPendingPermanentRelics(screen);
			},
			"LoadoutBag.png",
			LocMan.Loc("LOADOUTBAG_TITLE", "Loadout Bag"),
			LocMan.Loc("LOADOUTBAG_DESC", "Right-click this relic to obtain any relic you want. Ctrl x5, Shift x10. Ctrl + right click to repeat the last action."),
			HandleAddRelicActivatedAsync,
			LastActionService.LoadoutBagKey,
			ReplayLoadoutBagLastActionAsync,
			selectScreenScenePath: CommonHelpers.RelicSelectScreenScenePath);
		LoadoutBagRelicScreen = loadoutBagItem.BoundScreen;
		LoadoutBagRelicScreen.VisibilityChanged += () =>
		{
			if (LoadoutBagRelicScreen?.IsVisibleInTree() == true)
				NLoadoutPanel.Instance?.RefreshPendingPermanentRelics(LoadoutBagRelicScreen);
		};
    }

    private const string HextechRelicGroupKey = "relic:hextech";
    private const string HextechCharacterGroupPrefix = "relic:hextech:character:";
    private const string HextechForgeGroupKey = "relic:hextech:forges";

    private static readonly string[] HextechKnownCharacterPoolOrder =
    {
	    "IRONCLAD",
	    "SILENT",
	    "REGENT",
	    "DEFECT",
	    "NECROBINDER"
    };

    private const string LoadoutBagTargetDropdownName = "LoadoutBagTargetDropdown";

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

    private static Control CreateRelicGridItem(RelicModel model)
    {
	    NRelicCollectionEntry holder = NRelicCollectionEntry.Create(model, ModelVisibility.Visible);
	    if (holder is null)
		    return CommonHelpers.CreateTextModelGridItem(model, CommonHelpers.FormatRelicTitle(model), model.Id.Entry, LocMan.Loc("CATEGORY_RELIC", "Relic"));

	    holder.MouseFilter = Control.MouseFilterEnum.Pass;
	    holder.CustomMinimumSize = new Vector2(68f, 68f);
	    return holder;
    }

    private static void RefreshRelicGridItem(Control view, RelicModel model)
    {
	    if (CommonHelpers.TryFindDescendantOrSelf(view, out NRelicCollectionEntry collectionEntry))
		    collectionEntry.relic = model;

	    if (!CommonHelpers.TryFindDescendantOrSelf(view, out NRelic relicView))
		    return;

	    RelicModel boundModel = relicView.Model;
	    if (boundModel is null || !boundModel.Id.Equals(model.Id))
		    relicView.Model = model;

	    if (!relicView.IsNodeReady())
		    return;
		
	    RelicModel displayModel = RelicModificationStateService.GetEffectivePermanentRelicForDisplay(model);
	    relicView.Icon.SelfModulate = Colors.White;
	    displayModel.UpdateTexture(relicView.Icon);
    }

    public static bool TryGetRelicPool(RelicModel relic, out RelicPoolModel pool)
    {
	    RelicPoolModel matchingPool = ModelDb.AllRelicPools
		    .FirstOrDefault(candidate => candidate.AllRelicIds.Contains(relic.Id));

	    pool = matchingPool!;
	    return matchingPool is not null;
    }

    private static int CompareRelicRarity(
	    RelicModel left,
	    RelicModel right,
	    RelicGroupingData groupingData)
    {
	    string leftId = left.Id.ToString();
	    string rightId = right.Id.ToString();
	    string leftGroup = GetRelicGroupKey(left, groupingData);
	    string rightGroup = GetRelicGroupKey(right, groupingData);
	    int groupOrder = string.Compare(leftGroup, rightGroup, StringComparison.Ordinal);
	    if (groupOrder != 0)
		    return groupOrder;

	    if (groupingData.CompatibilityRarityOrderByRelicId.TryGetValue(leftId, out int leftRarityOrder)
	        && groupingData.CompatibilityRarityOrderByRelicId.TryGetValue(rightId, out int rightRarityOrder))
	    {
		    int rarityOrder = leftRarityOrder.CompareTo(rightRarityOrder);
		    if (rarityOrder != 0)
			    return rarityOrder;

		    if (groupingData.CompatibilityCatalogOrderByRelicId.TryGetValue(leftId, out int leftCatalogOrder)
		        && groupingData.CompatibilityCatalogOrderByRelicId.TryGetValue(rightId, out int rightCatalogOrder))
		    {
			    int catalogOrder = leftCatalogOrder.CompareTo(rightCatalogOrder);
			    if (catalogOrder != 0)
				    return catalogOrder;
		    }
	    }

	    int rarity = GetRelicRaritySortValue(left.Rarity).CompareTo(GetRelicRaritySortValue(right.Rarity));
	    return rarity != 0 ? rarity : string.Compare(CommonHelpers.FormatRelicTitle(left), CommonHelpers.FormatRelicTitle(right), StringComparison.Ordinal);
    }

    public static int GetRelicRaritySortValue(RelicRarity rarity)
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

    private static RelicGroupingData BuildRelicGroupingData()
    {
	    Dictionary<string, string> keyByRelicId = new(StringComparer.Ordinal);
	    Dictionary<string, int> compatibilityRarityOrderByRelicId = new(StringComparer.Ordinal);
	    Dictionary<string, int> compatibilityCatalogOrderByRelicId = new(StringComparer.Ordinal);
	    HashSet<string> usedCharacterPoolKeys = new(StringComparer.Ordinal);
	    bool hasHextechRunes = false;
	    bool hasStatForgers = false;
	    IReadOnlyList<Type> runeTypes = HextechRunesInterop.GetAllRuneTypes();
	    Dictionary<Type, int> runeOrderByType = runeTypes
		    .Select((type, index) => (type, index))
		    .GroupBy(entry => entry.type)
		    .ToDictionary(group => group.Key, group => group.First().index);
	    IReadOnlyList<Type> forgeTypes = HextechRunesInterop.GetAllForgeTypes();
	    Dictionary<Type, int> forgeOrderByType = forgeTypes
		    .Select((type, index) => (type, index))
		    .GroupBy(entry => entry.type)
		    .ToDictionary(group => group.Key, group => group.First().index);
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
		    ancientGroupOrder.Add(groupKey);

		    foreach (RelicModel relic in ancientRelics)
			    keyByRelicId[relic.Id.ToString()] = groupKey;
	    }

	    foreach (RelicModel relic in ModelDb.AllRelics)
	    {
		    string relicId = relic.Id.ToString();
		    Type relicType = relic.CanonicalInstance?.GetType() ?? relic.GetType();
		    if (HextechRunesInterop.IsHextechRelic(relic))
		    {
			    hasHextechRunes = true;
			    string poolKey = HextechRunesInterop.GetPlayerRunePoolKey(relic)?.Trim().ToUpperInvariant()
			                     ?? string.Empty;
			    if (HextechKnownCharacterPoolOrder.Contains(poolKey, StringComparer.Ordinal))
			    {
				    usedCharacterPoolKeys.Add(poolKey);
				    keyByRelicId[relicId] = HextechCharacterGroupPrefix + poolKey;
			    }
			    else
			    {
				    // Generic, sponsor-pack, and future unknown pools stay in ARAM's main Hextech category.
				    keyByRelicId[relicId] = HextechRelicGroupKey;
			    }

			    compatibilityRarityOrderByRelicId[relicId] =
				    HextechRunesInterop.GetPlayerRuneRaritySortOrder(relicType);
			    compatibilityCatalogOrderByRelicId[relicId] = runeOrderByType.TryGetValue(relicType, out int runeOrder)
				    ? runeOrder
				    : int.MaxValue;
			    continue;
		    }

		    bool isForgeRelic = HextechRunesInterop.IsHextechForgeRelic(relic);
		    bool isForgeShopRelic = HextechRunesInterop.IsHextechShopRelic(relic);
		    if (!isForgeRelic && !isForgeShopRelic)
			    continue;

		    hasStatForgers = true;
		    keyByRelicId[relicId] = HextechForgeGroupKey;
		    compatibilityRarityOrderByRelicId[relicId] = isForgeShopRelic ? -1 : 0;
		    compatibilityCatalogOrderByRelicId[relicId] = isForgeShopRelic
			    ? -1
			    : forgeOrderByType.TryGetValue(relicType, out int forgeOrder)
				    ? forgeOrder
				    : int.MaxValue;
	    }

	    string starterHeader = new LocString("relic_collection", "STARTER").GetRawText();
	    List<string> hextechCharacterGroupOrder = BuildHextechHeaders(
		    headers,
		    usedCharacterPoolKeys,
		    starterHeader,
		    hasHextechRunes,
		    hasStatForgers);

	    List<string> groupOrder = new() { "relic:starter" };
	    if (hasHextechRunes)
	    {
		    groupOrder.Add(HextechRelicGroupKey);
		    groupOrder.AddRange(hextechCharacterGroupOrder);
	    }
	    if (hasStatForgers)
		    groupOrder.Add(HextechForgeGroupKey);
	    groupOrder.AddRange(new[]
	    {
		    "relic:common",
		    "relic:uncommon",
		    "relic:rare",
		    "relic:shop",
		    "relic:ancient"
	    });
	    groupOrder.AddRange(ancientGroupOrder);
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
		    "relic:common"
	    });
	    if (hasHextechRunes)
	    {
		    descendingGroupOrder.Add(HextechRelicGroupKey);
		    descendingGroupOrder.AddRange(hextechCharacterGroupOrder);
	    }
	    if (hasStatForgers)
		    descendingGroupOrder.Add(HextechForgeGroupKey);
	    descendingGroupOrder.Add("relic:starter");

	    return new RelicGroupingData(
		    keyByRelicId,
		    compatibilityRarityOrderByRelicId,
		    compatibilityCatalogOrderByRelicId,
		    headers,
		    groupOrder,
		    descendingGroupOrder);
    }

    private static string GetRelicGroupKey(RelicModel relic, RelicGroupingData groupingData)
    {
	    if (groupingData.GroupKeyByRelicId.TryGetValue(relic.Id.ToString(), out string assignedKey))
		    return assignedKey;

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

    private static List<string> BuildHextechHeaders(
	    IDictionary<string, SelectGroupHeader> headers,
	    IReadOnlySet<string> usedCharacterPoolKeys,
	    string starterHeader,
	    bool hasHextechRunes,
	    bool hasStatForgers)
    {
	    if (hasHextechRunes)
	    {
		    headers[HextechRelicGroupKey] = BuildHextechHeader(
			    starterHeader,
			    "海克斯：",
			    "来自海克斯符文池的自定义遗物。",
			    "Hextech:",
			    "Custom relics from the Hextech rune pool.",
			    "HEXTECH_RUNES_SUBCATEGORY", HextechCharacterGroupPrefix);
	    }

	    List<string> characterGroups = new();
	    foreach (string poolKey in HextechKnownCharacterPoolOrder.Where(usedCharacterPoolKeys.Contains))
	    {
		    (string zhTitle, string zhBody, string enTitle, string enBody) = poolKey switch
		    {
			    "IRONCLAD" => ("铁甲战士海克斯：", "仅铁甲战士可抽取的海克斯符文。", "Ironclad Hexes:", "Hextech runes only available to Ironclad."),
			    "SILENT" => ("静默猎手海克斯：", "仅静默猎手可抽取的海克斯符文。", "Silent Hexes:", "Hextech runes only available to Silent."),
			    "REGENT" => ("储君海克斯：", "仅储君可抽取的海克斯符文。", "Regent Hexes:", "Hextech runes only available to Regent."),
			    "DEFECT" => ("故障机器人海克斯：", "仅故障机器人可抽取的海克斯符文。", "Defect Hexes:", "Hextech runes only available to Defect."),
			    "NECROBINDER" => ("亡灵契约师海克斯：", "仅亡灵契约师可抽取的海克斯符文。", "Necrobinder Hexes:", "Hextech runes only available to Necrobinder."),
			    _ => throw new InvalidOperationException($"Unsupported Hextech character pool '{poolKey}'.")
		    };
		    string groupKey = HextechCharacterGroupPrefix + poolKey;
		    headers[groupKey] = BuildHextechHeader(
			    starterHeader,
			    zhTitle,
			    zhBody,
			    enTitle,
			    enBody,
			    $"HEXTECH_{poolKey}");
		    characterGroups.Add(groupKey);
	    }

	    if (hasStatForgers)
	    {
		    headers[HextechForgeGroupKey] = BuildHextechHeader(
			    starterHeader,
			    "属性锻造器：",
			    "来自属性锻造系统的自定义遗物。",
			    "Stat Forgers:",
			    "Custom relics from the stat forging system.",
			    "HEXTECH_FORGES_SUBCATEGORY");
	    }

	    return characterGroups;
    }

    private static SelectGroupHeader BuildHextechHeader(
	    string starterHeader,
	    string zhTitle,
	    string zhBody,
	    string enTitle,
	    string enBody,
	    string logKey,
	    string childGroupPrefix = null)
    {
	    try
	    {
		    string formatted = HextechRunesCollectionInterop.FormatLikeStarterHeader(
			    starterHeader,
			    zhTitle,
			    zhBody,
			    enTitle,
			    enBody,
			    logKey);
		    bool useChinese = starterHeader.Contains("初始：", StringComparison.Ordinal);
		    string expectedTitle = useChinese ? zhTitle : enTitle;
		    string expectedBody = useChinese ? zhBody : enBody;
		    if (!string.IsNullOrWhiteSpace(formatted)
		        && formatted.Contains(expectedTitle, StringComparison.Ordinal)
		        && formatted.Contains(expectedBody, StringComparison.Ordinal))
		    {
			    return new SelectGroupHeader(formatted, childGroupPrefix: childGroupPrefix);
		    }
	    }
	    catch (Exception exception)
	    {
		    GD.PushWarning($"LoadoutPanel: ARAM collection header formatter failed for '{logKey}': {exception.Message}");
	    }

	    bool fallbackToChinese = starterHeader.Contains("初始：", StringComparison.Ordinal);
	    string fallbackTitle = fallbackToChinese ? zhTitle : enTitle;
	    string fallbackBody = fallbackToChinese ? zhBody : enBody;
	    string exactHeader = $"[gold][font_size=28][b]{fallbackTitle}[/b][/font_size][/gold] {fallbackBody}";
	    return new SelectGroupHeader(exactHeader, childGroupPrefix: childGroupPrefix);
    }

    private static SelectGroupHeader GetRelicGroupHeader(string key, RelicGroupingData groupingData)
    {
	    return groupingData.HeadersByKey.TryGetValue(key, out SelectGroupHeader header)
		    ? header
		    : SelectGroupHeader.Category(LocMan.Loc("OTHER", "Other"));
    }

    private sealed class RelicGroupingData
    {
	    public RelicGroupingData(
		    IReadOnlyDictionary<string, string> groupKeyByRelicId,
		    IReadOnlyDictionary<string, int> compatibilityRarityOrderByRelicId,
		    IReadOnlyDictionary<string, int> compatibilityCatalogOrderByRelicId,
		    IReadOnlyDictionary<string, SelectGroupHeader> headersByKey,
		    IReadOnlyList<string> groupOrder,
		    IReadOnlyList<string> descendingGroupOrder)
	    {
		    GroupKeyByRelicId = groupKeyByRelicId;
		    CompatibilityRarityOrderByRelicId = compatibilityRarityOrderByRelicId;
		    CompatibilityCatalogOrderByRelicId = compatibilityCatalogOrderByRelicId;
		    HeadersByKey = headersByKey;
		    GroupOrder = groupOrder;
		    DescendingGroupOrder = descendingGroupOrder;
	    }

	    public IReadOnlyDictionary<string, string> GroupKeyByRelicId { get; }
	    public IReadOnlyDictionary<string, int> CompatibilityRarityOrderByRelicId { get; }
	    public IReadOnlyDictionary<string, int> CompatibilityCatalogOrderByRelicId { get; }
	    public IReadOnlyDictionary<string, SelectGroupHeader> HeadersByKey { get; }
	    public IReadOnlyList<string> GroupOrder { get; }
	    public IReadOnlyList<string> DescendingGroupOrder { get; }
    }

    private static Task ReplayLoadoutBagLastActionAsync()
    {
	    LoadoutTargetSelection fallbackTarget = LoadoutTargetService.GetSelected(LastActionService.LoadoutBagKey, LoadoutTargetMode.AllPlayersAndPlayers);
	    foreach (LastActionEntry entry in LastActionService.GetAction(LastActionService.LoadoutBagKey))
	    {
		    if (entry.Kind != LastActionService.AddRelicKind || entry.Amount <= 0)
			    continue;

		    RelicModel relic = ResolveCanonicalRelic(entry.ContentId);
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

    internal static Action BindRelicActivationWithCleanup(Control view, Action activate)
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

    private static void AddRelicPoolFilters(SelectScreenBuilder<RelicModel> builder)
    {
	    IReadOnlyList<RelicPoolModel> pools = CommonHelpers.BuildOrderedPools(
		    ModelDb.AllRelics
			    .Select(relic => LoadoutBag.TryGetRelicPool(relic, out RelicPoolModel pool) ? pool : null)
			    .OfType<RelicPoolModel>(),
		    ModelDb.AllCharacters.Where(character => character.IsPlayable).Select(character => character.RelicPool),
		    pool => CommonHelpers.IsSharedPool(pool) && !CommonHelpers.IsInternalPool(pool));

	    foreach (RelicPoolModel pool in pools)
	    {
		    RelicPoolModel localPool = pool;
		    builder.Filter(CommonHelpers.PoolFilterId("relic", localPool), CommonHelpers.GetPoolLabel(localPool),
			    relic => LoadoutBag.TryGetRelicPool(relic, out RelicPoolModel relicPool)
			             && CommonHelpers.SamePool(relicPool, localPool),
			    "class");
	    }
    }

    private static RelicModel ResolveCanonicalRelic(string relicId)
    {
	    return ModelDb.AllRelics.FirstOrDefault(relic => CommonHelpers.ModelIdMatches(relic, relicId));
    }
}