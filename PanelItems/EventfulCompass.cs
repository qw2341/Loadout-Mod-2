using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Godot;
using Loadout.Services.Actions;
using Loadout.Services.LastActions;
using Loadout.UI;
using Loadout.UI.Managers;
using Loadout.UI.Screens;
using Loadout.UI.Screens.Controls;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace Loadout.PanelItems;

public class EventfulCompass
{
    public static void Initialize()
    {
	    IReadOnlyList<EventModel> allEvents = ModelDb.AllEvents
		    .Concat(ModelDb.AllAncients)
		    .GroupBy(eventModel => eventModel.Id.ToString(), StringComparer.Ordinal)
		    .Select(group => group.First())
		    .ToList();
	    EventCatalogData catalog = BuildEventCatalogData(allEvents);

        CommonHelpers.CreateAndAddLoadoutItem(
			allEvents,
			new SelectItemAdapter<EventModel>
			{
				GetId = eventModel => eventModel.Id.ToString(),
				GetName = FormatEventTitle,
				GetSearchText = eventModel => BuildEventSearchText(eventModel, catalog),
				CreateView = (eventModel, _) => CreateEventGridItem(eventModel)
			}, builder =>
			{
				EventGroupPresentation grouping = BuildEventGroupPresentation(catalog);
				builder.Options(new SelectScreenOptions { SelectionMode = SelectSelectionMode.None });
				builder.Materialization(SelectMaterializationMode.Lazy);
				builder.Layout(4, EventTileSize, 24, 24);
				AddActFilters(builder, catalog);
				builder.FilterGroup("layout", LocMan.Loc("FILTER_GROUP_LAYOUT", "Layout"));
				builder.Filter("default", LocMan.Loc("LAYOUT_DEFAULT", "Default"), eventModel => eventModel.LayoutType == EventLayoutType.Default, "layout");
				builder.Filter("combat", LocMan.Loc("LAYOUT_COMBAT", "Combat"), eventModel => eventModel.LayoutType == EventLayoutType.Combat, "layout");
				builder.Filter("ancient", LocMan.Loc("LAYOUT_ANCIENT", "Ancient"), eventModel => eventModel.LayoutType == EventLayoutType.Ancient, "layout");
				builder.FilterGroup("sharing", LocMan.Loc("FILTER_GROUP_SCOPE", "Scope"));
				builder.Filter("shared", LocMan.Loc("SCOPE_SHARED", "Shared"), eventModel => eventModel.IsShared, "sharing");
				builder.Filter("solo", LocMan.Loc("SCOPE_SOLO", "Solo"), eventModel => !eventModel.IsShared, "sharing");
				CommonHelpers.AddModFilters(builder, allEvents);
				builder.Sorter(
					"act",
					LocMan.Loc("FILTER_GROUP_ACT", "Act"),
					(left, right) => CompareEventsByAct(left, right, catalog, grouping),
					(left, right) => CompareEventsByAct(right, left, catalog, grouping),
					activeByDefault: true);
				builder.KeySorter("name", LocMan.Loc("SORT_NAME", "Name"), FormatEventTitle, comparer: StringComparer.Ordinal);
				builder.KeySorter("id", LocMan.Loc("SORT_ID", "ID"), model => model.Id.Entry, comparer: StringComparer.Ordinal);
				builder.GroupBySorter(
					"act",
					eventModel => GetEventGroupKey(eventModel, catalog),
					grouping.GetHeader,
					grouping.GroupOrder,
					grouping.DescendingGroupOrder);
				builder.GroupBySorter(
					"name",
					eventModel => GetEventGroupKey(eventModel, catalog),
					grouping.GetHeader,
					grouping.GroupOrder,
					grouping.GroupOrder);
				builder.GroupBySorter(
					"id",
					eventModel => GetEventGroupKey(eventModel, catalog),
					grouping.GetHeader,
					grouping.GroupOrder,
					grouping.GroupOrder);
			}, UpsertRoomJumpControls,
			"EventfulCompass.png",
			LocMan.Loc("EVENTFULCOMPASS_TITLE", "Eventful Compass"),
			LocMan.Loc("EVENTFULCOMPASS_DESC", "Right-click this relic to select the event you want. Ctrl + right click to repeat the last action."),
			HandleEnterEventActivatedAsync,
			LastActionService.EventfulCompassKey,
			ReplayEventfulCompassLastActionAsync,
			selectScreenScenePath: CommonHelpers.EventSelectScreenScenePath);
    }

