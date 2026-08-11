#nullable enable

namespace Loadout.UI.CustomRuns;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Loadout.PanelItems;
using Loadout.Services.CustomRuns.Catalog;
using Loadout.Services.CustomRuns.Compilation;
using Loadout.Services.CustomRuns.Models;
using Loadout.Services.CustomRuns.PermanentRules;
using Loadout.Services.CustomRuns.Persistence;
using Loadout.Services.CustomRuns.Registry;
using Loadout.UI.Screens;
using Loadout.UI.Screens.Controls;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.UI;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.Screens.RunHistoryScreen;

public partial class NCustomRunRuleEditorScreen : Control
{
    private const string ScenePath = "res://UI/CustomRuns/CustomRunRuleEditorScreen.tscn";
    private const int MaximumConditionDepth = 5;

    private CustomRunDefinition? _definitionContext;
    private RuleDefinition? _workingRule;
    private Action<RuleDefinition>? _saveAction;
    private NScrollableContainer? _contentScroll;
    private VBoxContainer? _contentHost;
    private MegaLabel? _ruleNameLabel;
    private MegaLabel? _statusLabel;
    private NConfirmButton? _confirmButton;
    private NLoadoutSettingsActionButton? _permanentButton;
    private IDisposable? _catalogSelectorSession;
    private bool _readOnly;
    private bool _editingPermanent;
    private bool _dirty;
    private bool _loadingFields;
    private bool _staticUiBuilt;
    private bool _discardPromptOpen;

    public static void OpenScenario(
        Control source,
        CustomRunDefinition definition,
        RuleDefinition rule,
        bool readOnly,
        Action<RuleDefinition> saveAction)
    {
        Open(source, definition, rule, readOnly, editingPermanent: false, saveAction);
    }

    public static void OpenPermanent(
        Control source,
        RuleDefinition rule,
        Action<RuleDefinition>? saved = null)
    {
        CustomRunDefinition context = new()
        {
            Name = "Permanent Rules",
            Rules = [CustomRunNormalizationService.CloneRule(rule)]
        };
        Open(
            source,
            context,
            rule,
            readOnly: false,
            editingPermanent: true,
            updated =>
            {
                RuleDefinition stored = PermanentRuleStorageService.Upsert(updated);
                saved?.Invoke(stored);
            });
    }

    private static void Open(
        Control source,
        CustomRunDefinition definition,
        RuleDefinition rule,
        bool readOnly,
        bool editingPermanent,
        Action<RuleDefinition> saveAction)
    {
        NLoadoutPanelRoot? root = NLoadoutPanelRoot.GetOrAttach(source.GetTree());
        if (root is null)
            return;

        NCustomRunRuleEditorScreen? screen = root.GetNodeOrNull<NCustomRunRuleEditorScreen>(
            "ScreenStack/CustomRunRuleEditorScreen");
        if (screen is null)
        {
            screen = Create();
            screen.Name = "CustomRunRuleEditorScreen";
        }
        screen.Init(definition, rule, readOnly, editingPermanent, saveAction);
        root.OpenScreen(screen);
    }

    public static NCustomRunRuleEditorScreen Create()
    {
        if (ResourceLoader.Exists(ScenePath)
            && GD.Load<PackedScene>(ScenePath) is { } scene
            && scene.Instantiate<NCustomRunRuleEditorScreen>() is { } screen)
        {
            return screen;
        }

        GD.PushWarning($"Loadout Custom Run: could not load '{ScenePath}'. Using a script-only rule editor.");
        return new NCustomRunRuleEditorScreen();
    }

