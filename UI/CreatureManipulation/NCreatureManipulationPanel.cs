#nullable enable

namespace Loadout.UI.CreatureManipulation;

using System;
using Godot;
using Loadout.PanelItems;
using Loadout.Services.CreatureManipulation;
using Loadout.Services.Loadouts;
using Loadout.UI.Managers;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Helpers;

public partial class NCreatureManipulationPanel : PanelContainer
{
    private const float DragSendIntervalSeconds = 0.05f;

    private NCreature? _targetNode;
    private Button _killButton = null!;
    private Button _duplicateButton = null!;
    private bool _killArmed;
    private bool _dragging;
    private bool _dragSendScheduled;
    private uint _dragCombatId;
    private ulong _dragSessionId;
    private Vector2 _dragGlobalOffset;
    private Vector2 _latestDragPosition;

    public override void _Ready()
    {
        Name = "CreatureManipulationPanel";
        ZIndex = 1005;
        Visible = false;
        MouseFilter = MouseFilterEnum.Stop;
        CustomMinimumSize = new Vector2(286f, 0f);

        StyleBoxFlat panelStyle = new()
        {
            BgColor = new Color(0.08f, 0.035f, 0.025f, 0.94f),
            BorderColor = new Color(0.55f, 0.28f, 0.13f, 0.95f),
            BorderWidthLeft = 3,
            BorderWidthTop = 3,
            BorderWidthRight = 3,
            BorderWidthBottom = 3,
            CornerRadiusTopLeft = 7,
            CornerRadiusTopRight = 7,
            CornerRadiusBottomLeft = 7,
            CornerRadiusBottomRight = 7,
            ContentMarginLeft = 8,
            ContentMarginTop = 8,
            ContentMarginRight = 8,
            ContentMarginBottom = 8
        };
        AddThemeStyleboxOverride("panel", panelStyle);

        VBoxContainer buttons = new()
        {
            Name = "Buttons",
            MouseFilter = MouseFilterEnum.Pass
        };
        buttons.AddThemeConstantOverride("separation", 5);
        AddChild(buttons);

        Button reposition = CreateMenuButton(
            LocMan.Loc("CREATURE_MANIP_REPOSITION", "Reposition"));
        reposition.GuiInput += OnRepositionInput;
        buttons.AddChild(reposition);

        Button powers = CreateMenuButton(
            LocMan.Loc("CREATURE_MANIP_POWERS", "Edit Powers"));
        powers.Pressed += OpenPowerScreen;
        buttons.AddChild(powers);

        _killButton = CreateMenuButton(
            LocMan.Loc("CREATURE_MANIP_KILL", "Insta Kill"));
        _killButton.Pressed += OnKillPressed;
        buttons.AddChild(_killButton);

        Button stats = CreateMenuButton(
            LocMan.Loc("CREATURE_MANIP_STATS", "Edit Stats"));
        stats.Pressed += OpenStatScreen;
        buttons.AddChild(stats);

        _duplicateButton = CreateMenuButton(
            LocMan.Loc("CREATURE_MANIP_DUPLICATE", "Duplicate"));
        _duplicateButton.Pressed += OnDuplicatePressed;
        buttons.AddChild(_duplicateButton);

        CreatureManipulationStateService.DragAuthoritative += OnAuthoritativeDrag;
        LoadoutPanelAccessService.AccessChanged += OnAccessChanged;
        SetProcessInput(false);
    }

    public override void _ExitTree()
    {
        CreatureManipulationStateService.DragAuthoritative -= OnAuthoritativeDrag;
        LoadoutPanelAccessService.AccessChanged -= OnAccessChanged;
    }

    public void OpenFor(NCreature node, Vector2 cursorPosition)
    {
        if (!GodotObject.IsInstanceValid(node))
            return;

        CancelDrag(sendFinal: false);
        _targetNode = node;
        _killArmed = false;
        RefreshKillText();
        _duplicateButton.Visible = node.Entity?.Monster is not null;
        Visible = true;
        SetProcessInput(true);
        Position = cursorPosition;
        CallDeferred(MethodName.ClampMenuToViewport);
    }

    public void Close()
    {
        if (_dragging)
            CancelDrag(sendFinal: true);
        Visible = false;
        _targetNode = null;
        _killArmed = false;
        RefreshKillText();
        if (!_dragging)
            SetProcessInput(false);
    }

    public override void _Input(InputEvent input)
    {
        if (_dragging)
        {
            if (input is InputEventMouseMotion motion)
            {
                UpdateLocalDrag(motion.Position);
                GetViewport().SetInputAsHandled();
                return;
            }

            if (input is InputEventMouseButton
                {
                    ButtonIndex: MouseButton.Left,
                    Pressed: false
                })
            {
                FinishDrag();
                GetViewport().SetInputAsHandled();
            }
            return;
        }

        if (!Visible)
            return;

        if (input is InputEventKey { Keycode: Key.Escape, Pressed: true, Echo: false })
        {
            Close();
            GetViewport().SetInputAsHandled();
            return;
        }

        if (input is InputEventMouseButton { Pressed: true } mouse
            && !GetGlobalRect().HasPoint(mouse.GlobalPosition))
        {
            Close();
        }
    }