    private static readonly Vector2 EventTileSize = new(264f, 144f);
    private static readonly Vector2I AncientPreviewTextureSize = new(264, 144);
    private const float EventTilePortraitRestAlpha = 0.45f;
    private const float EventTilePortraitHoverAlpha = 0.78f;
    private const float EventTileShadeHoverAlpha = 0.16f;
    private const string RoomJumpControlName = "EventfulCompassRoomJumpControls";
    private const string RoomJumpDropdownName = "EventfulCompassRoomDropdown";
    private const string RoomJumpButtonName = "EventfulCompassGoToButton";
    private static RoomType SelectedRoomType = RoomType.Treasure;
    private static readonly Dictionary<Control, Tween> EventTileHoverTweens = new();
    private static readonly Dictionary<string, bool> EventPortraitLoadability = new(StringComparer.Ordinal);

    private static void UpsertRoomJumpControls(NGenericSelectScreen screen)
    {
	    if (screen.FindChild(RoomJumpControlName, true, false) is not null)
		    return;

	    VBoxContainer controls = new()
	    {
		    Name = RoomJumpControlName,
		    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
		    CustomMinimumSize = new Vector2(0f, 102f),
		    MouseFilter = Control.MouseFilterEnum.Ignore
	    };
	    controls.AddThemeConstantOverride("separation", 8);

	    NLoadoutDropdown roomDropdown = new()
	    {
		    Name = RoomJumpDropdownName,
		    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
		    CustomMinimumSize = new Vector2(256f, 52f),
		    DropdownWidth = 286f,
		    MaxVisibleItems = 8
	    };
	    roomDropdown.SelectedItemChanged += OnRoomJumpDropdownChanged;
	    controls.AddChild(roomDropdown);

	    NLoadoutActionButton goToButton = new()
	    {
		    Name = RoomJumpButtonName,
		    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
		    CustomMinimumSize = new Vector2(0f, 42f)
	    };
	    goToButton.Init("go_to_room", LocMan.Loc("GO_TO_ROOM", "Go To"));
	    goToButton.Connect(NClickableControl.SignalName.Released, Callable.From<NClickableControl>(_ => HandleGoToRoomPressed()));
	    controls.AddChild(goToButton);

	    roomDropdown.SetItems(
		    LocMan.Loc("ROOM", "Room"),
		    GetRoomJumpOptions(),
		    SelectedRoomType.ToString());

	    screen.AddCustomSidebarControl(controls);
    }

    private static IReadOnlyList<LoadoutDropdownOption> GetRoomJumpOptions()
    {
	    return Enum.GetValues<RoomType>()
		    .Where(roomType => roomType != RoomType.Unassigned)
		    .Select(roomType => new LoadoutDropdownOption(roomType.ToString(), FormatRoomTypeLabel(roomType)))
		    .ToList();
    }

    private static string FormatRoomTypeLabel(RoomType roomType)
    {
	    return roomType switch
	    {
		    RoomType.Shop => LocMan.GameLoc("map", "LEGEND_MERCHANT.title", roomType.ToString()),
		    RoomType.Monster => LocMan.GameLoc("map", "LEGEND_ENEMY.title", roomType.ToString()),
		    RoomType.Event => LocMan.GameLoc("map", "LEGEND_EVENT.hoverTip.title", roomType.ToString()),
		    RoomType.RestSite => LocMan.GameLoc("map", "LEGEND_REST.title", roomType.ToString()),
		    RoomType.Map => LocMan.GameLoc("map", "LEGEND_MAP.hoverTip.title", roomType.ToString()),
		    _ => LocMan.GameLoc("map", $"LEGEND_{roomType.ToString().ToUpper()}.title", roomType.ToString())
	    };
    }

    private static void OnRoomJumpDropdownChanged(string selectedId)
    {
	    if (Enum.TryParse(selectedId, ignoreCase: true, out RoomType roomType) && roomType != RoomType.Unassigned)
		    SelectedRoomType = roomType;
    }

