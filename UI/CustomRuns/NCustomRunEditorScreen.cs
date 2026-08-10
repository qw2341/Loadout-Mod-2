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
using Loadout.Services.CustomRuns.Registry;
using Loadout.UI.Screens.Controls;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Entities.UI;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;

public partial class NCustomRunEditorScreen : Control
{
    private const string ScenePath = "res://UI/CustomRuns/CustomRunEditorScreen.tscn";
    private static readonly string[] TabNames =
    [
        "Overview", "Run Setup", "Roles & Choices", "Rules", "Variables"
    ];

    private StartRunLobby? _lobby;
    private CustomRunDefinition? _workingDefinition;
    private VBoxContainer? _savedList;
    private HBoxContainer? _toolbar;
    private HBoxContainer? _tabs;
    private readonly Dictionary<string, Button> _tabButtons = new(StringComparer.Ordinal);
    private VBoxContainer? _contentHost;
    private NScrollableContainer? _contentScroll;
    private MegaLabel? _runNameLabel;
    private MegaLabel? _authorityLabel;
    private MegaLabel? _statusLabel;
    private NLoadoutSettingsActionButton? _duplicateButton;
    private NConfirmButton? _confirmButton;
    private NLoadoutActionButton? _deleteButton;
    private string _activeTab = TabNames[0];
    private string? _deleteConfirmationId;
    private bool _readOnly;
    private bool _dirty;
    private bool _loadingFields;
    private bool _staticUiBuilt;
    private bool _discardPromptOpen;
    private StringName _returnRoute = "CustomRunLibraryScreen";

    public static void OpenFromLibrary(
        Control libraryScreen,
        StartRunLobby lobby,
        string definitionId,
        bool readOnly,
        StringName returnRoute)
    {
        CustomRunDefinition? definition = readOnly
            ? CustomRunLobbyService.GetRemoteDefinition()
            : CustomRunStorageService.GetDefinitions().FirstOrDefault(candidate =>
                string.Equals(candidate.Id, definitionId, StringComparison.Ordinal));
        if (definition is null || !string.Equals(definition.Id, definitionId, StringComparison.Ordinal))
        {
            GD.PushWarning($"Loadout Custom Run: definition '{definitionId}' was not available for editing.");
            return;
        }

        NLoadoutPanelRoot? root = NLoadoutPanelRoot.GetOrAttach(libraryScreen.GetTree());
        if (root is null)
            return;

        NCustomRunEditorScreen? screen = root.GetNodeOrNull<NCustomRunEditorScreen>(
            "ScreenStack/CustomRunEditorScreen");
        if (screen is null)
        {
            screen = Create();
            screen.Name = "CustomRunEditorScreen";
        }

        screen.Init(lobby, definition, readOnly, returnRoute);
        root.OpenScreen(screen);
    }

    public static void CloseForLobby(StartRunLobby lobby)
    {
        NCustomRunEditorScreen? screen = NLoadoutPanelRoot.Instance?
            .GetNodeOrNull<NCustomRunEditorScreen>("ScreenStack/CustomRunEditorScreen");
        screen?.DetachLobby(lobby);
    }

    public static NCustomRunEditorScreen Create()
    {
        if (ResourceLoader.Exists(ScenePath)
            && GD.Load<PackedScene>(ScenePath) is { } scene
            && scene.Instantiate<NCustomRunEditorScreen>() is { } screen)
        {
            return screen;
        }

        GD.PushWarning($"Loadout Custom Run: could not load '{ScenePath}'. Using a script-only editor.");
        return new NCustomRunEditorScreen();
    }

