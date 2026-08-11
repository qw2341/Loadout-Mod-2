#nullable enable

namespace Loadout.UI.CustomRuns;

using System;
using System.Collections.Generic;
using Godot;
using Loadout.Services.CustomRuns.Models;
using Loadout.Services.CustomRuns.Registry;
using Loadout.UI.Screens.Controls;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;

public sealed record CustomRunRuleRowOptions(
    RuleDefinition Rule,
    Action OpenAction,
    Action<bool> ToggleAction,
    Action DuplicateAction,
    Action PermanentAction,
    Action DeleteAction,
    Action<string, string?, bool> ReorderAction,
    bool ReadOnly = false);

public partial class NCustomRunRuleRow : NButton
{
    private const string DragPrefix = "loadout-custom-rule|";

    private CustomRunRuleRowOptions? _options;
    private ColorRect? _hoverTint;
    private ColorRect? _topDropIndicator;
    private ColorRect? _bottomDropIndicator;
    private Tween? _tween;
    private readonly List<Control> _actions = [];

    public IReadOnlyList<Control> Actions => _actions;
    public string RuleId => _options?.Rule.Id ?? string.Empty;

    public void Init(CustomRunRuleRowOptions options)
    {
        _options = options;
        if (IsNodeReady())
            Build();
    }

    public override void _Ready()
    {
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        FocusMode = FocusModeEnum.All;
        MouseFilter = MouseFilterEnum.Stop;
        _ignoreDragThreshold = 8f;
        Build();
        Connect(NClickableControl.SignalName.Released, Callable.From<NClickableControl>(_ => _options?.OpenAction()));
    }

    public override void _ExitTree()
    {
        NCustomRunDragVisual.HideInsertion(this);
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
        Animate(false);
    }

    public override Variant _GetDragData(Vector2 atPosition)
    {
        if (_options is null || _options.ReadOnly)
            return default;
        NCustomRunDragVisual.Show(_options.Rule.Name);
        return DragPrefix + _options.Rule.Id;
    }

    public override bool _CanDropData(Vector2 atPosition, Variant data)
    {
        bool canDrop = _options is { ReadOnly: false }
                       && TryGetDragId(data, out string sourceId)
                       && !string.Equals(sourceId, _options.Rule.Id, StringComparison.Ordinal);
        if (canDrop)
            ShowDropIndicator(atPosition.Y < Size.Y * 0.5f);
        else
            NCustomRunDragVisual.HideInsertion(this);
        return canDrop;
    }

    public override void _DropData(Vector2 atPosition, Variant data)
    {
        if (_options is null || !TryGetDragId(data, out string sourceId))
            return;
        NCustomRunDragVisual.Clear();
        _options.ReorderAction(sourceId, _options.Rule.Id, atPosition.Y >= Size.Y * 0.5f);
    }

    public override void _Notification(int what)
    {
        base._Notification(what);
        if (what == NotificationDragEnd)
            NCustomRunDragVisual.Clear();
    }

    private void Build()
    {
        if (_options is null)
            return;

        foreach (Node child in GetChildren())
            child.QueueFree();
        _actions.Clear();
        CustomMinimumSize = new Vector2(0f, 112f);

        _hoverTint = new ColorRect
        {
            Color = new Color(0.95f, 0.79f, 0.36f, 0f),
            MouseFilter = MouseFilterEnum.Ignore
        };
        _hoverTint.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(_hoverTint);

        MarginContainer margin = new() { MouseFilter = MouseFilterEnum.Ignore };
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        margin.AddThemeConstantOverride("margin_left", 12);
        margin.AddThemeConstantOverride("margin_top", 9);
        margin.AddThemeConstantOverride("margin_right", 12);
        margin.AddThemeConstantOverride("margin_bottom", 9);
        AddChild(margin);

        HBoxContainer row = new() { MouseFilter = MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 14);
        margin.AddChild(row);

        NLoadoutToggle enabled = new()
        {
            Name = "EnabledToggle",
            CustomMinimumSize = new Vector2(70f, 64f),
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            TooltipText = _options.Rule.Enabled ? "Disable rule" : "Enable rule"
        };
        enabled.Init(_options.Rule.Id, string.Empty, _options.Rule.Enabled);
        enabled.Connect(
            NLoadoutToggle.SignalName.Toggled,
            Callable.From<NLoadoutToggle>(toggle => _options.ToggleAction(toggle.IsChecked)));
        row.AddChild(enabled);
        _actions.Add(enabled);

        VBoxContainer text = new()
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            MouseFilter = MouseFilterEnum.Ignore
        };
        text.AddThemeConstantOverride("separation", -2);
        row.AddChild(text);
        text.AddChild(CreateLabel(_options.Rule.Name, 27, StsColors.gold, bold: true, 42f));
        MegaLabel summary = CreateLabel(DescribeRule(_options.Rule), 19, StsColors.cream, bold: false, 42f);
        summary.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        summary.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        summary.TooltipText = summary.Text;
        text.AddChild(summary);