    private static void HandleGoToRoomPressed()
    {
	    Player localPlayer = CommonHelpers.GetLocalRunPlayer();
	    if (localPlayer is null || !RunManager.Instance.IsInProgress)
	    {
		    GD.PushWarning($"LoadoutPanel: cannot go to room '{SelectedRoomType}' because no local run player was resolved.");
		    return;
	    }

	    try
	    {
		    if (!LoadoutImmediateMutationService.RequestGoToRoom(SelectedRoomType))
			    GD.PushWarning($"LoadoutPanel: failed to request room jump to '{SelectedRoomType}'.");
	    }
	    catch (Exception exception)
	    {
		    GD.PushError($"LoadoutPanel: failed to request room jump to '{SelectedRoomType}': {exception}");
	    }
    }

    private static async Task<IReadOnlyList<LastActionEntry>> HandleEnterEventActivatedAsync(NGenericSelectScreen _, IGenericSelectItem selectItem)
    {
	    if (selectItem.UntypedModel is not EventModel eventModel)
		    return [];

	    bool entered = await EnterEventAsync(eventModel, selectItem.Id);
	    if (entered)
		    Callable.From(NLoadoutPanelRoot.CloseTopLoadoutScreen).CallDeferred();

	    return entered
		    ?
		    [
			    new LastActionEntry
			    {
				    Kind = LastActionService.EnterEventKind,
				    ContentId = eventModel.Id.ToString(),
				    Amount = 1
			    }
		    ]
		    : [];
    }

    private static Task<bool> EnterEventAsync(EventModel eventModel, string logId)
    {

	    Player localPlayer = CommonHelpers.GetLocalRunPlayer();
	    if (localPlayer is null || !RunManager.Instance.IsInProgress)
	    {
		    GD.PushWarning($"LoadoutPanel: cannot enter event '{logId}' because no local run player was resolved.");
		    return Task.FromResult(false);
	    }

	    try
	    {
		    return Task.FromResult(LoadoutImmediateMutationService.RequestEnterEvent(eventModel.Id));
	    }
	    catch (Exception exception)
	    {
		    GD.PushError($"LoadoutPanel: failed to enter event '{eventModel.Id}': {exception}");
		    return Task.FromResult(false);
	    }
    }

    private static async Task ReplayEventfulCompassLastActionAsync()
    {
	    LastActionEntry entry = LastActionService.GetAction(LastActionService.EventfulCompassKey)
		    .LastOrDefault(action => action.Kind == LastActionService.EnterEventKind && action.Amount > 0);
	    if (entry is null)
		    return;

	    EventModel eventModel = ResolveEvent(entry.ContentId);
	    if (eventModel is null)
	    {
		    GD.PushWarning($"LoadoutPanel: cannot replay event action for unknown event '{entry.ContentId}'.");
		    return;
	    }

	    await EnterEventAsync(eventModel, entry.ContentId);
    }

    private static EventModel ResolveEvent(string eventId)
    {
	    return ModelDb.AllEvents
		    .Concat(ModelDb.AllAncients)
		    .Distinct()
		    .FirstOrDefault(eventModel => CommonHelpers.ModelIdMatches(eventModel, eventId));
    }

