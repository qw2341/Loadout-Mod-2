#nullable enable

namespace Loadout.UI.CustomRuns;

using System;
using System.Collections.Generic;
using Godot;
using Loadout.Services.CustomRuns.Models;
using Loadout.Services.CustomRuns.Registry;
using Loadout.UI.Managers;
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
    bool ReadOnly = false,
    bool SuppressedByPermanent = false);

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
        ConnectSignals();
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
            TooltipText = _options.SuppressedByPermanent
                ? LocMan.Loc("CUSTOM_RUN_SUPPRESSED_BY_PERMANENT", "Disabled here because an enabled Permanent Rule has identical behavior")
                : _options.Rule.Enabled
                    ? LocMan.Loc("CUSTOM_RUN_DISABLE_RULE", "Disable rule")
                    : LocMan.Loc("CUSTOM_RUN_ENABLE_RULE", "Enable rule")
        };
        enabled.Init(_options.Rule.Id, string.Empty, _options.Rule.Enabled && !_options.SuppressedByPermanent);
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
        string summaryText = _options.SuppressedByPermanent
            ? LocMan.Loc("CUSTOM_RUN_PERMANENT_COPY_ACTIVE", "Permanent copy active  •  {0}", DescribeRule(_options.Rule)).ToUpperInvariant()
            : DescribeRule(_options.Rule);
        MegaLabel summary = CreateLabel(summaryText, 19, _options.SuppressedByPermanent ? StsColors.gray : StsColors.cream, bold: false, 42f);
        summary.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        summary.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        summary.TooltipText = summary.Text;
        text.AddChild(summary);

        NLoadoutSettingsActionButton duplicate = AddAction(row, "duplicate", LocMan.Loc("CREATURE_MANIP_DUPLICATE", "Duplicate").ToUpperInvariant(), 148f, _options.DuplicateAction);
        NLoadoutSettingsActionButton permanent = AddAction(row, "permanent", LocMan.Loc("CUSTOM_RUN_PERMANENT", "Permanent").ToUpperInvariant(), 158f, _options.PermanentAction);

        NCustomRunDeleteButton delete = new()
        {
            Name = "Delete",
            CustomMinimumSize = new Vector2(72f, 64f),
            TooltipText = LocMan.Loc("CUSTOM_RUN_DELETE_NAMED", "Delete {0}", _options.Rule.Name)
        };
        delete.Connect(
            NClickableControl.SignalName.Released,
            Callable.From<NClickableControl>(_ => _options.DeleteAction()));
        row.AddChild(delete);
        _actions.Add(delete);

        if (_options.ReadOnly || _options.SuppressedByPermanent)
        {
            enabled.MouseFilter = MouseFilterEnum.Ignore;
            enabled.FocusMode = FocusModeEnum.None;
        }
        if (_options.ReadOnly)
        {
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
            : string.IsNullOrWhiteSpace(rule.Trigger.TypeId)
                ? LocMan.Loc("CUSTOM_RUN_CHOOSE_TRIGGER", "Choose a trigger")
                : rule.Trigger.TypeId;
        int conditionCount = CountConditions(rule.Conditions);
        string conditions = conditionCount == 1
            ? LocMan.Loc("CUSTOM_RUN_ONE_CONDITION", "1 condition")
            : LocMan.Loc("CUSTOM_RUN_CONDITION_COUNT", "{0} conditions", conditionCount);
        string actions = rule.Actions.Count == 1
            ? LocMan.Loc("CUSTOM_RUN_ONE_ACTION", "1 action")
            : LocMan.Loc("CUSTOM_RUN_ACTION_COUNT", "{0} actions", rule.Actions.Count);
        string limit = rule.Limit.Kind switch
        {
            RuleLimitKind.Unlimited => LocMan.Loc("CUSTOM_RUN_LIMIT_UNLIMITED_LOWER", "unlimited"),
            RuleLimitKind.OncePerEventChain => LocMan.Loc("CUSTOM_RUN_LIMIT_ONCE_PER_EVENT_CHAIN_LOWER", "once per event chain"),
            RuleLimitKind.TimesPerTurn => LocMan.Loc("CUSTOM_RUN_LIMIT_PER_TURN", "{0} per turn", rule.Limit.Count),
            RuleLimitKind.TimesPerCombat => LocMan.Loc("CUSTOM_RUN_LIMIT_PER_COMBAT", "{0} per combat", rule.Limit.Count),
            RuleLimitKind.TimesPerRun => LocMan.Loc("CUSTOM_RUN_LIMIT_PER_RUN", "{0} per run", rule.Limit.Count),
            RuleLimitKind.UntilCondition => LocMan.Loc("CUSTOM_RUN_LIMIT_UNTIL_CONDITION_LOWER", "until condition"),
            _ => rule.Limit.Kind.ToString()
        };
        return LocMan.Loc("CUSTOM_RUN_RULE_SUMMARY", "WHEN {0}  •  {1}  •  {2}  •  {3}", trigger, conditions, actions, limit);
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