        NLoadoutSettingsActionButton duplicate = AddAction(row, "duplicate", "DUPLICATE", 148f, _options.DuplicateAction);
        NLoadoutSettingsActionButton permanent = AddAction(row, "permanent", "PERMANENT", 158f, _options.PermanentAction);

        NCustomRunDeleteButton delete = new()
        {
            Name = "Delete",
            CustomMinimumSize = new Vector2(72f, 64f),
            TooltipText = $"Delete {_options.Rule.Name}"
        };
        delete.Connect(
            NClickableControl.SignalName.Released,
            Callable.From<NClickableControl>(_ => _options.DeleteAction()));
        row.AddChild(delete);
        _actions.Add(delete);

        if (_options.ReadOnly)
        {
            enabled.MouseFilter = MouseFilterEnum.Ignore;
            enabled.FocusMode = FocusModeEnum.None;
            duplicate.Disable();
            permanent.Disable();
            delete.Disable();
        }

        ColorRect divider = new()
        {
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

    private NLoadoutSettingsActionButton AddAction(
        Control parent,
        string id,
        string label,
        float width,
        Action action)
    {
        NLoadoutSettingsActionButton button = new()
        {
            Name = id,
            CustomMinimumSize = new Vector2(width, 64f),
            SizeFlagsVertical = SizeFlags.ShrinkCenter
        };
        button.Init(id, label);
        button.Connect(
            NClickableControl.SignalName.Released,
            Callable.From<NClickableControl>(_ => action()));
        parent.AddChild(button);
        _actions.Add(button);
        return button;
    }

    private static string DescribeRule(RuleDefinition rule)
    {
        string trigger = CustomRunRegistry.TryGetTrigger(rule.Trigger.TypeId, out RuleComponentDescriptor descriptor)
            ? descriptor.DisplayName
            : string.IsNullOrWhiteSpace(rule.Trigger.TypeId) ? "Choose a trigger" : rule.Trigger.TypeId;
        int conditionCount = CountConditions(rule.Conditions);
        string conditions = conditionCount == 1 ? "1 condition" : $"{conditionCount} conditions";
        string actions = rule.Actions.Count == 1 ? "1 action" : $"{rule.Actions.Count} actions";
        string limit = rule.Limit.Kind switch
        {
            RuleLimitKind.Unlimited => "unlimited",
            RuleLimitKind.OncePerEventChain => "once per event chain",
            RuleLimitKind.OncePerTurn => "once per turn",
            RuleLimitKind.OncePerCombat => "once per combat",
            RuleLimitKind.OncePerRun => "once per run",
            RuleLimitKind.TimesPerTurn => $"{rule.Limit.Count} per turn",
            RuleLimitKind.TimesPerCombat => $"{rule.Limit.Count} per combat",
            RuleLimitKind.TimesPerRun => $"{rule.Limit.Count} per run",
            _ => rule.Limit.Kind.ToString()
        };
        return $"WHEN {trigger}  •  {conditions}  •  {actions}  •  {limit}";
    }

    private static int CountConditions(ConditionGroupDefinition group)
    {
        int count = group.Conditions.Count;
        foreach (ConditionGroupDefinition child in group.Groups)
            count += CountConditions(child);
        return count;
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
        const string materialPath = "res://themes/canvas_item_material_additive_shared.tres";
        if (ResourceLoader.Exists(materialPath))
            indicator.Material = GD.Load<Material>(materialPath);
        return indicator;
    }

    private void Animate(bool focused)
    {
        _tween?.Kill();
        if (_hoverTint is null)
            return;
        _tween = CreateTween();
        _tween.TweenProperty(_hoverTint, "color:a", focused ? 0.07f : 0f, focused ? 0.1f : 0.25f);
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
        string fontPath = bold
            ? "res://themes/kreon_bold_glyph_space_one.tres"
            : "res://themes/kreon_regular_shared.tres";
        if (ResourceLoader.Exists(fontPath))
            label.AddThemeFontOverride("font", GD.Load<Font>(fontPath));
        label.AddThemeFontSizeOverride("font_size", fontSize);
        label.AddThemeColorOverride("font_color", color);
        label.AddThemeColorOverride("font_shadow_color", new Color(0f, 0f, 0f, 0.65f));
        label.AddThemeConstantOverride("shadow_offset_x", 3);
        label.AddThemeConstantOverride("shadow_offset_y", 2);
        return label;
    }
}