    private static EventCatalogData BuildEventCatalogData(IReadOnlyList<EventModel> allEvents)
    {
	    IReadOnlyList<ActModel> acts = ModelDb.Acts
		    .Where(act => act.Index >= 0)
		    .GroupBy(act => act.Id.ToString(), StringComparer.Ordinal)
		    .Select(group => group.First())
		    .ToList();
	    Dictionary<string, List<ActModel>> regularActsByEventId = new(StringComparer.Ordinal);
	    Dictionary<string, List<ActModel>> ancientActsByEventId = new(StringComparer.Ordinal);

	    foreach (ActModel act in acts)
	    {
		    foreach (EventModel eventModel in act.AllEvents)
			    AddEventActMembership(regularActsByEventId, eventModel.Id.ToString(), act);

		    foreach (AncientEventModel ancient in act.AllAncients)
			    AddEventActMembership(ancientActsByEventId, ancient.Id.ToString(), act);
	    }

	    HashSet<string> sharedIds = ModelDb.AllSharedEvents
		    .Cast<EventModel>()
		    .Concat(ModelDb.AllSharedAncients)
		    .Select(eventModel => eventModel.Id.ToString())
		    .ToHashSet(StringComparer.Ordinal);
	    Dictionary<string, EventPlacement> placements = new(StringComparer.Ordinal);

	    foreach (EventModel eventModel in allEvents)
	    {
		    string eventId = eventModel.Id.ToString();
		    bool isAncient = eventModel is AncientEventModel;
		    Dictionary<string, List<ActModel>> memberships = isAncient
			    ? ancientActsByEventId
			    : regularActsByEventId;
		    IReadOnlyList<ActModel> matchedActs = memberships.TryGetValue(eventId, out List<ActModel> eventActs)
			    ? eventActs
			    : [];
		    bool isShared = eventModel.IsShared || sharedIds.Contains(eventId);
		    bool isOther = isShared || matchedActs.Count == 0;
		    IReadOnlyList<ActModel> applicableActs = isShared ? acts : matchedActs;
		    ActModel primaryAct = isOther ? null : matchedActs[0];
		    string groupKey = isOther
			    ? isAncient ? OtherAncientsGroupKey : OtherEventsGroupKey
			    : isAncient
				    ? GetActAncientsGroupKey(primaryAct.Index)
				    : GetActEventsGroupKey(primaryAct);

		    placements[eventId] = new EventPlacement(
			    groupKey,
			    applicableActs,
			    applicableActs.Select(act => act.Id.ToString()).ToHashSet(StringComparer.Ordinal),
			    isOther);
	    }

	    return new EventCatalogData(acts, placements);
    }

    private static void AddEventActMembership(
	    IDictionary<string, List<ActModel>> memberships,
	    string eventId,
	    ActModel act)
    {
	    if (!memberships.TryGetValue(eventId, out List<ActModel> acts))
	    {
		    acts = [];
		    memberships[eventId] = acts;
	    }

	    if (!acts.Any(candidate => candidate.Id == act.Id))
		    acts.Add(act);
    }

    private static EventGroupPresentation BuildEventGroupPresentation(EventCatalogData catalog)
    {
	    List<string> groupOrder = [];
	    List<IReadOnlyList<string>> groupBlocks = [];
	    Dictionary<string, SelectGroupHeader> headers = new(StringComparer.Ordinal);
	    Dictionary<string, int> leafOrder = new(StringComparer.Ordinal);

	    foreach (IGrouping<int, ActModel> actsAtIndex in catalog.Acts.GroupBy(act => act.Index))
	    {
		    string rootKey = GetActRootGroupKey(actsAtIndex.Key);
		    string childPrefix = rootKey + ":";
		    List<string> block = [rootKey];
		    headers[rootKey] = SelectGroupHeader.Category(
			    FormatActNumber(actsAtIndex.Key),
			    childGroupPrefix: childPrefix);

		    foreach (ActModel act in actsAtIndex)
		    {
			    string groupKey = GetActEventsGroupKey(act);
			    block.Add(groupKey);
			    headers[groupKey] = new SelectGroupHeader(FormatActTitle(act));
			    leafOrder[groupKey] = leafOrder.Count;
		    }

		    string ancientsKey = GetActAncientsGroupKey(actsAtIndex.Key);
		    block.Add(ancientsKey);
		    headers[ancientsKey] = new SelectGroupHeader(LocMan.Loc("ANCIENTS", "Ancients"));
		    leafOrder[ancientsKey] = leafOrder.Count;
		    groupBlocks.Add(block);
		    groupOrder.AddRange(block);
	    }

	    List<string> otherBlock =
	    [
		    OtherRootGroupKey,
		    OtherEventsGroupKey,
		    OtherAncientsGroupKey
	    ];
	    headers[OtherRootGroupKey] = SelectGroupHeader.Category(
		    LocMan.Loc("OTHER", "Other"),
		    childGroupPrefix: OtherRootGroupKey + ":");
	    headers[OtherEventsGroupKey] = new SelectGroupHeader(LocMan.Loc("EVENTS", "Events"));
	    headers[OtherAncientsGroupKey] = new SelectGroupHeader(LocMan.Loc("ANCIENTS", "Ancients"));
	    leafOrder[OtherEventsGroupKey] = leafOrder.Count;
	    leafOrder[OtherAncientsGroupKey] = leafOrder.Count;
	    groupBlocks.Add(otherBlock);
	    groupOrder.AddRange(otherBlock);

	    IReadOnlyList<string> descendingGroupOrder = groupBlocks
		    .AsEnumerable()
		    .Reverse()
		    .SelectMany(block => block)
		    .ToList();
	    return new EventGroupPresentation(groupOrder, descendingGroupOrder, headers, leafOrder);
    }