    public void Init(
        StartRunLobby lobby,
        CustomRunDefinition definition,
        bool readOnly,
        StringName returnRoute)
    {
        _lobby = lobby;
        _readOnly = readOnly;
        _workingDefinition = CustomRunNormalizationService.Clone(definition);
        _returnRoute = returnRoute;
        _dirty = false;
        _activeTab = TabNames[0];

        if (IsNodeReady())
            RefreshForLobby();
    }

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        ZIndex = 120;
        CustomRunStorageService.Register();
        CustomRunRegistry.EnsureBuiltInsRegistered();
        CustomRunStorageService.Changed += OnStoredDefinitionsChanged;
        CustomRunLobbyService.RemoteDefinitionChanged += OnRemoteDefinitionChanged;
        BindSceneNodes();
        BuildStaticUi();
        RefreshForLobby();
    }

    public override void _Notification(int what)
    {
        if (what != NotificationVisibilityChanged || !IsNodeReady())
            return;

        if (Visible)
            RefreshForLobby();
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
        if (!_contentScroll.GetGlobalRect().HasPoint(pointer))
            return;

        _contentScroll._GuiInput(inputEvent);
        GetViewport().SetInputAsHandled();
    }

    public override void _ExitTree()
    {
        CustomRunStorageService.Changed -= OnStoredDefinitionsChanged;
        CustomRunLobbyService.RemoteDefinitionChanged -= OnRemoteDefinitionChanged;
    }

    private void BindSceneNodes()
    {
        EnsureFallbackScene();
        _tabs = GetNodeOrNull<HBoxContainer>("%Tabs");
        EnsureNativeContentScroll();
    }

    private void BuildStaticUi()
    {
        if (_staticUiBuilt)
            return;
        _staticUiBuilt = true;

        Control? titleMount = GetNodeOrNull<Control>("OuterMargin/Root/Header/TitleMount");
        if (titleMount is not null)
        {
            HBoxContainer titleRow = new()
            {
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                MouseFilter = MouseFilterEnum.Ignore
            };
            titleRow.AddThemeConstantOverride("separation", 22);
            titleRow.SetAnchorsPreset(LayoutPreset.FullRect);
            MegaLabel title = CreateLabel("CUSTOM RUN EDITOR", 42, StsColors.gold, HorizontalAlignment.Left);
            title.CustomMinimumSize = new Vector2(430f, 0f);
            titleRow.AddChild(title);
            _runNameLabel = CreateLabel(string.Empty, 31, StsColors.cream, HorizontalAlignment.Left);
            _runNameLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            _runNameLabel.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            titleRow.AddChild(_runNameLabel);
            titleMount.AddChild(titleRow);
        }

        Control? duplicateMount = GetNodeOrNull<Control>("OuterMargin/Root/Header/DuplicateMount");
        if (duplicateMount is not null)
        {
            _duplicateButton = new NLoadoutSettingsActionButton
            {
                Name = "DuplicateButton",
                CustomMinimumSize = new Vector2(260f, 58f)
            };
            _duplicateButton.Init("duplicate", "DUPLICATE");
            _duplicateButton.SetAnchorsPreset(LayoutPreset.CenterRight);
            _duplicateButton.OffsetLeft = -260f;
            _duplicateButton.OffsetTop = -29f;
            _duplicateButton.OffsetRight = 0f;
            _duplicateButton.OffsetBottom = 29f;
            _duplicateButton.Connect(
                NClickableControl.SignalName.Released,
                Callable.From<NClickableControl>(_ => DuplicateDefinition()));
            duplicateMount.AddChild(_duplicateButton);
        }

        BuildTabs();
        EnsureBackButton();
        EnsureConfirmButton();

        Control? statusMount = GetNodeOrNull<Control>("OuterMargin/Root/StatusMount");
        if (statusMount is not null)
        {
            _statusLabel = CreateLabel("Ready.", 20, StsColors.cream, HorizontalAlignment.Left);
            _statusLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            _statusLabel.SetAnchorsPreset(LayoutPreset.FullRect);
            statusMount.AddChild(_statusLabel);
        }
    }

    private void BuildToolbar()
    {
        if (_toolbar is null)
            return;

        AddActionButton(_toolbar, "save", "Save", 118f, () => SaveCurrent(showStatus: true));
        AddActionButton(_toolbar, "duplicate", "Duplicate", 150f, DuplicateDefinition);
        AddActionButton(_toolbar, "validate", "Validate", 138f, ValidateDefinition);
    }

    private void BuildTabs()
    {
        if (_tabs is null)
            return;

        _tabButtons.Clear();
        foreach (string tabName in TabNames)
        {
            Button button = CreateCompactButton(tabName, 23, 0f);
            button.Name = tabName.Replace(" ", string.Empty).Replace("&", "And") + "Tab";
            button.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            button.Pressed += () => SelectTab(tabName);
            _tabs.AddChild(button);
            _tabButtons[tabName] = button;
        }
        RefreshTabVisuals();
    }

    private void EnsureBackButton()
    {
        Control? mount = GetNodeOrNull<Control>("%BackButtonMount");
        if (mount is null || mount.GetNodeOrNull<NBackButton>("BackButton") is not null)
            return;

        NBackButton backButton = NLoadoutBackButtonFactory.Create();
        backButton.Name = "BackButton";
        backButton.Connect(
            NClickableControl.SignalName.Released,
            Callable.From<NClickableControl>(_ => TaskHelper.RunSafely(TryCloseEditorAsync())));
        mount.AddChild(backButton);
        Callable.From(() =>
        {
            if (GodotObject.IsInstanceValid(backButton))
                backButton.Enable();
        }).CallDeferred();
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
        Callable.From(() =>
        {
            if (GodotObject.IsInstanceValid(_confirmButton))
                _confirmButton.Enable();
        }).CallDeferred();
    }

    private void RefreshForLobby()
    {
        if (!_staticUiBuilt || _lobby is null)
            return;

        RefreshRunName();
        RefreshTabVisuals();

        RebuildContent();
        RefreshEditableState();
    }

    private void RefreshRunName()
    {
        if (_runNameLabel is null)
            return;

        string name = _workingDefinition?.Name?.Trim() ?? string.Empty;
        _runNameLabel.Text = string.IsNullOrWhiteSpace(name) ? "UNTITLED CUSTOM RUN" : name;
        _runNameLabel.TooltipText = _runNameLabel.Text;
    }

    private void RefreshSavedList()
    {
        if (_savedList is null)
            return;
        ClearChildren(_savedList);

        if (_readOnly)
        {
            if (_workingDefinition is null)
            {
                AddSidebarMessage("The host has not applied a Custom Run definition.");
                return;
            }

            Button remote = CreateSavedDefinitionButton(_workingDefinition, selected: true);
            remote.Disabled = true;
            _savedList.AddChild(remote);
            return;
        }

        IReadOnlyList<CustomRunDefinition> definitions = CustomRunStorageService.GetDefinitions();
        if (definitions.Count == 0)
        {
            AddSidebarMessage("No saved Custom Runs.");
            return;
        }

        foreach (CustomRunDefinition definition in definitions)
        {
            bool selected = string.Equals(_workingDefinition?.Id, definition.Id, StringComparison.Ordinal);
            Button button = CreateSavedDefinitionButton(definition, selected);
            button.Pressed += () => LoadDefinition(definition);
            _savedList.AddChild(button);
        }
    }

    private Button CreateSavedDefinitionButton(CustomRunDefinition definition, bool selected)
    {
        Button button = CreateCompactButton(definition.Name, 21, 52f);
        button.Alignment = HorizontalAlignment.Left;
        button.TooltipText = definition.Description;
        button.AddThemeColorOverride(
            "font_color",
            selected ? StsColors.gold : StsColors.cream);
        return button;
    }

    private void AddSidebarMessage(string text)
    {
        if (_savedList is null)
            return;
        MegaLabel label = CreateLabel(text, 19, new Color(0.78f, 0.8f, 0.86f), HorizontalAlignment.Left);
        label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        label.CustomMinimumSize = new Vector2(0f, 90f);
        _savedList.AddChild(label);
    }

    private void SelectTab(string tabName)
    {
        _activeTab = tabName;
        _deleteConfirmationId = null;
        ResetDeleteButton();
        RefreshTabVisuals();
        RebuildContent();
    }

    private void RefreshTabVisuals()
    {
        foreach ((string tabName, Button button) in _tabButtons)
        {
            Color normalColor = string.Equals(tabName, _activeTab, StringComparison.Ordinal)
                ? StsColors.gold
                : StsColors.cream;
            button.AddThemeColorOverride("font_color", normalColor);
            button.AddThemeColorOverride("font_pressed_color", StsColors.gold);
            button.AddThemeColorOverride("font_hover_color", StsColors.gold);
            button.AddThemeColorOverride("font_focus_color", StsColors.gold);
        }
    }

    private void RebuildContent()
    {
        if (_contentHost is null)
            return;
        ClearChildren(_contentHost);

        if (_workingDefinition is null)
        {
            MegaLabel empty = CreateLabel(
                _readOnly ? "Waiting for the host's Custom Run setup." : "Create or select a Custom Run.",
                28,
                StsColors.cream,
                HorizontalAlignment.Center);
            empty.CustomMinimumSize = new Vector2(0f, 260f);
            _contentHost.AddChild(empty);
            RefreshContentLayoutDeferred();
            return;
        }

        _loadingFields = true;
        try
        {
            switch (_activeTab)
            {
                case "Overview":
                    BuildOverviewPanel();
                    break;
                case "Run Setup":
                    BuildRunSetupPanel();
                    break;
                default:
                    BuildFoundationPanel(_activeTab);
                    break;
            }
        }
        finally
        {
            _loadingFields = false;
        }

        if (_readOnly)
            SetEditableRecursive(_contentHost, editable: false);
        RefreshContentLayoutDeferred();
    }

    private void RefreshContentLayoutDeferred()
    {
        Callable.From(() =>
        {
            if (GodotObject.IsInstanceValid(_contentScroll)
                && GodotObject.IsInstanceValid(_contentHost))
            {
                _contentScroll.SetContent(_contentHost);
                ResizeContentToChildren();
                _contentScroll.InstantlyScrollToTop();
            }
        }).CallDeferred();
    }

    private void BuildOverviewPanel()
    {
        if (_contentHost is null || _workingDefinition is null)
            return;

        _contentHost.AddChild(CreateSectionTitle("OVERVIEW"));
        _contentHost.AddChild(CreateFieldLabel("Name"));
        LineEdit name = CreateLineEdit(_workingDefinition.Name);
        name.TextChanged += value =>
        {
            if (_loadingFields || _workingDefinition is null)
                return;
            _workingDefinition.Name = value;
            RefreshRunName();
            MarkDirty();
        };
        _contentHost.AddChild(name);

        _contentHost.AddChild(CreateFieldLabel("Description"));
        TextEdit description = new()
        {
            Text = _workingDefinition.Description,
            CustomMinimumSize = new Vector2(0f, 190f),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            WrapMode = TextEdit.LineWrappingMode.Boundary,
            MouseFilter = MouseFilterEnum.Stop
        };
        StyleTextEdit(description);
        description.TextChanged += () =>
        {
            if (_loadingFields || _workingDefinition is null)
                return;
            _workingDefinition.Description = description.Text;
            MarkDirty();
        };
        _contentHost.AddChild(description);

        VBoxContainer summary = new() { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        summary.AddThemeConstantOverride("separation", 10);
        AddSummaryRow(summary, "Definition ID", _workingDefinition.Id);
        AddSummaryRow(summary, "Schema", _workingDefinition.SchemaVersion.ToString());
        AddSummaryRow(summary, "Rules", _workingDefinition.Rules.Count.ToString());
        AddSummaryRow(summary, "Roles / Choices", $"{_workingDefinition.Roles.Count} / {_workingDefinition.PlayerChoices.Count}");
        AddSummaryRow(summary, "Variables", _workingDefinition.Variables.Count.ToString());
        _contentHost.AddChild(summary);
    }

    private void BuildRunSetupPanel()
    {
        if (_contentHost is null || _workingDefinition is null)
            return;

        RunSetupDefinition setup = _workingDefinition.Setup;
        _contentHost.AddChild(CreateSectionTitle("RUN SETUP"));
        _contentHost.AddChild(CreateHint(
            "Unchecked numeric rows use the character or game's normal value. Changing a number enables its override."));

        HBoxContainer characterRow = CreateRow();
        characterRow.AddChild(CreateRowLabel("Character"));
        NSelectFilterDropdown character = CreateCharacterSelector(setup.Character);
        character.SelectedItemChanged += modelId =>
        {
            if (_loadingFields || _workingDefinition is null)
                return;
            ApplyCharacterSelection(_workingDefinition.Setup.Character, modelId);
            MarkDirty();
        };
        characterRow.AddChild(character);
        _contentHost.AddChild(characterRow);

        HBoxContainer seedRow = CreateRow();
        seedRow.AddChild(CreateRowLabel("Run Seed"));
        LineEdit seed = CreateLineEdit(setup.RunSeed ?? string.Empty);
        seed.PlaceholderText = "Game default / random";
        seed.CustomMinimumSize = new Vector2(360f, 44f);
        seed.SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
        seed.TextChanged += value =>
        {
            if (_loadingFields || _workingDefinition is null)
                return;
            _workingDefinition.Setup.RunSeed = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            MarkDirty();
        };
        seedRow.AddChild(seed);
        _contentHost.AddChild(seedRow);

        AddNullableNumberRow("Starting Gold", setup.StartingGold, 99, 0, 999999,
            value => setup.StartingGold = value);
        AddNullableNumberRow("Starting Max HP", setup.StartingMaxHp, 80, 1, 99999,
            value => setup.StartingMaxHp = value);
        AddNullableNumberRow("Starting Current HP", setup.StartingCurrentHp, 80, 1, 99999,
            value => setup.StartingCurrentHp = value);
        AddNullableNumberRow("Potion Slots", setup.PotionSlots, 3, 0, 20,
            value => setup.PotionSlots = value);
        AddNullableNumberRow("Base Energy / Turn", setup.BaseEnergyPerTurn, 3, 0, 99,
            value => setup.BaseEnergyPerTurn = value);
        AddNullableNumberRow("Cards Drawn / Turn", setup.CardsDrawnPerTurn, 5, 0, 99,
            value => setup.CardsDrawnPerTurn = value);
    }

    private void BuildFoundationPanel(string tabName)
    {
        if (_contentHost is null || _workingDefinition is null)
            return;

        int count = tabName switch
        {
            "Roles & Choices" => _workingDefinition.Roles.Count + _workingDefinition.PlayerChoices.Count,
            "Rules" => _workingDefinition.Rules.Count,
            "Variables" => _workingDefinition.Variables.Count,
            _ => 0
        };
        _contentHost.AddChild(CreateSectionTitle(tabName.ToUpperInvariant()));
        _contentHost.AddChild(CreateHint(
            "The authoring model, stable IDs, validation, extension registry, storage, and share codec are active. " +
            "This editor panel is reserved for the next implementation phase."));
        MegaLabel countLabel = CreateLabel(
            tabName == "Permanent Rules"
                ? "Permanent Rules library: not yet populated"
                : $"Definitions currently stored here: {count}",
            26,
            StsColors.cream,
            HorizontalAlignment.Center);
        countLabel.CustomMinimumSize = new Vector2(0f, 210f);
        _contentHost.AddChild(countLabel);
    }

    private void AddNullableNumberRow(
        string label,
        int? current,
        int fallback,
        int minimum,
        int maximum,
        Action<int?> setter)
    {
        if (_contentHost is null)
            return;

        HBoxContainer row = CreateRow();
        NLoadoutToggle toggle = new() { CustomMinimumSize = new Vector2(390f, 50f) };
        toggle.Init(label.ToLowerInvariant().Replace(' ', '_'), label, current.HasValue);
        row.AddChild(toggle);

        NLoadoutNumberStepper stepper = new();
        stepper.Init(current ?? fallback, minimum, maximum);
        stepper.ValueChanged += value =>
        {
            if (_loadingFields)
                return;
            toggle.SetChecked(true);
            setter(value);
            MarkDirty();
        };
        toggle.Connect(
            NLoadoutToggle.SignalName.Toggled,
            Callable.From<NLoadoutToggle>(changed =>
            {
                if (_loadingFields)
                    return;
                setter(changed.IsChecked ? stepper.Value : null);
                MarkDirty();
            }));
        row.AddChild(stepper);
        _contentHost.AddChild(row);
    }

    private NSelectFilterDropdown CreateCharacterSelector(SelectionSpec selection)
    {
        string fixedId = selection.FixedModelIds.FirstOrDefault() ?? string.Empty;
        string selectedId = string.Empty;
        List<LoadoutDropdownOption> options =
        [
            new LoadoutDropdownOption(string.Empty, "Game Default")
        ];
        foreach (CharacterModel character in ModelDb.AllCharacters.OrderBy(GetCharacterDisplayName, StringComparer.OrdinalIgnoreCase))
        {
            string modelId = character.Id.ToString();
            options.Add(new LoadoutDropdownOption(modelId, GetCharacterDisplayName(character)));
            if (selection.Mode == SelectionMode.Fixed
                && (string.Equals(modelId, fixedId, StringComparison.Ordinal)
                    || string.Equals(character.Id.Entry, fixedId, StringComparison.OrdinalIgnoreCase)))
            {
                selectedId = modelId;
            }
        }

        NSelectFilterDropdown dropdown = new()
        {
            CustomMinimumSize = new Vector2(420f, 52f),
            SizeFlagsHorizontal = SizeFlags.ShrinkEnd,
            MouseFilter = MouseFilterEnum.Stop,
            FocusMode = FocusModeEnum.All,
            DropdownWidth = 420f,
            ButtonHeight = 52f,
            MaxVisibleItems = 7,
            LabelMinFontSize = 18,
            LabelMaxFontSize = 24,
            ItemFontSize = 22,
            ExpandToAvailableWidth = false
        };
        dropdown.SetItems(string.Empty, options, selectedId);
        return dropdown;
    }

    private static void ApplyCharacterSelection(SelectionSpec selection, string modelId)
    {
        selection.Kind = SelectionModelKind.Character;
        selection.FixedModelIds.Clear();
        if (string.IsNullOrWhiteSpace(modelId))
        {
            selection.Mode = SelectionMode.Default;
            return;
        }
        selection.Mode = SelectionMode.Fixed;
        selection.FixedModelIds.Add(modelId);
    }

    private static string GetCharacterDisplayName(CharacterModel character)
    {
        try
        {
            return new LocString("characters", character.CharacterSelectTitle).GetFormattedText();
        }
        catch
        {
            return character.Id.Entry;
        }
    }

    private void NewDefinition()
    {
        if (_readOnly)
            return;
        SaveCurrent(showStatus: false);
        LoadDefinition(CustomRunStorageService.CreateNew());
        SetStatus("Created a new Custom Run.", success: true);
    }

    private void DuplicateDefinition()
    {
        if (_workingDefinition is null)
            return;

        _workingDefinition = CustomRunStorageService.Duplicate(_workingDefinition);
        _readOnly = false;
        _dirty = false;
        RefreshRunName();
        RebuildContent();
        RefreshEditableState();
        SetStatus($"Saved as '{_workingDefinition.Name}'.", success: true);
    }

    private void DeleteDefinition()
    {
        if (_readOnly || _workingDefinition is null)
            return;

        if (!string.Equals(_deleteConfirmationId, _workingDefinition.Id, StringComparison.Ordinal))
        {
            _deleteConfirmationId = _workingDefinition.Id;
            _deleteButton?.Init("delete", "Confirm Delete");
            SetStatus($"Press Confirm Delete to remove '{_workingDefinition.Name}'.", success: false);
            return;
        }

        string deletedName = _workingDefinition.Name;
        CustomRunStorageService.Delete(_workingDefinition.Id);
        _workingDefinition = CustomRunStorageService.GetDefinitions().FirstOrDefault()
                             ?? CustomRunStorageService.CreateNew();
        _deleteConfirmationId = null;
        ResetDeleteButton();
        _dirty = false;
        RefreshSavedList();
        RebuildContent();
        SetStatus($"Deleted '{deletedName}'.", success: true);
    }

    private void LoadDefinition(CustomRunDefinition definition)
    {
        if (_readOnly)
            return;
        SaveCurrent(showStatus: false);
        _workingDefinition = CustomRunNormalizationService.Clone(definition);
        _dirty = false;
        _deleteConfirmationId = null;
        ResetDeleteButton();
        RefreshSavedList();
        RebuildContent();
        SetStatus($"Editing '{_workingDefinition.Name}'.", success: true);
    }

    private void SaveCurrent(bool showStatus)
    {
        if (_readOnly || _workingDefinition is null)
            return;
        _workingDefinition = CustomRunStorageService.Upsert(_workingDefinition);
        _dirty = false;
        RefreshRunName();
        if (showStatus)
            SetStatus($"Saved '{_workingDefinition.Name}'.", success: true);
    }

    private void ImportDefinition()
    {
        if (_readOnly)
            return;
        if (!CustomRunClipboardService.TryImport(out CustomRunDefinition definition, out string error))
        {
            SetStatus(error, success: false);
            return;
        }

        CustomRunValidationResult validation = CustomRunValidator.Validate(definition);
        LoadDefinition(CustomRunStorageService.Import(definition));
        SetStatus(
            validation.IsValid
                ? "Imported and validated the Custom Run."
                : $"Imported for editing with {validation.Issues.Count} validation issue(s).",
            validation.IsValid);
    }

    private void ExportDefinition()
    {
        if (_workingDefinition is null)
            return;
        if (!_readOnly)
            SaveCurrent(showStatus: false);
        if (CustomRunClipboardService.Copy(_workingDefinition, out string error))
            SetStatus("Copied an L2CR1 share string to the clipboard.", success: true);
        else
            SetStatus(error, success: false);
    }

    private void ValidateDefinition()
    {
        if (_workingDefinition is null || _lobby is null)
            return;
        CustomRunCompileResult result = CustomRunCompiler.Compile(_workingDefinition, _lobby);
        if (result.IsValid)
        {
            SetStatus("Validation passed. This definition is ready to Play.", success: true);
            return;
        }

        string summary = string.Join("  |  ", result.Issues.Take(3).Select(issue => $"{issue.Section}: {issue.Message}"));
        if (result.Issues.Count > 3)
            summary += $"  |  +{result.Issues.Count - 3} more";
        SetStatus(summary, success: false);
    }

    private void ApplyToLobby()
    {
        if (_readOnly || _workingDefinition is null || _lobby is null)
            return;
        SaveCurrent(showStatus: false);
        CustomRunValidationResult result = CustomRunValidator.Validate(_workingDefinition);
        if (!result.IsValid)
        {
            SetStatus($"Cannot apply: {result.Issues[0].Section}: {result.Issues[0].Message}", success: false);
            return;
        }

        if (!CustomRunLobbyService.ApplyHostDefinition(_lobby, _workingDefinition, out string error))
        {
            SetStatus(error, success: false);
            return;
        }

        SetStatus(
            _lobby.NetService.Type == NetGameType.Host
                ? "Applied and synchronized this editor definition to lobby clients."
                : "Applied this editor definition to the singleplayer lobby.",
            success: true);
    }

    private void SaveAndClose()
    {
        if (!_readOnly)
            SaveCurrent(showStatus: false);
        CloseEditorWithoutSaving();
    }

    private async Task TryCloseEditorAsync()
    {
        if (_discardPromptOpen)
            return;
        if (_readOnly || !_dirty)
        {
            CloseEditorWithoutSaving();
            return;
        }

        _discardPromptOpen = true;
        try
        {
            LocString body = new("settings_ui", "LOADOUT-CUSTOM_RUN_UNSAVED_BODY.title");
            body.Add("Name", _workingDefinition?.Name ?? string.Empty);
            bool discard = await WaitForDiscardConfirmation(
                body,
                new LocString("settings_ui", "LOADOUT-CUSTOM_RUN_UNSAVED_TITLE.title"),
                new LocString("settings_ui", "LOADOUT-CUSTOM_RUN_UNSAVED_CANCEL.title"),
                new LocString("settings_ui", "LOADOUT-CUSTOM_RUN_UNSAVED_DISCARD.title"));
            if (discard)
                CloseEditorWithoutSaving();
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

        if (modalContainer.OpenModal is GodotObject openModal
            && !GodotObject.IsInstanceValid(openModal))
        {
            modalContainer.Clear();
        }
        if (modalContainer.OpenModal is not null)
            return false;

        NGenericPopup? popup = NGenericPopup.Create();
        if (popup is null)
        {
            SetStatus("Could not open the unsaved-changes warning.", success: false);
            return false;
        }

        IDisposable? modalLease = NLoadoutPanelRoot.Instance?.HostNativeModal(modalContainer);
        try
        {
            modalContainer.Add(popup);
            return await popup.WaitForConfirmation(body, title, cancelText, discardText);
        }
        finally
        {
            if (GodotObject.IsInstanceValid(modalContainer))
                modalContainer.Clear();
            modalLease?.Dispose();
        }
    }

    private void CloseEditorWithoutSaving()
    {
        if (_returnRoute.IsEmpty)
            _returnRoute = "CustomRunLibraryScreen";
        NLoadoutPanelRoot.Instance?.CloseTopScreen();
    }

    private void DetachLobby(StartRunLobby lobby)
    {
        if (!ReferenceEquals(_lobby, lobby))
            return;
        NLoadoutPanelRoot.Instance?.CloseScreen(Name);
        _lobby = null;
        _workingDefinition = null;
        _dirty = false;
    }

    private void OnStoredDefinitionsChanged()
    {
        if (!_readOnly && IsNodeReady() && !_dirty && _workingDefinition is not null)
        {
            CustomRunDefinition? stored = CustomRunStorageService.GetDefinitions()
                .FirstOrDefault(definition => string.Equals(definition.Id, _workingDefinition.Id, StringComparison.Ordinal));
            if (stored is not null)
                _workingDefinition = stored;
        }
    }

    private void OnRemoteDefinitionChanged()
    {
        if (!_readOnly || !IsNodeReady())
            return;
        CustomRunDefinition? remote = CustomRunLobbyService.GetRemoteDefinition();
        if (remote is null || !string.Equals(remote.Id, _workingDefinition?.Id, StringComparison.Ordinal))
            return;
        _workingDefinition = remote;
        _dirty = false;
        RebuildContent();
    }

    private void MarkDirty()
    {
        if (_loadingFields || _readOnly)
            return;
        _dirty = true;
        _deleteConfirmationId = null;
        ResetDeleteButton();
        SetStatus("Unsaved changes.", success: true);
    }

    private void RefreshEditableState()
    {
        _duplicateButton?.Enable();
        _confirmButton?.Enable();
        if (_deleteButton is not null)
        {
            if (_readOnly)
                _deleteButton.Disable();
            else
                _deleteButton.Enable();
        }
    }

    private void ResetDeleteButton()
    {
        _deleteButton?.Init("delete", "Delete");
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

    private static void AddSummaryRow(VBoxContainer summary, string label, string value)
    {
        HBoxContainer row = new()
        {
            CustomMinimumSize = new Vector2(0f, 38f),
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        row.AddThemeConstantOverride("separation", 34);
        MegaLabel nameLabel = CreateLabel(label, 20, StsColors.gold, HorizontalAlignment.Left);
        nameLabel.CustomMinimumSize = new Vector2(250f, 38f);
        nameLabel.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        row.AddChild(nameLabel);
        MegaLabel valueLabel = CreateLabel(value, 20, StsColors.cream, HorizontalAlignment.Left);
        valueLabel.CustomMinimumSize = new Vector2(0f, 38f);
        valueLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        valueLabel.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        valueLabel.AutowrapMode = TextServer.AutowrapMode.Off;
        valueLabel.ClipText = true;
        valueLabel.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        valueLabel.TooltipText = value;
        row.AddChild(valueLabel);
        summary.AddChild(row);
    }

    private static HBoxContainer CreateRow()
    {
        HBoxContainer row = new()
        {
            CustomMinimumSize = new Vector2(0f, 54f),
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        row.AddThemeConstantOverride("separation", 16);
        return row;
    }

    private static MegaLabel CreateRowLabel(string text)
    {
        MegaLabel label = CreateLabel(text, 23, StsColors.gold, HorizontalAlignment.Left);
        label.CustomMinimumSize = new Vector2(390f, 50f);
        label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        return label;
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
        label.CustomMinimumSize = new Vector2(0f, 64f);
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
        label.AddThemeFontOverride("font", LoadFont("res://themes/kreon_bold_glyph_space_one.tres"));
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

    private static void StyleTextEdit(TextEdit edit)
    {
        ApplyInputFont(edit, 21);
        edit.AddThemeColorOverride("font_color", StsColors.cream);
        edit.AddThemeColorOverride("font_focus_color", StsColors.gold);
    }

    private static Button CreateCompactButton(string text, int fontSize, float minimumHeight)
    {
        Button button = new()
        {
            Text = text,
            CustomMinimumSize = new Vector2(0f, minimumHeight),
            FocusMode = FocusModeEnum.All,
            MouseFilter = MouseFilterEnum.Stop
        };
        ApplyInputFont(button, fontSize);
        button.AddThemeColorOverride("font_color", StsColors.cream);
        button.AddThemeColorOverride("font_hover_color", StsColors.gold);
        button.AddThemeColorOverride("font_focus_color", StsColors.gold);
        return button;
    }

    private static NLoadoutActionButton AddActionButton(
        Control parent,
        string id,
        string label,
        float width,
        Action action)
    {
        NLoadoutActionButton button = new()
        {
            CustomMinimumSize = new Vector2(width, 44f)
        };
        button.Init(id, label);
        button.Connect(
            NClickableControl.SignalName.Released,
            Callable.From<NClickableControl>(_ => action()));
        parent.AddChild(button);
        return button;
    }

    private static void ApplyInputFont(Control control, int size)
    {
        control.AddThemeFontOverride("font", LoadFont("res://themes/kreon_bold_glyph_space_one.tres"));
        control.AddThemeFontSizeOverride("font_size", size);
    }

    private static Font? LoadFont(string path)
    {
        return ResourceLoader.Exists(path) ? GD.Load<Font>(path) : null;
    }

    private static void SetEditableRecursive(Node node, bool editable)
    {
        switch (node)
        {
            case NLoadoutDropdown dropdown:
                dropdown.SetEnabled(editable);
                dropdown.FocusMode = editable ? FocusModeEnum.All : FocusModeEnum.None;
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

    private void EnsureNativeContentScroll()
    {
        Control? contentMount = GetNodeOrNull<Control>("%ContentMount");
        if (contentMount is null)
            return;

        _contentScroll = contentMount.GetNodeOrNull<NScrollableContainer>("ContentScroll");
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
        contentMount.AddChild(scroll);
        scroll.DisableScrollingIfContentFits();

        _contentScroll = scroll;
        _contentHost = content;
        Callable.From(() =>
        {
            if (GodotObject.IsInstanceValid(scroll) && GodotObject.IsInstanceValid(content))
                scroll.SetContent(content);
        }).CallDeferred();
    }

    private void ResizeContentToChildren()
    {
        if (_contentScroll is null
            || _contentHost is null
            || !GodotObject.IsInstanceValid(_contentScroll)
            || !GodotObject.IsInstanceValid(_contentHost))
        {
            return;
        }

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

        Control root = new()
        {
            Name = "ContentMount",
            UniqueNameInOwner = true,
            Position = new Vector2(180f, 120f),
            Size = new Vector2(1560f, 820f)
        };
        AddChild(root);
    }
}
