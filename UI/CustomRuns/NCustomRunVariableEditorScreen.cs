#nullable enable

namespace Loadout.UI.CustomRuns;

using System;
using System.Linq;
using Godot;
using Loadout.Services.CustomRuns.Models;
using Loadout.UI.Managers;
using Loadout.UI.Screens.Controls;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Entities.UI;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;

public partial class NCustomRunVariableEditorScreen : Control
{
    private const string ScenePath = "res://UI/CustomRuns/CustomRunVariableEditorScreen.tscn";

    private VariableDefinition _working = new();
    private Action<VariableDefinition>? _confirmed;
    private VariableValueType? _requiredType;
    private VBoxContainer? _fields;
    private LineEdit? _name;

    public static void Open(
        Control source,
        VariableValueType? requiredType,
        string suggestedName,
        Action<VariableDefinition> confirmed)
    {
        NLoadoutPanelRoot? root = NLoadoutPanelRoot.GetOrAttach(source.GetTree());
        if (root is null)
            return;
        NCustomRunVariableEditorScreen? screen = root.GetNodeOrNull<NCustomRunVariableEditorScreen>(
            "ScreenStack/CustomRunVariableEditorScreen");
        if (screen is null)
        {
            screen = ResourceLoader.Exists(ScenePath)
                     && GD.Load<PackedScene>(ScenePath) is { } scene
                ? scene.Instantiate<NCustomRunVariableEditorScreen>()
                : new NCustomRunVariableEditorScreen();
            screen.Name = "CustomRunVariableEditorScreen";
        }
        screen.Init(requiredType, suggestedName, confirmed);
        root.OpenScreen(screen);
    }

