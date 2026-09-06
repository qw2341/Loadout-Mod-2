#nullable enable

namespace Loadout.UI.MapEditing;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using HarmonyLib;
using Loadout.Services.MapEditing;
using Loadout.UI.Managers;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves.Runs;

public static class MapEditingUiService
{
    private static readonly AccessTools.FieldRef<NMapLegendItem, MapPointType> LegendPointTypeField =
        AccessTools.FieldRefAccess<NMapLegendItem, MapPointType>("_pointType");

    private static NMapEditingToolbar? _toolbar;

    public static void Attach(NMapScreen screen)
    {
        if (_toolbar is not null && GodotObject.IsInstanceValid(_toolbar))
        {
            if (ReferenceEquals(_toolbar.Screen, screen))
                return;
            _toolbar.QueueFree();
        }

        if (RunManager.Instance.NetService.Type == MegaCrit.Sts2.Core.Multiplayer.Game.NetGameType.Client)
            return;

        _toolbar = new NMapEditingToolbar(screen) { Name = "LoadoutMapEditor" };
        screen.AddChild(_toolbar);
    }

    public static void Detach()
    {
        if (_toolbar is not null && GodotObject.IsInstanceValid(_toolbar))
            _toolbar.QueueFree();
        _toolbar = null;
    }

    public static bool HandleClickableInput(NClickableControl control, InputEvent inputEvent)
    {
        if (_toolbar is null || !GodotObject.IsInstanceValid(_toolbar) || !_toolbar.IsEditing)
            return false;

        if (control is NMapPoint point)
            return _toolbar.HandlePointInput(point, inputEvent);

        if (control is NMapLegendItem legend)
            return _toolbar.HandleLegendInput(LegendPointTypeField(legend), inputEvent);

        return false;
    }

    public static bool HandleScreenInput(NMapScreen screen, InputEvent inputEvent)
    {
        return _toolbar is not null
               && GodotObject.IsInstanceValid(_toolbar)
               && ReferenceEquals(_toolbar.Screen, screen)
               && _toolbar.HandleMapInput(inputEvent);
    }

    public static void OnMapSet(NMapScreen screen)
    {
        bool positionsChanged = ApplyStoredVisualPositions(screen);
        if (positionsChanged)
            NMapEditingToolbar.RebuildAllPaths(screen);
        if (_toolbar is not null && GodotObject.IsInstanceValid(_toolbar) && ReferenceEquals(_toolbar.Screen, screen))
            _toolbar.OnMapRebuilt();
    }

    public static bool ApplyStoredVisualPositions(NMapScreen screen)
    {
        RunState? runState = TryGetRunState();
        if (runState is null)
            return false;
        IReadOnlyDictionary<string, MapEditingPosition> positions =
            MapEditingService.GetCurrentPositions(runState);
        if (positions.Count == 0)
            return false;
        return ApplyVisualPositions(screen, positions);
    }

    public static bool ApplyVisualPositions(
        NMapScreen screen,
        IReadOnlyDictionary<string, MapEditingPosition> positions)
    {
        RunState? runState = TryGetRunState();
        if (runState is null)
            return false;

        bool changed = false;
        foreach (NMapPoint node in screen.GetNode<Control>("TheMap/Points").GetChildren().OfType<NMapPoint>())
        {
            string key = MapEditingService.KeyFor(node.Point, runState.Map);
            if (positions.TryGetValue(key, out MapEditingPosition position))
            {
                Vector2 target = position.ToVector2();
                if (node.Position.DistanceSquaredTo(target) > 0.01f)
                {
                    node.Position = target;
                    changed = true;
                }
            }
        }
        return changed;
    }

    private static RunState? TryGetRunState()
    {
        try
        {
            return RunManager.Instance.IsInProgress ? RunManager.Instance.DebugOnlyGetState() : null;
        }
        catch
        {
            return null;
        }
    }
}

public partial class NMapEditingToolbar : Control
{
    private static readonly AccessTools.FieldRef<NMapScreen,
        Dictionary<(MapCoord, MapCoord), IReadOnlyList<TextureRect>>> PathsField =
        AccessTools.FieldRefAccess<NMapScreen,
            Dictionary<(MapCoord, MapCoord), IReadOnlyList<TextureRect>>>("_paths");
    private static readonly AccessTools.FieldRef<NMapScreen, Dictionary<MapCoord, NMapPoint>> PointNodesField =
        AccessTools.FieldRefAccess<NMapScreen, Dictionary<MapCoord, NMapPoint>>("_mapPointDictionary");
    private static readonly MethodInfo GridGetter =
        AccessTools.PropertyGetter(typeof(ActMap), "Grid")
        ?? throw new MissingMethodException(typeof(ActMap).FullName, "get_Grid");
    private static readonly MethodInfo CreatePathMethod =
        AccessTools.Method(typeof(NMapScreen), "CreatePath", [typeof(Vector2), typeof(Vector2)])
        ?? throw new MissingMethodException(typeof(NMapScreen).FullName, "CreatePath");
    private static readonly Dictionary<Type, FieldInfo> GridBackingFields = [];
    private const int MaxNativeGridWidth = byte.MaxValue;
    private const string QuillPath = "res://images/packed/map/drawing_quill.png";
    private const string QuillGlowPath = "res://images/packed/map/drawing_quill_glow.png";
    private const string SharePath = "res://images/packed/statistics_screen/share_stats.png";
    private const string UndoPath = "res://images/atlases/compressed.sprites/back_button_arrow.tres";
    private const string ClearPath = "res://images/packed/map/drawing_clear.png";
    private const string ClearGlowPath = "res://images/packed/map/drawing_clear_glow.png";
    private const string ToolbarBackgroundPath = "res://images/ui/tiny_nine_patch.png";
    private const int MaxHistoryEntries = 64;

    private readonly NMapScreen _screen;
    private Control _points = null!;
    private NinePatchRect _background = null!;
    private GridContainer _buttons = null!;
    private NMapToolButton _editButton = null!;
    private NMapToolButton _copyButton = null!;
    private NMapToolButton _importButton = null!;
    private NMapToolButton _undoButton = null!;
    private NMapToolButton _redoButton = null!;
    private NMapToolButton _clearButton = null!;
    private MegaLabel _status = null!;
    private TextureRect _ghost = null!;
    private NMapConnectionPreview _connectionPreview = null!;
    private MapPointType? _pickedType;
    private NMapPoint? _dragNode;
    private Vector2 _dragStartPosition;
    private Vector2 _dragOffset;
    private MapCoord? _placementChainTail;
    private MapCoord? _linkSource;
    private (MapCoord Source, MapCoord Destination)? _highlightedConnection;
    private readonly Dictionary<TextureRect, Color> _highlightedTickColors = [];
    private readonly List<MapEditingHistoryState> _undoHistory = [];
    private readonly List<MapEditingHistoryState> _redoHistory = [];
    private MapEditingHistoryState? _dragHistoryState;
    private int _historyActIndex = -1;

    public NMapEditingToolbar(NMapScreen screen)
    {
        _screen = screen;
    }

