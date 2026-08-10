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
    private NScrollableContainer? _scroll;
    private VBoxContainer? _list;
    private MegaLabel? _statusLabel;
    private NBackButton? _backButton;
    private Tween? _statusTween;
    private bool _launching;
    private bool _needsRebuild;
    private string? _focusDefinitionId;
    private int _focusActionIndex;
    private readonly List<(string Id, NCustomRunLibraryRow Row)> _rows = [];

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
        CustomRunStorageService.Changed += OnDefinitionsChanged;
        CustomRunLobbyService.RemoteDefinitionChanged += OnDefinitionsChanged;
        BuildStaticUi();
        EnsureNativeScroll();
        EnsureBackButton();
        RebuildLibrary();
    }

    public override void _ExitTree()
    {
        _statusTween?.Kill();
        CustomRunStorageService.Changed -= OnDefinitionsChanged;
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
        Control? titleMount = GetNodeOrNull<Control>("OuterMargin/Root/TitleMount");
        if (titleMount is not null && titleMount.GetChildCount() == 0)
        {
            MegaLabel title = CreateLabel("CUSTOM RUNS", 46, StsColors.gold, HorizontalAlignment.Center, bold: true);
            title.SetAnchorsPreset(LayoutPreset.FullRect);
            titleMount.AddChild(title);
        }

        Control? subtitleMount = GetNodeOrNull<Control>("OuterMargin/Root/SubtitleMount");
        if (subtitleMount is not null && subtitleMount.GetChildCount() == 0)
        {
            MegaLabel subtitle = CreateLabel(
                "Choose a saved run, or author a new one.",
                21,
                new Color(0.92f, 0.89f, 0.8f),
                HorizontalAlignment.Center,
                bold: false);
            subtitle.SetAnchorsPreset(LayoutPreset.FullRect);
            subtitleMount.AddChild(subtitle);
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

    private void EnsureNativeScroll()
    {
        Control? mount = GetNodeOrNull<Control>("%ListMount");
        if (mount is null)
            return;

        NScrollableContainer scroll = new()
        {
            Name = "LibraryScroll",
            MouseFilter = MouseFilterEnum.Stop
        };
        scroll.SetAnchorsPreset(LayoutPreset.FullRect);
        mount.AddChild(scroll);

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
        list.AddThemeConstantOverride("separation", 10);
        mask.AddChild(list);

        NScrollbar scrollbar = NLoadoutNativeScrollbar.Create();
        scrollbar.Name = "Scrollbar";
        scrollbar.CustomMinimumSize = new Vector2(NLoadoutNativeScrollbar.Width, 0f);
        scrollbar.SetAnchorsPreset(LayoutPreset.RightWide);
        scrollbar.OffsetLeft = -NLoadoutNativeScrollbar.Width;
        scrollbar.OffsetTop = NLoadoutNativeScrollbar.EndCapSize;
        scrollbar.OffsetBottom = -NLoadoutNativeScrollbar.EndCapSize;
        scroll.AddChild(scrollbar);
        scroll.DisableScrollingIfContentFits();

        _scroll = scroll;
        _list = list;
        Callable.From(() => scroll.SetContent(list)).CallDeferred();
    }

    private void EnsureBackButton()
    {
        Control? mount = GetNodeOrNull<Control>("%BackButtonMount");
        if (mount is null)
            return;
        _backButton = NLoadoutBackButtonFactory.Create();
        _backButton.Name = "BackButton";
        _backButton.Connect(
            NClickableControl.SignalName.Released,
            Callable.From<NClickableControl>(_ => NLoadoutPanelRoot.Instance?.CloseTopScreen()));
        mount.AddChild(_backButton);
    }

    private void RebuildLibrary()
    {
        if (_list is null || _lobby is null)
            return;

        CaptureFocus();
        foreach (Node child in _list.GetChildren())
        {
            _list.RemoveChild(child);
            child.QueueFree();
        }
        _rows.Clear();

        if (_lobby.NetService.Type == NetGameType.Client
            && CustomRunLobbyService.GetRemoteDefinition() is { } remote)
        {
            _list.AddChild(CreateSectionLabel("LOBBY CUSTOM RUN"));
            AddDefinitionRow(remote, isLobbyDefinition: true);
            _list.AddChild(CreateSectionLabel("YOUR CUSTOM RUNS"));
        }

        foreach (CustomRunDefinition definition in CustomRunStorageService.GetDefinitions())
            AddDefinitionRow(definition, isLobbyDefinition: false);

        AddNewRow();
        Callable.From(FinalizeRebuild).CallDeferred();
    }

    private void AddDefinitionRow(CustomRunDefinition definition, bool isLobbyDefinition)
    {
        if (_list is null || _lobby is null)
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
            isLobbyDefinition ? null : () => TaskHelper.RunSafely(PlayAsync(captured)),
            isLobbyDefinition ? "VIEW" : "EDIT",
            () => OpenEditor(captured, isLobbyDefinition),
            ShowDelete: !isLobbyDefinition,
            DeleteAction: isLobbyDefinition ? null : () => TaskHelper.RunSafely(DeleteAsync(captured)),
            TrailingLabel: "EXPORT",
            TrailingAction: () => Export(captured),
            PrimaryEnabled: canPlay));
        _list.AddChild(row);
        _rows.Add((isLobbyDefinition ? $"host:{captured.Id}" : captured.Id, row));
    }

    private void AddNewRow()
    {
        if (_list is null)
            return;

        NCustomRunLibraryRow row = new();
        row.Init(new CustomRunLibraryRowOptions(
            "CREATE A CUSTOM RUN",
            "Start with native defaults, then choose the setup values you want to override.",
            "+ NEW CUSTOM RUN",
            NewRun,
            null,
            null,
            ShowDelete: false,
            DeleteAction: null,
            TrailingLabel: "IMPORT",
            TrailingAction: Import));
        _list.AddChild(row);
        _rows.Add(("new", row));
    }

    private void FinalizeRebuild()
    {
        if (GodotObject.IsInstanceValid(_scroll) && GodotObject.IsInstanceValid(_list))
            _scroll.SetContent(_list);
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
                else if (_backButton is not null)
                {
                    action.FocusNeighborBottom = _backButton.GetPath();
                }
            }
        }

        if (_backButton is not null && _rows.Count > 0 && _rows[^1].Row.Actions.Count > 0)
            _backButton.FocusNeighborTop = _rows[^1].Row.Actions[0].GetPath();
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
        NModalContainer? modalContainer = NModalContainer.Instance;
        if (modalContainer is null
            || !GodotObject.IsInstanceValid(modalContainer)
            || modalContainer.OpenModal is not null)
        {
            SetStatus("The delete confirmation is unavailable while another popup is open.", success: false);
            return;
        }

        NGenericPopup? popup = NGenericPopup.Create();
        if (popup is null)
        {
            SetStatus("Could not open the delete confirmation.", success: false);
            return;
        }

        LocString body = new("settings_ui", "LOADOUT-DELETE_CUSTOM_RUN_CONFIRM_BODY.title");
        body.Add("Name", definition.Name);
        modalContainer.Add(popup);
        bool confirmed = await popup.WaitForConfirmation(
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

    private async Task PlayAsync(CustomRunDefinition definition)
    {
        if (_lobby is null || _launching)
            return;
        if (_lobby.NetService.Type == NetGameType.Client)
        {
            SetStatus("Only the host can Play a profile-local Custom Run in this lobby.", success: false);
            return;
        }

        CustomRunDefinition saved = CustomRunStorageService.Upsert(definition);
        CustomRunCompileResult compiled = CustomRunCompiler.Compile(saved, _lobby);
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
        NButton embark = _sourceConfirmButton;
        Callable.From(embark.ForceClick).CallDeferred();
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
