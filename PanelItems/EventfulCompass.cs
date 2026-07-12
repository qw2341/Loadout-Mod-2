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
	private static bool InsertedRoomJumpControl = false;
    public static void Initialize()
    {
	    IReadOnlyList<EventModel> allEvents = ModelDb.AllEvents.Concat(ModelDb.AllAncients).Distinct().ToList();

        CommonHelpers.CreateAndAddLoadoutItem(
			allEvents,
			new SelectItemAdapter<EventModel>
			{
				GetId = eventModel => eventModel.Id.ToString(),
				GetName = eventModel => CommonHelpers.FormatEventTitle(eventModel),
				GetSearchText = eventModel => $"{eventModel.Id} {CommonHelpers.FormatEventTitle(eventModel)} {eventModel.InitialDescription}",
				CreateView = (eventModel, _) => CreateEventGridItem(eventModel)
			}, builder =>
			{
				builder.Options(new SelectScreenOptions { SelectionMode = SelectSelectionMode.None });
				builder.Materialization(SelectMaterializationMode.Lazy);
				builder.Layout(4, EventTileSize, 24, 24);
				builder.FilterGroup("layout", LocMan.Loc("FILTER_GROUP_LAYOUT", "Layout"));
				builder.Filter("default", LocMan.Loc("LAYOUT_DEFAULT", "Default"), eventModel => eventModel.LayoutType == EventLayoutType.Default, "layout");
				builder.Filter("combat", LocMan.Loc("LAYOUT_COMBAT", "Combat"), eventModel => eventModel.LayoutType == EventLayoutType.Combat, "layout");
				builder.Filter("ancient", LocMan.Loc("LAYOUT_ANCIENT", "Ancient"), eventModel => eventModel.LayoutType == EventLayoutType.Ancient, "layout");
				builder.FilterGroup("sharing", LocMan.Loc("FILTER_GROUP_SCOPE", "Scope"));
				builder.Filter("shared", LocMan.Loc("SCOPE_SHARED", "Shared"), eventModel => eventModel.IsShared, "sharing");
				builder.Filter("solo", LocMan.Loc("SCOPE_SOLO", "Solo"), eventModel => !eventModel.IsShared, "sharing");
				CommonHelpers.AddModFilters(builder, allEvents);
				builder.Sorter("name", LocMan.Loc("SORT_NAME", "Name"), (a, b) => string.Compare(CommonHelpers.FormatEventTitle(a), CommonHelpers.FormatEventTitle(b), StringComparison.Ordinal), activeByDefault: true);
				builder.Sorter("id", LocMan.Loc("SORT_ID", "ID"), (a, b) => string.Compare(a.Id.Entry, b.Id.Entry, StringComparison.Ordinal));
			}, UpsertRoomJumpControls,
			"EventfulCompass.png",
			LocMan.Loc("EVENTFULCOMPASS_TITLE", "Eventful Compass"),
			LocMan.Loc("EVENTFULCOMPASS_DESC", "Right-click this relic to select the event you want. Ctrl + right click to repeat the last action."),
			HandleEnterEventActivatedAsync,
			LastActionService.EventfulCompassKey,
			ReplayEventfulCompassLastActionAsync);
    }

    private static readonly Vector2 EventTileSize = new(264f, 144f);
    private static readonly Vector2I AncientPreviewTextureSize = new(360, 196);
    private const float EventTilePortraitRestAlpha = 0.45f;
    private const float EventTilePortraitHoverAlpha = 0.78f;
    private const float EventTileShadeHoverAlpha = 0.16f;
    private const string RoomJumpControlName = "EventfulCompassRoomJumpControls";
    private const string RoomJumpDropdownName = "EventfulCompassRoomDropdown";
    private const string RoomJumpButtonName = "EventfulCompassGoToButton";
    private static RoomType SelectedRoomType = RoomType.Treasure;
    private static readonly Dictionary<Control, Tween> EventTileHoverTweens = new();
    private static readonly Dictionary<string, SubViewport> AncientPreviewViewports = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Texture2D> AncientPreviewTextures = new(StringComparer.Ordinal);

    private static void UpsertRoomJumpControls(NGenericSelectScreen screen)
    {
	    if (InsertedRoomJumpControl) return;
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
	    InsertedRoomJumpControl = true;
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

    private static Control CreateEventGridItem(EventModel model)
    {
	    Button button = CommonHelpers.CreateModelButton(EventTileSize);
	    button.ClipContents = true;

	    TextureRect background = CreateEventTileBackground(model);
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

	    CommonHelpers.AttachHoverTips(button, CreateEventHoverTips(model));
	    return button;
    }

    private static TextureRect CreateEventTileBackground(EventModel model)
    {
	    if (model is AncientEventModel ancientEvent)
	    {
		    Texture2D ancientPreview = GetAncientBackgroundPreviewTexture(ancientEvent);
		    if (ancientPreview is not null)
			    return CreateTileBackground(ancientPreview);
	    }

	    try
	    {
		    Texture2D portrait = model.CreateInitialPortrait();
		    return CreateTileBackground(portrait);
	    }
	    catch (Exception)
	    {
		    // if (model is not AncientEventModel ancient)
		    // {
			   //  GD.PushWarning($"LoadoutPanel: could not load event portrait for '{model.Id}'. {exception.Message}");
			   //  return null;
		    // }
		    //
		    // try
		    // {
			   //  return CreateTileBackground(ancient.RunHistoryIcon);
		    // }
		    // catch (Exception iconException)
		    // {
			   //  try
			   //  {
				  //   return CreateTileBackground(ancient.MapIcon);
			   //  }
			   //  catch (Exception mapIconException)
			   //  {
				  //   GD.PushWarning(
					 //    $"LoadoutPanel: could not load ancient portrait/icon for '{model.Id}'. portrait={exception.Message}; runHistory={iconException.Message}; map={mapIconException.Message}");
				  //   return null;
			   //  }
		    // }
		    return null;
	    }
    }

    public static TextureRect CreateTileBackground(Texture2D texture)
    {
	    return new TextureRect
	    {
		    Texture = texture,
		    ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
		    StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
		    MouseFilter = Control.MouseFilterEnum.Ignore,
		    Modulate = new Color(1f, 1f, 1f, EventTilePortraitRestAlpha),
		    Position = Vector2.Zero,
		    Size = EventTileSize
	    };
    }

    private static Texture2D GetAncientBackgroundPreviewTexture(AncientEventModel model)
    {
	    string id = model.Id.ToString();
	    if (AncientPreviewTextures.TryGetValue(id, out Texture2D cachedTexture))
		    return cachedTexture;

	    try
	    {
		    SubViewport viewport = new()
		    {
			    Name = $"LoadoutAncientPreview_{CommonHelpers.MakeSafeNodeName(id)}",
			    Size = AncientPreviewTextureSize,
			    TransparentBg = false,
			    Disable3D = false,
			    RenderTargetUpdateMode = SubViewport.UpdateMode.Once
		    };

		    Control backgroundScene = model.CreateBackgroundScene().Instantiate<Control>(PackedScene.GenEditState.Disabled);
		    backgroundScene.MouseFilter = Control.MouseFilterEnum.Ignore;
		    backgroundScene.Position = Vector2.Zero;
		    backgroundScene.Size = new Vector2(1920f, 1080f);
		    backgroundScene.Scale = Vector2.One * Math.Max(AncientPreviewTextureSize.X / 1920f, AncientPreviewTextureSize.Y / 1080f);
		    viewport.AddChild(backgroundScene);

		    NLoadoutPanelRoot.Instance?.AddChild(viewport);
		    Texture2D texture = viewport.GetTexture();
		    AncientPreviewViewports[id] = viewport;
		    AncientPreviewTextures[id] = texture;
		    return texture;
	    }
	    catch (Exception exception)
	    {
		    GD.PushWarning($"LoadoutPanel: could not create ancient background preview for '{model.Id}'. {exception.Message}");
		    return null;
	    }
    }

    public static void ReleaseAncientPreviewCache()
    {
	    foreach (SubViewport viewport in AncientPreviewViewports.Values)
	    {
		    if (!GodotObject.IsInstanceValid(viewport))
			    continue;

		    viewport.GetParent()?.RemoveChild(viewport);
		    viewport.QueueFree();
	    }

	    AncientPreviewViewports.Clear();
	    AncientPreviewTextures.Clear();
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
}