    public NMapScreen Screen => _screen;
    public bool IsEditing { get; private set; }

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        _points = _screen.GetNode<Control>("TheMap/Points");
        BuildToolbar();
        _historyActIndex = TryGetRunState()?.CurrentActIndex ?? -1;
        _screen.Connect(CanvasItem.SignalName.VisibilityChanged, Callable.From(OnScreenVisibilityChanged));
        SetProcessInput(false);
    }

    public override void _ExitTree()
    {
        if (_screen.IsConnected(CanvasItem.SignalName.VisibilityChanged, Callable.From(OnScreenVisibilityChanged)))
            _screen.Disconnect(CanvasItem.SignalName.VisibilityChanged, Callable.From(OnScreenVisibilityChanged));
        if (IsEditing)
            SetEditing(false);
        SetProcessInput(false);
        base._ExitTree();
    }

    public override void _Input(InputEvent inputEvent)
    {
        if (!IsEditing || !_screen.IsVisibleInTree())
            return;

        if (inputEvent is InputEventMouseMotion motion)
        {
            UpdateGhostPosition();
            if (_linkSource.HasValue)
                UpdateConnectionPreview(motion.GlobalPosition);
            if (_dragNode is not null)
            {
                _dragNode.Position = _points.GetLocalMousePosition() + _dragOffset;
                ReflowConnectedPaths(_dragNode.Point.coord);
                GetViewport().SetInputAsHandled();
            }
            else
            {
                UpdateHighlightedConnection();
            }
            return;
        }

        if (inputEvent is InputEventMouseButton
            {
                ButtonIndex: MouseButton.Left,
                Pressed: false
            } && _dragNode is not null)
        {
            FinishDrag();
            GetViewport().SetInputAsHandled();
            return;
        }

        if (inputEvent is InputEventMouseButton
            {
                ButtonIndex: MouseButton.Right,
                Pressed: false
            } rightRelease && _linkSource.HasValue)
        {
            FinishConnectionDrag(FindPointAt(rightRelease.GlobalPosition));
            GetViewport().SetInputAsHandled();
            return;
        }

        if (inputEvent is not InputEventKey { Pressed: true, Echo: false } key)
            return;

        if (key.Keycode is Key.Delete or Key.Backspace)
        {
            DeleteHoveredTarget();
            GetViewport().SetInputAsHandled();
        }
        else if (key.Keycode == Key.Escape && HasTransientAction())
        {
            CancelTransientAction();
            GetViewport().SetInputAsHandled();
        }
    }

    public bool HandlePointInput(NMapPoint point, InputEvent inputEvent)
    {
        if (inputEvent is not InputEventMouseButton mouse)
            return false;

        if (!mouse.Pressed)
            return mouse.ButtonIndex is MouseButton.Left or MouseButton.Middle or MouseButton.Right;

        switch (mouse.ButtonIndex)
        {
            case MouseButton.Left:
                if (_pickedType.HasValue)
                    PlacePickedNode(keepArmed: Input.IsKeyPressed(Key.Shift));
                else
                    BeginDrag(point);
                break;
            case MouseButton.Middle:
                Pick(point.Point.PointType);
                break;
            case MouseButton.Right:
                BeginConnectionDrag(point.Point.coord);
                break;
            default:
                return false;
        }

        point.AcceptEvent();
        return true;
    }

    public bool HandleLegendInput(MapPointType pointType, InputEvent inputEvent)
    {
        if (inputEvent is not InputEventMouseButton { ButtonIndex: MouseButton.Left } mouse)
            return false;

        if (mouse.Pressed)
            Pick(pointType);
        return true;
    }

    public bool HandleMapInput(InputEvent inputEvent)
    {
        if (!IsEditing || inputEvent is not InputEventMouseButton mouse)
            return false;

        if (mouse.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
            return false;

        if (mouse.ButtonIndex == MouseButton.Left && mouse.Pressed && _pickedType.HasValue)
        {
            PlacePickedNode(keepArmed: Input.IsKeyPressed(Key.Shift));
            _screen.AcceptEvent();
            return true;
        }

        if (mouse.ButtonIndex == MouseButton.Right && mouse.Pressed)
        {
            CancelTransientAction();
            _screen.AcceptEvent();
            return true;
        }

        if (mouse.ButtonIndex == MouseButton.Middle)
        {
            _screen.AcceptEvent();
            return true;
        }

        return false;
    }

    public void OnMapRebuilt()
    {
        RestoreHighlightedTicks();
        _highlightedConnection = null;
        _dragNode = null;
        _dragHistoryState = null;
        _placementChainTail = null;
        UpdateGhostPosition();
        if (TryGetRunState() is { } runState && _historyActIndex != runState.CurrentActIndex)
        {
            _historyActIndex = runState.CurrentActIndex;
            _undoHistory.Clear();
            _redoHistory.Clear();
            SetHistoryButtonsAvailability();
        }
    }

    private void BuildToolbar()
    {
        _background = new NinePatchRect
        {
            Name = "Background",
            Texture = LoadTexture(ToolbarBackgroundPath),
            SelfModulate = new Color(0f, 0f, 0f, 0.75f),
            PatchMarginLeft = 12,
            PatchMarginTop = 12,
            PatchMarginRight = 12,
            PatchMarginBottom = 12,
            MouseFilter = MouseFilterEnum.Stop
        };
        _background.SetAnchorsPreset(LayoutPreset.BottomLeft);
        _background.OffsetLeft = 56f;
        _background.OffsetTop = -184f;
        _background.OffsetRight = 136f;
        _background.OffsetBottom = -116f;
        AddChild(_background);

        _buttons = new GridContainer
        {
            Name = "Buttons",
            Columns = 3,
            MouseFilter = MouseFilterEnum.Pass
        };
        _buttons.SetAnchorsPreset(LayoutPreset.BottomLeft);
        _buttons.OffsetLeft = 66f;
        _buttons.OffsetTop = -180f;
        _buttons.OffsetRight = 126f;
        _buttons.OffsetBottom = -120f;
        _buttons.AddThemeConstantOverride("h_separation", 0);
        _buttons.AddThemeConstantOverride("v_separation", 0);
        AddChild(_buttons);

        _editButton = CreateButton(
            "Edit",
            QuillPath,
            QuillGlowPath,
            ToggleEditing,
            LocMan.Loc("MAP_EDITOR_EDIT_TITLE", "Map Editor"),
            LocMan.Loc("MAP_EDITOR_EDIT_TOOLTIP", "Map editor controls: Left-drag moves; legend/Middle-click picks; Shift-place chain-links; Right-drag connects; Delete/Backspace removes; Right-click empty space cancels."),
            rainbow: true);
        _copyButton = CreateButton(
            "Copy",
            SharePath,
            SharePath,
            CopyMaps,
            LocMan.Loc("MAP_EDITOR_COPY_TITLE", "Export Maps"),
            LocMan.Loc("MAP_EDITOR_COPY_TOOLTIP", "Copy all edited act maps to the clipboard."));
        _importButton = CreateButton(
            "Import",
            SharePath,
            SharePath,
            ImportMaps,
            LocMan.Loc("MAP_EDITOR_IMPORT_TITLE", "Import Maps"),
            LocMan.Loc("MAP_EDITOR_IMPORT_TOOLTIP", "Import current and future act maps from the clipboard."),
            flipVertical: true);
        _undoButton = CreateButton(
            "Undo",
            UndoPath,
            UndoPath,
            Undo,
            LocMan.Loc("MAP_EDITOR_UNDO_TITLE", "Undo"),
            LocMan.Loc("MAP_EDITOR_UNDO_TOOLTIP", "Undo the last map edit, including an import or Clear All."));
        _redoButton = CreateButton(
            "Redo",
            UndoPath,
            UndoPath,
            Redo,
            LocMan.Loc("MAP_EDITOR_REDO_TITLE", "Redo"),
            LocMan.Loc("MAP_EDITOR_REDO_TOOLTIP", "Redo the last undone map edit."),
            flipHorizontal: true);
        _clearButton = CreateButton(
            "ClearAll",
            ClearPath,
            ClearGlowPath,
            ClearAll,
            LocMan.Loc("MAP_EDITOR_CLEAR_TITLE", "Clear All Nodes"),
            LocMan.Loc("MAP_EDITOR_CLEAR_TOOLTIP", "Remove every node except your current node, the Ancient room, and the boss room."));
        _buttons.AddChild(_editButton);
        _buttons.AddChild(_copyButton);
        _buttons.AddChild(_importButton);
        _buttons.AddChild(_undoButton);
        _buttons.AddChild(_redoButton);
        _buttons.AddChild(_clearButton);

        _status = new MegaLabel
        {
            Name = "Status",
            MouseFilter = MouseFilterEnum.Ignore,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            MinFontSize = 12,
            MaxFontSize = 20
        };
        _status.SetAnchorsPreset(LayoutPreset.BottomLeft);
        _status.OffsetLeft = 56f;
        _status.OffsetTop = -224f;
        _status.OffsetRight = 676f;
        _status.OffsetBottom = -186f;
        _status.AddThemeFontOverride("font", LoadFont("res://themes/kreon_bold_glyph_space_two.tres"));
        _status.AddThemeFontSizeOverride("font_size", 20);
        _status.AddThemeColorOverride("font_color", new Color(1f, 0.964706f, 0.886275f));
        _status.AddThemeColorOverride("font_outline_color", Colors.Black);
        _status.AddThemeConstantOverride("outline_size", 8);
        AddChild(_status);

        _ghost = new TextureRect
        {
            Name = "PickedNodeGhost",
            Size = new Vector2(74f, 74f),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            Modulate = new Color(1f, 1f, 1f, 0.5f),
            MouseFilter = MouseFilterEnum.Ignore,
            Visible = false,
            ZIndex = 100
        };
        AddChild(_ghost);

        _connectionPreview = new NMapConnectionPreview
        {
            Name = "ConnectionPreview",
            MouseFilter = MouseFilterEnum.Ignore,
            ZIndex = 90,
            Visible = false
        };
        _connectionPreview.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(_connectionPreview);
        SetToolbarExpanded(false);
        _editButton.SetActive(false);
        _status.SetTextAutoSize(string.Empty);
        SetHistoryButtonsAvailability();
    }

    private NMapToolButton CreateButton(
        string name,
        string iconPath,
        string glowPath,
        Action action,
        string hoverTitle,
        string hoverDescription,
        bool rainbow = false,
        bool flipVertical = false,
        bool flipHorizontal = false)
    {
        return new NMapToolButton
        {
            Name = name,
            CustomMinimumSize = new Vector2(60f, 60f),
            Size = new Vector2(60f, 60f),
            FocusMode = FocusModeEnum.All,
            MouseFilter = MouseFilterEnum.Stop,
            IconPath = iconPath,
            GlowPath = glowPath,
            RainbowOutline = rainbow,
            FlipVertical = flipVertical,
            FlipHorizontal = flipHorizontal,
            HoverTitle = hoverTitle,
            HoverDescription = hoverDescription,
            HoverAnchor = _background,
            Activated = action
        };
    }

    private void ToggleEditing() => SetEditing(!IsEditing);

    private void SetEditing(bool editing)
    {
        IsEditing = editing;
        SetProcessInput(editing && _screen.IsVisibleInTree());
        CancelTransientAction();
        if (_editButton is not null)
        {
            SetToolbarExpanded(editing);
            _editButton.SetActive(editing);
        }

        if (!IsInsideTree())
            return;

        if (editing)
            _screen.Drawings.SetDrawingModeLocal(DrawingMode.None);
        SetNativeDrawingToolsEnabled(!editing);
        SetStatus(editing
            ? LocMan.Loc("MAP_EDITOR_ACTIVE", "Map editing mode")
            : string.Empty);
    }

    private void SetToolbarExpanded(bool expanded)
    {
        _copyButton.Visible = expanded;
        _importButton.Visible = expanded;
        _undoButton.Visible = expanded;
        _redoButton.Visible = expanded;
        _clearButton.Visible = expanded;
        _background.OffsetRight = expanded ? 264f : 136f;
        _background.OffsetBottom = expanded ? -40f : -116f;
        _buttons.OffsetRight = expanded ? 246f : 126f;
        _buttons.OffsetBottom = expanded ? -60f : -120f;
    }

    private void OnScreenVisibilityChanged()
    {
        bool visible = _screen.IsVisibleInTree();
        SetProcessInput(IsEditing && visible);
        if (!visible)
        {
            if (IsEditing)
                SetEditing(false);
            else
                CancelTransientAction();
        }
    }

    private void SetNativeDrawingToolsEnabled(bool enabled)
    {
        NClickableControl[] controls =
        [
            _screen.GetNode<NMapDrawButton>("%DrawButton"),
            _screen.GetNode<NMapEraseButton>("%EraseButton"),
            _screen.GetNode<NMapClearButton>("%ClearButton")
        ];
        foreach (NClickableControl control in controls)
        {
            if (enabled)
                control.Enable();
            else
                control.Disable();
        }
    }

    private void CopyMaps()
    {
        RunState? runState = TryGetRunState();
        string message;
        if (runState is not null)
            MapEditingService.CopyToClipboard(runState, out message);
        else
            message = LocMan.Loc("MAP_EDITOR_NO_RUN", "No active run map.");
        SetStatus(message);
    }

    private void ImportMaps()
    {
        RunState? runState = TryGetRunState();
        string message;
        if (runState is not null)
        {
            CancelTransientAction();
            MapEditingHistoryState? before = CaptureHistoryState(runState);
            if (before is null)
                return;
            if (MapEditingService.ImportFromClipboard(runState, _screen, out message))
                RecordSuccessfulEdit(before);
        }
        else
            message = LocMan.Loc("MAP_EDITOR_NO_RUN", "No active run map.");
        SetStatus(message);
    }

    private void Undo()
    {
        if (_undoHistory.Count == 0 || TryGetRunState() is not { } runState)
        {
            SetStatus(LocMan.Loc("MAP_EDITOR_NOTHING_TO_UNDO", "Nothing to undo."));
            return;
        }

        CancelTransientAction();
        MapEditingHistoryState? current = CaptureHistoryState(runState);
        if (current is null)
            return;
        MapEditingHistoryState previous = _undoHistory[^1];
        if (!MapEditingService.TryRestoreHistoryState(runState, _screen, previous, out string error))
        {
            SetStatus(error);
            return;
        }

        _undoHistory.RemoveAt(_undoHistory.Count - 1);
        _redoHistory.Add(current);
        SetHistoryButtonsAvailability();
        SetStatus(LocMan.Loc("MAP_EDITOR_UNDONE", "Undid map edit."));
    }

    private void Redo()
    {
        if (_redoHistory.Count == 0 || TryGetRunState() is not { } runState)
        {
            SetStatus(LocMan.Loc("MAP_EDITOR_NOTHING_TO_REDO", "Nothing to redo."));
            return;
        }

        CancelTransientAction();
        MapEditingHistoryState? current = CaptureHistoryState(runState);
        if (current is null)
            return;
        MapEditingHistoryState next = _redoHistory[^1];
        if (!MapEditingService.TryRestoreHistoryState(runState, _screen, next, out string error))
        {
            SetStatus(error);
            return;
        }

        _redoHistory.RemoveAt(_redoHistory.Count - 1);
        _undoHistory.Add(current);
        SetHistoryButtonsAvailability();
        SetStatus(LocMan.Loc("MAP_EDITOR_REDONE", "Redid map edit."));
    }

    private void ClearAll()
    {
        if (TryGetRunState() is not { } runState)
            return;

        CancelTransientAction();
        MapCoord? currentCoord = runState.VisitedMapCoords.Count > 0
            ? runState.VisitedMapCoords[^1]
            : null;
        NMapPoint[] nodesToDelete = GetPointNodes()
            .Where(node => !ReferenceEquals(node.Point, runState.Map.StartingMapPoint)
                           && !ReferenceEquals(node.Point, runState.Map.BossMapPoint)
                           && !ReferenceEquals(node.Point, runState.Map.SecondBossMapPoint)
                           && (!currentCoord.HasValue || node.Point.coord != currentCoord.Value))
            .ToArray();
        if (nodesToDelete.Length == 0)
        {
            SetStatus(LocMan.Loc("MAP_EDITOR_NOTHING_TO_CLEAR", "No removable nodes remain."));
            return;
        }

        MapEditingHistoryState? before = CaptureHistoryState(runState);
        if (before is null)
            return;

        HashSet<MapPoint> removed = nodesToDelete.Select(node => node.Point).ToHashSet();
        MapPoint[] allPoints = GetPointNodes().Select(node => node.Point).ToArray();
        (MapPoint Source, MapPoint Destination)[] removedConnections = allPoints
            .SelectMany(source => source.Children
                .Where(destination => removed.Contains(source) || removed.Contains(destination))
                .Select(destination => (source, destination)))
            .ToArray();
        Dictionary<MapPoint, bool> wasStartingPoint = removed.ToDictionary(
            point => point,
            point => runState.Map.startMapPoints.Contains(point));

        foreach ((MapPoint source, MapPoint destination) in removedConnections)
            source.RemoveChildPoint(destination);
        MapPoint?[,] grid = GetGrid(runState.Map);
        foreach (MapPoint point in removed)
        {
            runState.Map.startMapPoints.Remove(point);
            grid[point.coord.col, point.coord.row] = null;
        }

        Dictionary<string, MapEditingPosition> positions = CapturePositions(runState);
        foreach (MapPoint point in removed)
            positions.Remove($"p:{point.coord.col}:{point.coord.row}");
        if (!MapEditingService.CommitCurrentAct(
                runState,
                SerializableActMap.FromActMap(runState.Map),
                positions,
                out string error))
        {
            foreach (MapPoint point in removed)
            {
                grid[point.coord.col, point.coord.row] = point;
                if (wasStartingPoint[point])
                    runState.Map.startMapPoints.Add(point);
            }
            foreach ((MapPoint source, MapPoint destination) in removedConnections)
                source.AddChildPoint(destination);
            SetStatus(error);
            return;
        }

        RecordSuccessfulEdit(before);
        foreach (NMapPoint node in nodesToDelete)
        {
            RemoveConnectionVisualsFor(node.Point.coord);
            PointNodesField(_screen).Remove(node.Point.coord);
            node.QueueFree();
        }
        foreach (NMapPoint node in PointNodesField(_screen).Values)
            RefreshTravelability(node, runState);
        SetStatus(LocMan.Loc("MAP_EDITOR_CLEARED", "Cleared removable map nodes."));
    }

    private MapEditingHistoryState? CaptureHistoryState(RunState runState)
    {
        if (MapEditingService.TryCaptureHistoryState(
                runState,
                CapturePositions(runState),
                out MapEditingHistoryState state,
                out string error))
        {
            return state;
        }

        SetStatus(error);
        return null;
    }

    private void RecordSuccessfulEdit(MapEditingHistoryState before)
    {
        _undoHistory.Add(before);
        if (_undoHistory.Count > MaxHistoryEntries)
            _undoHistory.RemoveAt(0);
        _redoHistory.Clear();
        SetHistoryButtonsAvailability();
    }

    private void SetHistoryButtonsAvailability()
    {
        if (_undoButton is null || _redoButton is null)
            return;
        _undoButton.SetAvailable(_undoHistory.Count > 0);
        _redoButton.SetAvailable(_redoHistory.Count > 0);
    }

    private void Pick(MapPointType pointType)
    {
        _pickedType = pointType;
        _placementChainTail = null;
        _linkSource = null;
        _ghost.Texture = LoadPointTexture(pointType);
        _ghost.Visible = _ghost.Texture is not null;
        UpdateGhostPosition();
        SetStatus(LocMan.Loc("MAP_EDITOR_PICKED", "Picked {0}.", pointType));
    }

    private void PlacePickedNode(bool keepArmed)
    {
        if (!_pickedType.HasValue || TryGetRunState() is not { } runState)
            return;

        Vector2 position = _points.GetLocalMousePosition();
        int row = FindNearestRow(position.Y, runState);
        MapEditingHistoryState? before = CaptureHistoryState(runState);
        if (before is null)
            return;
        MapPoint?[,] originalGrid = GetGrid(runState.Map);
        MapPoint?[,] grid = originalGrid;
        int column = -1;
        for (int candidate = 0; candidate < grid.GetLength(0); candidate++)
        {
            if (grid[candidate, row] is null)
            {
                column = candidate;
                break;
            }
        }
        bool expandedGrid = false;
        if (column < 0 && grid.GetLength(0) < MaxNativeGridWidth)
        {
            grid = ExpandGrid(runState.Map, grid, MaxNativeGridWidth);
            column = originalGrid.GetLength(0);
            expandedGrid = true;
        }
        if (column < 0)
        {
            SetStatus(LocMan.Loc("MAP_EDITOR_ROW_FULL", "This logical floor has no free node slots."));
            return;
        }

        MapCoord coord = new(column, row);
        MapPoint point = new(column, row)
        {
            PointType = _pickedType.Value,
            CanBeModified = true
        };
        MapPoint? chainSource = keepArmed && _placementChainTail.HasValue
            ? runState.Map.GetPoint(_placementChainTail.Value)
            : null;
        grid[column, row] = point;
        chainSource?.AddChildPoint(point);

        NNormalMapPoint node = NNormalMapPoint.Create(point, _screen, runState);
        Vector2 nodePosition = position - node.Size * 0.5f;
        Dictionary<string, MapEditingPosition> positions = CapturePositions(runState);
        positions[$"p:{column}:{row}"] = MapEditingPosition.FromVector2(nodePosition);
        SerializableActMap map = SerializableActMap.FromActMap(runState.Map);
        if (MapEditingService.CommitCurrentAct(runState, map, positions, out string error))
        {
            RecordSuccessfulEdit(before);
            PointNodesField(_screen).Add(coord, node);
            _points.AddChild(node);
            node.Position = nodePosition;
            if (chainSource is not null)
            {
                CreateConnectionVisual(chainSource, point);
                RefreshTravelability(node, runState);
            }
            _placementChainTail = keepArmed ? coord : null;
            SetStatus(LocMan.Loc("MAP_EDITOR_NODE_PLACED", "Placed {0}.", _pickedType.Value));
            if (!keepArmed)
                ClearPicker();
        }
        else
        {
            node.Free();
            chainSource?.RemoveChildPoint(point);
            grid[column, row] = null;
            if (expandedGrid)
                SetGrid(runState.Map, originalGrid);
            SetStatus(error);
        }
    }

    private int FindNearestRow(float y, RunState runState)
    {
        List<(int Row, float Y)> rows = GetPointNodes()
            .Where(node => !ReferenceEquals(node.Point, runState.Map.StartingMapPoint)
                           && !ReferenceEquals(node.Point, runState.Map.BossMapPoint)
                           && !ReferenceEquals(node.Point, runState.Map.SecondBossMapPoint))
            .GroupBy(node => node.Point.coord.row)
            .Select(group => (group.Key, group.Average(node => node.Position.Y + node.Size.Y * 0.5f)))
            .ToList();
        return rows.Count == 0
            ? 0
            : rows.MinBy(candidate => Math.Abs(candidate.Y - y)).Row;
    }

    private void BeginDrag(NMapPoint point)
    {
        if (TryGetRunState() is not { } runState)
            return;
        _dragHistoryState = CaptureHistoryState(runState);
        if (_dragHistoryState is null)
            return;
        _dragNode = point;
        _dragStartPosition = point.Position;
        _dragOffset = point.Position - _points.GetLocalMousePosition();
        _linkSource = null;
        RestoreHighlightedTicks();
        SetStatus(LocMan.Loc("MAP_EDITOR_DRAGGING", "Dragging node."));
    }

    private void FinishDrag()
    {
        if (_dragNode is null || TryGetRunState() is not { } runState)
            return;

        NMapPoint dragged = _dragNode;
        _dragNode = null;
        MapEditingHistoryState? before = _dragHistoryState;
        _dragHistoryState = null;
        bool requiredAnchor = ReferenceEquals(dragged.Point, runState.Map.StartingMapPoint)
                              || ReferenceEquals(dragged.Point, runState.Map.BossMapPoint)
                              || ReferenceEquals(dragged.Point, runState.Map.SecondBossMapPoint);
        Rect2 safeScreen = _screen.GetGlobalRect().Grow(-36f);
        if (!safeScreen.HasPoint(GetViewport().GetMousePosition()))
        {
            if (requiredAnchor)
            {
                dragged.Position = _dragStartPosition;
                ReflowConnectedPaths(dragged.Point.coord);
                SetStatus(LocMan.Loc("MAP_EDITOR_ANCHOR_REQUIRED", "Start and boss anchors cannot be deleted."));
                return;
            }

            DeleteNode(dragged, before);
            return;
        }

        if (dragged.Position.DistanceSquaredTo(_dragStartPosition) <= 0.01f)
        {
            SetStatus(LocMan.Loc("MAP_EDITOR_ACTIVE", "Map editing mode"));
            return;
        }

        if (CommitTopology(runState))
        {
            RefreshConnectedPaths(dragged.Point.coord);
            if (before is not null)
                RecordSuccessfulEdit(before);
            SetStatus(LocMan.Loc("MAP_EDITOR_NODE_MOVED", "Moved node."));
        }
        else
        {
            dragged.Position = _dragStartPosition;
            ReflowConnectedPaths(dragged.Point.coord);
        }
    }

    private void BeginConnectionDrag(MapCoord coord)
    {
        _linkSource = coord;
        ClearPicker();
        UpdateConnectionPreview(GetViewport().GetMousePosition());
        SetStatus(LocMan.Loc("MAP_EDITOR_LINK_START", "Select the destination node."));
    }

    private void FinishConnectionDrag(NMapPoint? destinationNode)
    {
        if (!_linkSource.HasValue)
            return;

        MapCoord source = _linkSource.Value;
        _linkSource = null;
        _connectionPreview.HideLine();
        if (destinationNode is null || TryGetRunState() is not { } runState)
        {
            SetStatus(LocMan.Loc("MAP_EDITOR_ACTIVE", "Map editing mode"));
            return;
        }

        MapPoint? sourcePoint = runState.Map.GetPoint(source);
        MapPoint destinationPoint = destinationNode.Point;
        if (sourcePoint is null)
            return;
        if (sourcePoint.Children.Contains(destinationPoint))
        {
            SetStatus(LocMan.Loc("MAP_EDITOR_LINK_EXISTS", "That connection already exists."));
            return;
        }

        MapEditingHistoryState? before = CaptureHistoryState(runState);
        if (before is null)
            return;

        sourcePoint.AddChildPoint(destinationPoint);
        if (!CommitTopology(runState))
        {
            sourcePoint.RemoveChildPoint(destinationPoint);
            return;
        }
        RecordSuccessfulEdit(before);
        CreateConnectionVisual(sourcePoint, destinationPoint);
        RefreshTravelability(destinationNode, runState);
        SetStatus(LocMan.Loc("MAP_EDITOR_LINK_ADDED", "Added connection."));
    }

    private void DeleteHoveredTarget()
    {
        NMapPoint? hovered = GetPointNodes()
            .LastOrDefault(node => node.GetGlobalRect().HasPoint(GetViewport().GetMousePosition()));
        if (hovered is not null)
        {
            DeleteNode(hovered);
            return;
        }

        if (_highlightedConnection.HasValue)
            DeleteConnection(_highlightedConnection.Value);
    }

    private void DeleteNode(NMapPoint node, MapEditingHistoryState? historyState = null)
    {
        if (TryGetRunState() is not { } runState)
            return;

        if (ReferenceEquals(node.Point, runState.Map.StartingMapPoint)
            || ReferenceEquals(node.Point, runState.Map.BossMapPoint)
            || ReferenceEquals(node.Point, runState.Map.SecondBossMapPoint))
        {
            SetStatus(LocMan.Loc("MAP_EDITOR_ANCHOR_REQUIRED", "Start and boss anchors cannot be deleted."));
            return;
        }

        historyState ??= CaptureHistoryState(runState);
        if (historyState is null)
            return;

        MapCoord coord = node.Point.coord;
        MapPoint point = node.Point;
        MapPoint[] parents = point.parents.ToArray();
        MapPoint[] children = point.Children.ToArray();
        foreach (MapPoint parent in parents)
            parent.RemoveChildPoint(point);
        foreach (MapPoint child in children)
            point.RemoveChildPoint(child);
        bool wasStartMapPoint = runState.Map.startMapPoints.Remove(point);
        GetGrid(runState.Map)[coord.col, coord.row] = null;
        Dictionary<string, MapEditingPosition> positions = CapturePositions(runState);
        positions.Remove($"p:{coord.col}:{coord.row}");
        if (MapEditingService.CommitCurrentAct(
                runState,
                SerializableActMap.FromActMap(runState.Map),
                positions,
                out string error))
        {
            RecordSuccessfulEdit(historyState);
            RemoveConnectionVisualsFor(coord);
            PointNodesField(_screen).Remove(coord);
            node.QueueFree();
            SetStatus(LocMan.Loc("MAP_EDITOR_NODE_DELETED", "Deleted node."));
        }
        else
        {
            GetGrid(runState.Map)[coord.col, coord.row] = point;
            foreach (MapPoint parent in parents)
                parent.AddChildPoint(point);
            foreach (MapPoint child in children)
                point.AddChildPoint(child);
            if (wasStartMapPoint)
                runState.Map.startMapPoints.Add(point);
            SetStatus(error);
        }
    }

    private void DeleteConnection((MapCoord Source, MapCoord Destination) connection)
    {
        if (TryGetRunState() is not { } runState)
            return;

        MapPoint? source = runState.Map.GetPoint(connection.Source);
        MapPoint? destination = runState.Map.GetPoint(connection.Destination);
        if (source is null || destination is null || !source.Children.Contains(destination))
            return;

        MapEditingHistoryState? before = CaptureHistoryState(runState);
        if (before is null)
            return;

        source.RemoveChildPoint(destination);
        _highlightedConnection = null;
        RestoreHighlightedTicks();
        if (!CommitTopology(runState))
        {
            source.AddChildPoint(destination);
            return;
        }
        RecordSuccessfulEdit(before);
        RemoveConnectionVisual(connection);
        if (PointNodesField(_screen).TryGetValue(connection.Destination, out NMapPoint? destinationNode))
            RefreshTravelability(destinationNode, runState);
        SetStatus(LocMan.Loc("MAP_EDITOR_LINK_DELETED", "Deleted connection."));
    }

    private bool CommitTopology(RunState runState)
    {
        Dictionary<string, MapEditingPosition> positions = CapturePositions(runState);
        if (!MapEditingService.CommitCurrentAct(
                runState,
                SerializableActMap.FromActMap(runState.Map),
                positions,
                out string error))
        {
            SetStatus(error);
            return false;
        }
        return true;
    }

    private Dictionary<string, MapEditingPosition> CapturePositions(RunState runState)
    {
        return GetPointNodes().ToDictionary(
            node => MapEditingService.KeyFor(node.Point, runState.Map),
            node => MapEditingPosition.FromVector2(node.Position),
            StringComparer.Ordinal);
    }

    private void UpdateGhostPosition()
    {
        if (_ghost is null || !_ghost.Visible)
            return;
        _ghost.GlobalPosition = GetViewport().GetMousePosition() - _ghost.Size * 0.5f;
    }

    private void UpdateConnectionPreview(Vector2 mouseGlobalPosition)
    {
        if (!_linkSource.HasValue
            || !PointNodesField(_screen).TryGetValue(_linkSource.Value, out NMapPoint? sourceNode))
        {
            _connectionPreview.HideLine();
            return;
        }

        Color color = TryGetRunState()?.Act.MapUntraveledColor ?? Colors.White;
        _connectionPreview.ShowLine(sourceNode.GetGlobalRect().GetCenter(), mouseGlobalPosition, color);
    }

    private void UpdateHighlightedConnection()
    {
        RestoreHighlightedTicks();
        _highlightedConnection = FindClosestConnection(14f);
        if (!_highlightedConnection.HasValue)
            return;

        if (!PathsField(_screen).TryGetValue(
                (_highlightedConnection.Value.Source, _highlightedConnection.Value.Destination),
                out IReadOnlyList<TextureRect>? ticks))
        {
            return;
        }

        foreach (TextureRect tick in ticks)
        {
            _highlightedTickColors[tick] = tick.Modulate;
            tick.Modulate = new Color(1f, 0.3f, 0.3f, 1f);
        }
    }

    private (MapCoord Source, MapCoord Destination)? FindClosestConnection(float maxDistance)
    {
        Vector2 mouse = GetViewport().GetMousePosition();
        Dictionary<MapCoord, NMapPoint> nodes = PointNodesField(_screen);
        float bestDistance = maxDistance;
        (MapCoord, MapCoord)? best = null;
        foreach (NMapPoint sourceNode in nodes.Values)
        {
            foreach (MapPoint child in sourceNode.Point.Children)
            {
                if (!nodes.TryGetValue(child.coord, out NMapPoint? childNode))
                    continue;
                Vector2 start = sourceNode.GetGlobalRect().GetCenter();
                Vector2 end = childNode.GetGlobalRect().GetCenter();
                float distance = DistanceToSegment(mouse, start, end);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = (sourceNode.Point.coord, child.coord);
                }
            }
        }
        return best;
    }

    private void RestoreHighlightedTicks()
    {
        foreach ((TextureRect tick, Color color) in _highlightedTickColors)
        {
            if (GodotObject.IsInstanceValid(tick))
                tick.Modulate = color;
        }
        _highlightedTickColors.Clear();
    }

    private void ReflowConnectedPaths(MapCoord moved)
    {
        Dictionary<MapCoord, NMapPoint> nodes = PointNodesField(_screen);
        foreach (((MapCoord source, MapCoord destination), IReadOnlyList<TextureRect> ticks) in PathsField(_screen))
        {
            if (source != moved && destination != moved)
                continue;
            if (!nodes.TryGetValue(source, out NMapPoint? sourceNode)
                || !nodes.TryGetValue(destination, out NMapPoint? destinationNode))
            {
                continue;
            }
            ReflowPath(sourceNode, destinationNode, ticks);
        }
    }

    private void RefreshConnectedPaths(MapCoord changed)
    {
        RunState? runState = TryGetRunState();
        if (runState is null)
            return;
        Dictionary<(MapCoord, MapCoord), IReadOnlyList<TextureRect>> paths = PathsField(_screen);
        (MapCoord Source, MapCoord Destination)[] connections = paths.Keys
            .Where(connection => connection.Item1 == changed || connection.Item2 == changed)
            .Select(connection => (connection.Item1, connection.Item2))
            .ToArray();
        foreach ((MapCoord source, MapCoord destination) in connections)
        {
            RemoveConnectionVisual((source, destination));
            MapPoint? sourcePoint = runState.Map.GetPoint(source);
            MapPoint? destinationPoint = runState.Map.GetPoint(destination);
            if (sourcePoint is not null && destinationPoint is not null)
                CreateConnectionVisual(sourcePoint, destinationPoint);
        }
    }

    public static void ApplyActIncrementally(
        RunState runState,
        NMapScreen screen,
        MapEditingActArchive act,
        IReadOnlyDictionary<MapCoord, List<AbstractModel>> quests)
    {
        ActMap map = runState.Map;
        SavedActMap targetMap = new(act.Map);
        if ((map.SecondBossMapPoint is null) != (targetMap.SecondBossMapPoint is null))
            throw new InvalidOperationException("Map editor history changed the boss layout.");
        Control pointsHolder = screen.GetNode<Control>("TheMap/Points");
        Dictionary<MapCoord, NMapPoint> pointNodes = PointNodesField(screen);
        Dictionary<(MapCoord, MapCoord), IReadOnlyList<TextureRect>> paths = PathsField(screen);
        MapPoint[] currentPoints = EnumerateIncludingAnchors(map).ToArray();
        Dictionary<MapPoint, NMapPoint> nodeByPoint = pointNodes.Values
            .ToDictionary(node => node.Point, node => node);
        Dictionary<MapCoord, MapPoint> currentOrdinary = map.GetAllMapPoints()
            .ToDictionary(point => point.coord, point => point);
        Dictionary<MapCoord, MapPoint> targetOrdinary = targetMap.GetAllMapPoints()
            .ToDictionary(point => point.coord, point => point);
        HashSet<MapPoint> removedPoints = currentOrdinary
            .Where(pair => !targetOrdinary.ContainsKey(pair.Key))
            .Select(pair => pair.Value)
            .ToHashSet();
        HashSet<MapCoord> changedPositions = [];
        HashSet<MapCoord> changedVisuals = [];

        foreach (MapPoint source in currentPoints)
        {
            foreach (MapPoint destination in source.Children.Where(child => removedPoints.Contains(source) || removedPoints.Contains(child)).ToArray())
            {
                changedVisuals.Add(source.coord);
                changedVisuals.Add(destination.coord);
                source.RemoveChildPoint(destination);
            }
        }
        foreach (MapPoint removed in removedPoints)
        {
            pointNodes.Remove(removed.coord);
            if (nodeByPoint.TryGetValue(removed, out NMapPoint? removedNode))
                removedNode.QueueFree();
        }

        (MapPoint Current, MapPoint Target)[] anchors = targetMap.SecondBossMapPoint is null
            ? [(map.StartingMapPoint, targetMap.StartingMapPoint), (map.BossMapPoint, targetMap.BossMapPoint)]
            :
            [
                (map.StartingMapPoint, targetMap.StartingMapPoint),
                (map.BossMapPoint, targetMap.BossMapPoint),
                (map.SecondBossMapPoint ?? throw new InvalidOperationException("Map editor history changed the boss layout."), targetMap.SecondBossMapPoint)
            ];
        foreach ((MapPoint current, MapPoint target) in anchors)
        {
            MapCoord oldCoord = current.coord;
            if (nodeByPoint.TryGetValue(current, out NMapPoint? anchorNode) && oldCoord != target.coord)
            {
                pointNodes.Remove(oldCoord);
                current.coord = target.coord;
                pointNodes[target.coord] = anchorNode;
                changedPositions.Add(oldCoord);
                changedPositions.Add(target.coord);
            }
            else
            {
                current.coord = target.coord;
            }
            if (current.PointType != target.PointType || current.CanBeModified != target.CanBeModified)
                changedVisuals.Add(current.coord);
            current.PointType = target.PointType;
            current.CanBeModified = target.CanBeModified;
        }

        Dictionary<MapCoord, MapPoint> liveOrdinary = [];
        MapPoint?[,] replacementGrid = new MapPoint?[act.Map.GridWidth, act.Map.GridHeight];
        foreach ((MapCoord coord, MapPoint target) in targetOrdinary)
        {
            MapPoint live;
            if (currentOrdinary.TryGetValue(coord, out MapPoint? existing) && !removedPoints.Contains(existing))
            {
                live = existing;
                if (live.PointType != target.PointType || live.CanBeModified != target.CanBeModified)
                    changedVisuals.Add(coord);
                live.PointType = target.PointType;
                live.CanBeModified = target.CanBeModified;
            }
            else
            {
                live = new MapPoint(coord.col, coord.row)
                {
                    PointType = target.PointType,
                    CanBeModified = target.CanBeModified
                };
                NNormalMapPoint newNode = NNormalMapPoint.Create(live, screen, runState);
                pointNodes[coord] = newNode;
                pointsHolder.AddChild(newNode);
                nodeByPoint[live] = newNode;
                changedVisuals.Add(coord);
            }
            liveOrdinary[coord] = live;
            replacementGrid[coord.col, coord.row] = live;
        }
        SetGrid(map, replacementGrid);

        Dictionary<MapCoord, MapPoint> liveByCoord = liveOrdinary.ToDictionary(pair => pair.Key, pair => pair.Value);
        liveByCoord[map.StartingMapPoint.coord] = map.StartingMapPoint;
        liveByCoord[map.BossMapPoint.coord] = map.BossMapPoint;
        if (map.SecondBossMapPoint is not null)
            liveByCoord[map.SecondBossMapPoint.coord] = map.SecondBossMapPoint;

        map.startMapPoints.Clear();
        foreach (MapPoint targetStart in targetMap.startMapPoints)
        {
            if (liveByCoord.TryGetValue(targetStart.coord, out MapPoint? liveStart))
                map.startMapPoints.Add(liveStart);
        }

        HashSet<(MapCoord Source, MapCoord Destination)> desiredConnections = EnumerateIncludingAnchors(targetMap)
            .SelectMany(source => source.Children.Select(destination => (source.coord, destination.coord)))
            .ToHashSet();
        foreach (MapPoint source in EnumerateIncludingAnchors(map).ToArray())
        {
            foreach (MapPoint destination in source.Children.ToArray())
            {
                if (desiredConnections.Contains((source.coord, destination.coord)))
                    continue;
                source.RemoveChildPoint(destination);
                changedVisuals.Add(source.coord);
                changedVisuals.Add(destination.coord);
            }
        }
        foreach ((MapCoord sourceCoord, MapCoord destinationCoord) in desiredConnections)
        {
            if (!liveByCoord.TryGetValue(sourceCoord, out MapPoint? source)
                || !liveByCoord.TryGetValue(destinationCoord, out MapPoint? destination)
                || source.Children.Contains(destination))
            {
                continue;
            }
            source.AddChildPoint(destination);
            changedVisuals.Add(sourceCoord);
            changedVisuals.Add(destinationCoord);
        }

        foreach ((MapCoord coord, MapPoint point) in liveByCoord)
        {
            string key = KeyForHistoryPoint(point, map);
            IReadOnlyList<AbstractModel> desiredQuests = quests.GetValueOrDefault(coord) ?? [];
            foreach (AbstractModel quest in point.Quests.Where(existing => !desiredQuests.Contains(existing)).ToArray())
                point.RemoveQuest(quest);
            foreach (AbstractModel quest in desiredQuests.Where(desired => !point.Quests.Contains(desired)))
                point.AddQuest(quest);

            if (!pointNodes.TryGetValue(coord, out NMapPoint? node))
                continue;
            if (act.Positions.TryGetValue(key, out MapEditingPosition position)
                && node.Position.DistanceSquaredTo(position.ToVector2()) > 0.01f)
            {
                node.Position = position.ToVector2();
                changedPositions.Add(coord);
            }
            if (changedVisuals.Contains(coord))
                node.RefreshVisualsInstantly();
        }

        foreach ((MapCoord source, MapCoord destination) in paths.Keys.ToArray())
        {
            if (desiredConnections.Contains((source, destination))
                && !changedPositions.Contains(source)
                && !changedPositions.Contains(destination))
            {
                continue;
            }
            if (!paths.Remove((source, destination), out IReadOnlyList<TextureRect>? ticks))
                continue;
            foreach (TextureRect tick in ticks)
                tick.QueueFree();
        }
        foreach ((MapCoord source, MapCoord destination) in desiredConnections)
        {
            if (paths.ContainsKey((source, destination))
                || !pointNodes.TryGetValue(source, out NMapPoint? sourceNode)
                || !pointNodes.TryGetValue(destination, out NMapPoint? destinationNode))
            {
                continue;
            }
            IReadOnlyList<TextureRect> ticks = CreateNativePath(screen, sourceNode, destinationNode);
            ApplyTraveledStyle((source, destination), ticks);
            paths[(source, destination)] = ticks;
        }

        foreach (MapCoord coord in changedVisuals.Concat(changedPositions).Distinct())
        {
            if (pointNodes.TryGetValue(coord, out NMapPoint? node))
                RefreshTravelability(node, runState);
        }
    }

    public static bool CanApplyActIncrementally(NMapScreen screen)
        => PointNodesField(screen).Count > 0;

    private static IEnumerable<MapPoint> EnumerateIncludingAnchors(ActMap map)
    {
        yield return map.StartingMapPoint;
        foreach (MapPoint point in map.GetAllMapPoints())
            yield return point;
        yield return map.BossMapPoint;
        if (map.SecondBossMapPoint is not null)
            yield return map.SecondBossMapPoint;
    }

    private static string KeyForHistoryPoint(MapPoint point, ActMap map)
    {
        if (ReferenceEquals(point, map.StartingMapPoint))
            return "start";
        if (ReferenceEquals(point, map.BossMapPoint))
            return "boss";
        if (ReferenceEquals(point, map.SecondBossMapPoint))
            return "boss2";
        return $"p:{point.coord.col}:{point.coord.row}";
    }

    public static void RebuildAllPaths(NMapScreen screen)
    {
        Dictionary<(MapCoord, MapCoord), IReadOnlyList<TextureRect>> paths = PathsField(screen);
        foreach (IReadOnlyList<TextureRect> ticks in paths.Values)
        {
            foreach (TextureRect tick in ticks)
                tick.QueueFree();
        }
        paths.Clear();

        Dictionary<MapCoord, NMapPoint> nodes = PointNodesField(screen);
        foreach (NMapPoint sourceNode in nodes.Values)
        {
            foreach (MapPoint child in sourceNode.Point.Children)
            {
                if (nodes.TryGetValue(child.coord, out NMapPoint? destinationNode))
                {
                    (MapCoord, MapCoord) connection = (sourceNode.Point.coord, child.coord);
                    IReadOnlyList<TextureRect> ticks = CreateNativePath(screen, sourceNode, destinationNode);
                    ApplyTraveledStyle(connection, ticks);
                    paths.Add(connection, ticks);
                }
            }
        }
    }

    private static void ReflowPath(
        NMapPoint sourceNode,
        NMapPoint destinationNode,
        IReadOnlyList<TextureRect> ticks)
    {
        Vector2 start = sourceNode.GetGlobalRect().GetCenter();
        Vector2 end = destinationNode.GetGlobalRect().GetCenter();
        Vector2 direction = end - start;
        float rotation = direction.Angle() + Mathf.Pi * 0.5f;
        for (int index = 0; index < ticks.Count; index++)
        {
            TextureRect tick = ticks[index];
            float t = (index + 1f) / (ticks.Count + 1f);
            tick.GlobalPosition = start.Lerp(end, t) - tick.Size * 0.5f;
            tick.Rotation = rotation;
        }
    }

    private void CreateConnectionVisual(MapPoint source, MapPoint destination)
    {
        Dictionary<MapCoord, NMapPoint> nodes = PointNodesField(_screen);
        if (!nodes.TryGetValue(source.coord, out NMapPoint? sourceNode)
            || !nodes.TryGetValue(destination.coord, out NMapPoint? destinationNode))
        {
            return;
        }

        (MapCoord, MapCoord) connection = (source.coord, destination.coord);
        IReadOnlyList<TextureRect> ticks = CreateNativePath(_screen, sourceNode, destinationNode);
        ApplyTraveledStyle(connection, ticks);
        PathsField(_screen).Add(connection, ticks);
    }

    private static IReadOnlyList<TextureRect> CreateNativePath(
        NMapScreen screen,
        NMapPoint sourceNode,
        NMapPoint destinationNode)
    {
        Vector2 start = sourceNode is NNormalMapPoint
            ? sourceNode.Position
            : sourceNode.Position + sourceNode.Size * 0.5f;
        Vector2 end = destinationNode is NNormalMapPoint
            ? destinationNode.Position
            : destinationNode.Position + destinationNode.Size * 0.5f;
        return (IReadOnlyList<TextureRect>?)CreatePathMethod.Invoke(screen, [start, end])
               ?? throw new InvalidOperationException("Native map path creation failed.");
    }

    private static void ApplyTraveledStyle(
        (MapCoord Source, MapCoord Destination) connection,
        IReadOnlyList<TextureRect> ticks)
    {
        RunState? runState = TryGetRunState();
        if (runState is null)
            return;
        IReadOnlyList<MapCoord> visited = runState.VisitedMapCoords;
        for (int index = 0; index + 1 < visited.Count; index++)
        {
            if (visited[index] != connection.Source || visited[index + 1] != connection.Destination)
                continue;
            foreach (TextureRect tick in ticks)
            {
                tick.Modulate = runState.Act.MapTraveledColor;
                tick.Scale = Vector2.One * 1.2f;
            }
            return;
        }
    }

    private void RemoveConnectionVisual((MapCoord Source, MapCoord Destination) connection)
    {
        Dictionary<(MapCoord, MapCoord), IReadOnlyList<TextureRect>> paths = PathsField(_screen);
        if (!paths.Remove(connection, out IReadOnlyList<TextureRect>? ticks))
            return;
        foreach (TextureRect tick in ticks)
            tick.QueueFree();
    }

    private void RemoveConnectionVisualsFor(MapCoord coord)
    {
        (MapCoord, MapCoord)[] keys = PathsField(_screen).Keys
            .Where(key => key.Item1 == coord || key.Item2 == coord)
            .ToArray();
        foreach ((MapCoord source, MapCoord destination) in keys)
            RemoveConnectionVisual((source, destination));
    }

    private static void RefreshTravelability(NMapPoint node, RunState runState)
    {
        if (runState.VisitedMapCoords.Contains(node.Point.coord))
        {
            node.State = MapPointState.Traveled;
            return;
        }
        if (runState.VisitedMapCoords.Count == 0)
        {
            node.State = ReferenceEquals(node.Point, runState.Map.StartingMapPoint)
                ? MapPointState.Travelable
                : MapPointState.Untravelable;
            return;
        }

        MapCoord currentCoord = runState.VisitedMapCoords[^1];
        if (ReferenceEquals(node.Point, runState.Map.SecondBossMapPoint)
            && currentCoord == runState.Map.BossMapPoint.coord)
        {
            node.State = MapPointState.Travelable;
            return;
        }
        if (ReferenceEquals(node.Point, runState.Map.BossMapPoint)
            && currentCoord.row == runState.Map.GetRowCount() - 1)
        {
            node.State = MapPointState.Travelable;
            return;
        }

        MapPoint? current = runState.Map.GetPoint(currentCoord);
        node.State = current is not null && MapTravel.GetTravelablePointsFrom(runState, current).Contains(node.Point)
            ? MapPointState.Travelable
            : MapPointState.Untravelable;
    }

    private static MapPoint?[,] GetGrid(ActMap map)
        => (MapPoint?[,]?)GridGetter.Invoke(map, null)
           ?? throw new InvalidOperationException("Active map has no editable grid.");

    private static MapPoint?[,] ExpandGrid(ActMap map, MapPoint?[,] grid, int width)
    {
        MapPoint?[,] expanded = new MapPoint?[width, grid.GetLength(1)];
        Array.Copy(grid, expanded, grid.Length);
        SetGrid(map, expanded);
        return expanded;
    }

    private static void SetGrid(ActMap map, MapPoint?[,] grid)
    {
        Type mapType = map.GetType();
        if (!GridBackingFields.TryGetValue(mapType, out FieldInfo? field))
        {
            field = AccessTools.Field(mapType, "<Grid>k__BackingField")
                    ?? throw new MissingFieldException(mapType.FullName, "<Grid>k__BackingField");
            GridBackingFields.Add(mapType, field);
        }
        field.SetValue(map, grid);
    }

    private NMapPoint? FindPointAt(Vector2 globalPosition)
    {
        foreach (NMapPoint point in PointNodesField(_screen).Values)
        {
            if (point.GetGlobalRect().HasPoint(globalPosition))
                return point;
        }
        return null;
    }

    private void CancelTransientAction()
    {
        if (_dragNode is not null)
        {
            _dragNode.Position = _dragStartPosition;
            ReflowConnectedPaths(_dragNode.Point.coord);
            _dragNode = null;
        }
        _dragHistoryState = null;
        _linkSource = null;
        _connectionPreview?.HideLine();
        ClearPicker();
        RestoreHighlightedTicks();
        _highlightedConnection = null;
    }

    private bool HasTransientAction() => _dragNode is not null || _linkSource.HasValue || _pickedType.HasValue;

    private void ClearPicker()
    {
        _pickedType = null;
        _placementChainTail = null;
        if (_ghost is not null)
            _ghost.Visible = false;
    }

    private List<NMapPoint> GetPointNodes() => PointNodesField(_screen).Values.ToList();

    private Texture2D? LoadPointTexture(MapPointType pointType)
    {
        if (pointType == MapPointType.Boss && TryGetRunState() is { } runState)
        {
            string? path = ImageHelper.GetRoomIconPath(
                MapPointType.Boss,
                RoomType.Boss,
                runState.Act.BossEncounter.Id);
            return string.IsNullOrWhiteSpace(path) ? null : LoadTexture(path);
        }

        string name = pointType switch
        {
            MapPointType.Monster => "map_monster",
            MapPointType.Elite => "map_elite",
            MapPointType.Treasure => "map_chest",
            MapPointType.Shop => "map_shop",
            MapPointType.RestSite => "map_rest",
            _ => "map_unknown"
        };
        return LoadTexture($"res://images/atlases/ui_atlas.sprites/map/icons/{name}.tres");
    }

    private void SetStatus(string text)
    {
        if (_status is not null)
            _status.SetTextAutoSize(text);
    }

    private static float DistanceToSegment(Vector2 point, Vector2 start, Vector2 end)
    {
        Vector2 segment = end - start;
        float lengthSquared = segment.LengthSquared();
        if (lengthSquared <= 0.001f)
            return point.DistanceTo(start);
        float t = Mathf.Clamp((point - start).Dot(segment) / lengthSquared, 0f, 1f);
        return point.DistanceTo(start + segment * t);
    }

    private static RunState? TryGetRunState()
    {
        try
        {
            return RunManager.Instance.IsInProgress ? RunManager.Instance.DebugOnlyGetState() : null;
        }
        catch
        {
            return null;
        }
    }

    private static Texture2D? LoadTexture(string path)
        => ResourceLoader.Exists(path) ? GD.Load<Texture2D>(path) : null;

    private static Font? LoadFont(string path)
        => ResourceLoader.Exists(path) ? GD.Load<Font>(path) : null;
}

