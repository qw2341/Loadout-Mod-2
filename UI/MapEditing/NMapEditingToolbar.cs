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
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
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
        bool positionsApplied = ApplyStoredVisualPositions(screen);
        if (_toolbar is not null && GodotObject.IsInstanceValid(_toolbar) && ReferenceEquals(_toolbar.Screen, screen))
            _toolbar.OnMapRebuilt(positionsApplied);
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
        ApplyVisualPositions(screen, positions);
        return true;
    }

    public static void ApplyVisualPositions(
        NMapScreen screen,
        IReadOnlyDictionary<string, MapEditingPosition> positions)
    {
        RunState? runState = TryGetRunState();
        if (runState is null)
            return;

        foreach (NMapPoint node in screen.GetNode<Control>("TheMap/Points").GetChildren().OfType<NMapPoint>())
        {
            string key = MapEditingService.KeyFor(node.Point, runState.Map);
            if (positions.TryGetValue(key, out MapEditingPosition position))
                node.Position = position.ToVector2();
        }
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
    private static readonly MethodInfo DrawPathsMethod =
        AccessTools.Method(typeof(NMapScreen), "DrawPaths", [typeof(NMapPoint), typeof(MapPoint)])
        ?? throw new MissingMethodException(typeof(NMapScreen).FullName, "DrawPaths");
    private static readonly Vector2 TraveledTickScale = Vector2.One * 1.2f;

    private const string QuillPath = "res://images/packed/map/drawing_quill.png";
    private const string QuillGlowPath = "res://images/packed/map/drawing_quill_glow.png";
    private const string SharePath = "res://images/packed/statistics_screen/share_stats.png";

    private readonly NMapScreen _screen;
    private Control _points = null!;
    private Control _paths = null!;
    private HBoxContainer _buttons = null!;
    private NMapToolButton _editButton = null!;
    private NMapToolButton _copyButton = null!;
    private NMapToolButton _importButton = null!;
    private MegaLabel _status = null!;
    private TextureRect _ghost = null!;
    private MapPointType? _pickedType;
    private NMapPoint? _dragNode;
    private Vector2 _dragStartPosition;
    private Vector2 _dragOffset;
    private MapCoord? _linkSource;
    private (MapCoord Source, MapCoord Destination)? _highlightedConnection;
    private readonly Dictionary<TextureRect, Color> _highlightedTickColors = [];

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
        _paths = _screen.GetNode<Control>("TheMap/Paths");
        BuildToolbar();
        _screen.Connect(CanvasItem.SignalName.VisibilityChanged, Callable.From(OnScreenVisibilityChanged));
        SetProcessInput(true);
    }

    public override void _ExitTree()
    {
        if (_screen.IsConnected(CanvasItem.SignalName.VisibilityChanged, Callable.From(OnScreenVisibilityChanged)))
            _screen.Disconnect(CanvasItem.SignalName.VisibilityChanged, Callable.From(OnScreenVisibilityChanged));
        if (IsEditing)
            SetEditing(false);
        base._ExitTree();
    }

    public override void _Input(InputEvent inputEvent)
    {
        if (!IsEditing || !_screen.IsVisibleInTree())
            return;

        if (inputEvent is InputEventMouseMotion)
        {
            UpdateGhostPosition();
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

    public void OnMapRebuilt(bool rebuildPaths)
    {
        RestoreHighlightedTicks();
        _highlightedConnection = null;
        _dragNode = null;
        if (rebuildPaths)
            RebuildAllPaths();
        UpdateGhostPosition();
    }

    private void BuildToolbar()
    {
        ColorRect background = new()
        {
            Name = "Background",
            Color = new Color(0f, 0f, 0f, 0.75f),
            MouseFilter = MouseFilterEnum.Ignore
        };
        background.SetAnchorsPreset(LayoutPreset.BottomLeft);
        background.OffsetLeft = 56f;
        background.OffsetTop = -184f;
        background.OffsetRight = 264f;
        background.OffsetBottom = -116f;
        AddChild(background);

        _buttons = new HBoxContainer
        {
            Name = "Buttons",
            MouseFilter = MouseFilterEnum.Pass
        };
        _buttons.SetAnchorsPreset(LayoutPreset.BottomLeft);
        _buttons.OffsetLeft = 66f;
        _buttons.OffsetTop = -180f;
        _buttons.OffsetRight = 254f;
        _buttons.OffsetBottom = -120f;
        _buttons.AddThemeConstantOverride("separation", 2);
        AddChild(_buttons);

        _editButton = CreateButton(
            "Edit",
            QuillPath,
            QuillGlowPath,
            ToggleEditing,
            LocMan.Loc("MAP_EDITOR_EDIT_TOOLTIP", "Toggle map editing mode."),
            rainbow: true);
        _copyButton = CreateButton(
            "Copy",
            SharePath,
            SharePath,
            CopyMaps,
            LocMan.Loc("MAP_EDITOR_COPY_TOOLTIP", "Copy all edited act maps to the clipboard."));
        _importButton = CreateButton(
            "Import",
            SharePath,
            SharePath,
            ImportMaps,
            LocMan.Loc("MAP_EDITOR_IMPORT_TOOLTIP", "Import current and future act maps from the clipboard."),
            flipVertical: true);
        _buttons.AddChild(_editButton);
        _buttons.AddChild(_copyButton);
        _buttons.AddChild(_importButton);

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
        _copyButton.Visible = false;
        _importButton.Visible = false;
        _editButton.SetActive(false);
        _status.SetTextAutoSize(string.Empty);
    }

    private NMapToolButton CreateButton(
        string name,
        string iconPath,
        string glowPath,
        Action action,
        string tooltip,
        bool rainbow = false,
        bool flipVertical = false)
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
            TooltipText = tooltip,
            Activated = action
        };
    }

    private void ToggleEditing() => SetEditing(!IsEditing);

    private void SetEditing(bool editing)
    {
        IsEditing = editing;
        CancelTransientAction();
        if (_copyButton is not null)
        {
            _copyButton.Visible = editing;
            _importButton.Visible = editing;
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

    private void OnScreenVisibilityChanged()
    {
        if (!_screen.IsVisibleInTree())
            CancelTransientAction();
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
            MapEditingService.ImportFromClipboard(runState, _screen, out message);
        else
            message = LocMan.Loc("MAP_EDITOR_NO_RUN", "No active run map.");
        SetStatus(message);
    }

    private void Pick(MapPointType pointType)
    {
        _pickedType = pointType;
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

        Dictionary<string, MapEditingPosition> positions = CapturePositions(runState);
        MapEditingActArchive current = MapEditingService.CaptureCurrentAct(runState, positions);
        Vector2 position = _points.GetLocalMousePosition();
        int row = FindNearestRow(position.Y, runState);
        HashSet<int> usedColumns = current.Map.Points
            .Where(point => point.Coord.row == row)
            .Select(point => point.Coord.col)
            .ToHashSet();
        int column = Enumerable.Range(0, 255).FirstOrDefault(candidate => !usedColumns.Contains(candidate));
        if (usedColumns.Count >= 255)
        {
            SetStatus(LocMan.Loc("MAP_EDITOR_ROW_FULL", "This logical floor has no free node slots."));
            return;
        }

        MapCoord coord = new(column, row);
        current.Map.Points.Add(new SerializableMapPoint
        {
            Coord = coord,
            PointType = _pickedType.Value,
            CanBeModified = true
        });
        current.Map.GridWidth = Math.Max(current.Map.GridWidth, column + 1);
        positions[$"p:{column}:{row}"] = MapEditingPosition.FromVector2(position);
        if (MapEditingService.CommitCurrentAct(runState, _screen, current.Map, positions, out string error))
        {
            SetStatus(LocMan.Loc("MAP_EDITOR_NODE_PLACED", "Placed {0}.", _pickedType.Value));
            if (!keepArmed)
                ClearPicker();
        }
        else
        {
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
            .Select(group => (group.Key, group.Average(node => node.Position.Y)))
            .ToList();
        return rows.Count == 0
            ? 0
            : rows.MinBy(candidate => Math.Abs(candidate.Y - y)).Row;
    }

    private void BeginDrag(NMapPoint point)
    {
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

            DeleteNode(dragged);
            return;
        }

        CommitTopology(runState, SerializableActMap.FromActMap(runState.Map));
        SetStatus(LocMan.Loc("MAP_EDITOR_NODE_MOVED", "Moved node."));
    }

    private void BeginConnectionDrag(MapCoord coord)
    {
        _linkSource = coord;
        ClearPicker();
        SetStatus(LocMan.Loc("MAP_EDITOR_LINK_START", "Select the destination node."));
    }

    private void FinishConnectionDrag(NMapPoint? destinationNode)
    {
        if (!_linkSource.HasValue)
            return;

        MapCoord source = _linkSource.Value;
        _linkSource = null;
        if (destinationNode is null || TryGetRunState() is not { } runState)
        {
            SetStatus(LocMan.Loc("MAP_EDITOR_ACTIVE", "Map editing mode"));
            return;
        }

        MapCoord destination = destinationNode.Point.coord;
        SerializableActMap map = SerializableActMap.FromActMap(runState.Map);
        SerializableMapPoint? sourcePoint = FindSavedPoint(map, source);
        if (sourcePoint is null)
            return;
        sourcePoint.ChildCoords ??= [];
        if (sourcePoint.ChildCoords.Contains(destination))
        {
            SetStatus(LocMan.Loc("MAP_EDITOR_LINK_EXISTS", "That connection already exists."));
            return;
        }

        sourcePoint.ChildCoords.Add(destination);
        CommitTopology(runState, map);
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

    private void DeleteNode(NMapPoint node)
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

        MapCoord coord = node.Point.coord;
        SerializableActMap map = SerializableActMap.FromActMap(runState.Map);
        map.Points.RemoveAll(point => point.Coord == coord);
        foreach (SerializableMapPoint point in EnumerateSavedPoints(map))
            point.ChildCoords?.RemoveAll(child => child == coord);
        map.StartMapPointCoords?.RemoveAll(value => value == coord);
        Dictionary<string, MapEditingPosition> positions = CapturePositions(runState);
        positions.Remove($"p:{coord.col}:{coord.row}");
        if (MapEditingService.CommitCurrentAct(runState, _screen, map, positions, out string error))
            SetStatus(LocMan.Loc("MAP_EDITOR_NODE_DELETED", "Deleted node."));
        else
            SetStatus(error);
    }

    private void DeleteConnection((MapCoord Source, MapCoord Destination) connection)
    {
        if (TryGetRunState() is not { } runState)
            return;

        SerializableActMap map = SerializableActMap.FromActMap(runState.Map);
        SerializableMapPoint? source = FindSavedPoint(map, connection.Source);
        if (source?.ChildCoords?.Remove(connection.Destination) != true)
            return;

        _highlightedConnection = null;
        RestoreHighlightedTicks();
        CommitTopology(runState, map);
        SetStatus(LocMan.Loc("MAP_EDITOR_LINK_DELETED", "Deleted connection."));
    }

    private void CommitTopology(RunState runState, SerializableActMap map)
    {
        Dictionary<string, MapEditingPosition> positions = CapturePositions(runState);
        if (!MapEditingService.CommitCurrentAct(runState, _screen, map, positions, out string error))
            SetStatus(error);
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
        _ghost.Position = GetLocalMousePosition() - _ghost.Size * 0.5f;
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

            Vector2 start = GetLineEndpoint(sourceNode);
            Vector2 end = GetLineEndpoint(destinationNode);
            Vector2 direction = end - start;
            float rotation = direction.Angle() + Mathf.Pi * 0.5f;
            Vector2 anchorOffset = _screen.Size * 0.5f - new Vector2(20f, 20f);
            for (int index = 0; index < ticks.Count; index++)
            {
                TextureRect tick = ticks[index];
                float t = (index + 1f) / (ticks.Count + 1f);
                tick.Position = start.Lerp(end, t) - anchorOffset;
                tick.Rotation = rotation;
            }
        }
    }

    private static Vector2 GetLineEndpoint(NMapPoint point)
        => point is NNormalMapPoint ? point.Position : point.Position + point.Size * 0.5f;

    private NMapPoint? FindPointAt(Vector2 globalPosition)
    {
        foreach (NMapPoint point in PointNodesField(_screen).Values)
        {
            if (point.GetGlobalRect().HasPoint(globalPosition))
                return point;
        }
        return null;
    }

    private void RebuildAllPaths()
    {
        if (_paths is null || !GodotObject.IsInstanceValid(_paths))
            return;

        foreach (Node child in _paths.GetChildren())
            child.QueueFree();

        Dictionary<(MapCoord, MapCoord), IReadOnlyList<TextureRect>> paths = PathsField(_screen);
        paths.Clear();
        foreach (NMapPoint node in GetPointNodes())
            DrawPathsMethod.Invoke(_screen, [node, node.Point]);

        RunState? runState = TryGetRunState();
        if (runState is null)
            return;
        for (int index = 0; index < runState.VisitedMapCoords.Count - 1; index++)
        {
            (MapCoord, MapCoord) key =
                (runState.VisitedMapCoords[index], runState.VisitedMapCoords[index + 1]);
            if (!paths.TryGetValue(key, out IReadOnlyList<TextureRect>? ticks))
                continue;
            foreach (TextureRect tick in ticks)
            {
                tick.Modulate = runState.Act.MapTraveledColor;
                tick.Scale = TraveledTickScale;
            }
        }
    }

    private void CancelTransientAction()
    {
        if (_dragNode is not null)
        {
            _dragNode.Position = _dragStartPosition;
            ReflowConnectedPaths(_dragNode.Point.coord);
            _dragNode = null;
        }
        _linkSource = null;
        ClearPicker();
        RestoreHighlightedTicks();
        _highlightedConnection = null;
    }

    private bool HasTransientAction() => _dragNode is not null || _linkSource.HasValue || _pickedType.HasValue;

    private void ClearPicker()
    {
        _pickedType = null;
        if (_ghost is not null)
            _ghost.Visible = false;
    }

    private List<NMapPoint> GetPointNodes() => _points.GetChildren().OfType<NMapPoint>().ToList();

    private static SerializableMapPoint? FindSavedPoint(SerializableActMap map, MapCoord coord)
        => EnumerateSavedPoints(map).FirstOrDefault(point => point.Coord == coord);

    private static IEnumerable<SerializableMapPoint> EnumerateSavedPoints(SerializableActMap map)
    {
        yield return map.StartingPoint;
        foreach (SerializableMapPoint point in map.Points)
            yield return point;
        yield return map.BossPoint;
        if (map.SecondBossPoint is not null)
            yield return map.SecondBossPoint;
    }

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
        return LoadTexture($"res://images/packed/atlases/ui_atlas.sprites/map/icons/{name}.tres");
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

public partial class NMapToolButton : NButton
{
    private TextureRect? _icon;
    private Tween? _tween;
    private bool _active;

    public string IconPath { get; set; } = string.Empty;
    public string GlowPath { get; set; } = string.Empty;
    public bool RainbowOutline { get; set; }
    public bool FlipVertical { get; set; }
    public Action? Activated { get; set; }

    public override void _Ready()
    {
        BuildVisuals();
        ConnectSignals();
    }

    public override void _ExitTree()
    {
        _tween?.Kill();
        base._ExitTree();
    }

    public void SetActive(bool active)
    {
        _active = active;
        if (_icon is not null)
            _icon.SelfModulate = active ? Colors.White : new Color(1f, 1f, 1f, 0.65f);
    }

    protected override void OnRelease() => Activated?.Invoke();

    protected override void OnFocus()
    {
        base.OnFocus();
        Animate(Vector2.One * 1.18f, Colors.White, 0.05);
    }

    protected override void OnUnfocus()
    {
        base.OnUnfocus();
        Animate(Vector2.One * 1.05f, _active ? Colors.White : new Color(1f, 1f, 1f, 0.65f), 0.1);
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
            Scale = Vector2.One * 1.05f,
            PivotOffset = new Vector2(30f, 30f),
            SelfModulate = new Color(1f, 1f, 1f, 0.65f)
        };
        _icon.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(_icon);
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

    private static Texture2D? LoadTexture(string path)
        => ResourceLoader.Exists(path) ? GD.Load<Texture2D>(path) : null;
}
