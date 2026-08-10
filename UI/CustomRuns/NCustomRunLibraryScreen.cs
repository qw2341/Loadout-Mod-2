#nullable enable

namespace Loadout.UI.CustomRuns;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Loadout.Services.CustomRuns.Compilation;
using Loadout.Services.CustomRuns.Models;
using Loadout.Services.CustomRuns.Networking;
using Loadout.Services.CustomRuns.PermanentRules;
using Loadout.Services.CustomRuns.Persistence;
using Loadout.Services.CustomRuns.Runtime;
using Loadout.UI.Screens.Controls;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Entities.UI;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;

public partial class NCustomRunLibraryScreen : Control
{
    private const string ScenePath = "res://UI/CustomRuns/CustomRunLibraryScreen.tscn";

    private StartRunLobby? _lobby;
    private Control? _sourceScreen;
    private NButton? _sourceConfirmButton;
    private NScrollableContainer? _customScroll;
    private VBoxContainer? _customList;
    private NScrollableContainer? _permanentScroll;
    private VBoxContainer? _permanentList;
    private Control? _customListMount;
    private Control? _permanentListMount;
    private NDeckLoadoutTextAction? _customRunsHeader;
    private NDeckLoadoutTextAction? _permanentRulesHeader;
    private MegaLabel? _statusLabel;
    private NBackButton? _backButton;
    private Tween? _statusTween;
    private bool _launching;
    private bool _needsRebuild;
    private bool _showingPermanentRules;
    private bool _suppressPermanentRebuild;
    private string? _focusDefinitionId;
    private string? _focusPermanentRuleId;
    private int _focusActionIndex;
    private readonly List<(string Id, NCustomRunLibraryRow Row)> _rows = [];
    private readonly List<NCustomRunPermanentRuleRow> _permanentRows = [];

    public static void Open(Control sourceScreen, StartRunLobby lobby)
    {
        NLoadoutPanelRoot? root = NLoadoutPanelRoot.GetOrAttach(sourceScreen.GetTree());
        if (root is null)
            return;

        NCustomRunLibraryScreen? screen = root.GetNodeOrNull<NCustomRunLibraryScreen>(
            "ScreenStack/CustomRunLibraryScreen");
        if (screen is null)
        {
            screen = Create();
            screen.Name = "CustomRunLibraryScreen";
        }

        screen.Init(sourceScreen, lobby);
        root.OpenScreen(screen);
    }

    public static void CloseForLobby(StartRunLobby lobby)
    {
        NCustomRunLibraryScreen? screen = NLoadoutPanelRoot.Instance?
            .GetNodeOrNull<NCustomRunLibraryScreen>("ScreenStack/CustomRunLibraryScreen");
        screen?.DetachLobby(lobby);
    }

    public static NCustomRunLibraryScreen Create()
    {
        if (ResourceLoader.Exists(ScenePath)
            && GD.Load<PackedScene>(ScenePath) is { } scene
            && scene.Instantiate<NCustomRunLibraryScreen>() is { } screen)
        {
            return screen;
        }

        GD.PushWarning($"Loadout Custom Run: could not load '{ScenePath}'.");
        return new NCustomRunLibraryScreen();
    }