public partial class NMapConnectionPreview : Control
{
    private const string DotPath = "res://images/atlases/compressed.sprites/map/map_dot.tres";

    private readonly Texture2D? _dot = ResourceLoader.Exists(DotPath) ? GD.Load<Texture2D>(DotPath) : null;
    private Vector2 _start;
    private Vector2 _end;
    private Color _color = Colors.White;

    public void ShowLine(Vector2 startGlobal, Vector2 endGlobal, Color color)
    {
        Transform2D inverse = GetGlobalTransformWithCanvas().AffineInverse();
        _start = inverse * startGlobal;
        _end = inverse * endGlobal;
        _color = color;
        Visible = true;
        QueueRedraw();
    }

    public void HideLine()
    {
        Visible = false;
    }

    public override void _Draw()
    {
        if (_dot is null)
            return;

        Vector2 delta = _end - _start;
        float length = delta.Length();
        if (length < 1f)
            return;

        Vector2 direction = delta / length;
        Vector2 halfSize = _dot.GetSize() * 0.5f;
        for (float distance = 22f; distance < length; distance += 22f)
            DrawTexture(_dot, _start + direction * distance - halfSize, _color);
    }
}

public partial class NMapToolButton : NButton
{
    private static readonly PropertyInfo HoverTipTitleProperty =
        typeof(HoverTip).GetProperty(nameof(HoverTip.Title), BindingFlags.Instance | BindingFlags.Public)
        ?? throw new MissingMemberException(typeof(HoverTip).FullName, nameof(HoverTip.Title));
    private TextureRect? _icon;
    private Tween? _tween;
    private bool _active;
    private bool _available = true;

