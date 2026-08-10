#nullable enable

namespace Loadout.UI.CustomRuns;

using System;
using System.Collections.Generic;
using Godot;
using Loadout.UI.Screens.Controls;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Entities.UI;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;

public sealed record CustomRunLibraryRowOptions(
    string Name,
    string Description,
    string PrimaryLabel,
    Action? PrimaryAction,
    string? SecondaryLabel,
    Action? SecondaryAction,
    bool ShowDelete,
    Action? DeleteAction,
    string TrailingLabel,
    Action TrailingAction,
    bool PrimaryEnabled = true);

public partial class NCustomRunLibraryRow : Control
{
    private CustomRunLibraryRowOptions? _options;
    private TextureRect? _outline;
    private Tween? _tween;
    private int _focusedActions;
    private readonly List<NClickableControl> _actions = [];
    private readonly List<string> _actionSlots = [];

    public IReadOnlyList<NClickableControl> Actions => _actions;
    public NClickableControl? PrimaryFocusControl => _actions.Count > 0 ? _actions[0] : null;
    public string GetActionSlot(int index) => _actionSlots[index];
    public int FindActionSlot(string slot) => _actionSlots.FindIndex(candidate => string.Equals(candidate, slot, StringComparison.Ordinal));

    public void Init(CustomRunLibraryRowOptions options)
    {
        _options = options;
        if (IsNodeReady())
            Build();
    }

    public override void _Ready()
    {
        CustomMinimumSize = new Vector2(0f, 116f);
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        MouseFilter = MouseFilterEnum.Ignore;
        Build();
        Callable.From(() => PivotOffset = Size * 0.5f).CallDeferred();
    }

    public override void _ExitTree()
    {
        _tween?.Kill();
        _tween = null;
    }

    private void Build()
    {
        if (_options is null)
            return;

        foreach (Node child in GetChildren())
            child.QueueFree();
        _actions.Clear();
        _actionSlots.Clear();

        TextureRect background = new()
        {
            Name = "Background",
            Texture = LoadTexture("res://images/packed/common_ui/ancient_event_option_button.png"),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            Modulate = new Color(0.68f, 0.68f, 0.68f, 0.9f),
            MouseFilter = MouseFilterEnum.Ignore
        };
        background.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(background);

        _outline = new TextureRect
        {
            Name = "Outline",
            Texture = LoadTexture("res://images/packed/common_ui/ancient_event_option_button_outline.png"),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            Modulate = Colors.Transparent,
            MouseFilter = MouseFilterEnum.Ignore
        };
        _outline.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(_outline);

        MarginContainer margin = new();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        margin.AddThemeConstantOverride("margin_left", 26);
        margin.AddThemeConstantOverride("margin_top", 16);
        margin.AddThemeConstantOverride("margin_right", 24);
        margin.AddThemeConstantOverride("margin_bottom", 16);
        AddChild(margin);

        HBoxContainer row = new()
        {
            MouseFilter = MouseFilterEnum.Pass
        };
        row.AddThemeConstantOverride("separation", 12);
        margin.AddChild(row);

        NLoadoutActionButton primary = AddAction(row, "primary", _options.PrimaryLabel, 168f, _options.PrimaryAction);
        if (!_options.PrimaryEnabled)
            primary.Disable();

        VBoxContainer text = new()
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            MouseFilter = MouseFilterEnum.Ignore
        };
        text.AddThemeConstantOverride("separation", -2);
        row.AddChild(text);
        text.AddChild(CreateLabel(_options.Name, 27, StsColors.gold, bold: true, 38f));
        MegaLabel description = CreateLabel(
            string.IsNullOrWhiteSpace(_options.Description) ? "No description." : _options.Description,
            18,
            new Color(0.94f, 0.91f, 0.82f),
            bold: false,
            44f);
        description.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        description.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        description.TooltipText = _options.Description;
        description.MouseFilter = MouseFilterEnum.Pass;
        text.AddChild(description);