    public void Init(
        CustomRunDefinition definition,
        RuleDefinition rule,
        bool readOnly,
        bool editingPermanent,
        Action<RuleDefinition> saveAction)
    {
        _definitionContext = CustomRunNormalizationService.Clone(definition);
        _workingRule = CustomRunNormalizationService.CloneRule(rule);
        _readOnly = readOnly;
        _editingPermanent = editingPermanent;
        _saveAction = saveAction;
        _dirty = false;
        if (IsNodeReady())
            RefreshScreen();
    }

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        ZIndex = 120;
        CustomRunRegistry.EnsureBuiltInsRegistered();
        PermanentRuleStorageService.Register();
        EnsureFallbackScene();
        EnsureNativeContentScroll();
        BuildStaticUi();
        RefreshScreen();
    }

    public override void _Notification(int what)
    {
        if (what == NotificationVisibilityChanged && IsNodeReady() && Visible)
            RefreshScreen();
    }

    public override void _Input(InputEvent inputEvent)
    {
        if (!Visible || _contentScroll is null || !GodotObject.IsInstanceValid(_contentScroll))
            return;
        float drag = ScrollHelper.GetDragForScrollEvent(inputEvent);
        if (Mathf.IsZeroApprox(drag))
            return;
        Vector2 pointer = inputEvent is InputEventMouse mouse
            ? mouse.GlobalPosition
            : GetViewport().GetMousePosition();
        if (NLoadoutDropdown.IsOpenDropdownAt(pointer))
            return;
        if (!_contentScroll.GetGlobalRect().HasPoint(pointer))
            return;
        _contentScroll._GuiInput(inputEvent);
        GetViewport().SetInputAsHandled();
    }

    public override void _ExitTree()
    {
        _catalogSelectorSession?.Dispose();
        _catalogSelectorSession = null;
    }

    private void BuildStaticUi()
    {
        if (_staticUiBuilt)
            return;
        _staticUiBuilt = true;

        Control? titleMount = GetNodeOrNull<Control>("%TitleMount");
        if (titleMount is not null)
        {
            HBoxContainer titleRow = new()
            {
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                MouseFilter = MouseFilterEnum.Ignore
            };
            titleRow.AddThemeConstantOverride("separation", 22);
            titleRow.SetAnchorsPreset(LayoutPreset.FullRect);
            MegaLabel title = CreateLabel("RULE EDITOR", 42, StsColors.gold, HorizontalAlignment.Left);
            title.CustomMinimumSize = new Vector2(330f, 0f);
            titleRow.AddChild(title);
            _ruleNameLabel = CreateLabel(string.Empty, 31, StsColors.cream, HorizontalAlignment.Left);
            _ruleNameLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            _ruleNameLabel.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            titleRow.AddChild(_ruleNameLabel);
            titleMount.AddChild(titleRow);
        }

        Control? permanentMount = GetNodeOrNull<Control>("%PermanentButtonMount");
        if (permanentMount is not null)
        {
            _permanentButton = new NLoadoutSettingsActionButton
            {
                Name = "PermanentButton",
                CustomMinimumSize = new Vector2(300f, 58f)
            };
            _permanentButton.Init("permanent", "SAVE AS PERMANENT");
            _permanentButton.SetAnchorsPreset(LayoutPreset.CenterRight);
            _permanentButton.OffsetLeft = -300f;
            _permanentButton.OffsetTop = -29f;
            _permanentButton.OffsetRight = 0f;
            _permanentButton.OffsetBottom = 29f;
            _permanentButton.Connect(
                NClickableControl.SignalName.Released,
                Callable.From<NClickableControl>(_ => SaveDraftAsPermanent()));
            permanentMount.AddChild(_permanentButton);
        }

        Control? statusMount = GetNodeOrNull<Control>("%StatusMount");
        if (statusMount is not null)
        {
            _statusLabel = CreateLabel("Ready.", 20, StsColors.cream, HorizontalAlignment.Left);
            _statusLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            _statusLabel.SetAnchorsPreset(LayoutPreset.FullRect);
            statusMount.AddChild(_statusLabel);
        }

        EnsureBackButton();
        EnsureConfirmButton();
    }

    private void EnsureBackButton()
    {
        Control? mount = GetNodeOrNull<Control>("%BackButtonMount");
        if (mount is null || mount.GetNodeOrNull<NBackButton>("BackButton") is not null)
            return;
        NBackButton back = NLoadoutBackButtonFactory.Create();
        back.Name = "BackButton";
        back.Connect(
            NClickableControl.SignalName.Released,
            Callable.From<NClickableControl>(_ => TaskHelper.RunSafely(TryCloseAsync())));
        mount.AddChild(back);
        Callable.From(back.Enable).CallDeferred();
    }

    private void EnsureConfirmButton()
    {
        Control? mount = GetNodeOrNull<Control>("%ConfirmButtonMount");
        if (mount is null || mount.GetNodeOrNull<NConfirmButton>("ConfirmButton") is not null)
            return;
        _confirmButton = NLoadoutConfirmButtonFactory.Create();
        _confirmButton.Name = "ConfirmButton";
        _confirmButton.Connect(
            NClickableControl.SignalName.Released,
            Callable.From<NClickableControl>(_ => SaveAndClose()));
        mount.AddChild(_confirmButton);
        Callable.From(_confirmButton.Enable).CallDeferred();
    }

    private void RefreshScreen()
    {
        if (!_staticUiBuilt || _workingRule is null)
            return;
        if (_ruleNameLabel is not null)
        {
            _ruleNameLabel.Text = _workingRule.Name;
            _ruleNameLabel.TooltipText = _workingRule.Name;
        }
        if (_permanentButton is not null)
        {
            _permanentButton.Visible = !_readOnly && !_editingPermanent;
            if (_permanentButton.Visible)
                _permanentButton.Enable();
        }
        _confirmButton?.Enable();
        RebuildContent();
    }

    private void RebuildContent()
    {
        if (_contentHost is null || _workingRule is null)
            return;
        ClearChildren(_contentHost);
        _loadingFields = true;
        try
        {
            BuildIdentitySection();
            _contentHost.AddChild(CreateSectionDivider());
            BuildTriggerSection();
            _contentHost.AddChild(CreateSectionDivider());
            BuildConditionsSection();
            _contentHost.AddChild(CreateSectionDivider());
            BuildActionsSection();
            _contentHost.AddChild(CreateSectionDivider());
            BuildLimitSection();
        }
        finally
        {
            _loadingFields = false;
        }

        if (_readOnly)
            SetEditableRecursive(_contentHost, editable: false);
        RefreshContentLayoutDeferred();
    }

    private void BuildIdentitySection()
    {
        if (_contentHost is null || _workingRule is null)
            return;
        _contentHost.AddChild(CreateSectionTitle("RULE"));

        _contentHost.AddChild(CreateFieldLabel("Name"));
        LineEdit name = CreateLineEdit(_workingRule.Name);
        name.TextChanged += value =>
        {
            if (_loadingFields || _workingRule is null)
                return;
            _workingRule.Name = value;
            if (_ruleNameLabel is not null)
                _ruleNameLabel.Text = value;
            MarkDirty();
        };
        _contentHost.AddChild(name);
    }

    private void BuildTriggerSection()
    {
        if (_contentHost is null || _workingRule is null)
            return;
        _contentHost.AddChild(CreateSectionTitle("WHEN"));
        _contentHost.AddChild(BuildComponentEditor(
            _workingRule.Trigger,
            RuleComponentKind.Trigger,
            deleteAction: null,
            moveUpAction: null,
            moveDownAction: null));
    }

    private void BuildConditionsSection()
    {
        if (_contentHost is null || _workingRule is null)
            return;
        _contentHost.AddChild(CreateSectionTitle("IF"));
        _contentHost.AddChild(BuildConditionGroup(_workingRule.Conditions, depth: 0, deleteAction: null));
    }

    private Control BuildConditionGroup(
        ConditionGroupDefinition group,
        int depth,
        Action? deleteAction)
    {
        VBoxContainer panel = CreateInsetPanel(depth);

        HBoxContainer header = CreateRow();
        MegaLabel label = CreateLabel(depth == 0 ? "MATCH" : $"GROUP {depth}", 23, StsColors.gold, HorizontalAlignment.Left);
        label.CustomMinimumSize = new Vector2(150f, 52f);
        header.AddChild(label);

        NSelectFilterDropdown operatorDropdown = CreateDropdown(
            Enum.GetValues<ConditionGroupOperator>()
                .Select(value => new LoadoutDropdownOption(value.ToString(), value == ConditionGroupOperator.And ? "ALL (AND)" : "ANY (OR)")),
            group.Operator.ToString(),
            260f);
        operatorDropdown.SelectedItemChanged += value =>
        {
            if (Enum.TryParse(value, out ConditionGroupOperator parsed))
            {
                group.Operator = parsed;
                MarkDirty();
            }
        };
        header.AddChild(operatorDropdown);
        header.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        AddSettingsActionButton(header, "add_condition", "+ CONDITION", 174f, () => AddCondition(group));
        if (depth < MaximumConditionDepth)
            AddSettingsActionButton(header, "add_group", "+ GROUP", 150f, () => AddConditionGroup(group));
        if (deleteAction is not null)
            AddSettingsActionButton(header, "delete_group", "DELETE GROUP", 180f, deleteAction, danger: true);
        panel.AddChild(header);

        for (int index = 0; index < group.Conditions.Count; index++)
        {
            RuleComponentSpec condition = group.Conditions[index];
            int capturedIndex = index;
            panel.AddChild(BuildComponentEditor(
                condition,
                RuleComponentKind.Condition,
                () =>
                {
                    group.Conditions.RemoveAt(capturedIndex);
                    MarkDirty();
                    RebuildContentDeferred();
                },
                capturedIndex > 0 ? () => MoveItem(group.Conditions, capturedIndex, capturedIndex - 1) : null,
                capturedIndex + 1 < group.Conditions.Count ? () => MoveItem(group.Conditions, capturedIndex, capturedIndex + 1) : null));
        }

        for (int index = 0; index < group.Groups.Count; index++)
        {
            ConditionGroupDefinition child = group.Groups[index];
            int capturedIndex = index;
            panel.AddChild(BuildConditionGroup(
                child,
                depth + 1,
                () =>
                {
                    group.Groups.RemoveAt(capturedIndex);
                    MarkDirty();
                    RebuildContentDeferred();
                }));
        }

        if (group.Conditions.Count == 0 && group.Groups.Count == 0)
        {
            MegaLabel empty = CreateHint("No conditions. This group currently passes automatically.");
            empty.CustomMinimumSize = new Vector2(0f, 54f);
            panel.AddChild(empty);
        }
        return panel;
    }

    private void AddCondition(ConditionGroupDefinition group)
    {
        RuleComponentSpec condition = CreateDefaultComponent(RuleComponentKind.Condition, "Loadout2:Always");
        group.Conditions.Add(condition);
        MarkDirty();
        RebuildContentDeferred();
    }

    private void AddConditionGroup(ConditionGroupDefinition group)
    {
        group.Groups.Add(new ConditionGroupDefinition
        {
            Operator = ConditionGroupOperator.And,
            Conditions = [CreateDefaultComponent(RuleComponentKind.Condition, "Loadout2:Always")]
        });
        MarkDirty();
        RebuildContentDeferred();
    }

    private void BuildActionsSection()
    {
        if (_contentHost is null || _workingRule is null)
            return;
        HBoxContainer heading = CreateRow();
        MegaLabel title = CreateSectionTitle("THEN");
        title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        heading.AddChild(title);
        AddSettingsActionButton(heading, "add_action", "+ ACTION", 180f, AddAction);
        _contentHost.AddChild(heading);

        if (_workingRule.Actions.Count == 0)
        {
            MegaLabel empty = CreateHint("No actions. Add at least one action before saving.");
            empty.AddThemeColorOverride("font_color", new Color(1f, 0.58f, 0.46f));
            _contentHost.AddChild(empty);
            return;
        }

        for (int index = 0; index < _workingRule.Actions.Count; index++)
        {
            RuleComponentSpec action = _workingRule.Actions[index];
            int capturedIndex = index;
            _contentHost.AddChild(BuildComponentEditor(
                action,
                RuleComponentKind.Action,
                () =>
                {
                    _workingRule.Actions.RemoveAt(capturedIndex);
                    MarkDirty();
                    RebuildContentDeferred();
                },
                capturedIndex > 0 ? () => MoveItem(_workingRule.Actions, capturedIndex, capturedIndex - 1) : null,
                capturedIndex + 1 < _workingRule.Actions.Count
                    ? () => MoveItem(_workingRule.Actions, capturedIndex, capturedIndex + 1)
                    : null));
        }
    }

    private void AddAction()
    {
        if (_workingRule is null)
            return;
        _workingRule.Actions.Add(CreateDefaultComponent(RuleComponentKind.Action, "Loadout2:GainGold"));
        MarkDirty();
        RebuildContentDeferred();
    }

    private void MoveItem<T>(List<T> items, int from, int to)
    {
        if (from < 0 || from >= items.Count || to < 0 || to >= items.Count || from == to)
            return;
        T item = items[from];
        items.RemoveAt(from);
        items.Insert(to, item);
        MarkDirty();
        RebuildContentDeferred();
    }

    private void BuildLimitSection()
    {
        if (_contentHost is null || _workingRule is null)
            return;
        _contentHost.AddChild(CreateSectionTitle("LIMIT"));
        HBoxContainer row = CreateFieldRow("Frequency");
        NSelectFilterDropdown dropdown = CreateDropdown(
            Enum.GetValues<RuleLimitKind>().Select(value => new LoadoutDropdownOption(value.ToString(), FormatLimit(value))),
            _workingRule.Limit.Kind.ToString(),
            440f);
        dropdown.SelectedItemChanged += value =>
        {
            if (_workingRule is null || !Enum.TryParse(value, out RuleLimitKind parsed))
                return;
            _workingRule.Limit.Kind = parsed;
            MarkDirty();
            RebuildContentDeferred();
        };
        row.AddChild(dropdown);
        _contentHost.AddChild(row);

        if (_workingRule.Limit.Kind is RuleLimitKind.TimesPerTurn or RuleLimitKind.TimesPerCombat or RuleLimitKind.TimesPerRun)
        {
            HBoxContainer countRow = CreateFieldRow("Maximum executions");
            NLoadoutNumberStepper count = new();
            count.Init(_workingRule.Limit.Count, 1, 999);
            count.ValueChanged += value =>
            {
                if (_workingRule is null)
                    return;
                _workingRule.Limit.Count = value;
                MarkDirty();
            };
            countRow.AddChild(count);
            _contentHost.AddChild(countRow);
        }
    }

    private Control BuildComponentEditor(
        RuleComponentSpec component,
        RuleComponentKind kind,
        Action? deleteAction,
        Action? moveUpAction,
        Action? moveDownAction)
    {
        VBoxContainer panel = CreateInsetPanel(0);
        IReadOnlyList<RuleComponentDescriptor> descriptors = CustomRunRegistry.GetDescriptors(kind);
        if (string.IsNullOrWhiteSpace(component.TypeId) && descriptors.Count > 0)
            component.TypeId = descriptors[0].StableId;
        RuleComponentDescriptor? descriptor = descriptors.FirstOrDefault(candidate =>
            string.Equals(candidate.StableId, component.TypeId, StringComparison.Ordinal));
        if (descriptor is not null)
            RuleComponentParameterService.ApplyDefaults(component, descriptor);

        HBoxContainer typeRow = CreateRow();
        MegaLabel typeLabel = CreateLabel(kind.ToString().ToUpperInvariant(), 22, StsColors.gold, HorizontalAlignment.Left);
        typeLabel.CustomMinimumSize = new Vector2(150f, 54f);
        typeRow.AddChild(typeLabel);
        List<LoadoutDropdownOption> options = descriptors
            .Select(candidate => new LoadoutDropdownOption(
                candidate.StableId,
                $"{candidate.Category}  •  {candidate.DisplayName}"))
            .ToList();
        if (descriptor is null && !string.IsNullOrWhiteSpace(component.TypeId))
            options.Insert(0, new LoadoutDropdownOption(component.TypeId, $"Missing: {component.TypeId}"));
        NSelectFilterDropdown typeDropdown = CreateDropdown(options, component.TypeId, 560f);
        typeDropdown.SelectedItemChanged += selected =>
        {
            if (string.Equals(component.TypeId, selected, StringComparison.Ordinal))
                return;
            component.TypeId = selected;
            component.Parameters.Clear();
            RuleComponentDescriptor? selectedDescriptor = descriptors.FirstOrDefault(candidate =>
                string.Equals(candidate.StableId, selected, StringComparison.Ordinal));
            if (selectedDescriptor is not null)
                RuleComponentParameterService.ApplyDefaults(component, selectedDescriptor);
            MarkDirty();
            RebuildContentDeferred();
        };
        typeRow.AddChild(typeDropdown);
        if (kind == RuleComponentKind.Condition)
        {
            NLoadoutToggle notToggle = new()
            {
                Name = "NotToggle",
                CustomMinimumSize = new Vector2(150f, 50f),
                TooltipText = "Invert this condition"
            };
            notToggle.Init("not", "NOT", component.Negated);
            notToggle.Connect(
                NLoadoutToggle.SignalName.Toggled,
                Callable.From<NLoadoutToggle>(toggle =>
                {
                    component.Negated = toggle.IsChecked;
                    MarkDirty();
                }));
            typeRow.AddChild(notToggle);
        }
        typeRow.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        if (moveUpAction is not null)
            AddSettingsActionButton(typeRow, "up", "↑", 62f, moveUpAction);
        if (moveDownAction is not null)
            AddSettingsActionButton(typeRow, "down", "↓", 62f, moveDownAction);
        if (deleteAction is not null)
            AddSettingsActionButton(typeRow, "delete", "DELETE", 118f, deleteAction, danger: true);
        panel.AddChild(typeRow);

        if (descriptor is null)
        {
            panel.AddChild(CreateHint($"Component '{component.TypeId}' is unavailable. Install its defining mod or choose another component."));
            return panel;
        }
        foreach (RuleParameterDescriptor parameter in descriptor.Parameters)
            BuildParameterEditor(panel, component, parameter);
        if (descriptor.Parameters.Count == 0)
            panel.AddChild(CreateHint("This component has no parameters."));
        return panel;
    }

    private void BuildParameterEditor(
        VBoxContainer parent,
        RuleComponentSpec component,
        RuleParameterDescriptor parameter,
        Action? afterChanged = null)
    {
        switch (parameter.Kind)
        {
            case RuleParameterKind.Integer:
                BuildIntegerParameter(parent, component, parameter, afterChanged);
                break;
            case RuleParameterKind.Boolean:
                BuildBooleanParameter(parent, component, parameter, afterChanged);
                break;
            case RuleParameterKind.Enum:
                BuildEnumParameter(parent, component, parameter, afterChanged);
                break;
            case RuleParameterKind.Text:
                BuildTextParameter(parent, component, parameter, afterChanged);
                break;
            case RuleParameterKind.Card:
                BuildModelParameter(parent, component, parameter, SelectionModelKind.Card, afterChanged);
                break;
            case RuleParameterKind.Relic:
                BuildModelParameter(parent, component, parameter, SelectionModelKind.Relic, afterChanged);
                break;
            case RuleParameterKind.Potion:
                BuildModelParameter(parent, component, parameter, SelectionModelKind.Potion, afterChanged);
                break;
            case RuleParameterKind.Power:
                BuildModelParameter(parent, component, parameter, SelectionModelKind.Power, afterChanged);
                break;
            case RuleParameterKind.Role:
                BuildReferenceParameter(parent, component, parameter, isRole: true, afterChanged);
                break;
            case RuleParameterKind.Variable:
                BuildReferenceParameter(parent, component, parameter, isRole: false, afterChanged);
                break;
            case RuleParameterKind.PlayerTarget:
                BuildTargetParameter(parent, component, parameter, afterChanged);
                break;
            case RuleParameterKind.NumericSource:
                BuildNumericSourceParameter(parent, component, parameter, afterChanged);
                break;
            case RuleParameterKind.CardFilter:
                BuildCardFilterParameter(parent, component, parameter, afterChanged);
                break;
            default:
                parent.AddChild(CreateHint($"{parameter.DisplayName}: this parameter editor is not available yet."));
                break;
        }
    }

    private void BuildIntegerParameter(
        VBoxContainer parent,
        RuleComponentSpec component,
        RuleParameterDescriptor parameter,
        Action? afterChanged)
    {
        HBoxContainer row = CreateFieldRow(parameter.DisplayName);
        NLoadoutNumberStepper stepper = new();
        stepper.Init(
            RuleComponentParameterService.GetInt32(component, parameter.Key, Math.Clamp(1, parameter.Minimum, parameter.Maximum)),
            parameter.Minimum,
            parameter.Maximum);
        stepper.ValueChanged += value =>
        {
            RuleComponentParameterService.Set(component, parameter.Key, value);
            afterChanged?.Invoke();
            MarkDirty();
        };
        row.AddChild(stepper);
        parent.AddChild(row);
    }

    private void BuildBooleanParameter(
        VBoxContainer parent,
        RuleComponentSpec component,
        RuleParameterDescriptor parameter,
        Action? afterChanged)
    {
        HBoxContainer row = CreateFieldRow(parameter.DisplayName);
        NLoadoutToggle toggle = new() { CustomMinimumSize = new Vector2(260f, 50f) };
        toggle.Init(parameter.Key, string.Empty, RuleComponentParameterService.GetBoolean(component, parameter.Key));
        toggle.Connect(
            NLoadoutToggle.SignalName.Toggled,
            Callable.From<NLoadoutToggle>(changed =>
            {
                RuleComponentParameterService.Set(component, parameter.Key, changed.IsChecked);
                afterChanged?.Invoke();
                MarkDirty();
            }));
        row.AddChild(toggle);
        parent.AddChild(row);
    }

    private void BuildEnumParameter(
        VBoxContainer parent,
        RuleComponentSpec component,
        RuleParameterDescriptor parameter,
        Action? afterChanged)
    {
        HBoxContainer row = CreateFieldRow(parameter.DisplayName);
        string selected = RuleComponentParameterService.GetString(component, parameter.Key);
        NSelectFilterDropdown dropdown = CreateDropdown(
            parameter.Options.Select(option => new LoadoutDropdownOption(option.Id, option.DisplayName)),
            selected,
            420f);
        dropdown.SelectedItemChanged += value =>
        {
            RuleComponentParameterService.Set(component, parameter.Key, value);
            afterChanged?.Invoke();
            MarkDirty();
        };
        row.AddChild(dropdown);
        parent.AddChild(row);
    }

    private void BuildTextParameter(
        VBoxContainer parent,
        RuleComponentSpec component,
        RuleParameterDescriptor parameter,
        Action? afterChanged)
    {
        HBoxContainer row = CreateFieldRow(parameter.DisplayName);
        LineEdit edit = CreateLineEdit(RuleComponentParameterService.GetString(component, parameter.Key));
        edit.TextChanged += value =>
        {
            RuleComponentParameterService.Set(component, parameter.Key, value);
            afterChanged?.Invoke();
            MarkDirty();
        };
        row.AddChild(edit);
        parent.AddChild(row);
    }

    private void BuildModelParameter(
        VBoxContainer parent,
        RuleComponentSpec component,
        RuleParameterDescriptor parameter,
        SelectionModelKind kind,
        Action? afterChanged)
    {
        HBoxContainer row = CreateFieldRow(parameter.DisplayName);
        string id = RuleComponentParameterService.GetString(component, parameter.Key);
        MegaLabel selected = CreateLabel(
            string.IsNullOrWhiteSpace(id) ? "Not selected" : GetModelDisplayName(kind, id),
            20,
            string.IsNullOrWhiteSpace(id) ? new Color(1f, 0.58f, 0.46f) : StsColors.cream,
            HorizontalAlignment.Left);
        selected.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        selected.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        selected.TooltipText = id;
        row.AddChild(selected);
        AddSettingsActionButton(
            row,
            $"select_{parameter.Key}",
            "SELECT",
            132f,
            () => OpenModelSelector(kind, component, parameter.Key, afterChanged));
        if (!string.IsNullOrWhiteSpace(id))
        {
            AddSettingsActionButton(
                row,
                $"clear_{parameter.Key}",
                "CLEAR",
                116f,
                () =>
                {
                    RuleComponentParameterService.Set(component, parameter.Key, string.Empty);
                    afterChanged?.Invoke();
                    MarkDirty();
                    RebuildContentDeferred();
                });
        }
        parent.AddChild(row);
    }

    private void BuildCardFilterParameter(
        VBoxContainer parent,
        RuleComponentSpec component,
        RuleParameterDescriptor parameter,
        Action? afterChanged)
    {
        if (!RuleComponentParameterService.TryGet(component, parameter.Key, out CardMatchSpec filter))
            filter = new CardMatchSpec();
        filter.CardIds ??= [];
        filter.Value ??= string.Empty;
        CardMatchSpec captured = filter;

        HBoxContainer modeRow = CreateFieldRow("Match cards by");
        NSelectFilterDropdown mode = CreateDropdown(
            Enum.GetValues<CardMatchKind>()
                .Select(kind => new LoadoutDropdownOption(kind.ToString(), FormatCardMatchKind(kind))),
            captured.Kind.ToString(),
            420f);
        mode.SelectedItemChanged += value =>
        {
            if (!Enum.TryParse(value, out CardMatchKind kind) || captured.Kind == kind)
                return;
            captured.Kind = kind;
            captured.Value = string.Empty;
            RuleComponentParameterService.Set(component, parameter.Key, captured);
            afterChanged?.Invoke();
            MarkDirty();
            RebuildContentDeferred();
        };
        modeRow.AddChild(mode);
        parent.AddChild(modeRow);

        if (captured.Kind == CardMatchKind.SpecificCards)
        {
            BuildSpecificCardsFilter(parent, component, parameter, captured, afterChanged);
            return;
        }

        if (captured.Kind == CardMatchKind.TextContains)
        {
            HBoxContainer textRow = CreateFieldRow("Title or text contains");
            LineEdit text = CreateLineEdit(captured.Value);
            text.PlaceholderText = "Text to find";
            text.TextChanged += value =>
            {
                captured.Value = value;
                RuleComponentParameterService.Set(component, parameter.Key, captured);
                afterChanged?.Invoke();
                MarkDirty();
            };
            textRow.AddChild(text);
            parent.AddChild(textRow);
            return;
        }

        List<LoadoutDropdownOption> options = GetCardFilterOptions(captured.Kind);
        if (options.Count == 0)
        {
            parent.AddChild(CreateHint($"No {FormatCardMatchKind(captured.Kind).ToLowerInvariant()} values are available."));
            return;
        }
        if (options.All(option => !string.Equals(option.Id, captured.Value, StringComparison.Ordinal)))
        {
            captured.Value = options[0].Id;
            RuleComponentParameterService.Set(component, parameter.Key, captured);
        }

        HBoxContainer valueRow = CreateFieldRow(GetCardMatchValueLabel(captured.Kind));
        NSelectFilterDropdown valueDropdown = CreateDropdown(options, captured.Value, 420f);
        valueDropdown.SelectedItemChanged += value =>
        {
            captured.Value = value;
            RuleComponentParameterService.Set(component, parameter.Key, captured);
            afterChanged?.Invoke();
            MarkDirty();
        };
        valueRow.AddChild(valueDropdown);
        parent.AddChild(valueRow);
    }

    private void BuildSpecificCardsFilter(
        VBoxContainer parent,
        RuleComponentSpec component,
        RuleParameterDescriptor parameter,
        CardMatchSpec filter,
        Action? afterChanged)
    {
        HBoxContainer row = CreateFieldRow("Specific cards");
        MegaLabel selected = CreateLabel(
            FormatSpecificCards(filter.CardIds),
            20,
            filter.CardIds.Count == 0 ? new Color(1f, 0.58f, 0.46f) : StsColors.cream,
            HorizontalAlignment.Left);
        selected.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        selected.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        selected.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        row.AddChild(selected);
        AddSettingsActionButton(
            row,
            $"select_{parameter.Key}",
            filter.CardIds.Count == 0 ? "SELECT" : "EDIT",
            132f,
            () => OpenCardMatchSelector(component, parameter.Key, filter, afterChanged));
        if (filter.CardIds.Count > 0)
        {
            AddSettingsActionButton(
                row,
                $"clear_{parameter.Key}",
                "CLEAR",
                116f,
                () =>
                {
                    filter.CardIds.Clear();
                    RuleComponentParameterService.Set(component, parameter.Key, filter);
                    afterChanged?.Invoke();
                    MarkDirty();
                    RebuildContentDeferred();
                });
        }
        parent.AddChild(row);
    }

    private void OpenCardMatchSelector(
        RuleComponentSpec component,
        string key,
        CardMatchSpec filter,
        Action? afterChanged)
    {
        _catalogSelectorSession?.Dispose();
        if (!CustomRunCatalogSelector.TryOpenCatalogSelection(
                SelectionModelKind.Card,
                filter.CardIds,
                ids =>
                {
                    filter.CardIds = ids.ToList();
                    RuleComponentParameterService.Set(component, key, filter);
                    afterChanged?.Invoke();
                    MarkDirty();
                },
                out IDisposable? session,
                out string error))
        {
            SetStatus(error, success: false);
            return;
        }
        _catalogSelectorSession = session;
    }

    private static string FormatSpecificCards(IReadOnlyList<string> cardIds)
    {
        if (cardIds.Count == 0)
            return "No cards selected";
        string[] names = cardIds
            .Take(3)
            .Select(id => GetModelDisplayName(SelectionModelKind.Card, id))
            .ToArray();
        string summary = string.Join(", ", names);
        return cardIds.Count > names.Length ? $"{summary}  +{cardIds.Count - names.Length} more" : summary;
    }

    private static List<LoadoutDropdownOption> GetCardFilterOptions(CardMatchKind kind)
    {
        IReadOnlyList<CardModel> cards = ModelDb.AllCards.ToList();
        return kind switch
        {
            CardMatchKind.Pool => CardPrinter.BuildOrderedCardPools()
                .Select(pool => new LoadoutDropdownOption(pool.Id.ToString(), CommonHelpers.GetPoolLabel(pool)))
                .ToList(),
            CardMatchKind.Type => cards
                .Select(card => card.Type)
                .Distinct()
                .OrderBy(value => Convert.ToInt32(value))
                .Select(value => new LoadoutDropdownOption(value.ToString(), CardPrinter.GetCardTypeLabel(value)))
                .ToList(),
            CardMatchKind.Rarity => cards
                .Select(card => card.Rarity)
                .Where(value => value != CardRarity.None)
                .Distinct()
                .OrderBy(value => Convert.ToInt32(value))
                .Select(value => new LoadoutDropdownOption(value.ToString(), CardPrinter.GetCardRarityLabel(value)))
                .ToList(),
            CardMatchKind.Keyword => cards
                .SelectMany(card => card.GetKeywordsWithSources(KeywordSources.Local))
                .Where(value => value != CardKeyword.None)
                .Distinct()
                .OrderBy(value => Convert.ToInt32(value))
                .Select(value => new LoadoutDropdownOption(value.ToString(), CardPrinter.GetCardKeywordLabel(value)))
                .ToList(),
            CardMatchKind.Tag => cards
                .SelectMany(card => card.Tags)
                .Where(value => value != CardTag.None)
                .Distinct()
                .OrderBy(value => Convert.ToInt32(value))
                .Select(value => new LoadoutDropdownOption(value.ToString(), CardPrinter.GetCardTagLabel(value)))
                .ToList(),
            CardMatchKind.EnergyCost =>
            [
                new LoadoutDropdownOption("0", "0"),
                new LoadoutDropdownOption("1", "1"),
                new LoadoutDropdownOption("2", "2"),
                new LoadoutDropdownOption("3+", "3+"),
                new LoadoutDropdownOption("X", "X"),
                new LoadoutDropdownOption("unplayable", "Unplayable")
            ],
            CardMatchKind.Mod => CustomRunCatalogService.GetCatalog(SelectionModelKind.Card)
                .Select(entry => entry.ModId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(CommonHelpers.GetModName, StringComparer.OrdinalIgnoreCase)
                .Select(id => new LoadoutDropdownOption(id, CommonHelpers.GetModName(id)))
                .ToList(),
            _ => []
        };
    }

    private static string FormatCardMatchKind(CardMatchKind kind)
    {
        return kind switch
        {
            CardMatchKind.SpecificCards => "Specific cards",
            CardMatchKind.Pool => "Card pool",
            CardMatchKind.Type => "Card type",
            CardMatchKind.Rarity => "Card rarity",
            CardMatchKind.Keyword => "Keyword",
            CardMatchKind.Tag => "Tag",
            CardMatchKind.EnergyCost => "Energy cost",
            CardMatchKind.TextContains => "Text contains",
            CardMatchKind.Mod => "Mod",
            _ => kind.ToString()
        };
    }

    private static string GetCardMatchValueLabel(CardMatchKind kind)
    {
        return kind switch
        {
            CardMatchKind.Pool => "Pool",
            CardMatchKind.Type => "Type",
            CardMatchKind.Rarity => "Rarity",
            CardMatchKind.Keyword => "Keyword",
            CardMatchKind.Tag => "Tag",
            CardMatchKind.EnergyCost => "Cost",
            CardMatchKind.Mod => "Mod",
            _ => "Value"
        };
    }

    private void BuildReferenceParameter(
        VBoxContainer parent,
        RuleComponentSpec component,
        RuleParameterDescriptor parameter,
        bool isRole,
        Action? afterChanged)
    {
        HBoxContainer row = CreateFieldRow(parameter.DisplayName);
        string selected = RuleComponentParameterService.GetString(component, parameter.Key);
        List<LoadoutDropdownOption> options = isRole
            ? (_definitionContext?.Roles ?? []).Select(role => new LoadoutDropdownOption(role.Id, role.Name)).ToList()
            : (_definitionContext?.Variables ?? []).Select(variable => new LoadoutDropdownOption(variable.Id, variable.Name)).ToList();
        if (!string.IsNullOrWhiteSpace(selected) && options.All(option => option.Id != selected))
            options.Insert(0, new LoadoutDropdownOption(selected, $"Missing: {selected}"));
        if (options.Count == 0)
            options.Add(new LoadoutDropdownOption(string.Empty, isRole ? "No roles defined" : "No variables defined"));
        NSelectFilterDropdown dropdown = CreateDropdown(options, selected, 420f);
        dropdown.SelectedItemChanged += value =>
        {
            RuleComponentParameterService.Set(component, parameter.Key, value);
            afterChanged?.Invoke();
            MarkDirty();
        };
        row.AddChild(dropdown);
        parent.AddChild(row);
    }

    private void BuildTargetParameter(
        VBoxContainer parent,
        RuleComponentSpec component,
        RuleParameterDescriptor parameter,
        Action? afterChanged)
    {
        if (!RuleComponentParameterService.TryGet(component, parameter.Key, out RuleTargetSpec target))
            target = new RuleTargetSpec();
        RuleTargetSpec capturedTarget = target;
        capturedTarget.Parameters ??= new SortedDictionary<string, System.Text.Json.JsonElement>(StringComparer.Ordinal);
        IReadOnlyList<RuleComponentDescriptor> targets = CustomRunRegistry.GetDescriptors(RuleComponentKind.Target);
        RuleComponentDescriptor? targetDescriptor = targets.FirstOrDefault(descriptor =>
            string.Equals(descriptor.StableId, capturedTarget.TypeId, StringComparison.Ordinal));

        HBoxContainer row = CreateFieldRow(parameter.DisplayName);
        List<LoadoutDropdownOption> options = targets
            .Select(descriptor => new LoadoutDropdownOption(descriptor.StableId, descriptor.DisplayName))
            .ToList();
        if (targetDescriptor is null && !string.IsNullOrWhiteSpace(capturedTarget.TypeId))
            options.Insert(0, new LoadoutDropdownOption(capturedTarget.TypeId, $"Missing: {capturedTarget.TypeId}"));
        NSelectFilterDropdown dropdown = CreateDropdown(options, capturedTarget.TypeId, 420f);
        dropdown.SelectedItemChanged += value =>
        {
            capturedTarget.TypeId = value;
            capturedTarget.Parameters.Clear();
            RuleComponentDescriptor? nextDescriptor = targets.FirstOrDefault(candidate =>
                string.Equals(candidate.StableId, value, StringComparison.Ordinal));
            if (nextDescriptor is not null)
            {
                RuleComponentSpec targetComponent = new()
                {
                    TypeId = value,
                    Parameters = capturedTarget.Parameters
                };
                RuleComponentParameterService.ApplyDefaults(targetComponent, nextDescriptor);
                capturedTarget.Parameters = targetComponent.Parameters;
            }
            RuleComponentParameterService.Set(component, parameter.Key, capturedTarget);
            afterChanged?.Invoke();
            MarkDirty();
            RebuildContentDeferred();
        };
        row.AddChild(dropdown);
        parent.AddChild(row);

        if (targetDescriptor is null)
            return;
        RuleComponentSpec proxy = new()
        {
            TypeId = capturedTarget.TypeId,
            Parameters = capturedTarget.Parameters
        };
        RuleComponentParameterService.ApplyDefaults(proxy, targetDescriptor);
        capturedTarget.Parameters = proxy.Parameters;
        RuleComponentParameterService.Set(component, parameter.Key, capturedTarget);
        foreach (RuleParameterDescriptor targetParameter in targetDescriptor.Parameters)
        {
            BuildParameterEditor(parent, proxy, targetParameter, () =>
            {
                capturedTarget.Parameters = proxy.Parameters;
                RuleComponentParameterService.Set(component, parameter.Key, capturedTarget);
                afterChanged?.Invoke();
            });
        }
    }

    private void BuildNumericSourceParameter(
        VBoxContainer parent,
        RuleComponentSpec component,
        RuleParameterDescriptor parameter,
        Action? afterChanged)
    {
        if (!RuleComponentParameterService.TryGet(component, parameter.Key, out NumericValueSpec value))
        {
            value = new NumericValueSpec
            {
                Source = NumericValueSourceKind.Constant,
                Constant = 1m
            };
        }
        NumericValueSpec captured = value;

        HBoxContainer sourceRow = CreateFieldRow(parameter.DisplayName);
        NSelectFilterDropdown source = CreateDropdown(
            new[]
            {
                new LoadoutDropdownOption(NumericValueSourceKind.Constant.ToString(), "Constant"),
                new LoadoutDropdownOption(NumericValueSourceKind.Variable.ToString(), "Variable"),
                new LoadoutDropdownOption(NumericValueSourceKind.EventContext.ToString(), "Event Value")
            },
            captured.Source.ToString(),
            310f);
        source.SelectedItemChanged += selected =>
        {
            if (!Enum.TryParse(selected, out NumericValueSourceKind parsed))
                return;
            captured.Source = parsed;
            captured.ReferenceId = parsed switch
            {
                NumericValueSourceKind.Variable => _definitionContext?.Variables.FirstOrDefault()?.Id,
                NumericValueSourceKind.EventContext => "TurnNumber",
                _ => null
            };
            RuleComponentParameterService.Set(component, parameter.Key, captured);
            afterChanged?.Invoke();
            MarkDirty();
            RebuildContentDeferred();
        };
        sourceRow.AddChild(source);

        switch (captured.Source)
        {
            case NumericValueSourceKind.Constant:
            {
                LineEdit constant = CreateLineEdit(captured.Constant.ToString(CultureInfo.InvariantCulture));
                constant.CustomMinimumSize = new Vector2(230f, 46f);
                constant.TextChanged += text =>
                {
                    if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal parsed))
                        return;
                    captured.Constant = parsed;
                    RuleComponentParameterService.Set(component, parameter.Key, captured);
                    afterChanged?.Invoke();
                    MarkDirty();
                };
                sourceRow.AddChild(constant);
                break;
            }
            case NumericValueSourceKind.Variable:
            {
                List<LoadoutDropdownOption> variables = (_definitionContext?.Variables ?? [])
                    .Where(variable => variable.ValueType == VariableValueType.Number)
                    .Select(variable => new LoadoutDropdownOption(variable.Id, variable.Name))
                    .ToList();
                if (variables.Count == 0)
                    variables.Add(new LoadoutDropdownOption(string.Empty, "No number variables"));
                NSelectFilterDropdown variable = CreateDropdown(variables, captured.ReferenceId ?? string.Empty, 350f);
                variable.SelectedItemChanged += selected =>
                {
                    captured.ReferenceId = selected;
                    RuleComponentParameterService.Set(component, parameter.Key, captured);
                    afterChanged?.Invoke();
                    MarkDirty();
                };
                sourceRow.AddChild(variable);
                break;
            }
            case NumericValueSourceKind.EventContext:
            {
                NSelectFilterDropdown eventValue = CreateDropdown(
                    new[]
                    {
                        new LoadoutDropdownOption("CurrentHp", "Current HP"),
                        new LoadoutDropdownOption("MaxHp", "Max HP"),
                        new LoadoutDropdownOption("Gold", "Gold"),
                        new LoadoutDropdownOption("Energy", "Energy"),
                        new LoadoutDropdownOption("TurnNumber", "Turn Number"),
                        new LoadoutDropdownOption("PlayerCount", "Player Count")
                    },
                    captured.ReferenceId ?? "TurnNumber",
                    350f);
                eventValue.SelectedItemChanged += selected =>
                {
                    captured.ReferenceId = selected;
                    RuleComponentParameterService.Set(component, parameter.Key, captured);
                    afterChanged?.Invoke();
                    MarkDirty();
                };
                sourceRow.AddChild(eventValue);
                break;
            }
        }
        parent.AddChild(sourceRow);
    }

    private RuleComponentSpec CreateDefaultComponent(RuleComponentKind kind, string preferredId)
    {
        IReadOnlyList<RuleComponentDescriptor> descriptors = CustomRunRegistry.GetDescriptors(kind);
        RuleComponentDescriptor? descriptor = descriptors.FirstOrDefault(candidate => candidate.StableId == preferredId)
                                             ?? descriptors.FirstOrDefault();
        RuleComponentSpec component = new() { TypeId = descriptor?.StableId ?? string.Empty };
        if (descriptor is not null)
            RuleComponentParameterService.ApplyDefaults(component, descriptor);
        return component;
    }

    private void OpenModelSelector(
        SelectionModelKind kind,
        RuleComponentSpec component,
        string key,
        Action? afterChanged)
    {
        _catalogSelectorSession?.Dispose();
        if (!CustomRunCatalogSelector.TryOpenCatalogAction(
                kind,
                (_, item, _) =>
                {
                    if (item.UntypedModel is not AbstractModel model)
                        return;
                    RuleComponentParameterService.Set(component, key, model.Id.ToString());
                    afterChanged?.Invoke();
                    MarkDirty();
                    CloseCatalogSelector();
                    RebuildContentDeferred();
                },
                out IDisposable? session,
                out string error))
        {
            SetStatus(error, success: false);
            return;
        }
        _catalogSelectorSession = session;
    }

    private void CloseCatalogSelector()
    {
        _catalogSelectorSession?.Dispose();
        _catalogSelectorSession = null;
        NLoadoutPanelRoot.Instance?.CloseTopScreen();
    }

    private void SaveDraftAsPermanent()
    {
        if (_workingRule is null || _readOnly)
            return;
        if (!TryCreateValidatedRule(out RuleDefinition rule))
            return;
        PermanentRuleStorageService.Upsert(rule);
        SetStatus($"Saved '{rule.Name}' to Permanent Rules.", success: true);
    }

    private void SaveAndClose()
    {
        if (_workingRule is null)
            return;
        if (_readOnly)
        {
            CloseWithoutSaving();
            return;
        }
        if (!TryCreateValidatedRule(out RuleDefinition saved))
            return;
        _saveAction?.Invoke(saved);
        _dirty = false;
        CloseWithoutSaving();
    }

    private bool TryCreateValidatedRule(out RuleDefinition saved)
    {
        saved = new RuleDefinition();
        if (_workingRule is null || _definitionContext is null)
            return false;
        RuleDefinition candidate = CustomRunNormalizationService.NormalizeRule(
            CustomRunNormalizationService.CloneRule(_workingRule));
        CustomRunDefinition validationDefinition = CustomRunNormalizationService.Clone(_definitionContext);
        int index = validationDefinition.Rules.FindIndex(rule => string.Equals(rule.Id, candidate.Id, StringComparison.Ordinal));
        if (index >= 0)
            validationDefinition.Rules[index] = candidate;
        else
            validationDefinition.Rules.Add(candidate);
        IReadOnlyList<CustomRunValidationIssue> issues = CustomRunValidator.Validate(validationDefinition).Issues
            .Where(issue => issue.Section == "Rules" && string.Equals(issue.ObjectId, candidate.Id, StringComparison.Ordinal))
            .ToList();
        if (issues.Count > 0)
        {
            SetStatus(issues[0].Message, success: false);
            return false;
        }
        saved = candidate;
        return true;
    }

    private async Task TryCloseAsync()
    {
        if (_discardPromptOpen)
            return;
        if (_readOnly || !_dirty)
        {
            CloseWithoutSaving();
            return;
        }

        _discardPromptOpen = true;
        try
        {
            LocString body = new("settings_ui", "LOADOUT-CUSTOM_RUN_UNSAVED_BODY.title");
            body.Add("Name", _workingRule?.Name ?? string.Empty);
            bool discard = await WaitForDiscardConfirmation(
                body,
                new LocString("settings_ui", "LOADOUT-CUSTOM_RUN_UNSAVED_TITLE.title"),
                new LocString("settings_ui", "LOADOUT-CUSTOM_RUN_UNSAVED_CANCEL.title"),
                new LocString("settings_ui", "LOADOUT-CUSTOM_RUN_UNSAVED_DISCARD.title"));
            if (discard)
                CloseWithoutSaving();
        }
        finally
        {
            _discardPromptOpen = false;
        }
    }

    private async Task<bool> WaitForDiscardConfirmation(
        LocString body,
        LocString title,
        LocString cancelText,
        LocString discardText)
    {
        NModalContainer? modalContainer = NModalContainer.Instance;
        if (modalContainer is null || !GodotObject.IsInstanceValid(modalContainer))
        {
            SetStatus("Could not open the unsaved-changes warning.", success: false);
            return false;
        }
        if (modalContainer.OpenModal is GodotObject openModal && !GodotObject.IsInstanceValid(openModal))
            modalContainer.Clear();
        if (modalContainer.OpenModal is not null)
            return false;
        NGenericPopup? popup = NGenericPopup.Create();
        if (popup is null)
            return false;
        IDisposable? lease = NLoadoutPanelRoot.Instance?.HostNativeModal(modalContainer);
        try
        {
            modalContainer.Add(popup);
            return await popup.WaitForConfirmation(body, title, cancelText, discardText);
        }
        finally
        {
            if (GodotObject.IsInstanceValid(modalContainer))
                modalContainer.Clear();
            lease?.Dispose();
        }
    }

    private void CloseWithoutSaving()
    {
        _catalogSelectorSession?.Dispose();
        _catalogSelectorSession = null;
        NLoadoutPanelRoot.Instance?.CloseTopScreen();
    }

    private void MarkDirty()
    {
        if (_loadingFields || _readOnly)
            return;
        _dirty = true;
        SetStatus("Unsaved rule changes.", success: true);
    }

    private void SetStatus(string text, bool success)
    {
        if (_statusLabel is null)
            return;
        _statusLabel.Text = text;
        _statusLabel.AddThemeColorOverride(
            "font_color",
            success ? new Color(0.68f, 1f, 0.55f) : new Color(1f, 0.55f, 0.45f));
    }

    private void RebuildContentDeferred()
    {
        Callable.From(() =>
        {
            if (GodotObject.IsInstanceValid(this) && Visible)
                RebuildContent();
        }).CallDeferred();
    }

    private void RefreshContentLayoutDeferred()
    {
        Callable.From(() =>
        {
            if (!GodotObject.IsInstanceValid(_contentScroll) || !GodotObject.IsInstanceValid(_contentHost))
                return;
            _contentScroll.SetContent(_contentHost);
            ResizeContentToChildren();
        }).CallDeferred();
    }

    private void EnsureNativeContentScroll()
    {
        Control? mount = GetNodeOrNull<Control>("%ContentMount");
        if (mount is null)
            return;
        _contentScroll = mount.GetNodeOrNull<NScrollableContainer>("ContentScroll");
        if (_contentScroll is not null)
        {
            _contentHost = _contentScroll.GetNodeOrNull<VBoxContainer>("Mask/Content");
            return;
        }

        NScrollableContainer scroll = new()
        {
            Name = "ContentScroll",
            MouseFilter = MouseFilterEnum.Stop
        };
        scroll.SetAnchorsPreset(LayoutPreset.FullRect);
        Control mask = new()
        {
            Name = "Mask",
            ClipContents = true,
            MouseFilter = MouseFilterEnum.Ignore
        };
        mask.SetAnchorsPreset(LayoutPreset.FullRect);
        mask.OffsetRight = -NLoadoutNativeScrollbar.Width;
        scroll.AddChild(mask);
        VBoxContainer content = new()
        {
            Name = "Content",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0f, 1f),
            MouseFilter = MouseFilterEnum.Pass
        };
        content.AddThemeConstantOverride("separation", 12);
        content.SetAnchorsPreset(LayoutPreset.TopWide);
        mask.AddChild(content);
        NScrollbar scrollbar = NLoadoutNativeScrollbar.Create();
        scrollbar.Name = "Scrollbar";
        scrollbar.CustomMinimumSize = new Vector2(NLoadoutNativeScrollbar.Width, 0f);
        scrollbar.SetAnchorsPreset(LayoutPreset.RightWide);
        scrollbar.OffsetLeft = -NLoadoutNativeScrollbar.Width;
        scrollbar.OffsetTop = NLoadoutNativeScrollbar.EndCapSize;
        scrollbar.OffsetBottom = -NLoadoutNativeScrollbar.EndCapSize;
        scroll.AddChild(scrollbar);
        mount.AddChild(scroll);
        scroll.DisableScrollingIfContentFits();
        _contentScroll = scroll;
        _contentHost = content;
        Callable.From(() => scroll.SetContent(content)).CallDeferred();
    }

    private void ResizeContentToChildren()
    {
        if (_contentScroll is null || _contentHost is null)
            return;
        Vector2 minimum = _contentHost.GetCombinedMinimumSize();
        _contentHost.Size = new Vector2(_contentHost.Size.X, Mathf.Max(1f, minimum.Y));
        _contentScroll.SetContent(_contentHost);
    }

    private void EnsureFallbackScene()
    {
        if (GetNodeOrNull<Control>("%ContentMount") is not null)
            return;
        ColorRect backdrop = new() { Color = new Color(0f, 0f, 0f, 0.92f) };
        backdrop.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(backdrop);
        Control mount = new()
        {
            Name = "ContentMount",
            UniqueNameInOwner = true,
            Position = new Vector2(180f, 120f),
            Size = new Vector2(1560f, 820f)
        };
        AddChild(mount);
    }

    private static VBoxContainer CreateInsetPanel(int depth)
    {
        VBoxContainer panel = new()
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            MouseFilter = MouseFilterEnum.Pass
        };
        panel.AddThemeConstantOverride("separation", 10);
        panel.AddThemeConstantOverride("margin_left", depth * 24);
        ColorRect accent = new()
        {
            CustomMinimumSize = new Vector2(0f, 2f),
            Color = new Color(StsColors.gold, depth == 0 ? 0.24f : 0.14f),
            MouseFilter = MouseFilterEnum.Ignore
        };
        panel.AddChild(accent);
        return panel;
    }

    private static HBoxContainer CreateRow()
    {
        HBoxContainer row = new()
        {
            CustomMinimumSize = new Vector2(0f, 56f),
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        row.AddThemeConstantOverride("separation", 14);
        return row;
    }

    private static HBoxContainer CreateFieldRow(string label)
    {
        HBoxContainer row = CreateRow();
        MegaLabel field = CreateLabel(label, 22, StsColors.gold, HorizontalAlignment.Left);
        field.CustomMinimumSize = new Vector2(260f, 52f);
        row.AddChild(field);
        return row;
    }

    private static NSelectFilterDropdown CreateDropdown(
        IEnumerable<LoadoutDropdownOption> options,
        string selected,
        float width)
    {
        NSelectFilterDropdown dropdown = new()
        {
            CustomMinimumSize = new Vector2(width, 52f),
            DropdownWidth = width,
            ButtonHeight = 52f,
            MaxVisibleItems = 9,
            ExpandToAvailableWidth = false
        };
        dropdown.SetItems(string.Empty, options, selected);
        return dropdown;
    }

    private static NLoadoutSettingsActionButton AddSettingsActionButton(
        Control parent,
        string id,
        string label,
        float width,
        Action action,
        bool danger = false)
    {
        NLoadoutSettingsActionButton button = new()
        {
            CustomMinimumSize = new Vector2(width, 54f),
            UseDangerColor = danger
        };
        button.Init(id, label);
        button.Connect(
            NClickableControl.SignalName.Released,
            Callable.From<NClickableControl>(_ => action()));
        parent.AddChild(button);
        return button;
    }

    private static ColorRect CreateSectionDivider()
    {
        return new ColorRect
        {
            CustomMinimumSize = new Vector2(0f, 2f),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            Color = new Color(StsColors.gold, 0.42f),
            MouseFilter = MouseFilterEnum.Ignore
        };
    }

    private static MegaLabel CreateSectionTitle(string text)
    {
        MegaLabel title = CreateLabel(text, 32, StsColors.gold, HorizontalAlignment.Left);
        title.CustomMinimumSize = new Vector2(0f, 52f);
        return title;
    }

    private static MegaLabel CreateFieldLabel(string text)
    {
        MegaLabel label = CreateLabel(text, 22, StsColors.gold, HorizontalAlignment.Left);
        label.CustomMinimumSize = new Vector2(0f, 34f);
        return label;
    }

    private static MegaLabel CreateHint(string text)
    {
        MegaLabel label = CreateLabel(text, 19, new Color(0.76f, 0.79f, 0.86f), HorizontalAlignment.Left);
        label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        label.CustomMinimumSize = new Vector2(0f, 62f);
        return label;
    }

    private static MegaLabel CreateLabel(
        string text,
        int fontSize,
        Color color,
        HorizontalAlignment alignment)
    {
        MegaLabel label = new()
        {
            Text = text,
            AutoSizeEnabled = false,
            MinFontSize = Math.Max(12, fontSize - 6),
            MaxFontSize = fontSize,
            HorizontalAlignment = alignment,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore
        };
        const string fontPath = "res://themes/kreon_bold_glyph_space_one.tres";
        if (ResourceLoader.Exists(fontPath))
            label.AddThemeFontOverride("font", GD.Load<Font>(fontPath));
        label.AddThemeFontSizeOverride("font_size", fontSize);
        label.AddThemeColorOverride("font_color", color);
        label.AddThemeColorOverride("font_shadow_color", new Color(0f, 0f, 0f, 0.55f));
        label.AddThemeConstantOverride("shadow_offset_x", 3);
        label.AddThemeConstantOverride("shadow_offset_y", 2);
        return label;
    }

    private static LineEdit CreateLineEdit(string text)
    {
        LineEdit edit = new()
        {
            Text = text,
            CustomMinimumSize = new Vector2(0f, 46f),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            MouseFilter = MouseFilterEnum.Stop
        };
        ApplyInputFont(edit, 22);
        edit.AddThemeColorOverride("font_color", StsColors.cream);
        edit.AddThemeColorOverride("font_focus_color", StsColors.gold);
        return edit;
    }

    private static void ApplyInputFont(Control control, int size)
    {
        const string fontPath = "res://themes/kreon_bold_glyph_space_one.tres";
        if (ResourceLoader.Exists(fontPath))
            control.AddThemeFontOverride("font", GD.Load<Font>(fontPath));
        control.AddThemeFontSizeOverride("font_size", size);
    }

    private static string GetModelDisplayName(SelectionModelKind kind, string id)
    {
        if (!CustomRunCatalogService.TryResolve(kind, id, out CustomRunCatalogEntry entry))
            return $"Missing: {id}";
        return entry.Model switch
        {
            CardModel card => CardPrinter.FormatCardTitle(card),
            RelicModel relic => CommonHelpers.FormatRelicTitle(relic),
            PotionModel potion => CommonHelpers.FormatPotionTitle(potion),
            PowerModel power => CommonHelpers.FormatPowerTitle(power),
            _ => entry.Model.Id.Entry
        };
    }

    private static string FormatLimit(RuleLimitKind kind)
    {
        return kind switch
        {
            RuleLimitKind.Unlimited => "Unlimited",
            RuleLimitKind.OncePerEventChain => "Once per event chain",
            RuleLimitKind.OncePerTurn => "Once per turn",
            RuleLimitKind.TimesPerTurn => "N times per turn",
            RuleLimitKind.OncePerCombat => "Once per combat",
            RuleLimitKind.TimesPerCombat => "N times per combat",
            RuleLimitKind.OncePerRun => "Once per run",
            RuleLimitKind.TimesPerRun => "N times per run",
            _ => kind.ToString()
        };
    }

    private static void SetEditableRecursive(Node node, bool editable)
    {
        switch (node)
        {
            case NLoadoutDropdown dropdown:
                dropdown.SetEnabled(editable);
                dropdown.FocusMode = editable ? FocusModeEnum.All : FocusModeEnum.None;
                break;
            case NButton nativeButton:
                nativeButton.SetEnabled(editable);
                nativeButton.FocusMode = editable ? FocusModeEnum.All : FocusModeEnum.None;
                break;
            case BaseButton button:
                button.Disabled = !editable;
                button.FocusMode = editable ? FocusModeEnum.All : FocusModeEnum.None;
                break;
            case LineEdit lineEdit:
                lineEdit.Editable = editable;
                lineEdit.FocusMode = editable ? FocusModeEnum.All : FocusModeEnum.None;
                break;
            case TextEdit textEdit:
                textEdit.Editable = editable;
                textEdit.FocusMode = editable ? FocusModeEnum.All : FocusModeEnum.None;
                break;
            case NLoadoutToggle toggle:
                toggle.MouseFilter = editable ? MouseFilterEnum.Stop : MouseFilterEnum.Ignore;
                toggle.FocusMode = editable ? FocusModeEnum.All : FocusModeEnum.None;
                break;
        }
        foreach (Node child in node.GetChildren())
            SetEditableRecursive(child, editable);
    }

    private static void ClearChildren(Node node)
    {
        foreach (Node child in node.GetChildren())
        {
            node.RemoveChild(child);
            child.QueueFree();
        }
    }
}