    private void Init(
        VariableValueType? requiredType,
        string suggestedName,
        Action<VariableDefinition> confirmed)
    {
        _requiredType = requiredType;
        _confirmed = confirmed;
        _working = new VariableDefinition
        {
            Name = suggestedName,
            ValueType = requiredType ?? VariableValueType.Number,
            Scope = VariableScope.Run
        };
        if (IsNodeReady())
            RebuildFields();
    }

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        ZIndex = 130;
        EnsureFallbackScene();
        BuildStaticUi();
        RebuildFields();
    }

    public override void _Notification(int what)
    {
        if (what == NotificationVisibilityChanged && IsNodeReady() && Visible)
            RebuildFields();
    }

    private void BuildStaticUi()
    {
        Control? titleMount = GetNodeOrNull<Control>("%TitleMount");
        if (titleMount is not null && titleMount.GetChildCount() == 0)
        {
            MegaLabel title = CreateLabel(
                LocMan.Loc("CUSTOM_RUN_CREATE_VARIABLE", "Create Variable").ToUpperInvariant(),
                42,
                StsColors.gold);
            title.SetAnchorsPreset(LayoutPreset.FullRect);
            titleMount.AddChild(title);
        }

        _fields = GetNodeOrNull<VBoxContainer>("%Fields");
        Control? backMount = GetNodeOrNull<Control>("%BackButtonMount");
        if (backMount is not null && backMount.GetNodeOrNull<NBackButton>("BackButton") is null)
        {
            NBackButton back = NLoadoutBackButtonFactory.Create();
            back.Name = "BackButton";
            back.Connect(
                NClickableControl.SignalName.Released,
                Callable.From<NClickableControl>(_ => NLoadoutPanelRoot.Instance?.CloseTopScreen()));
            backMount.AddChild(back);
            Callable.From(back.Enable).CallDeferred();
        }

        Control? confirmMount = GetNodeOrNull<Control>("%ConfirmButtonMount");
        if (confirmMount is not null && confirmMount.GetNodeOrNull<NConfirmButton>("ConfirmButton") is null)
        {
            NConfirmButton confirm = NLoadoutConfirmButtonFactory.Create();
            confirm.Name = "ConfirmButton";
            confirm.Connect(
                NClickableControl.SignalName.Released,
                Callable.From<NClickableControl>(_ => Confirm()));
            confirmMount.AddChild(confirm);
            Callable.From(confirm.Enable).CallDeferred();
        }
    }

    private void RebuildFields()
    {
        if (_fields is null || !GodotObject.IsInstanceValid(_fields))
            return;
        foreach (Node child in _fields.GetChildren())
            child.QueueFree();

        _fields.AddChild(CreateFieldLabel(LocMan.Loc("CUSTOM_RUN_VARIABLE_NAME", "Variable Name")));
        _name = new LineEdit
        {
            Text = _working.Name,
            CustomMinimumSize = new Vector2(0f, 54f),
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        _name.AddThemeColorOverride("font_color", StsColors.cream);
        _name.AddThemeColorOverride("font_focus_color", StsColors.gold);
        _name.TextChanged += value => _working.Name = value;
        _fields.AddChild(_name);

        if (_requiredType is null)
        {
            _fields.AddChild(CreateFieldLabel(LocMan.Loc("CUSTOM_RUN_VALUE_TYPE", "Value Type")));
            NLoadoutDropdown type = CreateDropdown(
                Enum.GetValues<VariableValueType>()
                    .Select(value => new LoadoutDropdownOption(
                        value.ToString(),
                        value == VariableValueType.Number
                            ? LocMan.Loc("CUSTOM_RUN_NUMBER", "Number")
                            : LocMan.Loc("CUSTOM_RUN_BOOLEAN", "Boolean"))),
                _working.ValueType.ToString());
            type.SelectedItemChanged += selected =>
            {
                if (!Enum.TryParse(selected, out VariableValueType valueType))
                    return;
                _working.ValueType = valueType;
                RebuildFieldsDeferred();
            };
            _fields.AddChild(type);
        }

        _fields.AddChild(CreateFieldLabel(LocMan.Loc("FILTER_GROUP_SCOPE", "Scope")));
        NLoadoutDropdown scope = CreateDropdown(
            Enum.GetValues<VariableScope>()
                .Select(value => new LoadoutDropdownOption(value.ToString(), FormatScope(value))),
            _working.Scope.ToString());
        scope.SelectedItemChanged += selected =>
        {
            if (Enum.TryParse(selected, out VariableScope valueScope))
                _working.Scope = valueScope;
        };
        _fields.AddChild(scope);

        _fields.AddChild(CreateFieldLabel(LocMan.Loc("CUSTOM_RUN_DEFAULT_VALUE", "Default Value")));
        if (_working.ValueType == VariableValueType.Boolean)
        {
            NLoadoutToggle value = new() { CustomMinimumSize = new Vector2(320f, 54f) };
            value.Init(
                "variable_default",
                _working.DefaultBoolean
                    ? LocMan.Loc("CUSTOM_RUN_TRUE", "True").ToUpperInvariant()
                    : LocMan.Loc("CUSTOM_RUN_FALSE", "False").ToUpperInvariant(),
                _working.DefaultBoolean);
            value.Connect(
                NLoadoutToggle.SignalName.Toggled,
                Callable.From<NLoadoutToggle>(toggle =>
                {
                    _working.DefaultBoolean = toggle.IsChecked;
                    RebuildFieldsDeferred();
                }));
            _fields.AddChild(value);
        }
        else
        {
            NLoadoutDecimalStepper value = new();
            value.Init(_working.DefaultNumber, double.MinValue, double.MaxValue, 0.01d);
            value.ValueChanged += number => _working.DefaultNumber = number;
            _fields.AddChild(value);
        }
    }

    private void RebuildFieldsDeferred() => Callable.From(RebuildFields).CallDeferred();

    private void Confirm()
    {
        _working.Name = string.IsNullOrWhiteSpace(_name?.Text)
            ? LocMan.Loc("CUSTOM_RUN_DEFAULT_VARIABLE_NAME", "Variable {0}", 1)
            : _name.Text.Trim();
        VariableDefinition created = new()
        {
            Id = _working.Id,
            Name = _working.Name,
            ValueType = _working.ValueType,
            Scope = _working.Scope,
            DefaultNumber = _working.DefaultNumber,
            DefaultBoolean = _working.DefaultBoolean
        };
        _confirmed?.Invoke(created);
        NLoadoutPanelRoot.Instance?.CloseTopScreen();
    }

    private static NLoadoutDropdown CreateDropdown(System.Collections.Generic.IEnumerable<LoadoutDropdownOption> options, string selected)
    {
        NLoadoutDropdown dropdown = new()
        {
            CustomMinimumSize = new Vector2(520f, 54f),
            DropdownWidth = 520f
        };
        dropdown.SetItems(string.Empty, options, selected);
        return dropdown;
    }

    private static MegaLabel CreateFieldLabel(string text) => CreateLabel(text, 23, StsColors.gold);

    private static MegaLabel CreateLabel(string text, int size, Color color)
    {
        MegaLabel label = new()
        {
            Text = text,
            AutoSizeEnabled = false,
            MinFontSize = Math.Max(12, size - 6),
            MaxFontSize = size,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore,
            CustomMinimumSize = new Vector2(0f, 46f)
        };
        label.AddThemeColorOverride("font_color", color);
        return label;
    }

    private static string FormatScope(VariableScope scope)
    {
        return scope switch
        {
            VariableScope.Run => LocMan.Loc("CUSTOM_RUN_SCOPE_RUN", "Run"),
            VariableScope.Player => LocMan.Loc("CUSTOM_RUN_SCOPE_PLAYER", "Player"),
            VariableScope.Combat => LocMan.Loc("CUSTOM_RUN_SCOPE_COMBAT", "Combat"),
            VariableScope.Turn => LocMan.Loc("CUSTOM_RUN_SCOPE_TURN", "Turn"),
            VariableScope.Role => LocMan.Loc("CUSTOM_RUN_SCOPE_ROLE", "Role"),
            VariableScope.Rule => LocMan.Loc("CUSTOM_RUN_SCOPE_RULE", "Rule"),
            _ => scope.ToString()
        };
    }

    private void EnsureFallbackScene()
    {
        if (GetNodeOrNull<Control>("%TitleMount") is not null)
            return;
        ColorRect backdrop = new() { Color = new Color(0.015f, 0.02f, 0.035f, 0.96f) };
        backdrop.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(backdrop);
        VBoxContainer panel = new()
        {
            Name = "Panel",
            Position = new Vector2(500f, 100f),
            Size = new Vector2(920f, 780f)
        };
        AddChild(panel);
        Control title = new() { Name = "TitleMount", UniqueNameInOwner = true, CustomMinimumSize = new Vector2(0f, 70f) };
        panel.AddChild(title);
        VBoxContainer fields = new() { Name = "Fields", UniqueNameInOwner = true };
        fields.AddThemeConstantOverride("separation", 12);
        panel.AddChild(fields);
        Control back = new() { Name = "BackButtonMount", UniqueNameInOwner = true };
        back.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(back);
        Control confirm = new() { Name = "ConfirmButtonMount", UniqueNameInOwner = true };
        confirm.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(confirm);
    }
}
