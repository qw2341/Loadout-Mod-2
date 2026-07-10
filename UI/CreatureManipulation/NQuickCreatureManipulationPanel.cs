#nullable enable

namespace Loadout.UI.CreatureManipulation;

using System;
using Godot;
using Loadout.Services.CreatureManipulation;
using Loadout.Services.Loadouts;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Nodes.Combat;

public partial class NQuickCreatureManipulationPanel : PanelContainer
{
    private const float PanelWidth = 360f;
    private const float ButtonHeight = 48f;
    private const double KillConfirmSeconds = 3.5;

    private static NQuickCreatureManipulationPanel? _current;

    private readonly NCreature _targetNode;
    private readonly Creature _target;
    private Button? _moveButton;
    private Button? _killButton;
    private bool _dragging;
    private Vector2 _dragStartMouse;
    private Vector2 _dragStartPosition;
    private bool _killArmed;
    private double _killConfirmRemaining;

    private NQuickCreatureManipulationPanel(NCreature targetNode)
    {
        _targetNode = targetNode;
        _target = targetNode.Entity;
        CustomMinimumSize = new Vector2(PanelWidth, 0f);
        MouseFilter = MouseFilterEnum.Stop;
        ProcessMode = ProcessModeEnum.Always;
        ZIndex = 1200;
    }

    public static void ShowFor(NCreature targetNode, Vector2 globalPosition)
    {
        if (!CanOpen(targetNode))
            return;

        CloseCurrent();
        Control? layer = NLoadoutPanelRoot.Instance?.DropdownLayer;
        if (layer is null)
            return;

        NQuickCreatureManipulationPanel panel = new(targetNode)
        {
            Name = "QuickCreatureManipulationPanel",
            Position = globalPosition + new Vector2(18f, 12f)
        };
        _current = panel;
        layer.AddChild(panel);
        Callable.From(panel.ClampToViewport).CallDeferred();
    }

    public static void CloseCurrent()
    {
        if (_current is null)
            return;

        if (GodotObject.IsInstanceValid(_current))
            _current.QueueFree();
        _current = null;
    }

    public override void _Ready()
    {
        BuildUi();
        LoadoutPanelAccessService.AccessChanged += OnAccessChanged;
    }

    public override void _ExitTree()
    {
        LoadoutPanelAccessService.AccessChanged -= OnAccessChanged;
        if (ReferenceEquals(_current, this))
            _current = null;
    }

    public override void _Process(double delta)
    {
        if (!CanOpen(_targetNode))
        {
            CloseCurrent();
            return;
        }

        if (_dragging)
        {
            Vector2 mouse = GetViewport().GetMousePosition();
            Vector2 position = _dragStartPosition + mouse - _dragStartMouse;
            _targetNode.Position = ClampCreaturePosition(position);
        }

        if (!_killArmed)
            return;

        _killConfirmRemaining -= delta;
        if (_killConfirmRemaining <= 0d)
            ResetKillConfirmation();
    }

    public override void _Input(InputEvent inputEvent)
    {
        if (!IsVisibleInTree())
            return;

        if (inputEvent is InputEventKey { Keycode: Key.Escape, Pressed: true, Echo: false })
        {
            if (_dragging)
                EndDrag(commit: true);
            CloseCurrent();
            GetViewport().SetInputAsHandled();
            return;
        }

        if (inputEvent is not InputEventMouseButton mouseButton)
            return;

        if (_dragging && mouseButton.ButtonIndex == MouseButton.Left && !mouseButton.Pressed)
        {
            EndDrag(commit: true);
            GetViewport().SetInputAsHandled();
            return;
        }

        if (!mouseButton.Pressed || mouseButton.ButtonIndex is not (MouseButton.Left or MouseButton.Right))
            return;

        if (!GetGlobalRect().HasPoint(mouseButton.GlobalPosition))
            CloseCurrent();
    }

