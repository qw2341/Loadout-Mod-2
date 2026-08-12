#nullable enable

namespace Loadout.UI.CustomRuns;

using System;
using System.Collections.Generic;
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
using Loadout.Services.Loadouts;
using Loadout.Services.Targets;
using Loadout.UI.Screens;
using Loadout.UI.Screens.Controls;
using Loadout.UI.Managers;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.UI;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.Potions;
using MegaCrit.Sts2.Core.Nodes.Relics;
using MegaCrit.Sts2.Core.Nodes.Screens.RunHistoryScreen;

public partial class NCustomRunRuleEditorScreen : Control
{
    private const string ScenePath = "res://UI/CustomRuns/CustomRunRuleEditorScreen.tscn";
    private const int MaximumConditionDepth = 5;
    private const double SpecificCardHoverDelaySeconds = 0.2d;

    private CustomRunDefinition? _definitionContext;
    private RuleDefinition? _workingRule;
    private Action<RuleDefinition>? _saveAction;
    private NScrollableContainer? _contentScroll;
    private VBoxContainer? _contentHost;
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
        PermanentRuleBundle? bundle = PermanentRuleStorageService.GetBundles().FirstOrDefault(candidate =>
            string.Equals(candidate.Rule.Id, rule.Id, StringComparison.Ordinal));
        CustomRunDefinition context = new()
        {
            Name = "Permanent Rules",
            Rules = [CustomRunNormalizationService.CloneRule(rule)],
            Variables = bundle?.Variables ?? []
        };
        Open(
            source,
            context,
            rule,
            readOnly: false,
            editingPermanent: true,
            updated =>
            {
                RuleDefinition stored = PermanentRuleStorageService.Upsert(updated, context.Variables);
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
            MegaLabel title = CreateLabel(LocMan.Loc("CUSTOM_RUN_RULE_EDITOR", "Rule Editor").ToUpperInvariant(), 42, StsColors.gold, HorizontalAlignment.Left);
            title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            titleRow.AddChild(title);
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
            _permanentButton.Init("permanent", LocMan.Loc("CUSTOM_RUN_SAVE_AS_PERMANENT", "Save as Permanent").ToUpperInvariant());
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
            _statusLabel = CreateLabel(LocMan.Loc("CUSTOM_RUN_READY", "Ready."), 20, StsColors.cream, HorizontalAlignment.Left);
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
        HBoxContainer row = CreateRow();
        MegaLabel title = CreateSectionTitle(LocMan.Loc("CUSTOM_RUN_RULE", "Rule").ToUpperInvariant());
        title.CustomMinimumSize = new Vector2(150f, 52f);
        row.AddChild(title);
        LineEdit name = CreateLineEdit(_workingRule.Name);
        name.TextChanged += value =>
        {
            if (_loadingFields || _workingRule is null)
                return;
            _workingRule.Name = value;
            MarkDirty();
        };
        row.AddChild(name);
        _contentHost.AddChild(row);
    }

    private void BuildTriggerSection()
    {
        if (_contentHost is null || _workingRule is null)
            return;
        _contentHost.AddChild(CreateSectionTitle(LocMan.Loc("CUSTOM_RUN_WHEN", "When").ToUpperInvariant()));
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
        _contentHost.AddChild(CreateSectionTitle(LocMan.Loc("CUSTOM_RUN_IF", "If").ToUpperInvariant()));
        _contentHost.AddChild(BuildConditionGroup(_workingRule.Conditions, depth: 0, deleteAction: null));
    }

    private Control BuildConditionGroup(
        ConditionGroupDefinition group,
        int depth,
        Action? deleteAction)
    {
        VBoxContainer panel = CreateInsetPanel(depth);

        HBoxContainer header = CreateRow();
        MegaLabel label = CreateLabel(depth == 0
                ? LocMan.Loc("CUSTOM_RUN_MATCH", "Match").ToUpperInvariant()
                : LocMan.Loc("CUSTOM_RUN_GROUP_NUMBER", "Group {0}", depth).ToUpperInvariant(),
            23, StsColors.gold, HorizontalAlignment.Left);
        label.CustomMinimumSize = new Vector2(150f, 52f);
        header.AddChild(label);

        NSelectFilterDropdown operatorDropdown = CreateDropdown(
            Enum.GetValues<ConditionGroupOperator>()
                .Select(value => new LoadoutDropdownOption(value.ToString(), value == ConditionGroupOperator.And
                    ? LocMan.Loc("CUSTOM_RUN_ALL_AND", "All (AND)").ToUpperInvariant()
                    : LocMan.Loc("CUSTOM_RUN_ANY_OR", "Any (OR)").ToUpperInvariant())),
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
        AddSettingsActionButton(header, "add_condition", "+ " + LocMan.Loc("CUSTOM_RUN_CONDITION", "Condition").ToUpperInvariant(), 174f, () => AddCondition(group));
        if (depth < MaximumConditionDepth)
            AddSettingsActionButton(header, "add_group", "+ " + LocMan.Loc("CUSTOM_RUN_GROUP", "Group").ToUpperInvariant(), 150f, () => AddConditionGroup(group));
        if (deleteAction is not null)
            AddSettingsActionButton(header, "delete_group", LocMan.Loc("CUSTOM_RUN_DELETE_GROUP", "Delete Group").ToUpperInvariant(), 180f, deleteAction, danger: true);
        panel.AddChild(header);

        MarginContainer bodyIndent = new() { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        bodyIndent.AddThemeConstantOverride("margin_left", 36);
        VBoxContainer body = new() { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", 12);
        bodyIndent.AddChild(body);
        panel.AddChild(bodyIndent);

        for (int index = 0; index < group.Conditions.Count; index++)
        {
            RuleComponentSpec condition = group.Conditions[index];
            int capturedIndex = index;
            body.AddChild(BuildComponentEditor(
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
            body.AddChild(BuildConditionGroup(
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
            MegaLabel empty = CreateHint(LocMan.Loc("CUSTOM_RUN_NO_CONDITIONS", "No conditions. This group currently passes automatically."));
            empty.CustomMinimumSize = new Vector2(0f, 54f);
            body.AddChild(empty);
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
        MegaLabel title = CreateSectionTitle(LocMan.Loc("CUSTOM_RUN_THEN", "Then").ToUpperInvariant());
        title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        heading.AddChild(title);
        AddSettingsActionButton(heading, "add_action", "+ " + LocMan.Loc("CUSTOM_RUN_ACTION", "Action").ToUpperInvariant(), 180f, AddAction);
        _contentHost.AddChild(heading);

        if (_workingRule.Actions.Count == 0)
        {
            MegaLabel empty = CreateHint(LocMan.Loc("CUSTOM_RUN_NO_ACTIONS", "No actions. Add at least one action before saving."));
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
        _contentHost.AddChild(CreateSectionTitle(LocMan.Loc("CUSTOM_RUN_LIMIT", "Limit").ToUpperInvariant()));
        HBoxContainer row = CreateFieldRow(LocMan.Loc("CUSTOM_RUN_FREQUENCY", "Frequency"));
        RuleLimitKind[] authoringKinds =
        [
            RuleLimitKind.Unlimited,
            RuleLimitKind.OncePerEventChain,
            RuleLimitKind.TimesPerTurn,
            RuleLimitKind.TimesPerCombat,
            RuleLimitKind.TimesPerRun,
            RuleLimitKind.UntilCondition
        ];
        NSelectFilterDropdown dropdown = CreateDropdown(
            authoringKinds.Select(value => new LoadoutDropdownOption(value.ToString(), FormatLimit(value))),
            _workingRule.Limit.Kind.ToString(),
            440f);
        dropdown.SelectedItemChanged += value =>
        {
            if (_workingRule is null || !Enum.TryParse(value, out RuleLimitKind parsed))
                return;
            _workingRule.Limit.Kind = parsed;
            if (parsed == RuleLimitKind.UntilCondition
                && _workingRule.Limit.UntilConditions.Conditions.Count == 0
                && _workingRule.Limit.UntilConditions.Groups.Count == 0)
            {
                _workingRule.Limit.UntilConditions.Conditions.Add(
                    CreateDefaultComponent(RuleComponentKind.Condition, "Loadout2:Always"));
            }
            MarkDirty();
            RebuildContentDeferred();
        };
        row.AddChild(dropdown);
        _contentHost.AddChild(row);

        if (_workingRule.Limit.Kind is RuleLimitKind.TimesPerTurn or RuleLimitKind.TimesPerCombat or RuleLimitKind.TimesPerRun)
        {
            HBoxContainer countRow = CreateFieldRow(LocMan.Loc("CUSTOM_RUN_MAXIMUM_EXECUTIONS", "Maximum executions"));
            NLoadoutNumberStepper count = new();
            count.Init(_workingRule.Limit.Count);
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
        if (_workingRule.Limit.Kind == RuleLimitKind.UntilCondition)
        {
            _contentHost.AddChild(CreateSectionTitle(LocMan.Loc("CUSTOM_RUN_UNTIL_IF", "Until If").ToUpperInvariant()));
            _contentHost.AddChild(BuildConditionGroup(
                _workingRule.Limit.UntilConditions,
                depth: 0,
                deleteAction: null));
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
        IReadOnlyList<RuleComponentDescriptor> allDescriptors = CustomRunRegistry.GetDescriptors(kind);
        IReadOnlyList<RuleComponentDescriptor> descriptors = kind == RuleComponentKind.Condition && _workingRule is not null
            ? CustomRunRegistry.GetDescriptors(kind, _workingRule.Trigger.TypeId)
            : allDescriptors;
        RuleComponentDescriptor? currentDescriptor = allDescriptors.FirstOrDefault(candidate =>
            string.Equals(candidate.StableId, component.TypeId, StringComparison.Ordinal));
        if (currentDescriptor is not null && descriptors.All(candidate => candidate.StableId != currentDescriptor.StableId))
            descriptors = [currentDescriptor, .. descriptors];
        if (string.IsNullOrWhiteSpace(component.TypeId) && descriptors.Count > 0)
            component.TypeId = descriptors[0].StableId;
        RuleComponentDescriptor? descriptor = allDescriptors.FirstOrDefault(candidate =>
            string.Equals(candidate.StableId, component.TypeId, StringComparison.Ordinal));
        if (descriptor is not null)
            RuleComponentParameterService.ApplyDefaults(component, descriptor);

        HBoxContainer typeRow = CreateRow();
        MegaLabel typeLabel = CreateLabel(FormatComponentKind(kind).ToUpperInvariant(), 22, StsColors.gold, HorizontalAlignment.Left);
        typeLabel.CustomMinimumSize = new Vector2(150f, 54f);
        typeRow.AddChild(typeLabel);
        List<LoadoutDropdownOption> options = descriptors
            .Select(candidate => new LoadoutDropdownOption(
                candidate.StableId,
                $"{FormatComponentCategory(candidate.Category)}  •  {FormatComponentName(candidate)}{(CustomRunRegistry.IsCompatibleWithTrigger(candidate, _workingRule?.Trigger.TypeId ?? string.Empty) ? string.Empty : LocMan.Loc("CUSTOM_RUN_NOT_AVAILABLE_FOR_TRIGGER", "  (not available for this trigger)"))}"))
            .ToList();
        if (descriptor is null && !string.IsNullOrWhiteSpace(component.TypeId))
            options.Insert(0, new LoadoutDropdownOption(component.TypeId, LocMan.Loc("CUSTOM_RUN_MISSING_VALUE", "Missing: {0}", component.TypeId)));
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
                TooltipText = LocMan.Loc("CUSTOM_RUN_INVERT_CONDITION", "Invert this condition")
            };
            notToggle.Init("not", LocMan.Loc("CUSTOM_RUN_NOT", "Not").ToUpperInvariant(), component.Negated);
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
            AddSettingsActionButton(typeRow, "delete", LocMan.Loc("DELETE_LOADOUT", "Delete").ToUpperInvariant(), 118f, deleteAction, danger: true);
        panel.AddChild(typeRow);

        if (descriptor is null)
        {
            panel.AddChild(CreateHint(LocMan.Loc("CUSTOM_RUN_COMPONENT_UNAVAILABLE", "Component '{0}' is unavailable. Install its defining mod or choose another component.", component.TypeId)));
            return panel;
        }
        foreach (RuleParameterDescriptor parameter in descriptor.Parameters)
        {
            if (parameter.VisibleWhenParameterKey is not null
                && !string.Equals(
                    RuleComponentParameterService.GetString(component, parameter.VisibleWhenParameterKey),
                    parameter.VisibleWhenParameterValue,
                    StringComparison.Ordinal))
            {
                continue;
            }
            bool controlsVisibility = descriptor.Parameters.Any(candidate =>
                string.Equals(candidate.VisibleWhenParameterKey, parameter.Key, StringComparison.Ordinal));
            BuildParameterEditor(panel, component, parameter, controlsVisibility ? RebuildContentDeferred : null);
        }
        if (descriptor.Parameters.Count == 0)
            panel.AddChild(CreateHint(LocMan.Loc("CUSTOM_RUN_NO_PARAMETERS", "This component has no parameters.")));
        return panel;
    }

    private void BuildParameterEditor(
        VBoxContainer parent,
        RuleComponentSpec component,
        RuleParameterDescriptor parameter,
        Action? afterChanged = null)
    {
        if (IsBooleanVariableValueParameter(component, parameter))
        {
            BuildBooleanParameter(parent, component, parameter, afterChanged);
            return;
        }
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
            case RuleParameterKind.Monster:
                BuildModelParameter(parent, component, parameter, SelectionModelKind.Monster, afterChanged);
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
            case RuleParameterKind.ModelFilter:
                BuildModelFilterParameter(parent, component, parameter, afterChanged);
                break;
            default:
                parent.AddChild(CreateHint(LocMan.Loc("CUSTOM_RUN_PARAMETER_EDITOR_UNAVAILABLE", "{0}: this parameter editor is not available yet.", FormatParameterName(parameter))));
                break;
        }
    }

    private void BuildIntegerParameter(
        VBoxContainer parent,
        RuleComponentSpec component,
        RuleParameterDescriptor parameter,
        Action? afterChanged)
    {
        HBoxContainer row = CreateFieldRow(FormatParameterName(parameter));
        NLoadoutNumberStepper stepper = new();
        stepper.Init(
            RuleComponentParameterService.GetInt32(component, parameter.Key, parameter.DefaultInteger),
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
        HBoxContainer row = CreateFieldRow(FormatParameterName(parameter));
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
        HBoxContainer row = CreateFieldRow(FormatParameterName(parameter));
        string selected = RuleComponentParameterService.GetString(component, parameter.Key);
        IEnumerable<RuleParameterOption> options = parameter.Options;
        if (component.TypeId == "Loadout2:VariableComparison"
            && parameter.Key == "operator"
            && GetSelectedVariable(component)?.ValueType == VariableValueType.Boolean)
        {
            options = options.Where(option => option.Id is "Equal" or "NotEqual");
        }
        NSelectFilterDropdown dropdown = CreateDropdown(
            options.Select(option => new LoadoutDropdownOption(option.Id, FormatParameterOption(option))),
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
        HBoxContainer row = CreateFieldRow(FormatParameterName(parameter));
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
        HBoxContainer row = CreateFieldRow(FormatParameterName(parameter));
        string id = RuleComponentParameterService.GetString(component, parameter.Key);
        MegaLabel selected = CreateLabel(
            string.IsNullOrWhiteSpace(id) ? LocMan.Loc("CUSTOM_RUN_NOT_SELECTED", "Not selected") : GetModelDisplayName(kind, id),
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
            LocMan.Loc("TITLE_SELECT", "Select").ToUpperInvariant(),
            132f,
            () => OpenModelSelector(kind, component, parameter.Key, afterChanged));
        if (!string.IsNullOrWhiteSpace(id))
        {
            AddSettingsActionButton(
                row,
                $"clear_{parameter.Key}",
                LocMan.Loc("CUSTOM_RUN_CLEAR", "Clear").ToUpperInvariant(),
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
        if (!string.IsNullOrWhiteSpace(id)
            && CustomRunCatalogService.TryResolve(kind, id, out CustomRunCatalogEntry selectedEntry))
        {
            MarginContainer previewIndent = new() { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            previewIndent.AddThemeConstantOverride("margin_left", 274);
            Control preview = CreateSpecificModelPreview(selectedEntry.Model, kind);
            preview.TooltipText = LocMan.Loc("CUSTOM_RUN_CLICK_CLEAR_SELECTION", "Click to clear this selection.");
            BindSpecificModelRemoval(preview, () =>
            {
                RuleComponentParameterService.Set(component, parameter.Key, string.Empty);
                afterChanged?.Invoke();
                MarkDirty();
                RebuildContentDeferred();
            });
            previewIndent.AddChild(preview);
            parent.AddChild(previewIndent);
        }
    }

    private void BuildSpecificModelsPreview(
        VBoxContainer parent,
        RuleComponentSpec component,
        string key,
        ModelMatchSpec filter,
        SelectionModelKind kind,
        Action? afterChanged)
    {
        MarginContainer indent = new() { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        indent.AddThemeConstantOverride("margin_left", 274);
        HFlowContainer preview = new()
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            MouseFilter = MouseFilterEnum.Pass
        };
        preview.AddThemeConstantOverride("h_separation", 18);
        preview.AddThemeConstantOverride("v_separation", 14);
        indent.AddChild(preview);
        parent.AddChild(indent);

        if (kind != SelectionModelKind.Card)
        {
            foreach (string modelId in filter.ModelIds.ToList())
            {
                if (!CustomRunCatalogService.TryResolve(kind, modelId, out CustomRunCatalogEntry entry))
                    continue;
                Control view = CreateSpecificModelPreview(entry.Model, kind);
                view.TooltipText = LocMan.Loc("CUSTOM_RUN_CLICK_REMOVE_SELECTION", "Click to remove this selection.");
                BindSpecificModelRemoval(view, () =>
                {
                    filter.ModelIds.RemoveAll(id => ModelIdsMatch(kind, id, entry.ModelId));
                    RuleComponentParameterService.Set(component, key, filter);
                    afterChanged?.Invoke();
                    MarkDirty();
                    RebuildContentDeferred();
                });
                preview.AddChild(view);
            }
            return;
        }

        IReadOnlyList<LoadoutOwnedItem<CardModel>> cards =
            CustomRunEditorPreviewService.CreateOwnedCards(filter.ModelIds
                .Select(id => new SavedCardLoadoutEntry { ModelId = id })
                .ToList());
        foreach (LoadoutOwnedItem<CardModel> item in cards)
        {
            NDeckHistoryEntry? view = NDeckHistoryEntry.Create(item.Model, 1);
            if (view is null)
                continue;
            string modelId = item.Model.Id.ToString();
            view.TooltipText = LocMan.Loc("CUSTOM_RUN_CLICK_REMOVE_CARD", "Click to remove this card.");
            AttachSpecificCardHover(view, item.Model);
            view.Connect(
                NDeckHistoryEntry.SignalName.Clicked,
                Callable.From<NDeckHistoryEntry>(_ =>
                {
                    int removed = filter.ModelIds.RemoveAll(id => ModelIdsMatch(kind, id, modelId));
                    if (removed == 0)
                        return;
                    RuleComponentParameterService.Set(component, key, filter);
                    afterChanged?.Invoke();
                    MarkDirty();
                    RebuildContentDeferred();
                }));
            preview.AddChild(view);
        }
    }

    private Control CreateSpecificModelPreview(AbstractModel model, SelectionModelKind kind)
    {
        switch (kind)
        {
            case SelectionModelKind.Card when model is CardModel card:
            {
                NDeckHistoryEntry? view = NDeckHistoryEntry.Create(card, 1);
                if (view is null)
                    return CommonHelpers.CreateModelButton(new Vector2(238f, 58f));
                AttachSpecificCardHover(view, card);
                return view;
            }
            case SelectionModelKind.Relic when model is RelicModel relic:
                return (Control?)NRelicBasicHolder.Create(relic)
                       ?? CommonHelpers.CreateModelButton(new Vector2(72f, 72f));
            case SelectionModelKind.Potion when model is PotionModel potion:
            {
                NPotionHolder holder = NPotionHolder.Create(isUsable: false);
                if (NPotion.Create(potion.ToMutable()) is { } potionNode)
                    holder.AddPotion(potionNode);
                return holder;
            }
            case SelectionModelKind.Monster when model is MonsterModel monster:
                return BottledMonster.CreateMonsterGridItem(monster);
            default:
            {
                Button button = CommonHelpers.CreateModelButton(new Vector2(238f, 58f));
                if (model is PowerModel power)
                {
                    TextureRect icon = new()
                    {
                        Texture = power.Icon,
                        Position = new Vector2(8f, 7f),
                        Size = new Vector2(44f, 44f),
                        ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                        StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                        MouseFilter = MouseFilterEnum.Ignore
                    };
                    button.AddChild(icon);
                }
                button.AddChild(CommonHelpers.CreateButtonLabel(
                    "ModelTitle",
                    GetModelDisplayName(kind, model.Id.ToString()),
                    new Vector2(58f, 0f),
                    new Vector2(172f, 58f),
                    19,
                    HorizontalAlignment.Left,
                    StsColors.cream));
                return button;
            }
        }
    }

    private static void BindSpecificModelRemoval(Control view, Action remove)
    {
        if (view is NDeckHistoryEntry card)
        {
            card.Connect(
                NDeckHistoryEntry.SignalName.Clicked,
                Callable.From<NDeckHistoryEntry>(_ => remove()));
            return;
        }
        if (view is NClickableControl clickable)
        {
            clickable.Connect(
                NClickableControl.SignalName.Released,
                Callable.From<NClickableControl>(_ => remove()));
            return;
        }
        if (view is BaseButton button)
            button.Pressed += remove;
    }

    private void AttachSpecificCardHover(Control view, CardModel card)
    {
        bool hovered = false;
        int generation = 0;
        view.MouseEntered += () =>
        {
            hovered = true;
            int requestedGeneration = ++generation;
            TaskHelper.RunSafely(ShowSpecificCardHoverAfterDelay(
                view,
                card,
                () => hovered && generation == requestedGeneration));
        };
        view.MouseExited += () =>
        {
            hovered = false;
            generation++;
            NHoverTipSet.Remove(view);
        };
        view.TreeExiting += () => NHoverTipSet.Remove(view);
    }

    private async Task ShowSpecificCardHoverAfterDelay(
        Control view,
        CardModel card,
        Func<bool> shouldShow)
    {
        SceneTreeTimer timer = GetTree().CreateTimer(SpecificCardHoverDelaySeconds);
        await ToSignal(timer, SceneTreeTimer.SignalName.Timeout);
        if (!shouldShow() || !GodotObject.IsInstanceValid(view) || !view.IsVisibleInTree())
            return;

        List<IHoverTip> tips = [HoverTipFactory.FromCard(card)];
        tips.AddRange(card.HoverTips);
        CommonHelpers.ShowHoverTips(view, tips);
    }

    private static bool ModelIdsMatch(SelectionModelKind kind, string candidate, string canonicalId)
    {
        if (string.Equals(candidate, canonicalId, StringComparison.OrdinalIgnoreCase))
            return true;
        return CustomRunCatalogService.TryResolve(kind, candidate, out CustomRunCatalogEntry entry)
               && string.Equals(entry.ModelId, canonicalId, StringComparison.OrdinalIgnoreCase);
    }

    private void BuildModelFilterParameter(
        VBoxContainer parent,
        RuleComponentSpec component,
        RuleParameterDescriptor parameter,
        Action? afterChanged)
    {
        if (!RuleComponentParameterService.TryGet(component, parameter.Key, out ModelMatchSpec filter))
            filter = new ModelMatchSpec { ModelKind = parameter.ModelKind };
        filter.ModelKind = parameter.ModelKind;
        filter.ModelIds ??= [];
        filter.Value ??= string.Empty;
        ModelMatchSpec captured = filter;

        HBoxContainer modeRow = CreateFieldRow(LocMan.Loc("CUSTOM_RUN_MATCH_KIND_BY", "Match {0} by", FormatModelKind(parameter.ModelKind).ToLowerInvariant()));
        NSelectFilterDropdown mode = CreateDropdown(
            GetMatchKinds(parameter.ModelKind)
                .Select(kind => new LoadoutDropdownOption(kind.ToString(), FormatModelMatchKind(kind, parameter.ModelKind))),
            captured.Kind.ToString(),
            420f);
        mode.SelectedItemChanged += value =>
        {
            if (!Enum.TryParse(value, out ModelMatchKind kind) || captured.Kind == kind)
                return;
            captured.Kind = kind;
            captured.Value = string.Empty;
            captured.ModelIds.Clear();
            RuleComponentParameterService.Set(component, parameter.Key, captured);
            afterChanged?.Invoke();
            MarkDirty();
            RebuildContentDeferred();
        };
        modeRow.AddChild(mode);
        parent.AddChild(modeRow);

        if (captured.Kind == ModelMatchKind.SpecificModels)
        {
            BuildSpecificModelsFilter(parent, component, parameter, captured, afterChanged);
            return;
        }

        if (captured.Kind == ModelMatchKind.TextContains)
        {
            HBoxContainer textRow = CreateFieldRow(LocMan.Loc("CUSTOM_RUN_TITLE_OR_TEXT_CONTAINS", "Title or text contains"));
            LineEdit text = CreateLineEdit(captured.Value);
            text.PlaceholderText = LocMan.Loc("CUSTOM_RUN_TEXT_TO_FIND", "Text to find");
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

        List<LoadoutDropdownOption> options = GetModelFilterOptions(parameter.ModelKind, captured.Kind);
        if (options.Count == 0)
        {
            parent.AddChild(CreateHint(LocMan.Loc("CUSTOM_RUN_NO_MATCH_VALUES", "No {0} values are available.", FormatModelMatchKind(captured.Kind, parameter.ModelKind).ToLowerInvariant())));
            return;
        }
        if (options.All(option => !string.Equals(option.Id, captured.Value, StringComparison.Ordinal)))
        {
            captured.Value = options[0].Id;
            RuleComponentParameterService.Set(component, parameter.Key, captured);
        }

        HBoxContainer valueRow = CreateFieldRow(GetModelMatchValueLabel(captured.Kind));
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

    private void BuildSpecificModelsFilter(
        VBoxContainer parent,
        RuleComponentSpec component,
        RuleParameterDescriptor parameter,
        ModelMatchSpec filter,
        Action? afterChanged)
    {
        HBoxContainer row = CreateFieldRow(LocMan.Loc("CUSTOM_RUN_SPECIFIC_KIND", "Specific {0}", FormatModelKind(parameter.ModelKind).ToLowerInvariant()));
        MegaLabel selected = CreateLabel(
            FormatSpecificModels(parameter.ModelKind, filter.ModelIds),
            20,
            filter.ModelIds.Count == 0 ? new Color(1f, 0.58f, 0.46f) : StsColors.cream,
            HorizontalAlignment.Left);
        selected.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        selected.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        selected.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        row.AddChild(selected);
        AddSettingsActionButton(
            row,
            $"select_{parameter.Key}",
            filter.ModelIds.Count == 0
                ? LocMan.Loc("TITLE_SELECT", "Select").ToUpperInvariant()
                : LocMan.Loc("CUSTOM_RUN_EDIT", "Edit").ToUpperInvariant(),
            132f,
            () => OpenCardMatchSelector(component, parameter.Key, filter, afterChanged));
        if (filter.ModelIds.Count > 0)
        {
            AddSettingsActionButton(
                row,
                $"clear_{parameter.Key}",
                LocMan.Loc("CUSTOM_RUN_CLEAR", "Clear").ToUpperInvariant(),
                116f,
                () =>
                {
                    filter.ModelIds.Clear();
                    RuleComponentParameterService.Set(component, parameter.Key, filter);
                    afterChanged?.Invoke();
                    MarkDirty();
                    RebuildContentDeferred();
                });
        }
        parent.AddChild(row);
        if (filter.ModelIds.Count > 0)
            BuildSpecificModelsPreview(parent, component, parameter.Key, filter, parameter.ModelKind, afterChanged);
    }

    private void OpenCardMatchSelector(
        RuleComponentSpec component,
        string key,
        ModelMatchSpec filter,
        Action? afterChanged)
    {
        _catalogSelectorSession?.Dispose();
        if (!CustomRunCatalogSelector.TryOpenCatalogSelection(
                filter.ModelKind,
                filter.ModelIds,
                ids =>
                {
                    filter.ModelIds = ids.ToList();
                    RuleComponentParameterService.Set(component, key, filter);
                    afterChanged?.Invoke();
                    MarkDirty();
                },
                (screen, item, added) =>
                {
                    if (!added || filter.ModelKind != SelectionModelKind.Card || item.UntypedModel is not CardModel card)
                        return;
                    CustomRunEditorPreviewService.PreviewCardAdd(
                        card,
                        upgradeLevel: 0,
                        amount: 1,
                        screen.GetNodeOrNull<Control>(screen.CancelButtonPath));
                },
                out IDisposable? session,
                out string error))
        {
            SetStatus(error, success: false);
            return;
        }
        _catalogSelectorSession = session;
    }

    private static string FormatSpecificModels(SelectionModelKind kind, IReadOnlyList<string> modelIds)
    {
        if (modelIds.Count == 0)
            return LocMan.Loc("CUSTOM_RUN_NO_KIND_SELECTED", "No {0} selected", FormatModelKind(kind).ToLowerInvariant());
        string[] names = modelIds
            .Take(3)
            .Select(id => GetModelDisplayName(kind, id))
            .ToArray();
        string summary = string.Join(", ", names);
        return modelIds.Count > names.Length
            ? LocMan.Loc("CUSTOM_RUN_MORE_SUMMARY", "{0}  +{1} more", summary, modelIds.Count - names.Length)
            : summary;
    }

    private static IReadOnlyList<ModelMatchKind> GetMatchKinds(SelectionModelKind kind)
    {
        return kind switch
        {
            SelectionModelKind.Card =>
            [
                ModelMatchKind.SpecificModels, ModelMatchKind.Pool, ModelMatchKind.Type,
                ModelMatchKind.Rarity, ModelMatchKind.Keyword, ModelMatchKind.Tag,
                ModelMatchKind.EnergyCost, ModelMatchKind.TextContains, ModelMatchKind.Mod
            ],
            SelectionModelKind.Relic or SelectionModelKind.Potion =>
            [
                ModelMatchKind.SpecificModels, ModelMatchKind.Pool, ModelMatchKind.Rarity,
                ModelMatchKind.TextContains, ModelMatchKind.Mod
            ],
            SelectionModelKind.Monster =>
            [
                ModelMatchKind.SpecificModels, ModelMatchKind.Act, ModelMatchKind.MonsterCategory,
                ModelMatchKind.TextContains, ModelMatchKind.Mod
            ],
            _ => [ModelMatchKind.SpecificModels, ModelMatchKind.TextContains, ModelMatchKind.Mod]
        };
    }

    private static List<LoadoutDropdownOption> GetModelFilterOptions(
        SelectionModelKind modelKind,
        ModelMatchKind matchKind)
    {
        IReadOnlyList<CardModel> cards = ModelDb.AllCards.ToList();
        if (matchKind == ModelMatchKind.Mod)
        {
            return CustomRunCatalogService.GetCatalog(modelKind)
                .Select(entry => entry.ModId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(CommonHelpers.GetModName, StringComparer.OrdinalIgnoreCase)
                .Select(id => new LoadoutDropdownOption(id, CommonHelpers.GetModName(id)))
                .ToList();
        }
        if (matchKind == ModelMatchKind.Act)
        {
            return ModelDb.Acts
                .Where(act => act.Index >= 0)
                .OrderBy(act => act.Index)
                .ThenBy(act => act.Id.ToString(), StringComparer.Ordinal)
                .Select(act => new LoadoutDropdownOption(act.Id.ToString(),
                    LocMan.Loc("CUSTOM_RUN_ACT_OPTION", "{0}: {1}",
                        LocMan.Loc("ACT_NUMBER", "Act {0}", act.Index + 1),
                        FormatActTitle(act))))
                .ToList();
        }
        if (matchKind == ModelMatchKind.MonsterCategory)
        {
            return CustomRunCatalogService.GetCatalog(SelectionModelKind.Monster)
                .SelectMany(entry => entry.Types)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .Select(value => new LoadoutDropdownOption(value,
                    LocMan.Loc($"MONSTER_CATEGORY_{value.ToUpperInvariant()}", value)))
                .ToList();
        }

        return (modelKind, matchKind) switch
        {
            (SelectionModelKind.Card, ModelMatchKind.Pool) => CardPrinter.BuildOrderedCardPools()
                .Select(pool => new LoadoutDropdownOption(pool.Id.ToString(), CommonHelpers.GetPoolLabel(pool)))
                .ToList(),
            (SelectionModelKind.Potion, ModelMatchKind.Pool) => ModelDb.AllPotions
                .Select(potion => potion.Pool)
                .DistinctBy(pool => pool.Id.ToString())
                .Select(pool => new LoadoutDropdownOption(pool.Id.ToString(), CommonHelpers.GetPoolLabel(pool)))
                .ToList(),
            (SelectionModelKind.Relic, ModelMatchKind.Pool) => ModelDb.AllRelics
                .Select(relic => LoadoutBag.TryGetRelicPool(relic, out var pool) ? pool : null)
                .Where(pool => pool is not null)
                .DistinctBy(pool => pool!.Id.ToString())
                .Select(pool => new LoadoutDropdownOption(pool!.Id.ToString(), CommonHelpers.GetPoolLabel(pool)))
                .ToList(),
            (SelectionModelKind.Card, ModelMatchKind.Type) => cards
                .Select(card => card.Type)
                .Distinct()
                .OrderBy(value => Convert.ToInt32(value))
                .Select(value => new LoadoutDropdownOption(value.ToString(), CardPrinter.GetCardTypeLabel(value)))
                .ToList(),
            (SelectionModelKind.Card, ModelMatchKind.Rarity) => cards
                .Select(card => card.Rarity)
                .Where(value => value != CardRarity.None)
                .Distinct()
                .OrderBy(value => Convert.ToInt32(value))
                .Select(value => new LoadoutDropdownOption(value.ToString(), CardPrinter.GetCardRarityLabel(value)))
                .ToList(),
            (SelectionModelKind.Relic, ModelMatchKind.Rarity) => ModelDb.AllRelics
                .Select(relic => relic.Rarity.ToString())
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .Select(value => new LoadoutDropdownOption(value,
                    LocMan.Loc($"ENUM_RELICRARITY_{value.ToUpperInvariant()}", value)))
                .ToList(),
            (SelectionModelKind.Potion, ModelMatchKind.Rarity) => ModelDb.AllPotions
                .Select(potion => potion.Rarity.ToString())
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .Select(value => new LoadoutDropdownOption(value,
                    LocMan.Loc($"ENUM_POTIONRARITY_{value.ToUpperInvariant()}", value)))
                .ToList(),
            (SelectionModelKind.Card, ModelMatchKind.Keyword) => cards
                .SelectMany(card => card.GetKeywordsWithSources(KeywordSources.Local))
                .Where(value => value != CardKeyword.None)
                .Distinct()
                .OrderBy(value => Convert.ToInt32(value))
                .Select(value => new LoadoutDropdownOption(value.ToString(), CardPrinter.GetCardKeywordLabel(value)))
                .ToList(),
            (SelectionModelKind.Card, ModelMatchKind.Tag) => cards
                .SelectMany(card => card.Tags)
                .Where(value => value != CardTag.None)
                .Distinct()
                .OrderBy(value => Convert.ToInt32(value))
                .Select(value => new LoadoutDropdownOption(value.ToString(), CardPrinter.GetCardTagLabel(value)))
                .ToList(),
            (SelectionModelKind.Card, ModelMatchKind.EnergyCost) =>
            [
                new LoadoutDropdownOption("0", "0"),
                new LoadoutDropdownOption("1", "1"),
                new LoadoutDropdownOption("2", "2"),
                new LoadoutDropdownOption("3+", "3+"),
                new LoadoutDropdownOption("X", "X"),
                new LoadoutDropdownOption("unplayable", LocMan.Loc("CUSTOM_RUN_UNPLAYABLE", "Unplayable"))
            ],
            _ => []
        };
    }

    private static string FormatModelMatchKind(ModelMatchKind kind, SelectionModelKind modelKind)
    {
        return kind switch
        {
            ModelMatchKind.SpecificModels => LocMan.Loc("CUSTOM_RUN_SPECIFIC_KIND", "Specific {0}", FormatModelKind(modelKind).ToLowerInvariant()),
            ModelMatchKind.Pool => LocMan.Loc("CUSTOM_RUN_KIND_POOL", "{0} pool", FormatModelKind(modelKind)),
            ModelMatchKind.Type => LocMan.Loc("CUSTOM_RUN_CARD_TYPE", "Card type"),
            ModelMatchKind.Rarity => LocMan.Loc("FILTER_GROUP_RARITY", "Rarity"),
            ModelMatchKind.Keyword => LocMan.Loc("FILTER_GROUP_KEYWORD", "Keyword"),
            ModelMatchKind.Tag => LocMan.Loc("FILTER_GROUP_TAG", "Tag"),
            ModelMatchKind.EnergyCost => LocMan.Loc("CUSTOM_RUN_ENERGY_COST", "Energy cost"),
            ModelMatchKind.TextContains => LocMan.Loc("CUSTOM_RUN_TITLE_OR_TEXT_CONTAINS", "Title or text contains"),
            ModelMatchKind.Mod => LocMan.Loc("CUSTOM_RUN_MOD", "Mod"),
            ModelMatchKind.Act => LocMan.Loc("FILTER_GROUP_ACT", "Act"),
            ModelMatchKind.MonsterCategory => LocMan.Loc("CUSTOM_RUN_MONSTER_CATEGORY", "Monster category"),
            _ => kind.ToString()
        };
    }

    private static string GetModelMatchValueLabel(ModelMatchKind kind)
    {
        return kind switch
        {
            ModelMatchKind.Pool => LocMan.Loc("CUSTOM_RUN_POOL", "Pool"),
            ModelMatchKind.Type => LocMan.Loc("FILTER_GROUP_TYPE", "Type"),
            ModelMatchKind.Rarity => LocMan.Loc("FILTER_GROUP_RARITY", "Rarity"),
            ModelMatchKind.Keyword => LocMan.Loc("FILTER_GROUP_KEYWORD", "Keyword"),
            ModelMatchKind.Tag => LocMan.Loc("FILTER_GROUP_TAG", "Tag"),
            ModelMatchKind.EnergyCost => LocMan.Loc("SORT_COST", "Cost"),
            ModelMatchKind.Mod => LocMan.Loc("CUSTOM_RUN_MOD", "Mod"),
            ModelMatchKind.Act => LocMan.Loc("FILTER_GROUP_ACT", "Act"),
            ModelMatchKind.MonsterCategory => LocMan.Loc("CUSTOM_RUN_CATEGORY", "Category"),
            _ => LocMan.Loc("CUSTOM_RUN_VALUE", "Value")
        };
    }

    private static string FormatModelKind(SelectionModelKind kind)
    {
        return kind switch
        {
            SelectionModelKind.Card => LocMan.Loc("LOADOUT_KIND_CARDS", "Cards"),
            SelectionModelKind.Relic => LocMan.Loc("LOADOUT_KIND_RELICS", "Relics"),
            SelectionModelKind.Potion => LocMan.Loc("CUSTOM_RUN_POTIONS", "Potions"),
            SelectionModelKind.Power => LocMan.Loc("POWER_GIVER_ALL_POWERS", "Powers"),
            SelectionModelKind.Monster => LocMan.Loc("POWER_GIVER_TARGET_MONSTERS", "Monsters"),
            _ => kind.ToString()
        };
    }

    private static string FormatComponentKind(RuleComponentKind kind)
    {
        return kind switch
        {
            RuleComponentKind.Trigger => LocMan.Loc("CUSTOM_RUN_TRIGGER", "Trigger"),
            RuleComponentKind.Condition => LocMan.Loc("CUSTOM_RUN_CONDITION", "Condition"),
            RuleComponentKind.Action => LocMan.Loc("CUSTOM_RUN_ACTION", "Action"),
            RuleComponentKind.Target => LocMan.Loc("LOADOUT_TARGET", "Target"),
            _ => kind.ToString()
        };
    }

    private static string FormatComponentName(RuleComponentDescriptor descriptor)
    {
        string id = descriptor.StableId[(descriptor.StableId.LastIndexOf(':') + 1)..];
        return LocMan.Loc($"CUSTOM_RUN_COMPONENT_{LocMan.ToUpperSnakeCase(id)}", descriptor.DisplayName);
    }

    private static string FormatComponentCategory(string category)
    {
        return LocMan.Loc($"CUSTOM_RUN_CATEGORY_{LocMan.ToUpperSnakeCase(category)}", category);
    }

    private static string FormatParameterName(RuleParameterDescriptor parameter)
    {
        return LocMan.Loc($"CUSTOM_RUN_PARAMETER_{LocMan.ToUpperSnakeCase(parameter.DisplayName).Replace(' ', '_')}", parameter.DisplayName);
    }

    private static string FormatParameterOption(RuleParameterOption option)
    {
        return LocMan.Loc($"CUSTOM_RUN_OPTION_{LocMan.ToUpperSnakeCase(option.Id)}", option.DisplayName);
    }

    private void BuildReferenceParameter(
        VBoxContainer parent,
        RuleComponentSpec component,
        RuleParameterDescriptor parameter,
        bool isRole,
        Action? afterChanged)
    {
        HBoxContainer row = CreateFieldRow(FormatParameterName(parameter));
        string selected = RuleComponentParameterService.GetString(component, parameter.Key);
        IEnumerable<VariableDefinition> availableVariables = _definitionContext?.Variables ?? [];
        if (!isRole && component.TypeId is "Loadout2:AddToVariable" or "Loadout2:SubtractFromVariable")
            availableVariables = availableVariables.Where(variable => variable.ValueType == VariableValueType.Number);
        List<LoadoutDropdownOption> options = isRole
            ? (_definitionContext?.Roles ?? []).Select(role => new LoadoutDropdownOption(role.Id, role.Name)).ToList()
            : availableVariables.Select(variable => new LoadoutDropdownOption(variable.Id, variable.Name)).ToList();
        if (!string.IsNullOrWhiteSpace(selected) && options.All(option => option.Id != selected))
            options.Insert(0, new LoadoutDropdownOption(selected, LocMan.Loc("CUSTOM_RUN_MISSING_VALUE", "Missing: {0}", selected)));
        if (options.Count == 0)
            options.Add(new LoadoutDropdownOption(string.Empty, isRole
                ? LocMan.Loc("CUSTOM_RUN_NO_ROLES_DEFINED", "No roles defined")
                : LocMan.Loc("CUSTOM_RUN_NO_VARIABLES_DEFINED", "No variables defined")));
        NSelectFilterDropdown dropdown = CreateDropdown(options, selected, 420f);
        dropdown.SelectedItemChanged += value =>
        {
            RuleComponentParameterService.Set(component, parameter.Key, value);
            if (!isRole && parameter.Key == "variableId")
                InitializeVariableValueControl(component);
            afterChanged?.Invoke();
            MarkDirty();
            if (!isRole && parameter.Key == "variableId")
                RebuildContentDeferred();
        };
        row.AddChild(dropdown);
        parent.AddChild(row);
    }

    private VariableDefinition? GetSelectedVariable(RuleComponentSpec component)
    {
        string variableId = RuleComponentParameterService.GetString(component, "variableId");
        return _definitionContext?.Variables.FirstOrDefault(variable =>
            string.Equals(variable.Id, variableId, StringComparison.Ordinal));
    }

    private bool IsBooleanVariableValueParameter(
        RuleComponentSpec component,
        RuleParameterDescriptor parameter)
    {
        bool valueParameter = component.TypeId == "Loadout2:SetVariable" && parameter.Key == "amount"
                              || component.TypeId == "Loadout2:VariableComparison" && parameter.Key == "value";
        return valueParameter && GetSelectedVariable(component)?.ValueType == VariableValueType.Boolean;
    }

    private void InitializeVariableValueControl(RuleComponentSpec component)
    {
        VariableDefinition? variable = GetSelectedVariable(component);
        if (variable is null)
            return;
        string? key = component.TypeId switch
        {
            "Loadout2:SetVariable" => "amount",
            "Loadout2:AddToVariable" => "amount",
            "Loadout2:SubtractFromVariable" => "amount",
            "Loadout2:VariableComparison" => "value",
            _ => null
        };
        if (key is null)
            return;
        if (variable.ValueType == VariableValueType.Boolean)
        {
            if (!RuleComponentParameterService.TryGet(component, key, out bool _))
                RuleComponentParameterService.Set(component, key, false);
            if (component.TypeId == "Loadout2:VariableComparison")
            {
                string comparison = RuleComponentParameterService.GetString(component, "operator");
                if (comparison is not ("Equal" or "NotEqual"))
                    RuleComponentParameterService.Set(component, "operator", "Equal");
            }
        }
        else if (!RuleComponentParameterService.TryGet(component, key, out NumericValueSpec _))
        {
            RuleComponentParameterService.Set(component, key, new NumericValueSpec
            {
                Source = NumericValueSourceKind.Constant,
                Constant = 1d,
                ConstantKind = NumericConstantKind.Double
            });
        }
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

        HBoxContainer row = CreateFieldRow(FormatParameterName(parameter));
        List<LoadoutDropdownOption> options = targets
            .Select(descriptor => new LoadoutDropdownOption(descriptor.StableId, FormatComponentName(descriptor)))
            .ToList();
        if (targetDescriptor is null && !string.IsNullOrWhiteSpace(capturedTarget.TypeId))
            options.Insert(0, new LoadoutDropdownOption(capturedTarget.TypeId, LocMan.Loc("CUSTOM_RUN_MISSING_VALUE", "Missing: {0}", capturedTarget.TypeId)));
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
                Constant = 1d,
                ConstantKind = parameter.DefaultConstantKind
            };
        }
        NumericValueSpec captured = value;

        HBoxContainer sourceRow = CreateFieldRow(FormatParameterName(parameter));
        NSelectFilterDropdown source = CreateDropdown(
            new[]
            {
                new LoadoutDropdownOption(NumericValueSourceKind.Constant.ToString(), LocMan.Loc("CUSTOM_RUN_CONSTANT", "Constant")),
                new LoadoutDropdownOption(NumericValueSourceKind.Variable.ToString(), LocMan.Loc("CUSTOM_RUN_VARIABLE", "Variable")),
                new LoadoutDropdownOption(NumericValueSourceKind.EventContext.ToString(), LocMan.Loc("CUSTOM_RUN_EVENT_VALUE", "Event Value"))
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
                NumericValueSourceKind.Variable => _definitionContext?.Variables
                    .FirstOrDefault(variable => variable.ValueType == VariableValueType.Number)?.Id,
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
                if (!parameter.AllowDouble)
                    captured.ConstantKind = NumericConstantKind.Integer;
                if (parameter.AllowDouble)
                {
                    NSelectFilterDropdown numberType = CreateDropdown(
                        new[]
                        {
                            new LoadoutDropdownOption(NumericConstantKind.Integer.ToString(), LocMan.Loc("CUSTOM_RUN_INTEGER", "Integer")),
                            new LoadoutDropdownOption(NumericConstantKind.Double.ToString(), LocMan.Loc("CUSTOM_RUN_DOUBLE", "Double"))
                        },
                        captured.ConstantKind.ToString(),
                        190f);
                    numberType.SelectedItemChanged += selected =>
                    {
                        if (!Enum.TryParse(selected, out NumericConstantKind parsed))
                            return;
                        captured.ConstantKind = parsed;
                        if (parsed == NumericConstantKind.Integer)
                            captured.Constant = Math.Truncate(captured.Constant);
                        RuleComponentParameterService.Set(component, parameter.Key, captured);
                        afterChanged?.Invoke();
                        MarkDirty();
                        RebuildContentDeferred();
                    };
                    sourceRow.AddChild(numberType);
                }
                if (captured.ConstantKind == NumericConstantKind.Double)
                {
                    NLoadoutDecimalStepper constant = new();
                    constant.Init(captured.Constant, double.MinValue, double.MaxValue, 0.01d);
                    constant.ValueChanged += changedValue =>
                    {
                        captured.Constant = changedValue;
                        RuleComponentParameterService.Set(component, parameter.Key, captured);
                        afterChanged?.Invoke();
                        MarkDirty();
                    };
                    sourceRow.AddChild(constant);
                }
                else
                {
                    NLoadoutNumberStepper constant = new();
                    constant.Init((int)Math.Clamp(captured.Constant, int.MinValue, int.MaxValue));
                    constant.ValueChanged += changedValue =>
                    {
                        captured.Constant = changedValue;
                        RuleComponentParameterService.Set(component, parameter.Key, captured);
                        afterChanged?.Invoke();
                        MarkDirty();
                    };
                    sourceRow.AddChild(constant);
                }
                break;
            }
            case NumericValueSourceKind.Variable:
            {
                List<LoadoutDropdownOption> variables = (_definitionContext?.Variables ?? [])
                    .Where(variable => variable.ValueType == VariableValueType.Number)
                    .Select(variable => new LoadoutDropdownOption(variable.Id, variable.Name))
                    .ToList();
                if (!string.IsNullOrWhiteSpace(captured.ReferenceId)
                    && variables.All(option => !string.Equals(option.Id, captured.ReferenceId, StringComparison.Ordinal)))
                {
                    variables.Insert(0, new LoadoutDropdownOption(
                        captured.ReferenceId,
                        LocMan.Loc("CUSTOM_RUN_MISSING_OR_NON_NUMBER", "Missing or non-Number: {0}", captured.ReferenceId)));
                }
                if (variables.Count == 0)
                    variables.Add(new LoadoutDropdownOption(string.Empty, LocMan.Loc("CUSTOM_RUN_NO_NUMBER_VARIABLES", "No number variables")));
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
                        new LoadoutDropdownOption("CurrentHp", LocMan.Loc("CREATURE_MANIP_CURRENT_HP", "Current HP")),
                        new LoadoutDropdownOption("MaxHp", LocMan.Loc("CREATURE_MANIP_MAX_HP", "Max HP")),
                        new LoadoutDropdownOption("Gold", LocMan.Loc("CUSTOM_RUN_GOLD", "Gold")),
                        new LoadoutDropdownOption("Energy", LocMan.Loc("CUSTOM_RUN_ENERGY", "Energy")),
                        new LoadoutDropdownOption("TurnNumber", LocMan.Loc("TILDEKEY_STAT_TURN_NUMBER", "Turn Number")),
                        new LoadoutDropdownOption("PlayerCount", LocMan.Loc("CUSTOM_RUN_PLAYER_COUNT", "Player Count"))
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
        string currentId = RuleComponentParameterService.GetString(component, key);
        if (!CustomRunCatalogSelector.TryOpenCatalogSingleSelection(
                kind,
                currentId,
                model =>
                {
                    RuleComponentParameterService.Set(component, key, model.Id.ToString());
                    afterChanged?.Invoke();
                    MarkDirty();
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
        PermanentRuleStorageService.Upsert(rule, _definitionContext?.Variables ?? []);
        SetStatus(LocMan.Loc("CUSTOM_RUN_SAVED_TO_PERMANENT", "Saved '{0}' to Permanent Rules.", rule.Name), success: true);
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
            SetStatus(LocMan.Loc("CUSTOM_RUN_UNSAVED_WARNING_FAILED", "Could not open the unsaved-changes warning."), success: false);
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
        SetStatus(LocMan.Loc("CUSTOM_RUN_UNSAVED_RULE_CHANGES", "Unsaved rule changes."), success: true);
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
            return LocMan.Loc("CUSTOM_RUN_MISSING_VALUE", "Missing: {0}", id);
        return entry.Model switch
        {
            CardModel card => CardPrinter.FormatCardTitle(card),
            RelicModel relic => CommonHelpers.FormatRelicTitle(relic),
            PotionModel potion => CommonHelpers.FormatPotionTitle(potion),
            PowerModel power => CommonHelpers.FormatPowerTitle(power),
            MonsterModel monster => FormatMonsterTitle(monster),
            _ => entry.Model.Id.Entry
        };
    }

    private static string FormatLimit(RuleLimitKind kind)
    {
        return kind switch
        {
            RuleLimitKind.Unlimited => LocMan.Loc("CUSTOM_RUN_LIMIT_UNLIMITED", "Unlimited"),
            RuleLimitKind.OncePerEventChain => LocMan.Loc("CUSTOM_RUN_LIMIT_ONCE_PER_EVENT_CHAIN", "Once per event chain"),
            RuleLimitKind.OncePerTurn => LocMan.Loc("CUSTOM_RUN_LIMIT_ONCE_PER_TURN", "Once per turn"),
            RuleLimitKind.TimesPerTurn => LocMan.Loc("CUSTOM_RUN_LIMIT_N_PER_TURN", "N times per turn"),
            RuleLimitKind.OncePerCombat => LocMan.Loc("CUSTOM_RUN_LIMIT_ONCE_PER_COMBAT", "Once per combat"),
            RuleLimitKind.TimesPerCombat => LocMan.Loc("CUSTOM_RUN_LIMIT_N_PER_COMBAT", "N times per combat"),
            RuleLimitKind.OncePerRun => LocMan.Loc("CUSTOM_RUN_LIMIT_ONCE_PER_RUN", "Once per run"),
            RuleLimitKind.TimesPerRun => LocMan.Loc("CUSTOM_RUN_LIMIT_N_PER_RUN", "N times per run"),
            RuleLimitKind.UntilCondition => LocMan.Loc("CUSTOM_RUN_LIMIT_UNTIL_CONDITION", "Until condition"),
            _ => kind.ToString()
        };
    }

    private static string FormatActTitle(ActModel act)
    {
        try
        {
            return act.Title.GetFormattedText();
        }
        catch
        {
            return act.Id.Entry;
        }
    }

    private static string FormatMonsterTitle(MonsterModel monster)
    {
        try
        {
            return monster.Title.GetFormattedText();
        }
        catch
        {
            return monster.Id.Entry;
        }
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