    private static void AddActFilters(
	    SelectScreenBuilder<EventModel> builder,
	    EventCatalogData catalog)
    {
	    if (catalog.Acts.Count == 0)
		    return;

	    builder.FilterGroup("act", LocMan.Loc("FILTER_GROUP_ACT", "Act"));
	    for (int actOrder = 0; actOrder < catalog.Acts.Count; actOrder++)
	    {
		    ActModel act = catalog.Acts[actOrder];
		    string actId = act.Id.ToString();
		    builder.Filter(
			    $"act_{actOrder}",
			    $"{FormatActNumber(act.Index)}: {FormatActTitle(act)}",
			    eventModel => catalog.TryGetPlacement(eventModel, out EventPlacement placement)
			                  && placement.ApplicableActIds.Contains(actId),
			    "act");
	    }

	    builder.Filter(
		    "act_other",
		    LocMan.Loc("OTHER", "Other"),
		    eventModel => catalog.TryGetPlacement(eventModel, out EventPlacement placement)
		                  && placement.IsOther,
		    "act");
    }

    private static int CompareEventsByAct(
	    EventModel left,
	    EventModel right,
	    EventCatalogData catalog,
	    EventGroupPresentation grouping)
    {
	    int leftOrder = catalog.TryGetPlacement(left, out EventPlacement leftPlacement)
		    ? grouping.GetLeafOrder(leftPlacement.GroupKey)
		    : int.MaxValue;
	    int rightOrder = catalog.TryGetPlacement(right, out EventPlacement rightPlacement)
		    ? grouping.GetLeafOrder(rightPlacement.GroupKey)
		    : int.MaxValue;
	    int byGroup = leftOrder.CompareTo(rightOrder);
	    return byGroup != 0
		    ? byGroup
		    : string.Compare(left.Id.Entry, right.Id.Entry, StringComparison.Ordinal);
    }

    private static string GetEventGroupKey(EventModel eventModel, EventCatalogData catalog)
    {
	    if (catalog.TryGetPlacement(eventModel, out EventPlacement placement))
		    return placement.GroupKey;

	    return eventModel is AncientEventModel
		    ? OtherAncientsGroupKey
		    : OtherEventsGroupKey;
    }

    private static string BuildEventSearchText(EventModel eventModel, EventCatalogData catalog)
    {
	    List<string> searchParts =
	    [
		    eventModel.Id.ToString(),
		    FormatEventTitle(eventModel)
	    ];

	    if (catalog.TryGetPlacement(eventModel, out EventPlacement placement))
	    {
		    foreach (ActModel act in placement.ApplicableActs)
		    {
			    searchParts.Add(FormatActNumber(act.Index));
			    searchParts.Add(FormatActTitle(act));
		    }

		    if (placement.IsOther)
			    searchParts.Add(LocMan.Loc("OTHER", "Other"));
	    }

	    return string.Join(" ", searchParts.Distinct(StringComparer.Ordinal));
    }

    private static string FormatEventTitle(EventModel eventModel)
    {
	    try
	    {
		    return CommonHelpers.FormatEventTitle(eventModel);
	    }
	    catch
	    {
		    return eventModel.Id.Entry;
	    }
    }

    private static string FormatActTitle(ActModel act)
    {
	    try
	    {
		    return act.Title.GetFormattedText();
	    }
	    catch
	    {
		    return act.Id.Entry;
	    }
    }