    public string IconPath { get; set; } = string.Empty;
    public string GlowPath { get; set; } = string.Empty;
    public bool RainbowOutline { get; set; }
    public bool FlipVertical { get; set; }
    public bool FlipHorizontal { get; set; }
    public string HoverTitle { get; set; } = string.Empty;
    public string HoverDescription { get; set; } = string.Empty;
    public Control? HoverAnchor { get; set; }
    public Action? Activated { get; set; }

    public override void _Ready()
    {
        BuildVisuals();
        ConnectSignals();
    }

    public override void _ExitTree()
    {
        _tween?.Kill();
        NHoverTipSet.Remove(this);
        base._ExitTree();
    }

    public void SetActive(bool active)
    {
        _active = active;
        if (_icon is not null)
            _icon.SelfModulate = IdleColor;
    }

    public void SetAvailable(bool available)
    {
        _available = available;
        if (available)
            Enable();
        else
            Disable();
        if (_icon is not null)
            _icon.SelfModulate = IdleColor;
    }

    protected override void OnRelease() => Activated?.Invoke();

    protected override void OnFocus()
    {
        base.OnFocus();
        Animate(Vector2.One * 1.18f, Colors.White, 0.05);
        HoverTip hoverTip = CreateHoverTip();
        Control anchor = HoverAnchor is not null && GodotObject.IsInstanceValid(HoverAnchor)
            ? HoverAnchor
            : this;
        NHoverTipSet.CreateAndShow(this, hoverTip)?.SetGlobalPosition(anchor.GlobalPosition + new Vector2(10f, -132f));
    }