    private Button CreateMenuButton(string text)
    {
        Button button = CommonHelpers.CreateModelButton(new Vector2(270f, 58f));
        button.Text = text;
        button.AddThemeFontOverride(
            "font",
            CommonHelpers.LoadGameFont("res://themes/kreon_bold_glyph_space_one.tres"));
        button.AddThemeFontSizeOverride("font_size", 24);
        button.AddThemeColorOverride("font_color", StsColors.cream);
        button.AddThemeColorOverride("font_hover_color", StsColors.gold);
        return button;
    }

    private void OnRepositionInput(InputEvent input)
    {
        if (input is not InputEventMouseButton
            {
                ButtonIndex: MouseButton.Left,
                Pressed: true
            } mouse
            || _targetNode?.Entity?.CombatId is not uint combatId)
        {
            return;
        }

        _dragSessionId = CreatureManipulationStateService.BeginDrag(
            _targetNode.Entity,
            _targetNode.Position);
        if (_dragSessionId == 0)
            return;

        _dragCombatId = combatId;
        _dragging = true;
        _dragGlobalOffset = _targetNode.GlobalPosition - mouse.GlobalPosition;
        _latestDragPosition = _targetNode.Position;
        Visible = false;
        SetProcessInput(true);
        GetViewport().SetInputAsHandled();
    }

    private void UpdateLocalDrag(Vector2 pointerPosition)
    {
        if (!_dragging || !GodotObject.IsInstanceValid(_targetNode))
        {
            CancelDrag(sendFinal: false);
            return;
        }

        _targetNode.GlobalPosition = pointerPosition + _dragGlobalOffset;
        _latestDragPosition = _targetNode.Position;
        if (_dragSendScheduled)
            return;

        _dragSendScheduled = true;
        SceneTreeTimer timer = GetTree().CreateTimer(DragSendIntervalSeconds);
        timer.Timeout += FlushDragUpdate;
    }

    private void FlushDragUpdate()
    {
        _dragSendScheduled = false;
        if (_dragging)
        {
            CreatureManipulationStateService.UpdateDrag(
                _dragCombatId,
                _dragSessionId,
                _latestDragPosition);
        }
    }

    private void FinishDrag()
    {
        if (!_dragging)
            return;

        CreatureManipulationStateService.EndDrag(
            _dragCombatId,
            _dragSessionId,
            _latestDragPosition);
        _dragging = false;
        _targetNode = null;
        SetProcessInput(false);
    }

    private void CancelDrag(bool sendFinal)
    {
        if (!_dragging)
            return;

        if (sendFinal)
        {
            CreatureManipulationStateService.EndDrag(
                _dragCombatId,
                _dragSessionId,
                _latestDragPosition);
        }

        _dragging = false;
        _targetNode = null;
        SetProcessInput(Visible);
    }

    private void OnAuthoritativeDrag(CreatureDragMessage message)
    {
        if (!_dragging
            || message.sessionId != _dragSessionId
            || message.combatId != _dragCombatId)
        {
            return;
        }

        if (message.ownerNetId == CreatureManipulationStateService.LocalNetId
            && message.phase == CreatureDragPhase.Cancel)
            CancelDrag(sendFinal: false);
    }

    private void OnAccessChanged()
    {
        if (!LoadoutPanelAccessService.CanLocalPlayerUsePanel())
            Close();
    }

    private void OpenPowerScreen()
    {
        if (_targetNode?.Entity is not { } creature)
            return;
        CloseWithoutEndingTarget();
        NCreaturePowerSelectScreen.Open(creature);
    }

    private void OpenStatScreen()
    {
        if (_targetNode?.Entity is not { } creature)
            return;
        CloseWithoutEndingTarget();
        NCreatureStatScreen.Open(creature);
    }

    private void OnKillPressed()
    {
        if (_targetNode?.Entity is not { } creature)
            return;

        if (!_killArmed)
        {
            _killArmed = true;
            RefreshKillText();
            return;
        }

        CreatureManipulationStateService.RequestKill(creature);
        Close();
    }

    private void OnDuplicatePressed()
    {
        if (_targetNode?.Entity is not { Monster: not null } creature)
            return;
        CreatureManipulationStateService.RequestDuplicate(creature);
        Close();
    }

    private void CloseWithoutEndingTarget()
    {
        Visible = false;
        _killArmed = false;
        RefreshKillText();
        _targetNode = null;
        SetProcessInput(false);
    }

    private void RefreshKillText()
    {
        if (_killButton is null)
            return;
        _killButton.Text = _killArmed
            ? LocMan.Loc("CREATURE_MANIP_KILL_CONFIRM", "Are you sure?")
            : LocMan.Loc("CREATURE_MANIP_KILL", "Insta Kill");
    }

    private void ClampMenuToViewport()
    {
        Vector2 viewport = GetViewportRect().Size;
        Position = new Vector2(
            Mathf.Clamp(Position.X, 0f, MathF.Max(0f, viewport.X - Size.X)),
            Mathf.Clamp(Position.Y, 0f, MathF.Max(0f, viewport.Y - Size.Y)));
    }
}