    private static string FormatActNumber(int actIndex)
    {
	    int actNumber = actIndex + 1;
	    return LocMan.Loc("ACT_NUMBER", $"Act {actNumber}", actNumber);
    }

    private static string GetActRootGroupKey(int actIndex) => $"event:act:{actIndex}";
    private static string GetActEventsGroupKey(ActModel act) => $"{GetActRootGroupKey(act.Index)}:events:{act.Id}";
    private static string GetActAncientsGroupKey(int actIndex) => $"{GetActRootGroupKey(actIndex)}:ancients";

    private const string OtherRootGroupKey = "event:other";
    private const string OtherEventsGroupKey = "event:other:events";
    private const string OtherAncientsGroupKey = "event:other:ancients";

    private static Control CreateEventGridItem(EventModel model)
    {
	    Button button = CommonHelpers.CreateModelButton(EventTileSize);
	    button.ClipContents = true;

	    TextureRect background = CreateEventTileBackground(model, button);
	    if (background is not null)
		    button.AddChild(background);

	    float restingShadeAlpha = model is AncientEventModel ? 0.38f : 0.35f;
	    ColorRect shade = new()
	    {
		    Color = new Color(0f, 0f, 0f, restingShadeAlpha),
		    MouseFilter = Control.MouseFilterEnum.Ignore,
		    Position = Vector2.Zero,
		    Size = EventTileSize
	    };
	    button.AddChild(shade);
	    AttachEventTileHoverAnimation(button, background, shade, restingShadeAlpha);

	    bool isAncient = model is AncientEventModel;

	    MegaLabel titleLabel = CommonHelpers.CreateButtonLabel(
		    "EventTitle",
		    isAncient ? CommonHelpers.FormatEventTitle(model).ToUpperInvariant() : CommonHelpers.FormatEventTitle(model),
		    isAncient ? new Vector2(14f, 26f) : new Vector2(14f, 19f),
		    isAncient ? new Vector2(235f, 50f) : new Vector2(235f, 106f),
		    isAncient ? 32 : 26,
		    HorizontalAlignment.Center,
		    isAncient ? new Color(0.937255f, 0.784314f, 0.317647f, 1f) : StsColors.cream);
	    if (isAncient)
		    titleLabel.AddThemeFontOverride("font", CommonHelpers.LoadGameFont("res://themes/spectral_bold_shared.tres"));
	    else
		    ConfigureWrappingEventTitle(titleLabel);
	    button.AddChild(titleLabel);

	    if (model is AncientEventModel ancientEvent)
	    {
		    MegaLabel epithetLabel = CommonHelpers.CreateButtonLabel(
			    "AncientEpithet", LocMan.SafeFormatLocString(ancientEvent.Epithet, string.Empty),
			    new Vector2(14f, 74f),
			    new Vector2(235f, 53f),
			    19,
			    HorizontalAlignment.Center,
			    new Color(0.529412f, 0.807843f, 0.921569f, 0.88f));
		    epithetLabel.AddThemeFontOverride("font", CommonHelpers.LoadGameFont("res://themes/bitter_medium_italic_glyph_space_one.tres"));
		    button.AddChild(epithetLabel);
	    }

	    CommonHelpers.AttachHoverTips(button, () => CreateEventHoverTips(model));
	    return button;
    }

    private static TextureRect CreateEventTileBackground(EventModel model, Node viewportOwner)
    {
	    if (model is AncientEventModel ancientEvent)
	    {
		    Texture2D ancientPreview = GetAncientBackgroundPreviewTexture(ancientEvent, viewportOwner);
		    if (ancientPreview is not null)
			    return CreateTileBackground(ancientPreview, useMipmaps: false);
	    }

	    if (model.LayoutType != EventLayoutType.Default)
		    return null;

	    string portraitPath = ImageHelper.GetImagePath(
		    $"events/{model.Id.Entry.ToLowerInvariant()}.png");
	    if (!CanLoadEventPortrait(portraitPath))
		    return null;

	    try
	    {
		    Texture2D portrait = model.CreateInitialPortrait();
		    return portrait is null ? null : CreateTileBackground(portrait, useMipmaps: true);
	    }
	    catch (Exception)
	    {
		    // A custom loader can disappear or reject the resource after the capability
		    // check. Negative-cache the path so filtering/prewarm does not retry it.
		    EventPortraitLoadability[portraitPath] = false;
		    return null;
	    }
    }

