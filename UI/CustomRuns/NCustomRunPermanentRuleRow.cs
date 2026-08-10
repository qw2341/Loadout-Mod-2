#nullable enable

namespace Loadout.UI.CustomRuns;

using System;
using System.Collections.Generic;
using Godot;
using Loadout.Services.CustomRuns.Models;
using Loadout.UI.Screens.Controls;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;

public partial class NCustomRunPermanentRuleRow : Control
{
    private const string DragPrefix = "loadout-permanent-rule|";

    private RuleDefinition? _rule;
    private Action<string, bool>? _toggleAction;
    private Action<RuleDefinition>? _deleteAction;
    private Action<string, string?, bool>? _reorderAction;
    private ColorRect? _topDropIndicator;
    private ColorRect? _bottomDropIndicator;
    private readonly List<Control> _actions = [];

    public IReadOnlyList<Control> Actions => _actions;
    public string RuleId => _rule?.Id ?? string.Empty;

    public void Init(
        RuleDefinition rule,
        Action<string, bool> toggleAction,
        Action<RuleDefinition> deleteAction,
        Action<string, string?, bool> reorderAction)
    {
        _rule = rule;
        _toggleAction = toggleAction;
        _deleteAction = deleteAction;
        _reorderAction = reorderAction;
    }

    public override void _Ready()
    {
        CustomMinimumSize = new Vector2(0f, 96f);
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        MouseFilter = MouseFilterEnum.Stop;
        Build();
    }

    public override Variant _GetDragData(Vector2 atPosition)
    {
        if (_rule is null)
            return default;
        NCustomRunDragVisual.Show(_rule.Name);
        return DragPrefix + _rule.Id;
    }

    public override bool _CanDropData(Vector2 atPosition, Variant data)
    {
        bool canDrop = TryGetDragId(data, out string sourceId)
                       && _rule is not null
                       && !string.Equals(sourceId, _rule.Id, StringComparison.Ordinal);
        if (canDrop)
            ShowDropIndicator(atPosition.Y < Size.Y * 0.5f);
        else
            NCustomRunDragVisual.HideInsertion(this);
        return canDrop;
    }

    public override void _DropData(Vector2 atPosition, Variant data)
    {
        if (!TryGetDragId(data, out string sourceId) || _rule is null)
            return;
        NCustomRunDragVisual.Clear();
        _reorderAction?.Invoke(sourceId, _rule.Id, atPosition.Y >= Size.Y * 0.5f);
    }

    public override void _Notification(int what)
    {
        base._Notification(what);
        if (what == NotificationDragEnd)
            NCustomRunDragVisual.Clear();
    }

    public override void _ExitTree()
    {
        NCustomRunDragVisual.HideInsertion(this);
    }

    private void Build()
    {
        if (_rule is null)
            return;

        MarginContainer margin = new() { MouseFilter = MouseFilterEnum.Ignore };
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        margin.AddThemeConstantOverride("margin_left", 12);
        margin.AddThemeConstantOverride("margin_top", 7);
        margin.AddThemeConstantOverride("margin_right", 12);
        margin.AddThemeConstantOverride("margin_bottom", 7);
        AddChild(margin);

        HBoxContainer row = new() { MouseFilter = MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 16);
        margin.AddChild(row);

        VBoxContainer text = new()
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            MouseFilter = MouseFilterEnum.Ignore
        };
        text.AddThemeConstantOverride("separation", -4);
        row.AddChild(text);
        text.AddChild(CreateLabel(_rule.Name, 26, StsColors.gold, true, 38f));
        string descriptionText = string.IsNullOrWhiteSpace(_rule.Description)
            ? DescribeRule(_rule)
            : _rule.Description;
        MegaLabel description = CreateLabel(descriptionText, 18, StsColors.cream, false, 40f);
        description.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        description.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        description.TooltipText = descriptionText;
        text.AddChild(description);

        NLoadoutToggle toggle = new()
        {
            Name = "EnabledToggle",
            CustomMinimumSize = new Vector2(72f, 64f),
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            TooltipText = _rule.Enabled ? "Disable permanent rule" : "Enable permanent rule"
        };
        toggle.Init(_rule.Id, string.Empty, _rule.Enabled);
        toggle.Connect(
            NLoadoutToggle.SignalName.Toggled,
            Callable.From<NLoadoutToggle>(changed =>
            {
                _toggleAction?.Invoke(_rule.Id, changed.IsChecked);
            }));
        row.AddChild(toggle);
        _actions.Add(toggle);

        NCustomRunDeleteButton delete = new()
        {
            Name = "Delete",
            CustomMinimumSize = new Vector2(72f, 64f),
            TooltipText = $"Delete {_rule.Name}"
        };
        delete.Connect(
            NClickableControl.SignalName.Released,
            Callable.From<NClickableControl>(_ =>
            {
                _deleteAction?.Invoke(_rule);
            }));
        row.AddChild(delete);
        _actions.Add(delete);

        ColorRect divider = new()
        {
            Name = "Divider",
            Color = new Color(0.909804f, 0.862745f, 0.745098f, 0.25098f),
            MouseFilter = MouseFilterEnum.Ignore
        };
        divider.SetAnchorsPreset(LayoutPreset.BottomWide);
        divider.OffsetTop = -2f;
        AddChild(divider);

        _topDropIndicator = CreateDropIndicator();
        _topDropIndicator.SetAnchorsPreset(LayoutPreset.TopWide);
        _topDropIndicator.OffsetBottom = 5f;
        AddChild(_topDropIndicator);

        _bottomDropIndicator = CreateDropIndicator();
        _bottomDropIndicator.SetAnchorsPreset(LayoutPreset.BottomWide);
        _bottomDropIndicator.OffsetTop = -5f;
        AddChild(_bottomDropIndicator);
    }

    private static string DescribeRule(RuleDefinition rule)
    {
        string trigger = string.IsNullOrWhiteSpace(rule.Trigger.TypeId) ? "Unconfigured trigger" : rule.Trigger.TypeId;
        string actionCount = rule.Actions.Count == 1 ? "1 action" : $"{rule.Actions.Count} actions";
        return $"{trigger} · {actionCount}";
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

    private void ShowDropIndicator(bool before)
    {
        ColorRect? indicator = before ? _topDropIndicator : _bottomDropIndicator;
        if (indicator is not null)
            NCustomRunDragVisual.ShowInsertion(this, indicator);
    }

    private static ColorRect CreateDropIndicator()
    {
        ColorRect indicator = new()
        {
            Visible = false,
            ZIndex = 80,
            Color = new Color(1f, 0.77f, 0.18f, 0.98f),
            MouseFilter = MouseFilterEnum.Ignore
        };
        string materialPath = "res://themes/canvas_item_material_additive_shared.tres";
        if (ResourceLoader.Exists(materialPath))
            indicator.Material = GD.Load<Material>(materialPath);
        return indicator;
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
            ResourceLoader.Exists(bold
                ? "res://themes/kreon_bold_glyph_space_one.tres"
                : "res://themes/kreon_regular_shared.tres")
                ? GD.Load<Font>(bold
                    ? "res://themes/kreon_bold_glyph_space_one.tres"
                    : "res://themes/kreon_regular_shared.tres")
                : null);
        label.AddThemeFontSizeOverride("font_size", fontSize);
        label.AddThemeColorOverride("font_color", color);
        label.AddThemeColorOverride("font_shadow_color", new Color(0f, 0f, 0f, 0.65f));
        label.AddThemeConstantOverride("shadow_offset_x", 3);
        label.AddThemeConstantOverride("shadow_offset_y", 2);
        return label;
    }
}
