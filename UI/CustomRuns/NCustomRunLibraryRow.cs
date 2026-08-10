#nullable enable

namespace Loadout.UI.CustomRuns;

using System;
using System.Collections.Generic;
using Godot;
using Loadout.UI.Screens.Controls;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;

public sealed record CustomRunLibraryRowOptions(
    string Name,
    string Description,
    string PrimaryLabel,
    Action? PrimaryAction,
    Action RowAction,
    bool ShowDelete,
    Action? DeleteAction,
    string TrailingLabel,
    Action TrailingAction,
    bool PrimaryEnabled = true,
    bool IsCreateRow = false,
    string? ReorderId = null,
    Action<string, string?, bool>? ReorderAction = null);

public partial class NCustomRunLibraryRow : NButton
{
    private const string DragPrefix = "loadout-custom-run|";

    private CustomRunLibraryRowOptions? _options;
    private ColorRect? _hoverTint;
    private ColorRect? _divider;
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
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        PivotOffset = Size * 0.5f;
        _ignoreDragThreshold = 8f;
        Build();
        FocusMode = FocusModeEnum.All;
        MouseFilter = MouseFilterEnum.Stop;
        ConnectSignals();
        Connect(NClickableControl.SignalName.Released, Callable.From<NClickableControl>(_ => _options?.RowAction()));
    }

    public override void _ExitTree()
    {
        _tween?.Kill();
        _tween = null;
        base._ExitTree();
    }

    protected override void OnFocus()
    {
        base.OnFocus();
        Animate(true);
    }

    protected override void OnUnfocus()
    {
        base.OnUnfocus();
        if (_focusedActions == 0)
            Animate(false);
    }

    public override Variant _GetDragData(Vector2 atPosition)
    {
        if (string.IsNullOrEmpty(_options?.ReorderId))
            return default;
        SetDragPreview(CreateDragPreview(_options.Name));
        return DragPrefix + _options.ReorderId;
    }

    public override bool _CanDropData(Vector2 atPosition, Variant data)
    {
        return _options?.ReorderAction is not null
               && TryGetDragId(data, out string sourceId)
               && !string.Equals(sourceId, _options.ReorderId, StringComparison.Ordinal);
    }

    public override void _DropData(Vector2 atPosition, Variant data)
    {
        if (_options?.ReorderAction is null || !TryGetDragId(data, out string sourceId))
            return;
        string? targetId = _options.IsCreateRow ? null : _options.ReorderId;
        _options.ReorderAction(sourceId, targetId, targetId is not null && atPosition.Y >= Size.Y * 0.5f);
    }

    private void Build()
    {
        if (_options is null)
            return;

        foreach (Node child in GetChildren())
            child.QueueFree();
        _actions.Clear();
        _actionSlots.Clear();

        CustomMinimumSize = new Vector2(0f, _options.IsCreateRow ? 92f : 104f);
        _hoverTint = new ColorRect
        {
            Name = "HoverTint",
            Color = new Color(0.95f, 0.79f, 0.36f, 0f),
            MouseFilter = MouseFilterEnum.Ignore
        };
        _hoverTint.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(_hoverTint);

        if (_options.IsCreateRow)
            BuildCreateRow();
        else
            BuildSavedRow();

        _divider = new ColorRect
        {
            Name = "Divider",
            Color = new Color(0.909804f, 0.862745f, 0.745098f, 0.25098f),
            MouseFilter = MouseFilterEnum.Ignore
        };
        _divider.SetAnchorsPreset(LayoutPreset.BottomWide);
        _divider.OffsetTop = -2f;
        AddChild(_divider);
    }

    private void BuildSavedRow()
    {
        if (_options is null)
            return;

        MarginContainer margin = new();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        margin.AddThemeConstantOverride("margin_left", 12);
        margin.AddThemeConstantOverride("margin_top", 8);
        margin.AddThemeConstantOverride("margin_right", 12);
        margin.AddThemeConstantOverride("margin_bottom", 8);
        margin.MouseFilter = MouseFilterEnum.Ignore;
        AddChild(margin);

        HBoxContainer row = new() { MouseFilter = MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 16);
        margin.AddChild(row);

        NLoadoutSettingsActionButton primary = AddSettingsAction(
            row,
            "primary",
            _options.PrimaryLabel,
            154f,
            _options.PrimaryAction);
        if (!_options.PrimaryEnabled)
            primary.Disable();

        VBoxContainer text = new()
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            MouseFilter = MouseFilterEnum.Ignore
        };
        text.AddThemeConstantOverride("separation", -4);
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
        text.AddChild(description);

        RegisterAction(this, "edit");
        AddSettingsAction(row, "export", _options.TrailingLabel, 150f, _options.TrailingAction);

        if (_options.ShowDelete && _options.DeleteAction is not null)
        {
            NCustomRunDeleteButton delete = new()
            {
                CustomMinimumSize = new Vector2(72f, 64f),
                TooltipText = $"Delete {_options.Name}"
            };
            delete.Connect(
                NClickableControl.SignalName.Released,
                Callable.From<NClickableControl>(_ => _options.DeleteAction()));
            row.AddChild(delete);
            RegisterAction(delete, "delete");
        }
    }

    private void BuildCreateRow()
    {
        if (_options is null)
            return;

        NLoadoutSettingsCategoryButton createVisual = new()
        {
            Name = "CreateCustomRunVisual",
            CustomMinimumSize = new Vector2(540f, 78f),
            FocusMode = FocusModeEnum.None,
            MouseFilter = MouseFilterEnum.Ignore
        };
        createVisual.Init("create_visual", _options.PrimaryLabel);
        createVisual.SetAnchorsPreset(LayoutPreset.Center);
        createVisual.OffsetLeft = -270f;
        createVisual.OffsetTop = -39f;
        createVisual.OffsetRight = 270f;
        createVisual.OffsetBottom = 39f;
        AddChild(createVisual);
        createVisual.FocusMode = FocusModeEnum.None;
        createVisual.MouseFilter = MouseFilterEnum.Ignore;
        RegisterAction(this, "primary");

        NLoadoutSettingsActionButton import = new()
        {
            Name = "Import",
            CustomMinimumSize = new Vector2(164f, 64f)
        };
        import.Init("import", _options.TrailingLabel);
        import.SetAnchorsPreset(LayoutPreset.CenterRight);
        import.OffsetLeft = -180f;
        import.OffsetTop = -32f;
        import.OffsetRight = -16f;
        import.OffsetBottom = 32f;
        import.Connect(
            NClickableControl.SignalName.Released,
            Callable.From<NClickableControl>(_ => _options.TrailingAction()));
        AddChild(import);
        RegisterAction(import, "import");
    }

    private NLoadoutSettingsActionButton AddSettingsAction(
        Control parent,
        string id,
        string label,
        float width,
        Action? action)
    {
        NLoadoutSettingsActionButton button = new()
        {
            CustomMinimumSize = new Vector2(width, 64f),
            SizeFlagsVertical = SizeFlags.ShrinkCenter
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
        Animate(true);
    }

    private void OnActionUnfocused()
    {
        _focusedActions = Math.Max(0, _focusedActions - 1);
        if (_focusedActions == 0 && !IsFocused)
            Animate(false);
    }

    private void Animate(bool focused)
    {
        _tween?.Kill();
        _tween = CreateTween().SetParallel();
        if (_hoverTint is not null)
        {
            _tween.TweenProperty(_hoverTint, "color:a", focused ? 0.07f : 0f, focused ? 0.1f : 0.3f);
        }
        if (_divider is not null)
        {
            _tween.TweenProperty(_divider, "color:a", focused ? 0.5f : 0.25098f, focused ? 0.1f : 0.3f);
        }
    }

    private static bool TryGetDragId(Variant data, out string id)
    {
        id = string.Empty;
        if (data.VariantType != Variant.Type.String)
            return false;
        string value = data.AsString();
        if (!value.StartsWith(DragPrefix, StringComparison.Ordinal))
            return false;
        id = value[DragPrefix.Length..];
        return id.Length > 0;
    }

    private static Control CreateDragPreview(string title)
    {
        PanelContainer panel = new()
        {
            CustomMinimumSize = new Vector2(460f, 68f),
            MouseFilter = MouseFilterEnum.Ignore
        };
        StyleBoxFlat style = new()
        {
            BgColor = new Color(0.04f, 0.08f, 0.1f, 0.94f),
            BorderColor = new Color(0.91f, 0.72f, 0.2f, 0.9f)
        };
        style.SetBorderWidthAll(2);
        panel.AddThemeStyleboxOverride("panel", style);
        panel.AddChild(CreateLabel(title, 24, StsColors.gold, true, 64f));
        return panel;
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

    private static Font? LoadFont(string path)
    {
        return ResourceLoader.Exists(path) ? GD.Load<Font>(path) : null;
    }
}