    private static bool CanLoadEventPortrait(string portraitPath)
    {
	    if (string.IsNullOrWhiteSpace(portraitPath))
		    return false;

	    if (EventPortraitLoadability.TryGetValue(portraitPath, out bool loadable))
		    return loadable;

	    try
	    {
		    
		    loadable = PreloadManager.Cache.ContainsKey(portraitPath)
		               || ResourceLoader.Exists(portraitPath, nameof(Texture2D));
	    }
	    catch (Exception)
	    {
		    loadable = false;
	    }

	    EventPortraitLoadability[portraitPath] = loadable;
	    return loadable;
    }

    public static TextureRect CreateTileBackground(Texture2D texture, bool useMipmaps)
    {
	    return new TextureRect
	    {
		    Texture = texture,
		    TextureFilter = useMipmaps
			    ? CanvasItem.TextureFilterEnum.LinearWithMipmaps
			    : CanvasItem.TextureFilterEnum.Linear,
		    ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
		    StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
		    MouseFilter = Control.MouseFilterEnum.Ignore,
		    Modulate = new Color(1f, 1f, 1f, EventTilePortraitRestAlpha),
		    Position = Vector2.Zero,
		    Size = EventTileSize
	    };
    }

    private static Texture2D GetAncientBackgroundPreviewTexture(AncientEventModel model, Node viewportOwner)
    {
	    string id = model.Id.ToString();

	    try
	    {
		    SubViewport viewport = new()
		    {
			    Name = $"LoadoutAncientPreview_{CommonHelpers.MakeSafeNodeName(id)}",
			    Size = AncientPreviewTextureSize,
			    TransparentBg = false,
			    Disable3D = false,
			    RenderTargetUpdateMode = SubViewport.UpdateMode.WhenVisible
		    };

		    Control backgroundScene = model.CreateBackgroundScene().Instantiate<Control>(PackedScene.GenEditState.Disabled);
		    backgroundScene.MouseFilter = Control.MouseFilterEnum.Ignore;
		    backgroundScene.Position = Vector2.Zero;
		    backgroundScene.Size = new Vector2(1920f, 1080f);
		    backgroundScene.Scale = Vector2.One * Math.Max(AncientPreviewTextureSize.X / 1920f, AncientPreviewTextureSize.Y / 1080f);
		    viewport.AddChild(backgroundScene);

		    viewportOwner.AddChild(viewport);
		    return viewport.GetTexture();
	    }
	    catch (Exception exception)
	    {
		    GD.PushWarning($"LoadoutPanel: could not create ancient background preview for '{model.Id}'. {exception.Message}");
		    return null;
	    }
    }

    public static void AttachEventTileHoverAnimation(Control tile, TextureRect background, ColorRect shade, float restingShadeAlpha)
    {
	    tile.MouseEntered += () => AnimateEventTileHover(tile, background, shade, EventTilePortraitHoverAlpha, EventTileShadeHoverAlpha);
	    tile.MouseExited += () => AnimateEventTileHover(tile, background, shade, EventTilePortraitRestAlpha, restingShadeAlpha);
	    tile.TreeExiting += () =>
	    {
		    if (EventTileHoverTweens.TryGetValue(tile, out Tween tween) && GodotObject.IsInstanceValid(tween))
			    tween.Kill();

		    EventTileHoverTweens.Remove(tile);
	    };
    }

    public static void AnimateEventTileHover(Control tile, TextureRect background, ColorRect shade, float portraitAlpha, float shadeAlpha)
    {
	    if (EventTileHoverTweens.TryGetValue(tile, out Tween oldTween) && GodotObject.IsInstanceValid(oldTween))
		    oldTween.Kill();

	    Tween tween = tile.CreateTween().SetParallel();
	    if (background is not null && GodotObject.IsInstanceValid(background))
		    tween.TweenProperty(background, "modulate", new Color(1f, 1f, 1f, portraitAlpha), 0.12)
			    .SetEase(Tween.EaseType.Out)
			    .SetTrans(Tween.TransitionType.Cubic);

	    tween.TweenProperty(shade, "color", new Color(0f, 0f, 0f, shadeAlpha), 0.12)
		    .SetEase(Tween.EaseType.Out)
		    .SetTrans(Tween.TransitionType.Cubic);

	    EventTileHoverTweens[tile] = tween;
    }