        if (!string.IsNullOrWhiteSpace(_options.SecondaryLabel) && _options.SecondaryAction is not null)
            AddAction(row, "edit", _options.SecondaryLabel, 116f, _options.SecondaryAction);

        if (_options.ShowDelete && _options.DeleteAction is not null)
        {
            NCustomRunDeleteButton delete = new() { TooltipText = $"Delete {_options.Name}" };
            delete.Connect(
                NClickableControl.SignalName.Released,
                Callable.From<NClickableControl>(_ => _options.DeleteAction()));
            row.AddChild(delete);
            RegisterAction(delete, "delete");
        }

        AddAction(row, "export", _options.TrailingLabel, 132f, _options.TrailingAction);
    }

    private NLoadoutActionButton AddAction(
        Control parent,
        string id,
        string label,
        float width,
        Action? action)
    {
        NLoadoutActionButton button = new()
        {
            CustomMinimumSize = new Vector2(width, 58f)
        };
        button.Init(id, label);
        if (action is not null)
        {
            button.Connect(
                NClickableControl.SignalName.Released,
                Callable.From<NClickableControl>(_ => action()));
        }
        parent.AddChild(button);
        RegisterAction(button, id);
        return button;
    }

    private void RegisterAction(NClickableControl control, string slot)
    {
        _actions.Add(control);
        _actionSlots.Add(slot);
        control.Connect(NClickableControl.SignalName.Focused, Callable.From<NClickableControl>(_ => OnActionFocused()));
        control.Connect(NClickableControl.SignalName.Unfocused, Callable.From<NClickableControl>(_ => OnActionUnfocused()));
    }

    private void OnActionFocused()
    {
        _focusedActions++;
        Animate(new Vector2(1.01f, 1.01f), new Color(0.55f, 0.82f, 1f, 0.9f), 0.14f);
    }

    private void OnActionUnfocused()
    {
        _focusedActions = Math.Max(0, _focusedActions - 1);
        if (_focusedActions == 0)
            Animate(Vector2.One, Colors.Transparent, 0.24f);
    }

    private void Animate(Vector2 scale, Color outline, float duration)
    {
        _tween?.Kill();
        _tween = CreateTween().SetParallel();
        _tween.TweenProperty(this, "scale", scale, duration)
            .SetEase(Tween.EaseType.Out)
            .SetTrans(Tween.TransitionType.Expo);
        if (_outline is not null)
            _tween.TweenProperty(_outline, "modulate", outline, duration);
    }

    private static MegaLabel CreateLabel(string value, int fontSize, Color color, bool bold, float height)
    {
        MegaLabel label = new()
        {
            Text = value,
            AutoSizeEnabled = false,
            MinFontSize = Math.Max(12, fontSize - 5),
            MaxFontSize = fontSize,
            CustomMinimumSize = new Vector2(0f, height),
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore
        };
        label.AddThemeFontOverride(
            "font",
            LoadFont(bold
                ? "res://themes/kreon_bold_glyph_space_one.tres"
                : "res://themes/kreon_regular_shared.tres"));
        label.AddThemeFontSizeOverride("font_size", fontSize);
        label.AddThemeColorOverride("font_color", color);
        label.AddThemeColorOverride("font_shadow_color", new Color(0f, 0f, 0f, 0.75f));
        label.AddThemeConstantOverride("shadow_offset_x", 3);
        label.AddThemeConstantOverride("shadow_offset_y", 2);
        return label;
    }

    private static Texture2D? LoadTexture(string path)
    {
        string localPath = path.Replace("res://images/", "res://Loadout/images/");
        if (ResourceLoader.Exists(localPath))
            return GD.Load<Texture2D>(localPath);
        return ResourceLoader.Exists(path) ? GD.Load<Texture2D>(path) : null;
    }

    private static Font? LoadFont(string path)
    {
        return ResourceLoader.Exists(path) ? GD.Load<Font>(path) : null;
    }
}
