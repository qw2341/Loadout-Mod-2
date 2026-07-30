#nullable enable

namespace Loadout.UI.CreatureManipulation;

using System;
using System.Collections.Generic;
using Godot;
using Loadout.PanelItems;
using Loadout.Services.Configuration;
using Loadout.Services.CreatureManipulation;
using Loadout.Services.Loadouts;
using Loadout.UI.Managers;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Audio;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Helpers;

public partial class NCreatureManipulationPanel : PanelContainer
{
    private const float DragSendIntervalSeconds = 0.05f;
    private static readonly Vector2 MenuButtonSize = new(294f, 52f);
    private static readonly StyleBoxEmpty EmptyStyle = new();
    private static readonly Dictionary<string, Texture2D?> TextureCache = new(StringComparer.Ordinal);

    private NCreature? _targetNode;
    private readonly List<QuickMenuButton> _menuButtons = [];
    private QuickMenuButton _killButton = null!;
    private QuickMenuButton _duplicateButton = null!;
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
        CustomMinimumSize = new Vector2(354f, 0f);
        AddThemeStyleboxOverride("panel", EmptyStyle);

        Texture2D? panelTexture = LoadGameTexture("res://images/ui/reward_screen/reward_panel.png");
        ColorRect panelFallback = new()
        {
            Name = "PanelFallback",
            Color = new Color(0.035f, 0.075f, 0.095f, 0.9f),
            Visible = panelTexture is null,
            MouseFilter = MouseFilterEnum.Ignore
        };
        panelFallback.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(panelFallback);

        NinePatchRect panelBackground = new()
        {
            Name = "RewardPanelBackground",
            Texture = panelTexture,
            Visible = panelTexture is not null,
            DrawCenter = true,
            PatchMarginLeft = 92,
            PatchMarginTop = 102,
            PatchMarginRight = 92,
            PatchMarginBottom = 102,
            AxisStretchHorizontal = NinePatchRect.AxisStretchMode.Stretch,
            AxisStretchVertical = NinePatchRect.AxisStretchMode.Stretch,
            SelfModulate = new Color(1f, 1f, 1f, 0.9f),
            MouseFilter = MouseFilterEnum.Ignore
        };
        panelBackground.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(panelBackground);

        MarginContainer outerShell = new()
        {
            Name = "OuterShell",
            MouseFilter = MouseFilterEnum.Pass
        };
        outerShell.AddThemeConstantOverride("margin_left", 24);
        outerShell.AddThemeConstantOverride("margin_top", 24);
        outerShell.AddThemeConstantOverride("margin_right", 24);
        outerShell.AddThemeConstantOverride("margin_bottom", 24);
        AddChild(outerShell);

        MarginContainer contentMargin = new()
        {
            Name = "ContentMargin",
            MouseFilter = MouseFilterEnum.Pass
        };
        contentMargin.AddThemeConstantOverride("margin_left", 18);
        contentMargin.AddThemeConstantOverride("margin_top", 16);
        contentMargin.AddThemeConstantOverride("margin_right", 18);
        contentMargin.AddThemeConstantOverride("margin_bottom", 16);
        outerShell.AddChild(contentMargin);

        VBoxContainer buttons = new()
        {
            Name = "Buttons",
            MouseFilter = MouseFilterEnum.Pass
        };
        buttons.AddThemeConstantOverride("separation", 0);
        contentMargin.AddChild(buttons);

        QuickMenuButton reposition = CreateMenuButton(
            LocMan.Loc("CREATURE_MANIP_REPOSITION", "Reposition"),
            "res://images/ui/combat/targeting_arrow_head.png",
            new Color(0.55f, 0.78f, 1f));
        reposition.GuiInput += OnRepositionInput;
        buttons.AddChild(reposition);

        QuickMenuButton powers = CreateMenuButton(
            LocMan.Loc("CREATURE_MANIP_POWERS", "Edit Powers"),
            "res://images/atlases/power_atlas.sprites/strength_power.tres",
            new Color(0.78f, 0.58f, 1f));
        powers.Pressed += OpenPowerScreen;
        buttons.AddChild(powers);

        _killButton = CreateMenuButton(
            LocMan.Loc("CREATURE_MANIP_KILL", "Insta Kill"),
            "res://images/ui/emote/skull.png",
            new Color(1f, 0.42f, 0.34f));
        _killButton.Pressed += OnKillPressed;
        buttons.AddChild(_killButton);