    private static IReadOnlyList<IHoverTip> CreateEventHoverTips(EventModel model)
    {
	    string description = GetFirstEventDescriptionParagraph(model);
	    string idLine = $"[color=#9a9a9a]{model.Id}[/color]";
	    string hoverDescription = string.IsNullOrWhiteSpace(description)
		    ? idLine
		    : $"{description}\n\n{idLine}";

	    return [new HoverTip(model.Title, hoverDescription)];
    }

    private static string GetFirstEventDescriptionParagraph(EventModel model)
    {
		
	    if (model is AncientEventModel ancient)
	    {
		    return LocMan.SafeFormatLocString(ancient.Epithet, string.Empty);
	    }
		
	    string text;
	    try
	    {
		    text = model.InitialDescription.GetFormattedText()
			    .Replace("[p]", "\n\n", StringComparison.OrdinalIgnoreCase);
	    }
	    catch (Exception exception)
	    {
		    GD.PushWarning($"LoadoutPanel: could not format initial event description for '{model.Id}'. {exception.Message}");
		    return string.Empty;
	    }

	    foreach (string paragraph in Regex.Split(text, @"(?:\r?\n){2,}"))
	    {
		    string cleaned = CommonHelpers.StripUiMarkup(paragraph);
		    if (!string.IsNullOrWhiteSpace(cleaned))
			    return cleaned;
	    }

	    return string.Empty;
    }

    public static void ConfigureWrappingEventTitle(MegaLabel label)
    {
	    label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
	    label.TextOverrunBehavior = TextServer.OverrunBehavior.NoTrimming;
	    label.AutoSizeEnabled = true;
	    label.MinFontSize = 19;
	    label.MaxFontSize = 26;
	    label.AddThemeFontSizeOverride("font_size", label.MaxFontSize);
    }

    private sealed class EventCatalogData
    {
	    public EventCatalogData(
		    IReadOnlyList<ActModel> acts,
		    IReadOnlyDictionary<string, EventPlacement> placements)
	    {
		    Acts = acts;
		    Placements = placements;
	    }

	    public IReadOnlyList<ActModel> Acts { get; }
	    public IReadOnlyDictionary<string, EventPlacement> Placements { get; }

	    public bool TryGetPlacement(EventModel eventModel, out EventPlacement placement)
	    {
		    return Placements.TryGetValue(eventModel.Id.ToString(), out placement);
	    }
    }

    private sealed record EventPlacement(
	    string GroupKey,
	    IReadOnlyList<ActModel> ApplicableActs,
	    IReadOnlySet<string> ApplicableActIds,
	    bool IsOther);

    private sealed class EventGroupPresentation
    {
	    private readonly IReadOnlyDictionary<string, SelectGroupHeader> _headers;
	    private readonly IReadOnlyDictionary<string, int> _leafOrder;

	    public EventGroupPresentation(
		    IReadOnlyList<string> groupOrder,
		    IReadOnlyList<string> descendingGroupOrder,
		    IReadOnlyDictionary<string, SelectGroupHeader> headers,
		    IReadOnlyDictionary<string, int> leafOrder)
	    {
		    GroupOrder = groupOrder;
		    DescendingGroupOrder = descendingGroupOrder;
		    _headers = headers;
		    _leafOrder = leafOrder;
	    }

	    public IReadOnlyList<string> GroupOrder { get; }
	    public IReadOnlyList<string> DescendingGroupOrder { get; }

	    public SelectGroupHeader GetHeader(string key)
	    {
		    return _headers.TryGetValue(key, out SelectGroupHeader header)
			    ? header
			    : new SelectGroupHeader(key);
	    }

	    public int GetLeafOrder(string key)
	    {
		    return _leafOrder.GetValueOrDefault(key, int.MaxValue);
	    }
    }
}