    private void BuildUi()
    {
        MarginContainer margin = new();
        margin.AddThemeConstantOverride("margin_left", 16);
        margin.AddThemeConstantOverride("margin_right", 16);
        margin.AddThemeConstantOverride("margin_top", 14);
        margin.AddThemeConstantOverride("margin_bottom", 14);
        AddChild(margin);

        VBoxContainer root = new();
        root.AddThemeConstantOverride("separation", 10);
        margin.AddChild(root);

        HBoxContainer header = new();
        root.AddChild(header);

        Label title = new()
        {
            Text = _target.Name,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            VerticalAlignment = VerticalAlignment.Center,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis
        };
        title.AddThemeFontSizeOverride("font_size", 23);
        header.AddChild(title);

        Button close = new()
        {
            Text = "×",
            CustomMinimumSize = new Vector2(42f, 42f)
        };
        close.Pressed += CloseCurrent;
        header.AddChild(close);

        GridContainer buttons = new()
        {
            Columns = 2
        };
        buttons.AddThemeConstantOverride("h_separation", 8);
        buttons.AddThemeConstantOverride("v_separation", 8);
        root.AddChild(buttons);

        _moveButton = AddButton(buttons, "Move / Drag", null);
        _moveButton.GuiInput += OnMoveButtonInput;

        AddButton(buttons, "Current Powers", () =>
        {
            CloseCurrent();
            CreatureManipulationScreens.OpenPowerScreen(_target);
        });

        AddButton(buttons, "Morph", () =>
        {
            CloseCurrent();
            CreatureManipulationScreens.OpenMorphScreen(_target);
        });

        _killButton = AddButton(buttons, "Instant Kill", OnKillPressed);

        AddButton(buttons, "Edit Stats", () =>
        {
            CloseCurrent();
            CreatureManipulationScreens.OpenStatScreen(_target);
        });

        if (_target.IsMonster)
        {
            AddButton(buttons, "Duplicate", () =>
            {
                CreatureManipulationStateService.RequestDuplicate(_target);
                CloseCurrent();
            });
        }
    }

    private static Button AddButton(Container parent, string text, Action? onPressed)
    {
        Button button = new()
        {
            Text = text,
            CustomMinimumSize = new Vector2((PanelWidth - 48f) * 0.5f, ButtonHeight),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            FocusMode = FocusModeEnum.All,
            MouseFilter = MouseFilterEnum.Stop
        };
        button.AddThemeFontSizeOverride("font_size", 18);
        if (onPressed is not null)
            button.Pressed += onPressed;
        parent.AddChild(button);
        return button;
    }

    private void OnMoveButtonInput(InputEvent inputEvent)
    {
        if (inputEvent is not InputEventMouseButton { ButtonIndex: MouseButton.Left } mouseButton)
            return;

        if (mouseButton.Pressed)
        {
            _dragging = true;
            _dragStartMouse = GetViewport().GetMousePosition();
            _dragStartPosition = _targetNode.Position;
            _moveButton!.Text = "Dragging…";
            AcceptEvent();
        }
        else if (_dragging)
        {
            EndDrag(commit: true);
            AcceptEvent();
        }
    }

    private void EndDrag(bool commit)
    {
        if (!_dragging)
            return;

        _dragging = false;
        if (_moveButton is not null)
            _moveButton.Text = "Move / Drag";

        if (commit)
            CreatureManipulationStateService.RequestSetPosition(_target, _targetNode.Position);
    }

    private void OnKillPressed()
    {
        if (!_killArmed)
        {
            _killArmed = true;
            _killConfirmRemaining = KillConfirmSeconds;
            if (_killButton is not null)
                _killButton.Text = "Are you sure?";
            return;
        }

        CreatureManipulationStateService.RequestKill(_target);
        CloseCurrent();
    }

    private void ResetKillConfirmation()
    {
        _killArmed = false;
        _killConfirmRemaining = 0d;
        if (_killButton is not null)
            _killButton.Text = "Instant Kill";
    }

    private void ClampToViewport()
    {
        Vector2 viewportSize = GetViewportRect().Size;
        Vector2 panelSize = Size;
        Position = new Vector2(
            Mathf.Clamp(Position.X, 8f, MathF.Max(8f, viewportSize.X - panelSize.X - 8f)),
            Mathf.Clamp(Position.Y, 8f, MathF.Max(8f, viewportSize.Y - panelSize.Y - 8f)));
    }

    private Vector2 ClampCreaturePosition(Vector2 position)
    {
        Vector2 viewportSize = GetViewportRect().Size;
        return new Vector2(
            Mathf.Clamp(position.X, -100f, viewportSize.X + 100f),
            Mathf.Clamp(position.Y, -100f, viewportSize.Y + 100f));
    }

    private void OnAccessChanged()
    {
        if (!LoadoutPanelAccessService.CanLocalPlayerUsePanel())
            CloseCurrent();
    }

    private static bool CanOpen(NCreature? targetNode)
    {
        return targetNode is not null
               && GodotObject.IsInstanceValid(targetNode)
               && targetNode.Entity is not null
               && targetNode.Entity.CombatId.HasValue
               && targetNode.Entity.CombatId.Value != 0
               && CombatManager.Instance.IsInProgress
               && LoadoutPanelAccessService.CanLocalPlayerUsePanel();
    }
}