        QuickMenuButton stats = CreateMenuButton(
            LocMan.Loc("CREATURE_MANIP_STATS", "Edit Stats"),
            "res://images/atlases/ui_atlas.sprites/top_bar/top_bar_heart.tres",
            new Color(0.96f, 0.52f, 0.43f));
        stats.Pressed += OpenStatScreen;
        buttons.AddChild(stats);

        _duplicateButton = CreateMenuButton(
            LocMan.Loc("CREATURE_MANIP_DUPLICATE", "Duplicate"),
            "res://images/atlases/power_atlas.sprites/duplication_power.tres",
            new Color(0.67f, 0.88f, 1f));
        _duplicateButton.Pressed += OnDuplicatePressed;
        buttons.AddChild(_duplicateButton);
        RefreshVisibleSeparators();

        CreatureManipulationStateService.DragAuthoritative += OnAuthoritativeDrag;
        LoadoutPanelAccessService.AccessChanged += OnAvailabilityChanged;
        LoadoutConfigService.CreatureManipulationPanelVisibilityChanged += OnAvailabilityChanged;
        SetProcessInput(false);
    }

    public override void _ExitTree()
    {
        CreatureManipulationStateService.DragAuthoritative -= OnAuthoritativeDrag;
        LoadoutPanelAccessService.AccessChanged -= OnAvailabilityChanged;
        LoadoutConfigService.CreatureManipulationPanelVisibilityChanged -= OnAvailabilityChanged;
    }

    public void OpenFor(NCreature node, Vector2 cursorPosition)
    {
        if (!GodotObject.IsInstanceValid(node))
            return;

        CancelDrag(sendFinal: false);
        _targetNode = node;
        _killArmed = false;
        RefreshKillText();
        foreach (QuickMenuButton button in _menuButtons)
            button.ResetInteractionState();
        _duplicateButton.Visible = node.Entity?.Monster is not null;
        RefreshVisibleSeparators();
        Visible = true;
        SetProcessInput(true);
        Position = cursorPosition;
        CallDeferred(MethodName.FinalizeOpenLayout);
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

    private QuickMenuButton CreateMenuButton(string text, string iconPath, Color iconTint)
    {
        QuickMenuButton button = new(
            text,
            LoadGameTexture(iconPath),
            iconTint)
        {
            CustomMinimumSize = MenuButtonSize
        };
        _menuButtons.Add(button);
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
        SfxCmd.Play("event:/sfx/ui/clicks/ui_click");
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

    private void OnAvailabilityChanged()
    {
        if (!LoadoutConfigService.EnableCreatureManipulationPanel
            || !LoadoutPanelAccessService.CanLocalPlayerUsePanel())
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
        _killButton.SetLabel(
            _killArmed
            ? LocMan.Loc("CREATURE_MANIP_KILL_CONFIRM", "Are you sure?")
            : LocMan.Loc("CREATURE_MANIP_KILL", "Insta Kill"));
        _killButton.SetArmed(_killArmed);
    }

    private void RefreshVisibleSeparators()
    {
        QuickMenuButton? previous = null;
        foreach (QuickMenuButton button in _menuButtons)
        {
            button.SetSeparatorVisible(false);
            if (!button.Visible)
                continue;

            previous?.SetSeparatorVisible(true);
            previous = button;
        }
    }

    private void FinalizeOpenLayout()
    {
        Size = GetCombinedMinimumSize();
        ClampMenuToViewport();
    }

    private void ClampMenuToViewport()
    {
        Vector2 viewport = GetViewportRect().Size;
        Position = new Vector2(
            Mathf.Clamp(Position.X, 0f, MathF.Max(0f, viewport.X - Size.X)),
            Mathf.Clamp(Position.Y, 0f, MathF.Max(0f, viewport.Y - Size.Y)));
    }

    private static Texture2D? LoadGameTexture(string path)
    {
        if (TextureCache.TryGetValue(path, out Texture2D? cached))
            return cached;

        string localPath = path.Replace("res://images/", "res://Loadout/images/");
        string resolvedPath = ResourceLoader.Exists(localPath) ? localPath : path;
        Texture2D? texture = ResourceLoader.Exists(resolvedPath)
            ? ResourceLoader.Load<Texture2D>(resolvedPath, null, ResourceLoader.CacheMode.Reuse)
            : null;
        TextureCache[path] = texture;
        return texture;
    }

    private sealed partial class QuickMenuButton : Button
    {
        private static readonly Color HoverColor = new(0.172549f, 0.345098f, 0.439216f, 0.82f);
        private static readonly Color IdleHighlightColor = new(0.172549f, 0.345098f, 0.439216f, 0f);
        private static readonly Color ArmedColor = new(0.42f, 0.08f, 0.07f, 0.68f);
        private static readonly Color ArmedHoverColor = new(0.62f, 0.12f, 0.09f, 0.84f);
        private static readonly Color ArmedTextColor = new(1f, 0.72f, 0.55f);

        private readonly ColorRect _highlight;
        private readonly ColorRect _separator;
        private readonly TextureRect _icon;
        private readonly MegaLabel _label;
        private readonly Color _iconTint;
        private Tween? _tween;
        private bool _hovered;
        private bool _focused;
        private bool _pressed;
        private bool _armed;

        public QuickMenuButton(string text, Texture2D? iconTexture, Color iconTint)
        {
            _iconTint = iconTint;
            Text = string.Empty;
            FocusMode = FocusModeEnum.All;
            MouseFilter = MouseFilterEnum.Stop;
            SizeFlagsHorizontal = SizeFlags.ExpandFill;
            SizeFlagsVertical = SizeFlags.ShrinkBegin;
            ClipContents = false;

            AddThemeStyleboxOverride("normal", EmptyStyle);
            AddThemeStyleboxOverride("hover", EmptyStyle);
            AddThemeStyleboxOverride("pressed", EmptyStyle);
            AddThemeStyleboxOverride("focus", EmptyStyle);
            AddThemeStyleboxOverride("disabled", EmptyStyle);

            _highlight = new ColorRect
            {
                Name = "Highlight",
                Color = IdleHighlightColor,
                MouseFilter = MouseFilterEnum.Ignore
            };
            _highlight.SetAnchorsPreset(LayoutPreset.FullRect);
            _highlight.OffsetLeft = 2f;
            _highlight.OffsetTop = 2f;
            _highlight.OffsetRight = -2f;
            _highlight.OffsetBottom = -2f;
            AddChild(_highlight);

            HBoxContainer content = new()
            {
                Name = "Content",
                Alignment = BoxContainer.AlignmentMode.Begin,
                MouseFilter = MouseFilterEnum.Ignore
            };
            content.SetAnchorsPreset(LayoutPreset.FullRect);
            content.OffsetLeft = 12f;
            content.OffsetRight = -12f;
            content.AddThemeConstantOverride("separation", 12);
            AddChild(content);

            _icon = new TextureRect
            {
                Name = "Icon",
                Texture = iconTexture,
                CustomMinimumSize = new Vector2(30f, 30f),
                SizeFlagsHorizontal = SizeFlags.ShrinkBegin,
                SizeFlagsVertical = SizeFlags.ShrinkCenter,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                Modulate = iconTint,
                PivotOffset = new Vector2(15f, 15f),
                MouseFilter = MouseFilterEnum.Ignore
            };
            content.AddChild(_icon);

            _label = new MegaLabel
            {
                Name = "Label",
                CustomMinimumSize = new Vector2(0f, 52f),
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                SizeFlagsVertical = SizeFlags.ShrinkCenter,
                AutoSizeEnabled = false,
                MinFontSize = 16,
                MaxFontSize = 22,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                MouseFilter = MouseFilterEnum.Ignore
            };
            _label.AddThemeFontOverride(
                "font",
                CommonHelpers.LoadGameFont("res://themes/kreon_bold_glyph_space_one.tres"));
            _label.AddThemeFontSizeOverride("font_size", 22);
            _label.AddThemeColorOverride("font_color", StsColors.cream);
            _label.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f, 0.55f));
            _label.AddThemeConstantOverride("outline_size", 8);
            _label.AddThemeColorOverride("font_shadow_color", new Color(0f, 0f, 0f, 0.35f));
            _label.AddThemeConstantOverride("shadow_offset_x", 2);
            _label.AddThemeConstantOverride("shadow_offset_y", 2);
            content.AddChild(_label);

            _separator = new ColorRect
            {
                Name = "Separator",
                Color = new Color(0.48f, 0.62f, 0.66f, 0.22f),
                MouseFilter = MouseFilterEnum.Ignore
            };
            _separator.AnchorLeft = 0f;
            _separator.AnchorTop = 1f;
            _separator.AnchorRight = 1f;
            _separator.AnchorBottom = 1f;
            _separator.OffsetLeft = 54f;
            _separator.OffsetTop = -1f;
            _separator.OffsetRight = -8f;
            _separator.OffsetBottom = 0f;
            AddChild(_separator);

            SetLabel(text);
            MouseEntered += OnMouseEntered;
            MouseExited += OnMouseExited;
            FocusEntered += OnFocusEntered;
            FocusExited += OnFocusExited;
            ButtonDown += OnButtonDown;
            ButtonUp += OnButtonUp;
            Pressed += OnPressedAudio;
        }

        public override void _ExitTree()
        {
            _tween?.Kill();
            _tween = null;
            MouseEntered -= OnMouseEntered;
            MouseExited -= OnMouseExited;
            FocusEntered -= OnFocusEntered;
            FocusExited -= OnFocusExited;
            ButtonDown -= OnButtonDown;
            ButtonUp -= OnButtonUp;
            Pressed -= OnPressedAudio;
        }

        public void SetLabel(string text) => _label.SetTextAutoSize(text);

        public void SetArmed(bool armed)
        {
            _armed = armed;
            ApplyVisualState(animate: true);
        }

        public void SetSeparatorVisible(bool visible) => _separator.Visible = visible;

        public void ResetInteractionState()
        {
            _hovered = false;
            _focused = false;
            _pressed = false;
            if (HasFocus())
                ReleaseFocus();
            ApplyVisualState(animate: false);
        }

        private bool IsHighlighted => _hovered || _focused;

        private void OnMouseEntered()
        {
            bool wasHighlighted = IsHighlighted;
            _hovered = true;
            if (!wasHighlighted)
                SfxCmd.Play(FmodSfx.uiHover);
            ApplyVisualState(animate: true);
        }

        private void OnMouseExited()
        {
            _hovered = false;
            ApplyVisualState(animate: true);
        }

        private void OnFocusEntered()
        {
            bool wasHighlighted = IsHighlighted;
            _focused = true;
            if (!wasHighlighted)
                SfxCmd.Play(FmodSfx.uiHover);
            ApplyVisualState(animate: true);
        }

        private void OnFocusExited()
        {
            _focused = false;
            ApplyVisualState(animate: true);
        }

        private void OnButtonDown()
        {
            _pressed = true;
            ApplyVisualState(animate: true);
        }

        private void OnButtonUp()
        {
            _pressed = false;
            ApplyVisualState(animate: true);
        }

        private static void OnPressedAudio() =>
            SfxCmd.Play("event:/sfx/ui/clicks/ui_click");

        private void ApplyVisualState(bool animate)
        {
            bool highlighted = IsHighlighted;
            Color highlightColor = _armed
                ? (highlighted ? ArmedHoverColor : ArmedColor)
                : (highlighted ? HoverColor : IdleHighlightColor);
            Color textColor = _armed
                ? ArmedTextColor
                : (highlighted ? StsColors.gold : StsColors.cream);
            float scale = _pressed ? 0.94f : (highlighted ? 1.06f : 1f);
            Vector2 iconScale = Vector2.One * scale;
            _label.AddThemeColorOverride("font_color", textColor);
            _icon.Modulate = _armed ? new Color(1f, 0.62f, 0.48f) : _iconTint;

            _tween?.Kill();
            if (!animate || !IsInsideTree())
            {
                _highlight.Color = highlightColor;
                _icon.Scale = iconScale;
                return;
            }

            _tween = CreateTween().SetParallel();
            _tween.TweenProperty(_highlight, "color", highlightColor, 0.12)
                .SetEase(Tween.EaseType.Out)
                .SetTrans(Tween.TransitionType.Expo);
            _tween.TweenProperty(_icon, "scale", iconScale, 0.12)
                .SetEase(Tween.EaseType.Out)
                .SetTrans(Tween.TransitionType.Expo);
        }
    }
}