    public void Init(Control sourceScreen, StartRunLobby lobby)
    {
        _sourceScreen = sourceScreen;
        _sourceConfirmButton = sourceScreen.GetNodeOrNull<NButton>("ConfirmButton");
        _lobby = lobby;
        _launching = false;
        if (IsNodeReady())
            RebuildLibrary();
    }

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        ZIndex = 120;
        CustomRunStorageService.Register();
        PermanentRuleStorageService.Register();
        CustomRunStorageService.Changed += OnDefinitionsChanged;
        PermanentRuleStorageService.Changed += OnPermanentRulesChanged;
        CustomRunLobbyService.RemoteDefinitionChanged += OnDefinitionsChanged;
        BuildStaticUi();
        EnsureNativeScrolls();
        EnsureBackButton();
        RebuildLibrary();
        SwitchSection(showPermanentRules: false, moveFocus: false);
    }

    public override void _ExitTree()
    {
        _statusTween?.Kill();
        CustomRunStorageService.Changed -= OnDefinitionsChanged;
        PermanentRuleStorageService.Changed -= OnPermanentRulesChanged;
        CustomRunLobbyService.RemoteDefinitionChanged -= OnDefinitionsChanged;
    }

    public override void _Notification(int what)
    {
        if (what == NotificationVisibilityChanged && IsNodeReady() && Visible && _needsRebuild)
        {
            _needsRebuild = false;
            RebuildLibrary();
        }
    }

    private void BuildStaticUi()
    {
        Control? customHeaderMount = GetNodeOrNull<Control>("%CustomRunsHeaderMount");
        if (customHeaderMount is not null && customHeaderMount.GetChildCount() == 0)
        {
            _customRunsHeader = CreateSectionHeader("custom_runs", "CUSTOM RUNS");
            _customRunsHeader.Connect(
                NClickableControl.SignalName.Released,
                Callable.From<NClickableControl>(_ => SwitchSection(showPermanentRules: false)));
            customHeaderMount.AddChild(_customRunsHeader);
        }

        Control? permanentHeaderMount = GetNodeOrNull<Control>("%PermanentRulesHeaderMount");
        if (permanentHeaderMount is not null && permanentHeaderMount.GetChildCount() == 0)
        {
            _permanentRulesHeader = CreateSectionHeader("permanent_rules", "PERMANENT RULES");
            _permanentRulesHeader.Connect(
                NClickableControl.SignalName.Released,
                Callable.From<NClickableControl>(_ => SwitchSection(showPermanentRules: true)));
            permanentHeaderMount.AddChild(_permanentRulesHeader);
        }

        Control? statusMount = GetNodeOrNull<Control>("OuterMargin/Root/StatusMount");
        if (statusMount is not null && statusMount.GetChildCount() == 0)
        {
            _statusLabel = CreateLabel(string.Empty, 21, StsColors.cream, HorizontalAlignment.Center, bold: false);
            _statusLabel.SetAnchorsPreset(LayoutPreset.FullRect);
            _statusLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            statusMount.AddChild(_statusLabel);
        }
    }

    private void EnsureNativeScrolls()
    {
        _customListMount = GetNodeOrNull<Control>("%ListMount");
        _permanentListMount = GetNodeOrNull<Control>("%PermanentRulesListMount");
        if (_customListMount is null || _permanentListMount is null)
            return;

        (_customScroll, _customList) = CreateNativeScroll(_customListMount, "CustomRuns");
        (_permanentScroll, _permanentList) = CreateNativeScroll(_permanentListMount, "PermanentRules");
    }

    private static (NScrollableContainer Scroll, VBoxContainer List) CreateNativeScroll(Control mount, string name)
    {

        NScrollableContainer scroll = new()
        {
            Name = $"{name}Scroll",
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

        VBoxContainer list = new()
        {
            Name = "Content",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            MouseFilter = MouseFilterEnum.Pass
        };
        list.SetAnchorsPreset(LayoutPreset.TopWide);
        list.AddThemeConstantOverride("separation", 0);
        mask.AddChild(list);

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

        return (scroll, list);
    }

    private void EnsureBackButton()
    {
        Control? mount = GetNodeOrNull<Control>("%BackButtonMount");
        if (mount is null)
            return;
        const string backButtonScenePath = "res://scenes/ui/back_button.tscn";
        _backButton = ResourceLoader.Exists(backButtonScenePath)
                      && GD.Load<PackedScene>(backButtonScenePath) is { } scene
            ? scene.Instantiate<NBackButton>()
            : NLoadoutBackButtonFactory.Create();
        _backButton.Name = "BackButton";
        _backButton.ZIndex = 200;
        _backButton.Connect(
            NClickableControl.SignalName.Released,
            Callable.From<NClickableControl>(_ =>
            {
                NLoadoutPanelRoot.Instance?.CloseTopScreen();
            }));
        mount.AddChild(_backButton);
        Callable.From(() =>
        {
            if (GodotObject.IsInstanceValid(_backButton))
                _backButton.Enable();
        }).CallDeferred();
    }

    private void RebuildLibrary()
    {
        if (_customList is null || _lobby is null)
            return;

        CaptureFocus();
        foreach (Node child in _customList.GetChildren())
        {
            _customList.RemoveChild(child);
            child.QueueFree();
        }
        _rows.Clear();

        if (_lobby.NetService.Type == NetGameType.Client
            && CustomRunLobbyService.GetRemoteDefinition() is { } remote)
        {
            _customList.AddChild(CreateSectionLabel("LOBBY CUSTOM RUN"));
            AddDefinitionRow(remote, isLobbyDefinition: true);
            _customList.AddChild(CreateSectionLabel("YOUR CUSTOM RUNS"));
        }

        foreach (CustomRunDefinition definition in CustomRunStorageService.GetDefinitions())
            AddDefinitionRow(definition, isLobbyDefinition: false);

        AddNewRow();
        RebuildPermanentRules();
        Callable.From(FinalizeRebuild).CallDeferred();
    }

    private void AddDefinitionRow(CustomRunDefinition definition, bool isLobbyDefinition)
    {
        if (_customList is null || _lobby is null)
            return;

        CustomRunDefinition captured = CustomRunNormalizationService.Clone(definition);
        bool canPlay = !isLobbyDefinition
                       && _lobby.NetService.Type != NetGameType.Client
                       && !_launching;
        NCustomRunLibraryRow row = new();
        row.Init(new CustomRunLibraryRowOptions(
            captured.Name,
            captured.Description,
            isLobbyDefinition ? "LOBBY" : "PLAY",
            isLobbyDefinition ? null : () =>
            {
                TaskHelper.RunSafely(PlayAsync(captured));
            },
            RowAction: () => OpenEditor(captured, isLobbyDefinition),
            ShowDelete: !isLobbyDefinition,
            DeleteAction: isLobbyDefinition ? null : () =>
            {
                TaskHelper.RunSafely(DeleteAsync(captured));
            },
            TrailingLabel: "EXPORT",
            TrailingAction: () => Export(captured),
            PrimaryEnabled: canPlay,
            ReorderId: isLobbyDefinition ? null : captured.Id,
            ReorderAction: isLobbyDefinition ? null : ReorderCustomRun));
        _customList.AddChild(row);
        _rows.Add((isLobbyDefinition ? $"host:{captured.Id}" : captured.Id, row));
    }

    private void AddNewRow()
    {
        if (_customList is null)
            return;

        NCustomRunLibraryRow row = new();
        row.Init(new CustomRunLibraryRowOptions(
            string.Empty,
            string.Empty,
            "+  CREATE NEW CUSTOM RUN",
            NewRun,
            RowAction: NewRun,
            ShowDelete: false,
            DeleteAction: null,
            TrailingLabel: "IMPORT",
            TrailingAction: Import,
            IsCreateRow: true,
            ReorderAction: ReorderCustomRun));
        _customList.AddChild(row);
        _rows.Add(("new", row));
    }

    private void RebuildPermanentRules()
    {
        if (_permanentList is null)
            return;

        foreach (Node child in _permanentList.GetChildren())
        {
            _permanentList.RemoveChild(child);
            child.QueueFree();
        }
        _permanentRows.Clear();

        IReadOnlyList<RuleDefinition> rules = PermanentRuleStorageService.GetRules();
        if (rules.Count == 0)
        {
            MegaLabel empty = CreateLabel(
                "No permanent rules have been saved yet.",
                24,
                StsColors.cream,
                HorizontalAlignment.Center,
                bold: false);
            empty.CustomMinimumSize = new Vector2(0f, 180f);
            _permanentList.AddChild(empty);
            return;
        }

        foreach (RuleDefinition rule in rules)
        {
            RuleDefinition captured = CustomRunNormalizationService.CloneRule(rule);
            NCustomRunPermanentRuleRow row = new();
            row.Init(
                captured,
                TogglePermanentRule,
                selected =>
                {
                    TaskHelper.RunSafely(DeletePermanentRuleAsync(selected));
                },
                ReorderPermanentRule);
            _permanentList.AddChild(row);
            _permanentRows.Add(row);
        }
    }

    private void SwitchSection(bool showPermanentRules, bool moveFocus = true)
    {
        _showingPermanentRules = showPermanentRules;
        if (_customListMount is not null)
            _customListMount.Visible = !showPermanentRules;
        if (_permanentListMount is not null)
            _permanentListMount.Visible = showPermanentRules;

        if (showPermanentRules)
        {
            if (_customRunsHeader is not null)
                _customRunsHeader.Modulate = new Color(0.72f, 0.72f, 0.72f, 0.78f);
            if (_permanentRulesHeader is not null)
                _permanentRulesHeader.Modulate = Colors.White;
        }
        else
        {
            if (_permanentRulesHeader is not null)
                _permanentRulesHeader.Modulate = new Color(0.72f, 0.72f, 0.72f, 0.78f);
            if (_customRunsHeader is not null)
                _customRunsHeader.Modulate = Colors.White;
        }

        Control? activeMount = showPermanentRules ? _permanentListMount : _customListMount;
        if (activeMount is not null)
        {
            activeMount.Modulate = new Color(1f, 1f, 1f, 0f);
            Tween tween = CreateTween();
            tween.TweenProperty(activeMount, "modulate:a", 1f, 0.18f)
                .SetEase(Tween.EaseType.Out)
                .SetTrans(Tween.TransitionType.Cubic);
        }

        Callable.From(FinalizeRebuild).CallDeferred();
        if (!moveFocus)
            return;
        Callable.From(() =>
        {
            if (_showingPermanentRules)
            {
                Control? focus = _permanentRows.FirstOrDefault()?.Actions.FirstOrDefault();
                (focus ?? _permanentRulesHeader)?.GrabFocus();
            }
            else
            {
                (_rows.FirstOrDefault().Row?.PrimaryFocusControl ?? _customRunsHeader)?.GrabFocus();
            }
        }).CallDeferred();
    }

    private static NDeckLoadoutTextAction CreateSectionHeader(string id, string label)
    {
        NDeckLoadoutTextAction button = new()
        {
            CustomMinimumSize = new Vector2(520f, 52f),
            TextAlignment = HorizontalAlignment.Center,
            FontSize = 28
        };
        button.Init(id, label);
        button.SetAnchorsPreset(LayoutPreset.Center);
        button.OffsetLeft = -260f;
        button.OffsetTop = -26f;
        button.OffsetRight = 260f;
        button.OffsetBottom = 26f;
        return button;
    }

    private void ReorderCustomRun(string sourceId, string? targetId, bool placeAfter)
    {
        _focusDefinitionId = sourceId;
        CustomRunStorageService.Move(sourceId, targetId, placeAfter);
    }

    private void TogglePermanentRule(string id, bool enabled)
    {
        _suppressPermanentRebuild = true;
        try
        {
            PermanentRuleStorageService.SetEnabled(id, enabled);
            SetStatus($"Permanent rule {(enabled ? "enabled" : "disabled")}.", success: true);
        }
        finally
        {
            _suppressPermanentRebuild = false;
        }
    }

    private void ReorderPermanentRule(string sourceId, string? targetId, bool placeAfter)
    {
        _focusPermanentRuleId = sourceId;
        PermanentRuleStorageService.Move(sourceId, targetId, placeAfter);
    }

    private void FinalizeRebuild()
    {
        if (GodotObject.IsInstanceValid(_customScroll) && GodotObject.IsInstanceValid(_customList))
            _customScroll.SetContent(_customList);
        if (GodotObject.IsInstanceValid(_permanentScroll) && GodotObject.IsInstanceValid(_permanentList))
            _permanentScroll.SetContent(_permanentList);
        ConfigureFocusNavigation();
        RestoreFocus();
    }

    private void ConfigureFocusNavigation()
    {
        for (int rowIndex = 0; rowIndex < _rows.Count; rowIndex++)
        {
            IReadOnlyList<NClickableControl> actions = _rows[rowIndex].Row.Actions;
            for (int actionIndex = 0; actionIndex < actions.Count; actionIndex++)
            {
                NClickableControl action = actions[actionIndex];
                if (actionIndex > 0)
                    action.FocusNeighborLeft = actions[actionIndex - 1].GetPath();
                if (actionIndex + 1 < actions.Count)
                    action.FocusNeighborRight = actions[actionIndex + 1].GetPath();
                if (rowIndex > 0)
                {
                    NCustomRunLibraryRow aboveRow = _rows[rowIndex - 1].Row;
                    int aboveIndex = aboveRow.FindActionSlot(_rows[rowIndex].Row.GetActionSlot(actionIndex));
                    if (aboveIndex < 0)
                        aboveIndex = Math.Min(actionIndex, aboveRow.Actions.Count - 1);
                    action.FocusNeighborTop = aboveRow.Actions[aboveIndex].GetPath();
                }
                if (rowIndex + 1 < _rows.Count)
                {
                    NCustomRunLibraryRow belowRow = _rows[rowIndex + 1].Row;
                    int belowIndex = belowRow.FindActionSlot(_rows[rowIndex].Row.GetActionSlot(actionIndex));
                    if (belowIndex < 0)
                        belowIndex = Math.Min(actionIndex, belowRow.Actions.Count - 1);
                    action.FocusNeighborBottom = belowRow.Actions[belowIndex].GetPath();
                }
                else if (_permanentRulesHeader is not null)
                {
                    action.FocusNeighborBottom = _permanentRulesHeader.GetPath();
                }
            }
        }

        if (_customRunsHeader is not null)
        {
            if (_showingPermanentRules && _permanentRows.Count > 0 && _permanentRows[0].Actions.Count > 0)
                _customRunsHeader.FocusNeighborBottom = _permanentRows[0].Actions[0].GetPath();
            else if (!_showingPermanentRules && _rows.Count > 0 && _rows[0].Row.Actions.Count > 0)
                _customRunsHeader.FocusNeighborBottom = _rows[0].Row.Actions[0].GetPath();
            else if (_permanentRulesHeader is not null)
                _customRunsHeader.FocusNeighborBottom = _permanentRulesHeader.GetPath();
        }
        if (_permanentRulesHeader is not null)
        {
            if (_showingPermanentRules && _permanentRows.Count > 0 && _permanentRows[^1].Actions.Count > 0)
                _permanentRulesHeader.FocusNeighborTop = _permanentRows[^1].Actions[0].GetPath();
            else if (!_showingPermanentRules && _rows.Count > 0 && _rows[^1].Row.Actions.Count > 0)
                _permanentRulesHeader.FocusNeighborTop = _rows[^1].Row.Actions[0].GetPath();
            else
                _permanentRulesHeader.FocusNeighborTop = _customRunsHeader?.GetPath() ?? _permanentRulesHeader.GetPath();
            if (_backButton is not null)
                _permanentRulesHeader.FocusNeighborBottom = _backButton.GetPath();
        }

        for (int rowIndex = 0; rowIndex < _permanentRows.Count; rowIndex++)
        {
            IReadOnlyList<Control> actions = _permanentRows[rowIndex].Actions;
            for (int actionIndex = 0; actionIndex < actions.Count; actionIndex++)
            {
                Control action = actions[actionIndex];
                if (actionIndex > 0)
                    action.FocusNeighborLeft = actions[actionIndex - 1].GetPath();
                if (actionIndex + 1 < actions.Count)
                    action.FocusNeighborRight = actions[actionIndex + 1].GetPath();
                action.FocusNeighborTop = rowIndex > 0 && _permanentRows[rowIndex - 1].Actions.Count > 0
                    ? _permanentRows[rowIndex - 1].Actions[Math.Min(actionIndex, _permanentRows[rowIndex - 1].Actions.Count - 1)].GetPath()
                    : _customRunsHeader?.GetPath() ?? action.GetPath();
                if (rowIndex + 1 < _permanentRows.Count && _permanentRows[rowIndex + 1].Actions.Count > 0)
                {
                    action.FocusNeighborBottom = _permanentRows[rowIndex + 1].Actions[
                        Math.Min(actionIndex, _permanentRows[rowIndex + 1].Actions.Count - 1)].GetPath();
                }
                else if (_permanentRulesHeader is not null)
                {
                    action.FocusNeighborBottom = _permanentRulesHeader.GetPath();
                }
            }
        }

        if (_backButton is not null)
        {
            if (_permanentRulesHeader is not null)
                _backButton.FocusNeighborTop = _permanentRulesHeader.GetPath();
        }
    }

    private void CaptureFocus()
    {
        Control? focus = GetViewport()?.GuiGetFocusOwner();
        if (focus is null)
            return;
        for (int rowIndex = 0; rowIndex < _rows.Count; rowIndex++)
        {
            IReadOnlyList<NClickableControl> actions = _rows[rowIndex].Row.Actions;
            for (int actionIndex = 0; actionIndex < actions.Count; actionIndex++)
            {
                if (ReferenceEquals(actions[actionIndex], focus))
                {
                    _focusDefinitionId = _rows[rowIndex].Id;
                    _focusActionIndex = actionIndex;
                    return;
                }
            }
        }
    }

    private void RestoreFocus()
    {
        if (_showingPermanentRules)
        {
            NCustomRunPermanentRuleRow? permanentRow = _focusPermanentRuleId is null
                ? _permanentRows.FirstOrDefault()
                : _permanentRows.FirstOrDefault(row => string.Equals(
                    row.RuleId,
                    _focusPermanentRuleId,
                    StringComparison.Ordinal));
            permanentRow ??= _permanentRows.FirstOrDefault();
            (permanentRow?.Actions.FirstOrDefault() ?? _permanentRulesHeader)?.GrabFocus();
            return;
        }

        if (_rows.Count == 0)
            return;
        int rowIndex = _focusDefinitionId is null
            ? 0
            : _rows.FindIndex(row => string.Equals(row.Id, _focusDefinitionId, StringComparison.Ordinal));
        if (rowIndex < 0)
            rowIndex = Math.Min(_rows.Count - 1, Math.Max(0, _rows.Count - 2));
        IReadOnlyList<NClickableControl> actions = _rows[rowIndex].Row.Actions;
        if (actions.Count == 0)
            return;
        actions[Math.Min(_focusActionIndex, actions.Count - 1)].GrabFocus();
    }

    private void NewRun()
    {
        CustomRunDefinition definition = CustomRunStorageService.CreateNew();
        _focusDefinitionId = definition.Id;
        OpenEditor(definition, readOnly: false);
    }

    private void OpenEditor(CustomRunDefinition definition, bool readOnly)
    {
        if (_lobby is null)
            return;
        NCustomRunEditorScreen.OpenFromLibrary(this, _lobby, definition.Id, readOnly, Name);
    }

    private void Import()
    {
        if (!CustomRunClipboardService.TryImport(out CustomRunDefinition definition, out string error))
        {
            SetStatus(error, success: false);
            return;
        }

        CustomRunDefinition imported = CustomRunStorageService.Import(definition);
        CustomRunCompileResult validation = CustomRunCompiler.Compile(imported, _lobby!);
        _focusDefinitionId = imported.Id;
        SetStatus(
            validation.IsValid
                ? $"Imported '{imported.Name}'."
                : $"Imported '{imported.Name}' with {validation.Issues.Count} issue(s); it remains editable.",
            validation.IsValid);
    }

    private void Export(CustomRunDefinition definition)
    {
        if (CustomRunClipboardService.Copy(definition, out string error))
            SetStatus($"Copied '{definition.Name}' to clipboard.", success: true);
        else
            SetStatus(error, success: false);
    }

    private async Task DeleteAsync(CustomRunDefinition definition)
    {
        LocString body = new("settings_ui", "LOADOUT-DELETE_CUSTOM_RUN_CONFIRM_BODY.title");
        body.Add("Name", definition.Name);
        bool confirmed = await WaitForConfirmationAboveScreen(
            body,
            new LocString("settings_ui", "LOADOUT-DELETE_CUSTOM_RUN_CONFIRM_TITLE.title"),
            new LocString("settings_ui", "LOADOUT-DELETE_CUSTOM_RUN_NO.title"),
            new LocString("settings_ui", "LOADOUT-DELETE_CUSTOM_RUN_YES.title"));
        if (!confirmed)
            return;

        int index = _rows.FindIndex(row => string.Equals(row.Id, definition.Id, StringComparison.Ordinal));
        _focusDefinitionId = index >= 0 && index + 1 < _rows.Count ? _rows[index + 1].Id : "new";
        if (CustomRunStorageService.Delete(definition.Id))
            SetStatus($"Deleted '{definition.Name}'.", success: true);
        else
            SetStatus($"Could not find '{definition.Name}' to delete.", success: false);
    }

    private async Task DeletePermanentRuleAsync(RuleDefinition rule)
    {
        LocString body = new("settings_ui", "LOADOUT-DELETE_PERMANENT_RULE_CONFIRM_BODY.title");
        body.Add("Name", rule.Name);
        bool confirmed = await WaitForConfirmationAboveScreen(
            body,
            new LocString("settings_ui", "LOADOUT-DELETE_PERMANENT_RULE_CONFIRM_TITLE.title"),
            new LocString("settings_ui", "LOADOUT-DELETE_CUSTOM_RUN_NO.title"),
            new LocString("settings_ui", "LOADOUT-DELETE_CUSTOM_RUN_YES.title"));
        if (!confirmed)
            return;

        int index = _permanentRows.FindIndex(row => string.Equals(row.RuleId, rule.Id, StringComparison.Ordinal));
        _focusPermanentRuleId = index >= 0 && index + 1 < _permanentRows.Count
            ? _permanentRows[index + 1].RuleId
            : _permanentRows.ElementAtOrDefault(Math.Max(0, index - 1))?.RuleId;
        if (PermanentRuleStorageService.Delete(rule.Id))
            SetStatus($"Deleted permanent rule '{rule.Name}'.", success: true);
        else
            SetStatus($"Could not find permanent rule '{rule.Name}' to delete.", success: false);
    }

    private async Task<bool> WaitForConfirmationAboveScreen(
        LocString body,
        LocString title,
        LocString noText,
        LocString yesText)
    {
        NModalContainer? modalContainer = NModalContainer.Instance;
        if (modalContainer is null || !GodotObject.IsInstanceValid(modalContainer))
        {
            SetStatus("Could not open the delete confirmation.", success: false);
            return false;
        }

        if (modalContainer.OpenModal is GodotObject openModal
            && !GodotObject.IsInstanceValid(openModal))
        {
            modalContainer.Clear();
        }
        if (modalContainer.OpenModal is not null)
        {
            SetStatus("The delete confirmation is unavailable while another popup is open.", success: false);
            return false;
        }

        NGenericPopup? popup = NGenericPopup.Create();
        if (popup is null)
        {
            SetStatus("Could not open the delete confirmation.", success: false);
            return false;
        }

        IDisposable? modalLease = NLoadoutPanelRoot.Instance?.HostNativeModal(modalContainer);
        try
        {
            modalContainer.Add(popup);
            return await popup.WaitForConfirmation(body, title, noText, yesText);
        }
        finally
        {
            if (GodotObject.IsInstanceValid(modalContainer))
                modalContainer.Clear();
            modalLease?.Dispose();
        }
    }

    private async Task PlayAsync(CustomRunDefinition definition)
    {
        if (_lobby is null || _launching)
            return;
        if (_lobby.NetService.Type == NetGameType.Client)
        {
            SetStatus("Only the host can Play a local Custom Run in this lobby.", success: false);
            return;
        }

        CustomRunDefinition saved = CustomRunStorageService.Upsert(definition);
        CustomRunDefinition effectiveDefinition = BuildEffectiveDefinition(saved);
        CustomRunCompileResult compiled = CustomRunCompiler.Compile(effectiveDefinition, _lobby);
        if (!compiled.IsValid || compiled.Snapshot is null)
        {
            CustomRunValidationIssue? issue = compiled.Issues
                .FirstOrDefault(candidate => candidate.Severity == CustomRunValidationSeverity.Error);
            SetStatus(issue is null ? "This Custom Run could not be compiled." : $"{issue.Section}: {issue.Message}", success: false);
            return;
        }

        if (!CustomRunLobbyService.ApplyHostDefinition(_lobby, saved, out string applyError))
        {
            SetStatus(applyError, success: false);
            return;
        }

        _launching = true;
        RebuildLibrary();
        SetStatus("Preparing the Custom Run…", success: true, persistent: true);
        bool screensClosedForLaunch = false;
        try
        {
            CustomRunPreparationResult result = await CustomRunLobbyService.PrepareHostRunAsync(_lobby, compiled.Snapshot);
            if (!result.Succeeded)
            {
                _launching = false;
                RebuildLibrary();
                SetStatus(result.Error, success: false);
                return;
            }

            if (_sourceConfirmButton is null || !GodotObject.IsInstanceValid(_sourceConfirmButton))
            {
                _launching = false;
                CustomRunRuntimeSnapshotService.ClearPending();
                SetStatus("Could not find the source screen's Embark button.", success: false);
                return;
            }

            NLoadoutPanelRoot.Instance?.CloseScreen("CustomRunEditorScreen");
            NLoadoutPanelRoot.Instance?.CloseScreen(Name);
            screensClosedForLaunch = true;
            NButton embark = _sourceConfirmButton;
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (!GodotObject.IsInstanceValid(embark))
                throw new InvalidOperationException("The source screen's Embark button was removed before launch.");
            embark.ForceClick();
        }
        catch (Exception exception)
        {
            _launching = false;
            CustomRunRuntimeSnapshotService.ClearPending();
            MainFile.Logger.Error($"[Loadout] Custom Run launch failed: {exception}");
            if (screensClosedForLaunch)
                NLoadoutPanelRoot.Instance?.OpenScreen(this);
            RebuildLibrary();
            SetStatus($"Could not start the Custom Run: {exception.Message}", success: false);
        }
    }

    private void SetStatus(string text, bool success, bool persistent = false)
    {
        if (_statusLabel is null)
            return;
        _statusTween?.Kill();
        _statusLabel.Modulate = Colors.White;
        _statusLabel.Text = text;
        _statusLabel.AddThemeColorOverride(
            "font_color",
            success ? new Color(0.68f, 1f, 0.55f) : new Color(1f, 0.58f, 0.48f));
        if (persistent)
            return;
        _statusTween = CreateTween();
        _statusTween.TweenInterval(2.8f);
        _statusTween.TweenProperty(_statusLabel, "modulate:a", 0f, 0.45f);
    }

    private void OnDefinitionsChanged()
    {
        if (!IsNodeReady() || _launching)
            return;
        if (Visible)
            RebuildLibrary();
        else
            _needsRebuild = true;
    }

    private void OnPermanentRulesChanged()
    {
        if (_suppressPermanentRebuild || !IsNodeReady() || _launching)
            return;
        if (Visible)
            RebuildLibrary();
        else
            _needsRebuild = true;
    }

    private static CustomRunDefinition BuildEffectiveDefinition(CustomRunDefinition saved)
    {
        CustomRunDefinition effective = CustomRunNormalizationService.Clone(saved);
        HashSet<string> scenarioRuleIds = effective.Rules
            .Select(rule => rule.Id)
            .ToHashSet(StringComparer.Ordinal);
        List<RuleDefinition> enabledPermanentRules = PermanentRuleStorageService.GetRules()
            .Where(rule => rule.Enabled && !scenarioRuleIds.Contains(rule.Id))
            .Select(CustomRunNormalizationService.CloneRule)
            .ToList();
        enabledPermanentRules.AddRange(effective.Rules);
        effective.Rules = enabledPermanentRules;
        return effective;
    }

    private void DetachLobby(StartRunLobby lobby)
    {
        if (!ReferenceEquals(_lobby, lobby))
            return;
        NLoadoutPanelRoot.Instance?.CloseScreen(Name);
        _lobby = null;
        _sourceScreen = null;
        _sourceConfirmButton = null;
    }

    private static MegaLabel CreateSectionLabel(string text)
    {
        MegaLabel label = CreateLabel(text, 22, StsColors.gold, HorizontalAlignment.Left, bold: true);
        label.CustomMinimumSize = new Vector2(0f, 38f);
        return label;
    }

    private static MegaLabel CreateLabel(
        string text,
        int fontSize,
        Color color,
        HorizontalAlignment alignment,
        bool bold)
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
        label.AddThemeFontOverride(
            "font",
            LoadFont(bold
                ? "res://themes/kreon_bold_glyph_space_one.tres"
                : "res://themes/kreon_regular_shared.tres"));
        label.AddThemeFontSizeOverride("font_size", fontSize);
        label.AddThemeColorOverride("font_color", color);
        label.AddThemeColorOverride("font_shadow_color", new Color(0f, 0f, 0f, 0.7f));
        label.AddThemeConstantOverride("shadow_offset_x", 3);
        label.AddThemeConstantOverride("shadow_offset_y", 2);
        return label;
    }

    private static Font? LoadFont(string path)
    {
        return ResourceLoader.Exists(path) ? GD.Load<Font>(path) : null;
    }
}