    protected override void OnUnfocus()
    {
        base.OnUnfocus();
        Animate(Vector2.One * 1.05f, IdleColor, 0.1);
        NHoverTipSet.Remove(this);
    }

    private void BuildVisuals()
    {
        if (RainbowOutline)
        {
            AddGlow(new Color(1f, 0.2f, 0.35f, 0.72f), new Vector2(-2f, 0f));
            AddGlow(new Color(0.2f, 1f, 0.45f, 0.72f), new Vector2(2f, 0f));
            AddGlow(new Color(0.25f, 0.55f, 1f, 0.72f), new Vector2(0f, 2f));
        }

        _icon = new TextureRect
        {
            Name = "Icon",
            Texture = LoadTexture(IconPath),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = MouseFilterEnum.Ignore,
            FlipV = FlipVertical,
            FlipH = FlipHorizontal,
            Scale = Vector2.One * 1.05f,
            PivotOffset = new Vector2(30f, 30f),
            SelfModulate = IdleColor
        };
        _icon.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(_icon);
    }

    private HoverTip CreateHoverTip()
    {
        object boxed = new HoverTip(new LocString("map", "DRAWING_BUTTON.title_mkb"), HoverDescription);
        HoverTipTitleProperty.SetValue(boxed, HoverTitle);
        return (HoverTip)boxed;
    }

    private void AddGlow(Color color, Vector2 offset)
    {
        TextureRect glow = new()
        {
            Texture = LoadTexture(GlowPath),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = MouseFilterEnum.Ignore,
            Modulate = color,
            Position = offset,
            Scale = Vector2.One * 1.1f,
            PivotOffset = new Vector2(30f, 30f),
            ShowBehindParent = true
        };
        glow.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(glow);
    }

    private void Animate(Vector2 scale, Color color, double seconds)
    {
        if (_icon is null)
            return;
        _tween?.Kill();
        _tween = CreateTween().SetParallel();
        _tween.TweenProperty(_icon, "scale", scale, seconds);
        _tween.TweenProperty(_icon, "self_modulate", color, seconds);
    }

    private Color IdleColor => !_available
        ? new Color(1f, 1f, 1f, 0.5f)
        : _active
            ? Colors.White
            : new Color(1f, 1f, 1f, 0.65f);

    private static Texture2D? LoadTexture(string path)
        => ResourceLoader.Exists(path) ? GD.Load<Texture2D>(path) : null;
}
