#nullable enable

namespace Loadout.UI.CustomRuns;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using Loadout.Services.CustomRuns.Catalog;
using Loadout.Services.CustomRuns.Compilation;
using Loadout.Services.CustomRuns.Models;
using Loadout.Services.CustomRuns.Networking;
using Loadout.Services.CustomRuns.Persistence;
using Loadout.Services.CustomRuns.PermanentRules;
using Loadout.Services.CustomRuns.Registry;
using Loadout.Services.Compatibility;
using Loadout.Services.Loadouts;
using Loadout.Patches.Cards.CardModification;
using Loadout.Services.RelicModification;
using Loadout.Services.Targets;
using Loadout.PanelItems;
using Loadout.UI.Screens;
using Loadout.UI.Screens.Controls;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Entities.UI;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Nodes.Potions;
using MegaCrit.Sts2.Core.Nodes.Relics;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.RunHistoryScreen;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Models.Characters;

public partial class NCustomRunEditorScreen : Control
{
    private const string ScenePath = "res://UI/CustomRuns/CustomRunEditorScreen.tscn";
    private const string CharacterSelectButtonScenePath =
        "res://scenes/screens/char_select/char_select_button.tscn";
    private const float RunSetupToolButtonWidth = 224f;
    private const double StartingCardHoverDelaySeconds = 0.2d;
    private const string PotionInventoryBackdropPath =
        "res://images/atlases/ui_atlas.sprites/top_bar/top_bar_char_backdrop.tres";
    private static readonly string[] TabNames =
    [
        "Overview", "Run Setup", "Player Choices", "Rules", "Variables"
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
    private IDisposable? _catalogSelectorSession;
    private HFlowContainer? _startingDeckPreview;
    private HFlowContainer? _startingRelicPreview;
    private HBoxContainer? _startingPotionHolders;
    private string? _activeSetupRoleId;

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
        _activeSetupRoleId = null;

        if (IsNodeReady())
            RefreshForLobby();
    }

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        ZIndex = 120;
        CustomRunStorageService.Register();
        PermanentRuleStorageService.Register();
        CustomRunRegistry.EnsureBuiltInsRegistered();
        CustomRunStorageService.Changed += OnStoredDefinitionsChanged;
        PermanentRuleStorageService.Changed += OnPermanentRulesChanged;
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
        CustomRunStorageService.Changed -= OnStoredDefinitionsChanged;
        PermanentRuleStorageService.Changed -= OnPermanentRulesChanged;
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

        RebuildContent(scrollToTop: true);
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
        RebuildContent(scrollToTop: true);
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

    private void RebuildContent(bool scrollToTop = false)
    {
        if (_contentHost is null)
            return;
        _startingDeckPreview = null;
        _startingRelicPreview = null;
        _startingPotionHolders = null;
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
            RefreshContentLayoutDeferred(scrollToTop);
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
                case "Rules":
                    BuildRulesPanel();
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
        RefreshContentLayoutDeferred(scrollToTop);
    }

    private void RefreshContentLayoutDeferred(bool scrollToTop = false)
    {
        Callable.From(() =>
        {
            if (GodotObject.IsInstanceValid(_contentScroll)
                && GodotObject.IsInstanceValid(_contentHost))
            {
                _contentScroll.SetContent(_contentHost);
                ResizeContentToChildren();
                if (scrollToTop)
                    _contentScroll.InstantlyScrollToTop();
            }
        }).CallDeferred();
    }

    private void RefreshContentSizeDeferred()
    {
        Callable.From(() =>
        {
            if (GodotObject.IsInstanceValid(_contentScroll)
                && GodotObject.IsInstanceValid(_contentHost))
            {
                ResizeContentToChildren();
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
        AddSummaryRow(summary, "Roles", _workingDefinition.Roles.Count.ToString());
        AddSummaryRow(summary, "Player Choices", _workingDefinition.PlayerChoices.Count.ToString());
        AddSummaryRow(summary, "Variables", _workingDefinition.Variables.Count.ToString());
        _contentHost.AddChild(summary);
    }

    private void BuildRunSetupPanel()
    {
        if (_contentHost is null || _workingDefinition is null)
            return;

        RunSetupDefinition defaultSetup = _workingDefinition.Setup;
        _contentHost.AddChild(CreateSectionTitle("RUN SETUP"));

        HBoxContainer seedRow = CreateRow();
        seedRow.AddChild(CreateRowLabel("Run Seed"));
        LineEdit seed = CreateLineEdit(defaultSetup.RunSeed ?? string.Empty);
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
        AddNullableNumberRow("Starting Ascension", defaultSetup.StartingAscension, 0, 0, 10,
            value => defaultSetup.StartingAscension = value);

        HBoxContainer assignmentRow = CreateRow();
        assignmentRow.AddChild(CreateRowLabel("Role Assignment"));
        NLoadoutDropdown assignmentMode = new()
        {
            CustomMinimumSize = new Vector2(420f, 52f),
            SizeFlagsHorizontal = SizeFlags.ShrinkEnd,
            DropdownWidth = 420f
        };
        assignmentMode.SetItems(string.Empty,
        [
            new LoadoutDropdownOption(RoleAssignmentMode.PlayersChoose.ToString(), "Players Choose"),
            new LoadoutDropdownOption(RoleAssignmentMode.HostAssigns.ToString(), "Host Assigns"),
            new LoadoutDropdownOption(RoleAssignmentMode.Random.ToString(), "Random on Embark")
        ], _workingDefinition.RoleAssignmentMode.ToString());
        assignmentMode.SelectedItemChanged += selected =>
        {
            if (_loadingFields || _workingDefinition is null
                || !Enum.TryParse(selected, out RoleAssignmentMode mode))
                return;
            _workingDefinition.RoleAssignmentMode = mode;
            MarkDirty();
        };
        assignmentRow.AddChild(assignmentMode);
        _contentHost.AddChild(assignmentRow);
        _contentHost.AddChild(CreateSectionDivider());

        Button noRole = CreateCompactButton(_workingDefinition.DefaultRoleName, 21, 52f);
        noRole.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        if (_activeSetupRoleId is null)
            noRole.AddThemeColorOverride("font_color", StsColors.gold);
        noRole.Pressed += () =>
        {
            _activeSetupRoleId = null;
            RebuildContent();
        };
        _contentHost.AddChild(noRole);

        Button? activeRoleButton = null;
        foreach (RoleDefinition role in _workingDefinition.Roles)
        {
            RoleDefinition capturedRole = role;
            string required = role.MinimumPlayers > 0 ? " *" : string.Empty;
            string maximum = role.MaximumPlayers > 0 ? $"    MAX {role.MaximumPlayers}" : string.Empty;
            Button row = CreateCompactButton(
                $"{role.Name}{required}    MIN {role.MinimumPlayers}{maximum}",
                21,
                52f);
            row.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            if (string.Equals(_activeSetupRoleId, role.Id, StringComparison.Ordinal))
            {
                row.AddThemeColorOverride("font_color", StsColors.gold);
                activeRoleButton = row;
            }
            row.Pressed += () =>
            {
                _activeSetupRoleId = capturedRole.Id;
                RebuildContent();
            };
            _contentHost.AddChild(row);
        }

        HBoxContainer addRoleRow = CreateRow();
        AddSettingsActionButton(addRoleRow, "add_role", "ADD ROLE", 240f, AddRole);
        AddToolSpacer(addRoleRow);
        _contentHost.AddChild(addRoleRow);

        RoleDefinition? activeRole = ResolveRole(_activeSetupRoleId);
        if (_activeSetupRoleId is not null && activeRole is null)
            _activeSetupRoleId = null;
        RunSetupDefinition setup = activeRole?.Setup ?? defaultSetup;
        _contentHost.AddChild(CreateSectionDivider());
        _contentHost.AddChild(CreateSectionTitle("ROLE DETAILS"));
        if (activeRole is not null)
            BuildRoleDetails(activeRole, activeRoleButton);
        else
            BuildDefaultRoleDetails(noRole);

        BuildCharacterRestrictionEditor(setup);

        BuildStartingDeckSection(setup);
        BuildStartingRelicSection(setup);
        BuildStartingPotionSection(setup);
        BuildStartingPowerSection(setup);
        BuildStartingMorphSection(setup);

        _contentHost.AddChild(CreateSectionDivider());

        AddNullableNumberRow("Starting Gold", setup.StartingGold, 99, 0, 999999,
            value => setup.StartingGold = value);
        AddNullableNumberRow("Starting Max HP", setup.StartingMaxHp, 80, 1, 99999,
            value => setup.StartingMaxHp = value);
        AddNullableNumberRow("Starting Current HP", setup.StartingCurrentHp, 80, 1, 99999,
            value => setup.StartingCurrentHp = value);
        AddNullableNumberRow("Potion Slots", setup.PotionSlots, 3, 0, 20,
            value => setup.PotionSlots = value,
            () =>
            {
                NormalizeStartingPotionCapacity(setup);
                RefreshStartingPotionInventory(setup);
            });
        AddNullableNumberRow("Base Energy / Turn", setup.BaseEnergyPerTurn, 3, 0, 99,
            value => setup.BaseEnergyPerTurn = value);
        AddNullableNumberRow("Cards Drawn / Turn", setup.CardsDrawnPerTurn, 5, 0, 99,
            value => setup.CardsDrawnPerTurn = value);
    }

    private void AddRole()
    {
        if (_workingDefinition is null || _readOnly)
            return;
        RoleDefinition role = new();
        _workingDefinition.Roles.Add(role);
        _activeSetupRoleId = role.Id;
        MarkDirty();
        RebuildContent();
    }

    private void BuildRoleDetails(RoleDefinition role, Button? roleButton)
    {
        if (_contentHost is null)
            return;
        HBoxContainer nameRow = CreateRow();
        nameRow.AddChild(CreateRowLabel("Role Name"));
        LineEdit name = CreateLineEdit(role.Name);
        name.TextChanged += value =>
        {
            if (_loadingFields || ResolveRole(role.Id) != role)
                return;
            role.Name = value;
            if (roleButton is not null)
            {
                string required = role.MinimumPlayers > 0 ? " *" : string.Empty;
                string maximum = role.MaximumPlayers > 0 ? $"    MAX {role.MaximumPlayers}" : string.Empty;
                roleButton.Text = $"{value}{required}    MIN {role.MinimumPlayers}{maximum}";
            }
            MarkDirty();
        };
        nameRow.AddChild(name);
        _contentHost.AddChild(nameRow);

        HBoxContainer limits = CreateRow();
        limits.AddChild(CreateRowLabel("Number of Players for This Role"));
        limits.AddChild(CreateFieldLabel("MIN"));
        NLoadoutNumberStepper minimum = new();
        minimum.Init(role.MinimumPlayers, 0, role.MaximumPlayers == 0 ? 4 : role.MaximumPlayers);
        minimum.ValueChanged += value =>
        {
            if (_loadingFields || ResolveRole(role.Id) != role)
                return;
            role.MinimumPlayers = Math.Clamp(value, 0, role.MaximumPlayers == 0 ? 4 : role.MaximumPlayers);
            MarkDirty();
            RebuildContent();
        };
        limits.AddChild(minimum);
        limits.AddChild(CreateFieldLabel("MAX (0 = NONE)"));
        NLoadoutNumberStepper maximum = new();
        maximum.Init(role.MaximumPlayers, 0, 4);
        maximum.ValueChanged += value =>
        {
            if (_loadingFields || ResolveRole(role.Id) != role)
                return;
            role.MaximumPlayers = Math.Clamp(value, 0, 4);
            if (role.MaximumPlayers > 0)
                role.MinimumPlayers = Math.Min(role.MinimumPlayers, role.MaximumPlayers);
            MarkDirty();
            RebuildContent();
        };
        limits.AddChild(maximum);
        AddToolSpacer(limits);
        AddSettingsActionButton(limits, "delete_role", "DELETE ROLE", 190f, () => DeleteRole(role), danger: true);
        _contentHost.AddChild(limits);
    }

    private void BuildDefaultRoleDetails(Button roleButton)
    {
        if (_contentHost is null || _workingDefinition is null)
            return;
        HBoxContainer nameRow = CreateRow();
        nameRow.AddChild(CreateRowLabel("Role Name"));
        LineEdit name = CreateLineEdit(_workingDefinition.DefaultRoleName);
        name.TextChanged += value =>
        {
            if (_loadingFields || _workingDefinition is null || _activeSetupRoleId is not null)
                return;
            _workingDefinition.DefaultRoleName = value;
            roleButton.Text = value;
            MarkDirty();
        };
        nameRow.AddChild(name);
        _contentHost.AddChild(nameRow);
    }

    private void DeleteRole(RoleDefinition role)
    {
        if (_workingDefinition is null || ResolveRole(role.Id) != role)
            return;
        _workingDefinition.Roles.Remove(role);
        _activeSetupRoleId = null;
        MarkDirty();
        RebuildContent();
    }

    private RoleDefinition? ResolveRole(string? roleId)
    {
        return roleId is null || _workingDefinition is null
            ? null
            : _workingDefinition.Roles.FirstOrDefault(role => string.Equals(role.Id, roleId, StringComparison.Ordinal));
    }

    private RunSetupDefinition? ResolveSetup(string? roleId)
    {
        return roleId is null ? _workingDefinition?.Setup : ResolveRole(roleId)?.Setup;
    }

    private RunSetupDefinition GetActiveSetup()
    {
        return ResolveSetup(_activeSetupRoleId) ?? _workingDefinition?.Setup ?? new RunSetupDefinition();
    }

    private bool IsSetupOwnerValid(string? roleId, RunSetupDefinition setup)
    {
        return ReferenceEquals(ResolveSetup(roleId), setup);
    }

    private void BuildStartingDeckSection(RunSetupDefinition setup)
    {
        if (_contentHost is null)
            return;

        HBoxContainer actions = CreateToolRow("Starting Deck");
        AddSettingsActionButton(actions, "card_printer", "CARD PRINTER", RunSetupToolButtonWidth, OpenStartingCardPrinter);
        AddSettingsActionButton(actions, "card_shredder", "CARD SHREDDER", RunSetupToolButtonWidth, OpenStartingCardShredder);
        AddSettingsActionButton(actions, "card_modifier", "CARD MODIFIER", RunSetupToolButtonWidth, OpenStartingCardModifierInventory);
        AddToolSpacer(actions);
        AddSettingsActionButton(actions, "revert_deck", "REVERT", 142f, () => ResetSelection(setup.StartingDeck), danger: true);
        _contentHost.AddChild(actions);

        _startingDeckPreview = CreateInventoryPreview();
        _contentHost.AddChild(_startingDeckPreview);
        RefreshStartingDeckPreview(setup);
    }

    private void BuildStartingRelicSection(RunSetupDefinition setup)
    {
        if (_contentHost is null)
            return;

        HBoxContainer actions = CreateToolRow("Starting Relics");
        AddSettingsActionButton(actions, "loadout_bag", "LOADOUT BAG", RunSetupToolButtonWidth, OpenStartingLoadoutBag);
        AddSettingsActionButton(actions, "trash_bin", "TRASH BIN", RunSetupToolButtonWidth, OpenStartingTrashBin);
        AddSettingsActionButton(actions, "relic_modifier", "RELIC MODIFIER", RunSetupToolButtonWidth, OpenStartingRelicModifierInventory);
        AddToolSpacer(actions);
        AddSettingsActionButton(actions, "revert_relics", "REVERT", 142f, () => ResetSelection(setup.StartingRelics), danger: true);
        _contentHost.AddChild(actions);

        _startingRelicPreview = CreateInventoryPreview();
        _contentHost.AddChild(_startingRelicPreview);
        RefreshStartingRelicPreview(setup);
    }

    private void BuildStartingPotionSection(RunSetupDefinition setup)
    {
        if (_contentHost is null)
            return;

        HBoxContainer actions = CreateToolRow("Starting Potions");
        AddSettingsActionButton(actions, "potion_cauldron", "POTION CAULDRON", RunSetupToolButtonWidth, OpenStartingPotionCauldron);
        AddToolSpacer(actions);
        AddSettingsActionButton(actions, "revert_potions", "REVERT", 142f, () => ResetSelection(setup.StartingPotions), danger: true);
        _contentHost.AddChild(actions);

        VBoxContainer inventory = new() { SizeFlagsHorizontal = SizeFlags.ShrinkBegin };
        MarginContainer topBarInventory = CreateTopBarPotionInventory();
        inventory.AddChild(topBarInventory);
        _contentHost.AddChild(inventory);
        RefreshStartingPotionInventory(setup);
    }

    private void RefreshStartingDeckPreview(RunSetupDefinition setup)
    {
        if (_startingDeckPreview is null || !GodotObject.IsInstanceValid(_startingDeckPreview))
            return;

        ClearChildren(_startingDeckPreview);
        List<CardModel> cards = GetStartingCardEntries(setup)
            .Select(CreateStartingCardPreview)
            .Where(card => card is not null)
            .Cast<CardModel>()
            .ToList();
        if (cards.Count == 0)
        {
            _startingDeckPreview.AddChild(CreateHint("This starting deck is empty."));
            RefreshContentSizeDeferred();
            return;
        }

        foreach (IGrouping<string, CardModel> group in cards.GroupBy(
                     card => $"{card.Id}|{card.CurrentUpgradeLevel}|{JsonSerializer.Serialize(CardModificationRuntime.GetEffectiveSpec(card))}",
                     StringComparer.Ordinal))
        {
            NDeckHistoryEntry? view = NDeckHistoryEntry.Create(group.First(), group.Count());
            if (view is null)
                continue;
            AttachStartingCardHover(view, view.Card);
            _startingDeckPreview.AddChild(view);
        }
        RefreshContentSizeDeferred();
    }

    private void AttachStartingCardHover(Control view, CardModel card)
    {
        bool hovered = false;
        int generation = 0;

        view.MouseEntered += () =>
        {
            hovered = true;
            int requestedGeneration = ++generation;
            TaskHelper.RunSafely(ShowStartingCardHoverAfterDelay(
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

    private async Task ShowStartingCardHoverAfterDelay(
        Control view,
        CardModel card,
        Func<bool> shouldShow)
    {
        SceneTreeTimer timer = GetTree().CreateTimer(StartingCardHoverDelaySeconds);
        await ToSignal(timer, SceneTreeTimer.SignalName.Timeout);
        if (!shouldShow() || !GodotObject.IsInstanceValid(view) || !view.IsVisibleInTree())
            return;

        List<IHoverTip> tips = [HoverTipFactory.FromCard(card)];
        try
        {
            tips.AddRange(card.HoverTips);
        }
        catch
        {
        }

        CommonHelpers.ShowHoverTips(view, tips);
    }

    private void RefreshStartingRelicPreview(RunSetupDefinition setup)
    {
        if (_startingRelicPreview is null || !GodotObject.IsInstanceValid(_startingRelicPreview))
            return;

        ClearChildren(_startingRelicPreview);
        IReadOnlyList<SavedRelicLoadoutEntry> relics = GetStartingRelicEntries(setup);
        if (relics.Count == 0)
        {
            _startingRelicPreview.AddChild(CreateHint("This starting relic collection is empty."));
            RefreshContentSizeDeferred();
            return;
        }

        foreach (SavedRelicLoadoutEntry entry in relics)
        {
            RelicModel? relic = CreateStartingRelicPreview(entry);
            if (relic is null)
                continue;
            NRelicBasicHolder? holder = NRelicBasicHolder.Create(relic);
            if (holder is null)
                continue;
            holder.TooltipText = relic.Id.ToString();
            _startingRelicPreview.AddChild(holder);
        }
        RefreshContentSizeDeferred();
    }

    private MarginContainer CreateTopBarPotionInventory()
    {
        MarginContainer panel = new()
        {
            CustomMinimumSize = new Vector2(140f, 80f),
            SizeFlagsHorizontal = SizeFlags.ShrinkBegin,
            MouseFilter = MouseFilterEnum.Pass
        };

        if (ResourceLoader.Exists(PotionInventoryBackdropPath))
        {
            NinePatchRect background = new()
            {
                Texture = GD.Load<Texture2D>(PotionInventoryBackdropPath),
                PatchMarginLeft = 32,
                PatchMarginTop = 32,
                PatchMarginRight = 32,
                PatchMarginBottom = 32,
                MouseFilter = MouseFilterEnum.Ignore
            };
            background.SetAnchorsPreset(LayoutPreset.FullRect);
            panel.AddChild(background);
        }

        MarginContainer inset = new() { MouseFilter = MouseFilterEnum.Pass };
        inset.SetAnchorsPreset(LayoutPreset.FullRect);
        inset.AddThemeConstantOverride("margin_left", 18);
        inset.AddThemeConstantOverride("margin_top", 5);
        inset.AddThemeConstantOverride("margin_right", 19);
        inset.AddThemeConstantOverride("margin_bottom", 6);
        panel.AddChild(inset);

        _startingPotionHolders = new HBoxContainer { MouseFilter = MouseFilterEnum.Pass };
        _startingPotionHolders.AddThemeConstantOverride("separation", 2);
        inset.AddChild(_startingPotionHolders);
        return panel;
    }

    private void RefreshStartingPotionInventory(RunSetupDefinition setup)
    {
        if (_startingPotionHolders is null || !GodotObject.IsInstanceValid(_startingPotionHolders))
            return;

        ClearChildren(_startingPotionHolders);
        IReadOnlyList<string> potionIds = setup.StartingPotions.Mode == SelectionMode.Fixed
            ? setup.StartingPotions.FixedModelIds
            : ResolvePreviewCharacter(setup)?.StartingPotions.Select(potion => potion.Id.ToString()).ToList() ?? [];
        int slotCount = Math.Clamp(setup.PotionSlots ?? 3, 0, 20);

        for (int index = 0; index < slotCount; index++)
        {
            int capturedIndex = index;
            NPotionHolder holder = NPotionHolder.Create(isUsable: false);
            _startingPotionHolders.AddChild(holder);
            if (index >= potionIds.Count
                || !CustomRunCatalogService.TryResolve(SelectionModelKind.Potion, potionIds[index], out CustomRunCatalogEntry catalog)
                || catalog.Model is not PotionModel canonical
                || NPotion.Create(canonical.ToMutable()) is not { } potion)
            {
                holder.MouseFilter = MouseFilterEnum.Ignore;
                holder.MouseBehaviorRecursive = MouseBehaviorRecursiveEnum.Disabled;
                holder.FocusMode = FocusModeEnum.None;
                holder.FocusBehaviorRecursive = FocusBehaviorRecursiveEnum.Disabled;
                continue;
            }

            holder.AddPotion(potion);
            potion.Position = Vector2.Zero;
            holder.Connect(
                NClickableControl.SignalName.Released,
                Callable.From<NClickableControl>(_ => DiscardStartingPotion(capturedIndex)));
        }

        RefreshContentSizeDeferred();
    }

    private static void NormalizeStartingPotionCapacity(RunSetupDefinition setup)
    {
        if (setup.StartingPotions.Mode != SelectionMode.Fixed)
            return;
        int capacity = Math.Clamp(setup.PotionSlots ?? 3, 0, 20);
        if (setup.StartingPotions.FixedModelIds.Count > capacity)
            setup.StartingPotions.FixedModelIds.RemoveRange(capacity, setup.StartingPotions.FixedModelIds.Count - capacity);
    }

    private void BuildStartingPowerSection(RunSetupDefinition setup)
    {
        if (_contentHost is null)
            return;
        HBoxContainer actions = CreateToolRow("Starting Powers");
        AddSettingsActionButton(actions, "starting_powers", "SELECT POWERS", RunSetupToolButtonWidth, OpenStartingPowerSelector);
        AddToolSpacer(actions);
        AddSettingsActionButton(actions, "revert_powers", "REVERT", 142f, () =>
        {
            setup.StartingPowers.Clear();
            MarkDirty();
            RebuildContent();
        }, danger: true);
        _contentHost.AddChild(actions);
        _contentHost.AddChild(CreateStartingPowerPreview(setup));
    }

    private static Control CreateStartingPowerPreview(RunSetupDefinition setup)
    {
        if (setup.StartingPowers.Count == 0)
            return CreateHint("No starting Power Giver amounts.");

        VBoxContainer list = new()
        {
            SizeFlagsHorizontal = SizeFlags.ShrinkBegin,
            MouseFilter = MouseFilterEnum.Ignore
        };
        list.AddThemeConstantOverride("separation", 6);
        foreach (StartingPowerDefinition startingPower in setup.StartingPowers)
        {
            HBoxContainer row = new()
            {
                CustomMinimumSize = new Vector2(320f, 52f),
                MouseFilter = MouseFilterEnum.Ignore
            };
            row.AddThemeConstantOverride("separation", 12);

            string name = GetModelName(SelectionModelKind.Power, startingPower.ModelId);
            if (CustomRunCatalogService.TryResolve(
                    SelectionModelKind.Power,
                    startingPower.ModelId,
                    out CustomRunCatalogEntry catalog)
                && catalog.Model is PowerModel power)
            {
                try
                {
                    TextureRect icon = new()
                    {
                        Texture = power.Icon,
                        CustomMinimumSize = new Vector2(44f, 44f),
                        SizeFlagsVertical = SizeFlags.ShrinkCenter,
                        ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                        StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                        MouseFilter = MouseFilterEnum.Ignore
                    };
                    row.AddChild(icon);
                }
                catch
                {
                }
            }

            MegaLabel label = CreateLabel(
                $"{name} x{startingPower.Amount}",
                21,
                StsColors.cream,
                HorizontalAlignment.Left);
            label.CustomMinimumSize = new Vector2(250f, 52f);
            label.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            row.AddChild(label);
            list.AddChild(row);
        }
        return list;
    }

    private void BuildStartingMorphSection(RunSetupDefinition setup)
    {
        if (_contentHost is null)
            return;
        HBoxContainer actions = CreateToolRow("Starting Morph");
        AddSettingsActionButton(actions, "starting_morph", "SELECT MORPH", RunSetupToolButtonWidth, OpenStartingMorphSelector);
        AddToolSpacer(actions);
        AddSettingsActionButton(actions, "revert_morph", "REVERT", 142f, () =>
        {
            setup.StartingMorphModelId = null;
            MarkDirty();
            RebuildContent();
        }, danger: true);
        _contentHost.AddChild(actions);
        _contentHost.AddChild(CreateHint(setup.StartingMorphModelId is null
            ? "Original character form."
            : $"Starts morphed as {GetMorphName(setup.StartingMorphModelId)}."));
    }

    private void ResetSelection(SelectionSpec selection)
    {
        selection.Mode = SelectionMode.Default;
        selection.FixedModelIds.Clear();
        if (_workingDefinition is not null)
        {
            if (selection.Kind == SelectionModelKind.Card)
                GetActiveSetup().StartingCardEntries.Clear();
            else if (selection.Kind == SelectionModelKind.Relic)
                GetActiveSetup().StartingRelicEntries.Clear();
        }
        MarkDirty();
        RebuildContent();
    }

    private void SynchronizeDetailedEntries(SelectionSpec selection, IReadOnlyList<string> selectedIds)
    {
        if (_workingDefinition is null)
            return;

        if (selection.Kind == SelectionModelKind.Card)
        {
            List<SavedCardLoadoutEntry> previous = GetActiveSetup().StartingCardEntries.ToList();
            List<SavedCardLoadoutEntry> next = [];
            foreach (string id in selectedIds)
            {
                int index = previous.FindIndex(entry => string.Equals(entry.ModelId, id, StringComparison.OrdinalIgnoreCase));
                if (index >= 0)
                {
                    next.Add(previous[index]);
                    previous.RemoveAt(index);
                }
                else
                {
                    next.Add(new SavedCardLoadoutEntry { ModelId = id, Count = 1 });
                }
            }
            GetActiveSetup().StartingCardEntries = next;
        }
        else if (selection.Kind == SelectionModelKind.Relic)
        {
            List<SavedRelicLoadoutEntry> previous = GetActiveSetup().StartingRelicEntries.ToList();
            List<SavedRelicLoadoutEntry> next = [];
            foreach (string id in selectedIds)
            {
                int index = previous.FindIndex(entry => string.Equals(entry.ModelId, id, StringComparison.OrdinalIgnoreCase));
                if (index >= 0)
                {
                    next.Add(previous[index]);
                    previous.RemoveAt(index);
                }
                else
                {
                    next.Add(new SavedRelicLoadoutEntry { ModelId = id, Count = 1 });
                }
            }
            GetActiveSetup().StartingRelicEntries = next;
        }
    }

    private void OpenStartingCardPrinter()
    {
        if (_workingDefinition is null)
            return;
        OpenCatalogAction(SelectionModelKind.Card, (screen, item, amount) =>
        {
            if (_workingDefinition is null || item.UntypedModel is not CardModel card)
                return;
            EnsureDetailedStartingSelection(SelectionModelKind.Card);
            int upgradeLevel = screen.IsToggleEnabled("view_upgrades") && card.IsUpgradable ? 1 : 0;
            for (int copy = 0; copy < amount; copy++)
            {
                GetActiveSetup().StartingCardEntries.Add(new SavedCardLoadoutEntry
                {
                    ModelId = card.Id.ToString(),
                    UpgradeLevel = upgradeLevel,
                    Count = 1
                });
            }
            SynchronizeStartingSelectionIds(SelectionModelKind.Card);
            MarkDirty();
            RefreshStartingDeckPreview(GetActiveSetup());
            CustomRunEditorPreviewService.PreviewCardAdd(
                card,
                upgradeLevel,
                amount,
                screen.GetNodeOrNull<Control>(screen.CancelButtonPath));
        });
    }

    private void OpenStartingLoadoutBag()
    {
        if (_workingDefinition is null)
            return;
        OpenCatalogAction(SelectionModelKind.Relic, (screen, item, amount) =>
        {
            if (_workingDefinition is null || item.UntypedModel is not RelicModel relic)
                return;
            EnsureDetailedStartingSelection(SelectionModelKind.Relic);
            for (int copy = 0; copy < amount; copy++)
            {
                GetActiveSetup().StartingRelicEntries.Add(new SavedRelicLoadoutEntry
                {
                    ModelId = relic.Id.ToString(),
                    Count = 1
                });
            }
            SynchronizeStartingSelectionIds(SelectionModelKind.Relic);
            MarkDirty();
            RefreshStartingRelicPreview(GetActiveSetup());
            CustomRunEditorPreviewService.PreviewRelicAdd(
                relic,
                amount,
                item.View,
                screen.GetNodeOrNull<Control>(screen.CancelButtonPath));
        });
    }

    private void OpenStartingPotionCauldron()
    {
        if (_workingDefinition is null)
            return;
        OpenCatalogAction(SelectionModelKind.Potion, (screen, item, amount) =>
        {
            if (_workingDefinition is null || item.UntypedModel is not PotionModel potion)
                return;
            SelectionSpec selection = GetActiveSetup().StartingPotions;
            if (selection.Mode == SelectionMode.Default)
            {
                selection.Mode = SelectionMode.Fixed;
                selection.FixedModelIds = GetDefaultStartingIds(SelectionModelKind.Potion).ToList();
            }
            int capacity = Math.Clamp(GetActiveSetup().PotionSlots ?? 3, 0, 20);
            int copies = Math.Min(amount, Math.Max(0, capacity - selection.FixedModelIds.Count));
            for (int copy = 0; copy < copies; copy++)
                selection.FixedModelIds.Add(potion.Id.ToString());
            if (copies == 0)
            {
                SetStatus("The starting potion inventory is full.", success: false);
                return;
            }
            MarkDirty();
            RefreshStartingPotionInventory(GetActiveSetup());
            CustomRunEditorPreviewService.PreviewPotionAdd(
                potion,
                copies,
                item.View,
                screen.GetNodeOrNull<Control>(screen.CancelButtonPath));
        });
    }

    private void OpenStartingCardShredder()
    {
        if (_workingDefinition is null)
            return;
        string? ownerId = _activeSetupRoleId;
        RunSetupDefinition setup = GetActiveSetup();
        IReadOnlyList<LoadoutOwnedItem<CardModel>> cards = CustomRunEditorPreviewService.CreateOwnedCards(
            GetStartingCardEntries(setup));
        _catalogSelectorSession?.Dispose();
        if (!CustomRunCatalogSelector.TryOpenOwnedCardAction(
                "CardShredder",
                cards,
                (_, item, amount) =>
                {
                    if (!IsSetupOwnerValid(ownerId, setup))
                        return null;
                    EnsureDetailedStartingSelection(SelectionModelKind.Card);
                    List<SavedCardLoadoutEntry> entries = GetActiveSetup().StartingCardEntries;
                    if (item.Index < 0 || item.Index >= entries.Count)
                        return CustomRunEditorPreviewService.CreateOwnedCards(entries);
                    SavedCardLoadoutEntry selected = entries[item.Index];
                    List<CardModel> removedCards = [];
                    for (int copy = 0; copy < amount; copy++)
                    {
                        int index = copy == 0
                            ? item.Index
                            : entries.FindIndex(entry => CardEntriesMatch(entry, selected));
                        if (index < 0 || index >= entries.Count)
                            break;
                        CardModel? preview = CreateStartingCardPreview(entries[index]);
                        if (preview is not null)
                            removedCards.Add(preview);
                        entries.RemoveAt(index);
                    }
                    SynchronizeStartingSelectionIds(SelectionModelKind.Card);
                    MarkDirty();
                    RefreshStartingDeckPreview(GetActiveSetup());
                    CustomRunEditorPreviewService.PreviewCardRemoval(removedCards);
                    return CustomRunEditorPreviewService.CreateOwnedCards(entries);
                },
                out IDisposable? session,
                out string error))
        {
            SetStatus(error, success: false);
            return;
        }
        _catalogSelectorSession = session;
    }

    private void OpenStartingTrashBin()
    {
        if (_workingDefinition is null)
            return;
        string? ownerId = _activeSetupRoleId;
        RunSetupDefinition setup = GetActiveSetup();
        IReadOnlyList<LoadoutOwnedItem<RelicModel>> relics = CustomRunEditorPreviewService.CreateOwnedRelics(
            GetStartingRelicEntries(setup));
        _catalogSelectorSession?.Dispose();
        if (!CustomRunCatalogSelector.TryOpenOwnedRelicAction(
                "TrashBin",
                relics,
                (_, item, amount) =>
                {
                    if (!IsSetupOwnerValid(ownerId, setup))
                        return null;
                    EnsureDetailedStartingSelection(SelectionModelKind.Relic);
                    List<SavedRelicLoadoutEntry> entries = GetActiveSetup().StartingRelicEntries;
                    if (item.Index < 0 || item.Index >= entries.Count)
                        return CustomRunEditorPreviewService.CreateOwnedRelics(entries);
                    SavedRelicLoadoutEntry selected = entries[item.Index];
                    for (int copy = 0; copy < amount; copy++)
                    {
                        int index = copy == 0
                            ? item.Index
                            : entries.FindIndex(entry => RelicEntriesMatch(entry, selected));
                        if (index < 0 || index >= entries.Count)
                            break;
                        entries.RemoveAt(index);
                    }
                    SynchronizeStartingSelectionIds(SelectionModelKind.Relic);
                    MarkDirty();
                    RefreshStartingRelicPreview(GetActiveSetup());
                    return CustomRunEditorPreviewService.CreateOwnedRelics(entries);
                },
                out IDisposable? session,
                out string error))
        {
            SetStatus(error, success: false);
            return;
        }
        _catalogSelectorSession = session;
    }

    private void OpenStartingCardModifierInventory()
    {
        if (_workingDefinition is null)
            return;
        string? ownerId = _activeSetupRoleId;
        RunSetupDefinition setup = GetActiveSetup();
        IReadOnlyList<SavedCardLoadoutEntry> entries = GetStartingCardEntries(setup);
        if (entries.Count == 0)
        {
            SetStatus("The starting deck is empty.", success: false);
            return;
        }
        IReadOnlyList<LoadoutOwnedItem<CardModel>> cards = CustomRunEditorPreviewService.CreateOwnedCards(entries);
        _catalogSelectorSession?.Dispose();
        if (!CustomRunCatalogSelector.TryOpenOwnedCardActions(
                "CardModifier",
                cards,
                (screen, item, amount) => UpgradeStartingCard(screen, item, amount, ownerId, setup),
                (_, item, _) =>
                {
                    if (!IsSetupOwnerValid(ownerId, setup))
                        return null;
                    EnsureDetailedStartingSelection(SelectionModelKind.Card);
                    CloseCatalogSelector();
                    CustomRunEditorPreviewService.OpenCardModifier(
                        GetActiveSetup().StartingCardEntries,
                        item.Index,
                        () =>
                        {
                            if (!IsSetupOwnerValid(ownerId, setup))
                                return;
                            SynchronizeStartingSelectionIds(SelectionModelKind.Card);
                            MarkDirty();
                            RefreshStartingDeckPreview(GetActiveSetup());
                        });
                    return null;
                },
                out IDisposable? session,
                out string error))
        {
            SetStatus(error, success: false);
            return;
        }
        _catalogSelectorSession = session;
    }

    private void OpenStartingRelicModifierInventory()
    {
        if (_workingDefinition is null)
            return;
        string? ownerId = _activeSetupRoleId;
        RunSetupDefinition setup = GetActiveSetup();
        IReadOnlyList<SavedRelicLoadoutEntry> entries = GetStartingRelicEntries(setup);
        if (entries.Count == 0)
        {
            SetStatus("The starting relic inventory is empty.", success: false);
            return;
        }
        IReadOnlyList<LoadoutOwnedItem<RelicModel>> relics = CustomRunEditorPreviewService.CreateOwnedRelics(entries);
        _catalogSelectorSession?.Dispose();
        if (!CustomRunCatalogSelector.TryOpenOwnedRelicRightAction(
                "RelicModifier",
                relics,
                (_, item, _) =>
                {
                    if (!IsSetupOwnerValid(ownerId, setup))
                        return null;
                    EnsureDetailedStartingSelection(SelectionModelKind.Relic);
                    CloseCatalogSelector();
                    CustomRunEditorPreviewService.OpenRelicModifier(
                        GetActiveSetup().StartingRelicEntries,
                        item.Index,
                        () =>
                        {
                            if (!IsSetupOwnerValid(ownerId, setup))
                                return;
                            SynchronizeStartingSelectionIds(SelectionModelKind.Relic);
                            MarkDirty();
                            RefreshStartingRelicPreview(GetActiveSetup());
                        });
                    return null;
                },
                out IDisposable? session,
                out string error))
        {
            SetStatus(error, success: false);
            return;
        }
        _catalogSelectorSession = session;
    }

    private void OpenCatalogAction(
        SelectionModelKind kind,
        Action<NGenericSelectScreen, IGenericSelectItem, int> activated)
    {
        string? ownerId = _activeSetupRoleId;
        RunSetupDefinition setup = GetActiveSetup();
        _catalogSelectorSession?.Dispose();
        if (!CustomRunCatalogSelector.TryOpenCatalogAction(
                kind,
                (screen, item, amount) =>
                {
                    if (IsSetupOwnerValid(ownerId, setup))
                        activated(screen, item, amount);
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

    private void DiscardStartingPotion(int index)
    {
        if (_workingDefinition is null)
            return;
        SelectionSpec potions = GetActiveSetup().StartingPotions;
        if (potions.Mode == SelectionMode.Default)
        {
            potions.Mode = SelectionMode.Fixed;
            potions.FixedModelIds = GetDefaultStartingIds(SelectionModelKind.Potion).ToList();
        }
        if (index < 0 || index >= potions.FixedModelIds.Count)
            return;
        potions.FixedModelIds.RemoveAt(index);
        MarkDirty();
        RefreshStartingPotionInventory(GetActiveSetup());
    }

    private IReadOnlyList<LoadoutOwnedItem<CardModel>>? UpgradeStartingCard(
        NGenericSelectScreen screen,
        LoadoutOwnedItem<CardModel> item,
        int amount,
        string? ownerId,
        RunSetupDefinition setup)
    {
        if (!IsSetupOwnerValid(ownerId, setup))
            return null;
        EnsureDetailedStartingSelection(SelectionModelKind.Card);
        List<SavedCardLoadoutEntry> entries = GetActiveSetup().StartingCardEntries;
        if (item.Index < 0 || item.Index >= entries.Count)
            return CustomRunEditorPreviewService.CreateOwnedCards(entries);

        SavedCardLoadoutEntry entry = entries[item.Index];
        CardModel? preview = CreateStartingCardPreview(entry);
        int upgradesApplied = 0;
        while (preview is { IsUpgradable: true } && upgradesApplied < Math.Max(1, amount))
        {
            preview.UpgradeInternal();
            preview.FinalizeUpgradeInternal();
            entry.UpgradeLevel++;
            upgradesApplied++;
        }
        if (upgradesApplied == 0)
            return null;

        IGenericSelectItem? selectedItem = screen.Items.FirstOrDefault(candidate =>
            candidate.UntypedModel is LoadoutOwnedItem<CardModel> owned
            && owned.Index == item.Index);
        if (selectedItem?.View is Control view)
            CommonHelpers.PlayCardSmithFeedback(view);
        SynchronizeStartingSelectionIds(SelectionModelKind.Card);
        MarkDirty();
        RefreshStartingDeckPreview(GetActiveSetup());
        return CustomRunEditorPreviewService.CreateOwnedCards(entries);
    }

    private void OpenStartingPowerSelector()
    {
        if (_workingDefinition is null)
            return;
        string? ownerId = _activeSetupRoleId;
        RunSetupDefinition setup = GetActiveSetup();
        Dictionary<string, int> current = GetActiveSetup().StartingPowers
            .ToDictionary(power => power.ModelId, power => power.Amount, StringComparer.OrdinalIgnoreCase);
        _catalogSelectorSession?.Dispose();
        if (!CustomRunCatalogSelector.TryOpenPowerSelection(current, selected =>
            {
                if (!IsSetupOwnerValid(ownerId, setup)) return;
                GetActiveSetup().StartingPowers = selected
                    .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => new StartingPowerDefinition { ModelId = pair.Key, Amount = pair.Value })
                    .ToList();
                MarkDirty();
                RebuildContent();
            }, out IDisposable? session, out string error))
        {
            SetStatus(error, success: false);
            return;
        }
        _catalogSelectorSession = session;
    }

    private void OpenStartingMorphSelector()
    {
        if (_workingDefinition is null)
            return;
        string? ownerId = _activeSetupRoleId;
        RunSetupDefinition setup = GetActiveSetup();
        _catalogSelectorSession?.Dispose();
        if (!CustomRunCatalogSelector.TryOpenMorphSelection(
                GetActiveSetup().StartingMorphModelId,
                selected =>
                {
                    if (!IsSetupOwnerValid(ownerId, setup)) return;
                    GetActiveSetup().StartingMorphModelId = selected;
                    MarkDirty();
                    RebuildContent();
                    _catalogSelectorSession = null;
                },
                out IDisposable? session,
                out string error))
        {
            SetStatus(error, success: false);
            return;
        }
        _catalogSelectorSession = session;
    }

    private IReadOnlyList<SavedCardLoadoutEntry> GetStartingCardEntries(RunSetupDefinition setup)
    {
        if (setup.StartingDeck.Mode != SelectionMode.Fixed)
            return ResolvePreviewCharacter(setup)?.StartingDeck
                .Select(card => new SavedCardLoadoutEntry { ModelId = card.Id.ToString() })
                .ToList() ?? [];
        return setup.StartingCardEntries.Count > 0
            ? setup.StartingCardEntries
            : setup.StartingDeck.FixedModelIds.Select(id => new SavedCardLoadoutEntry { ModelId = id }).ToList();
    }

    private IReadOnlyList<SavedRelicLoadoutEntry> GetStartingRelicEntries(RunSetupDefinition setup)
    {
        if (setup.StartingRelics.Mode != SelectionMode.Fixed)
            return ResolvePreviewCharacter(setup)?.StartingRelics
                .Select(relic => new SavedRelicLoadoutEntry { ModelId = relic.Id.ToString() })
                .ToList() ?? [];
        return setup.StartingRelicEntries.Count > 0
            ? setup.StartingRelicEntries
            : setup.StartingRelics.FixedModelIds.Select(id => new SavedRelicLoadoutEntry { ModelId = id }).ToList();
    }

    private CharacterModel? ResolvePreviewCharacter(RunSetupDefinition? setup = null)
    {
        setup ??= GetActiveSetup();
        if (setup.Character.Mode == SelectionMode.Fixed)
        {
            foreach (string id in setup.Character.FixedModelIds)
            {
                if (CustomRunCatalogService.TryResolve(SelectionModelKind.Character, id, out CustomRunCatalogEntry entry)
                    && entry.Model is CharacterModel fixedCharacter
                    && fixedCharacter.IsPlayable)
                    return fixedCharacter;
            }
        }

        CharacterModel? lobbyCharacter = _lobby is null
            ? null
            : Sts2Compatibility.EnumerateStartRunLobbyPlayers(_lobby)
                .OrderBy(player => player.SlotId)
                .Select(player => player.Character)
                .FirstOrDefault(character => character is not null && character.IsPlayable);
        return lobbyCharacter ?? ModelDb.AllCharacters.FirstOrDefault(character => character.IsPlayable);
    }

    private IReadOnlyList<string> GetDefaultStartingIds(SelectionModelKind kind)
    {
        CharacterModel? character = ResolvePreviewCharacter();
        if (character is null)
            return [];
        return kind switch
        {
            SelectionModelKind.Card => character.StartingDeck.Select(card => card.Id.ToString()).ToList(),
            SelectionModelKind.Relic => character.StartingRelics.Select(relic => relic.Id.ToString()).ToList(),
            SelectionModelKind.Potion => character.StartingPotions.Select(potion => potion.Id.ToString()).ToList(),
            _ => []
        };
    }

    private void EnsureDetailedStartingSelection(SelectionModelKind kind)
    {
        if (_workingDefinition is null)
            return;
        SelectionSpec selection = kind == SelectionModelKind.Card
            ? GetActiveSetup().StartingDeck
            : GetActiveSetup().StartingRelics;
        if (selection.Mode == SelectionMode.Default)
        {
            selection.Mode = SelectionMode.Fixed;
            selection.FixedModelIds = GetDefaultStartingIds(kind).ToList();
        }
        SynchronizeDetailedEntries(selection, selection.FixedModelIds.ToList());
        MarkDirty();
    }

    private void SynchronizeStartingSelectionIds(SelectionModelKind kind)
    {
        if (_workingDefinition is null)
            return;
        if (kind == SelectionModelKind.Card)
        {
            GetActiveSetup().StartingDeck.Mode = SelectionMode.Fixed;
            GetActiveSetup().StartingDeck.FixedModelIds = GetActiveSetup().StartingCardEntries
                .Select(entry => entry.ModelId)
                .ToList();
        }
        else if (kind == SelectionModelKind.Relic)
        {
            GetActiveSetup().StartingRelics.Mode = SelectionMode.Fixed;
            GetActiveSetup().StartingRelics.FixedModelIds = GetActiveSetup().StartingRelicEntries
                .Select(entry => entry.ModelId)
                .ToList();
        }
    }

    private static bool CardEntriesMatch(SavedCardLoadoutEntry left, SavedCardLoadoutEntry right)
    {
        return string.Equals(left.ModelId, right.ModelId, StringComparison.OrdinalIgnoreCase)
               && left.UpgradeLevel == right.UpgradeLevel
               && JsonSerializer.Serialize(left.ModificationState) == JsonSerializer.Serialize(right.ModificationState);
    }

    private static bool RelicEntriesMatch(SavedRelicLoadoutEntry left, SavedRelicLoadoutEntry right)
    {
        return string.Equals(left.ModelId, right.ModelId, StringComparison.OrdinalIgnoreCase)
               && JsonSerializer.Serialize(left.ModificationState) == JsonSerializer.Serialize(right.ModificationState);
    }

    private static CardModel? CreateStartingCardPreview(SavedCardLoadoutEntry entry)
    {
        if (!CustomRunCatalogService.TryResolve(SelectionModelKind.Card, entry.ModelId, out CustomRunCatalogEntry catalog)
            || catalog.Model is not CardModel canonical)
            return null;
        CardModel card = canonical.ToMutable();
        for (int upgrade = 0; upgrade < entry.UpgradeLevel && card.IsUpgradable; upgrade++)
        {
            card.UpgradeInternal();
            card.FinalizeUpgradeInternal();
        }
        return entry.ModificationState is null
            ? card
            : CardModificationRuntime.CreatePreviewCard(card, entry.ModificationState);
    }

    private static RelicModel? CreateStartingRelicPreview(SavedRelicLoadoutEntry entry)
    {
        if (!CustomRunCatalogService.TryResolve(SelectionModelKind.Relic, entry.ModelId, out CustomRunCatalogEntry catalog)
            || catalog.Model is not RelicModel canonical)
            return null;
        RelicModel relic = canonical.ToMutable();
        RelicModificationStateService.ApplyPermanentToRelic(relic);
        return entry.ModificationState is null
            ? relic
            : RelicModificationStateService.CreatePreviewRelic(relic, entry.ModificationState);
    }

    private static string GetModelName(SelectionModelKind kind, string id)
    {
        if (!CustomRunCatalogService.TryResolve(kind, id, out CustomRunCatalogEntry entry))
            return id;
        return entry.Model switch
        {
            CardModel card => CardPrinter.FormatCardTitle(card),
            RelicModel relic => CommonHelpers.FormatRelicTitle(relic),
            PotionModel potion => CommonHelpers.FormatPotionTitle(potion),
            PowerModel power => CommonHelpers.FormatPowerTitle(power),
            _ => entry.Model.Id.Entry
        };
    }

    private static string GetMorphName(string id)
    {
        return CustomRunCatalogService.TryResolveMorph(id, out AbstractModel model)
            ? model.Id.Entry
            : id;
    }

    private void BuildRulesPanel()
    {
        if (_contentHost is null || _workingDefinition is null)
            return;

        _contentHost.AddChild(CreateSectionTitle("RULES"));
        _contentHost.AddChild(CreateHint(
            "Rules execute from top to bottom when they respond to the same event. Drag a rule to change its priority, or click it to open the full rule editor."));

        HBoxContainer summary = CreateRow();
        List<RuleDefinition> enabledPermanentRules = PermanentRuleStorageService.GetRules()
            .Where(rule => rule.Enabled)
            .GroupBy(RuleBehaviorHashService.Compute, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
        HashSet<string> permanentHashes = enabledPermanentRules
            .Select(RuleBehaviorHashService.Compute)
            .ToHashSet(StringComparer.Ordinal);
        int permanentCount = enabledPermanentRules.Count;
        int suppressedCount = _workingDefinition.Rules.Count(rule =>
            permanentHashes.Contains(RuleBehaviorHashService.Compute(rule)));
        int effectiveCount = _workingDefinition.Rules.Count - suppressedCount + permanentCount;
        MegaLabel count = CreateLabel(
            $"SCENARIO  {_workingDefinition.Rules.Count}    •    PERMANENT  {permanentCount}    •    SUPPRESSED  {suppressedCount}    •    EFFECTIVE  {effectiveCount}",
            24,
            StsColors.cream,
            HorizontalAlignment.Left);
        count.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        summary.AddChild(count);
        AddSettingsActionButton(summary, "new_rule", "+  NEW RULE", 220f, CreateRule);
        _contentHost.AddChild(summary);
        _contentHost.AddChild(CreateSectionDivider());

        if (_workingDefinition.Rules.Count == 0)
        {
            MegaLabel empty = CreateLabel(
                "No rules yet. Create one to define a WHEN / IF / THEN / LIMIT flow.",
                25,
                StsColors.cream,
                HorizontalAlignment.Center);
            empty.CustomMinimumSize = new Vector2(0f, 180f);
            empty.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            _contentHost.AddChild(empty);
            return;
        }

        foreach (RuleDefinition rule in _workingDefinition.Rules)
        {
            RuleDefinition captured = rule;
            NCustomRunRuleRow row = new();
            row.Init(new CustomRunRuleRowOptions(
                captured,
                () => OpenRuleEditor(captured),
                enabled => ToggleRule(captured.Id, enabled),
                () => DuplicateRule(captured),
                () => SaveRuleAsPermanent(captured),
                () => DeleteRule(captured),
                ReorderRule,
                _readOnly,
                permanentHashes.Contains(RuleBehaviorHashService.Compute(captured))));
            _contentHost.AddChild(row);
        }
    }

    private void CreateRule()
    {
        if (_workingDefinition is null || _readOnly)
            return;

        RuleDefinition rule = new()
        {
            Name = $"Rule {_workingDefinition.Rules.Count + 1}",
            Trigger = new RuleComponentSpec { TypeId = "Loadout2:CardPlayed" },
            Conditions = new ConditionGroupDefinition
            {
                Operator = ConditionGroupOperator.And,
                Conditions = [new RuleComponentSpec { TypeId = "Loadout2:Always" }]
            },
            Actions = [new RuleComponentSpec { TypeId = "Loadout2:GainGold" }]
        };
        ApplyRuleComponentDefaults(rule.Trigger, RuleComponentKind.Trigger);
        foreach (RuleComponentSpec condition in rule.Conditions.Conditions)
            ApplyRuleComponentDefaults(condition, RuleComponentKind.Condition);
        foreach (RuleComponentSpec action in rule.Actions)
            ApplyRuleComponentDefaults(action, RuleComponentKind.Action);

        OpenRuleEditor(rule, isNew: true);
    }

    private static void ApplyRuleComponentDefaults(RuleComponentSpec component, RuleComponentKind kind)
    {
        RuleComponentDescriptor? descriptor = CustomRunRegistry.GetDescriptors(kind)
            .FirstOrDefault(candidate => string.Equals(candidate.StableId, component.TypeId, StringComparison.Ordinal));
        if (descriptor is not null)
            RuleComponentParameterService.ApplyDefaults(component, descriptor);
    }

    private void OpenRuleEditor(RuleDefinition rule, bool isNew = false)
    {
        if (_workingDefinition is null)
            return;
        NCustomRunRuleEditorScreen.OpenScenario(
            this,
            _workingDefinition,
            rule,
            _readOnly,
            saved => ApplyEditedRule(saved, isNew));
    }

    private void ApplyEditedRule(RuleDefinition saved, bool isNew)
    {
        if (_workingDefinition is null || _readOnly)
            return;

        RuleDefinition normalized = CustomRunNormalizationService.NormalizeRule(
            CustomRunNormalizationService.CloneRule(saved));
        int index = _workingDefinition.Rules.FindIndex(rule => string.Equals(rule.Id, normalized.Id, StringComparison.Ordinal));
        if (index >= 0)
            _workingDefinition.Rules[index] = normalized;
        else if (isNew)
            _workingDefinition.Rules.Add(normalized);
        else
            return;
        MarkDirty();
    }

    private void ToggleRule(string ruleId, bool enabled)
    {
        if (_workingDefinition is null || _readOnly)
            return;
        RuleDefinition? rule = _workingDefinition.Rules.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, ruleId, StringComparison.Ordinal));
        if (rule is null || rule.Enabled == enabled)
            return;
        rule.Enabled = enabled;
        MarkDirty();
        SetStatus($"Rule '{rule.Name}' {(enabled ? "enabled" : "disabled")}.", success: true);
    }

    private void DuplicateRule(RuleDefinition source)
    {
        if (_workingDefinition is null || _readOnly)
            return;
        RuleDefinition copy = CustomRunNormalizationService.CloneRule(source);
        copy.Id = Guid.NewGuid().ToString("N");
        copy.Name = $"{source.Name} Copy";
        int index = _workingDefinition.Rules.FindIndex(rule => string.Equals(rule.Id, source.Id, StringComparison.Ordinal));
        _workingDefinition.Rules.Insert(Math.Max(0, index + 1), copy);
        MarkDirty();
        RebuildContent();
        SetStatus($"Duplicated rule '{source.Name}'.", success: true);
    }

    private void SaveRuleAsPermanent(RuleDefinition source)
    {
        if (_readOnly)
            return;
        RuleDefinition saved = PermanentRuleStorageService.Upsert(source);
        SetStatus($"Saved '{saved.Name}' to Permanent Rules.", success: true);
    }

    private void DeleteRule(RuleDefinition rule)
    {
        if (_workingDefinition is null || _readOnly)
            return;
        if (!string.Equals(_deleteConfirmationId, rule.Id, StringComparison.Ordinal))
        {
            _deleteConfirmationId = rule.Id;
            SetStatus($"Press delete again to remove rule '{rule.Name}'.", success: false);
            return;
        }

        _deleteConfirmationId = null;
        if (_workingDefinition.Rules.RemoveAll(candidate =>
                string.Equals(candidate.Id, rule.Id, StringComparison.Ordinal)) == 0)
        {
            return;
        }
        MarkDirty();
        RebuildContent();
        SetStatus($"Deleted rule '{rule.Name}'.", success: true);
    }

    private void ReorderRule(string sourceId, string? targetId, bool placeAfter)
    {
        if (_workingDefinition is null || _readOnly)
            return;
        int sourceIndex = _workingDefinition.Rules.FindIndex(rule => string.Equals(rule.Id, sourceId, StringComparison.Ordinal));
        if (sourceIndex < 0)
            return;

        RuleDefinition source = _workingDefinition.Rules[sourceIndex];
        _workingDefinition.Rules.RemoveAt(sourceIndex);
        int targetIndex = targetId is null
            ? _workingDefinition.Rules.Count
            : _workingDefinition.Rules.FindIndex(rule => string.Equals(rule.Id, targetId, StringComparison.Ordinal));
        if (targetIndex < 0)
            targetIndex = _workingDefinition.Rules.Count;
        else if (placeAfter)
            targetIndex++;
        _workingDefinition.Rules.Insert(Math.Clamp(targetIndex, 0, _workingDefinition.Rules.Count), source);
        MarkDirty();
        RebuildContent();
        SetStatus($"Moved rule '{source.Name}'.", success: true);
    }

    private void BuildFoundationPanel(string tabName)
    {
        if (_contentHost is null || _workingDefinition is null)
            return;

        int count = tabName switch
        {
            "Player Choices" => _workingDefinition.PlayerChoices.Count,
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
        Action<int?> setter,
        Action? afterChanged = null)
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
            afterChanged?.Invoke();
        };
        toggle.Connect(
            NLoadoutToggle.SignalName.Toggled,
            Callable.From<NLoadoutToggle>(toggleState =>
            {
                if (_loadingFields)
                    return;
                setter(toggleState.IsChecked ? stepper.Value : null);
                MarkDirty();
                afterChanged?.Invoke();
            }));
        row.AddChild(stepper);
        _contentHost.AddChild(row);
    }

    private void BuildCharacterRestrictionEditor(RunSetupDefinition setup)
    {
        if (_contentHost is null || _lobby is null)
            return;
        SelectionSpec selection = setup.Character;
        string? ownerId = _activeSetupRoleId;
        HBoxContainer modeRow = CreateRow();
        modeRow.AddChild(CreateRowLabel("Character Selection"));
        List<LoadoutDropdownOption> options =
        [
            new LoadoutDropdownOption(SelectionMode.Default.ToString(), "No Restriction"),
            new LoadoutDropdownOption(SelectionMode.Fixed.ToString(), "Restricted")
        ];
        NLoadoutDropdown mode = new()
        {
            CustomMinimumSize = new Vector2(420f, 52f),
            SizeFlagsHorizontal = SizeFlags.ShrinkEnd,
            DropdownWidth = 420f,
        };
        string selectedMode = selection.Mode is SelectionMode.Fixed or SelectionMode.Random
            ? SelectionMode.Fixed.ToString()
            : SelectionMode.Default.ToString();
        mode.SetItems(string.Empty, options, selectedMode);
        mode.SelectedItemChanged += selected =>
        {
            if (_loadingFields || !IsSetupOwnerValid(ownerId, setup)
                || !Enum.TryParse(selected, out SelectionMode selectedSelectionMode))
                return;
            selection.Kind = SelectionModelKind.Character;
            selection.Mode = selectedSelectionMode;
            if (selectedSelectionMode != SelectionMode.Fixed)
                selection.FixedModelIds.Clear();
            else if (selection.FixedModelIds.Count == 0)
            {
                selection.FixedModelIds = ModelDb.AllCharacters
                    .Where(character => character.IsPlayable && !IsRandomCharacterModel(character))
                    .Select(character => character.Id.ToString())
                    .ToList();
            }
            MarkDirty();
            RebuildContent();
        };
        modeRow.AddChild(mode);
        _contentHost.AddChild(modeRow);

        if (selection.Mode is not (SelectionMode.Fixed or SelectionMode.Random))
            return;

        HashSet<string> selectedIds = selection.FixedModelIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        HBoxContainer characters = CreateNativeCharacterButtonRow();
        PackedScene? buttonScene = ResourceLoader.Exists(CharacterSelectButtonScenePath)
            ? GD.Load<PackedScene>(CharacterSelectButtonScenePath)
            : null;
        if (buttonScene is null)
        {
            GD.PushWarning("Loadout Custom Run: native character-select button scene was unavailable.");
            return;
        }

        bool initializingButtons = true;
        HashSet<NCharacterSelectButton> selectedFromFocus = [];
        List<NCharacterSelectButton> nativeButtons = [];
        CharacterRestrictionButtonDelegate buttonDelegate = new(
            _lobby,
            (button, character) =>
            {
                if (initializingButtons || _loadingFields || !IsSetupOwnerValid(ownerId, setup))
                    return;
                selectedFromFocus.Add(button);
                ApplyNativeCharacterSelection(
                    selection,
                    button,
                    character,
                    nativeButtons,
                    ownerId,
                    setup);
            });

        List<CharacterModel> displayedCharacters = ModelDb.AllCharacters.ToList();
        displayedCharacters.Add(ModelDb.Character<RandomCharacter>());
        foreach (CharacterModel character in displayedCharacters)
        {
            string modelId = character.Id.ToString();
            bool isRandom = IsRandomCharacterModel(character);
            bool selected = isRandom
                ? selection.Mode == SelectionMode.Random
                : selection.Mode == SelectionMode.Fixed
                  && (selectedIds.Contains(modelId) || selectedIds.Contains(character.Id.Entry));
            NCharacterSelectButton characterButton = buttonScene.Instantiate<NCharacterSelectButton>();
            characterButton.Name = $"CharacterRestriction_{character.Id.Entry}";
            characters.AddChild(characterButton);
            characterButton.Init(character, buttonDelegate);
            characterButton.GetNodeOrNull<Control>("%PlayerIconContainer")?.Hide();
            nativeButtons.Add(characterButton);
            if (selected)
                characterButton.Select();
            CommonHelpers.AttachHoverTips(
                characterButton,
                [new HoverTip(
                    new LocString("characters", character.CharacterSelectTitle),
                    new LocString("characters", character.CharacterSelectDesc))]);
            characterButton.Connect(
                NClickableControl.SignalName.Released,
                Callable.From<NClickableControl>(_ =>
                {
                    if (_loadingFields || !IsSetupOwnerValid(ownerId, setup))
                        return;
                    if (selectedFromFocus.Remove(characterButton))
                        return;
                    if (characterButton.IsSelected)
                    {
                        characterButton.Deselect();
                        RemoveNativeCharacterSelection(selection, character);
                        MarkDirty();
                        return;
                    }

                    characterButton.Select();
                    selectedFromFocus.Remove(characterButton);
                }));
        }
        initializingButtons = false;
        _contentHost.AddChild(characters);
    }

    private HBoxContainer CreateNativeCharacterButtonRow()
    {
        HBoxContainer row = new()
        {
            Alignment = BoxContainer.AlignmentMode.Center,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            MouseFilter = MouseFilterEnum.Pass
        };
        NCharacterSelectButton? sourceButton = GetTree().Root
            .FindChildren("*", nameof(NCharacterSelectButton), recursive: true, owned: false)
            .OfType<NCharacterSelectButton>()
            .FirstOrDefault(button => !IsAncestorOf(button));
        if (sourceButton?.GetParent() is HBoxContainer sourceRow)
            row.AddThemeConstantOverride("separation", sourceRow.GetThemeConstant("separation"));
        return row;
    }

    private void ApplyNativeCharacterSelection(
        SelectionSpec selection,
        NCharacterSelectButton selectedButton,
        CharacterModel character,
        IReadOnlyList<NCharacterSelectButton> buttons,
        string? ownerId,
        RunSetupDefinition setup)
    {
        if (!IsSetupOwnerValid(ownerId, setup))
            return;
        selection.Kind = SelectionModelKind.Character;
        if (IsRandomCharacterModel(character))
        {
            selection.Mode = SelectionMode.Random;
            selection.FixedModelIds.Clear();
            foreach (NCharacterSelectButton button in buttons)
            {
                if (button != selectedButton && button.IsSelected)
                    button.Deselect();
            }
        }
        else
        {
            if (selection.Mode == SelectionMode.Random)
            {
                foreach (NCharacterSelectButton button in buttons)
                {
                    if (button.IsRandom && button.IsSelected)
                        button.Deselect();
                }
            }
            selection.Mode = SelectionMode.Fixed;
            selection.FixedModelIds.RemoveAll(id =>
                string.Equals(id, character.Id.ToString(), StringComparison.OrdinalIgnoreCase)
                || string.Equals(id, character.Id.Entry, StringComparison.OrdinalIgnoreCase));
            selection.FixedModelIds.Add(character.Id.ToString());
        }
        MarkDirty();
    }

    private static void RemoveNativeCharacterSelection(SelectionSpec selection, CharacterModel character)
    {
        if (IsRandomCharacterModel(character))
        {
            if (selection.Mode == SelectionMode.Random)
                selection.Mode = SelectionMode.Fixed;
            return;
        }
        selection.Mode = SelectionMode.Fixed;
        selection.FixedModelIds.RemoveAll(id =>
            string.Equals(id, character.Id.ToString(), StringComparison.OrdinalIgnoreCase)
            || string.Equals(id, character.Id.Entry, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsRandomCharacterModel(CharacterModel character)
    {
        return character.GetType().Name.Contains("Random", StringComparison.OrdinalIgnoreCase)
               || character.Id.Entry.Contains("RANDOM", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class CharacterRestrictionButtonDelegate : ICharacterSelectButtonDelegate
    {
        private readonly Action<NCharacterSelectButton, CharacterModel> _selectionChanged;

        public CharacterRestrictionButtonDelegate(
            StartRunLobby lobby,
            Action<NCharacterSelectButton, CharacterModel> selectionChanged)
        {
            Lobby = lobby;
            _selectionChanged = selectionChanged;
        }

        public StartRunLobby Lobby { get; }

        public void SelectCharacter(NCharacterSelectButton charSelectButton, CharacterModel characterModel)
        {
            _selectionChanged(charSelectButton, characterModel);
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
        CustomRunValidationResult result = CustomRunCompiler.ValidateForLobbyLoad(_workingDefinition);
        if (result.IsValid)
        {
            SetStatus("Validation passed. Role assignments will be checked on embark.", success: true);
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
        NLoadoutPanelRoot? root = NLoadoutPanelRoot.Instance;
        string? definitionId = _workingDefinition?.Id;
        NCustomRunLibraryScreen? library = root?.GetNodeOrNull<NCustomRunLibraryScreen>(
            $"ScreenStack/{_returnRoute}");
        root?.CloseTopScreen();
        if (definitionId is not null)
            library?.RestoreDefinitionHighlight(definitionId);
    }

    private void DetachLobby(StartRunLobby lobby)
    {
        if (!ReferenceEquals(_lobby, lobby))
            return;
        _catalogSelectorSession?.Dispose();
        _catalogSelectorSession = null;
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

    private void OnPermanentRulesChanged()
    {
        if (IsNodeReady() && _activeTab == "Rules")
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

    private static HBoxContainer CreateToolRow(string label)
    {
        HBoxContainer row = CreateRow();
        row.CustomMinimumSize = new Vector2(0f, 62f);
        MegaLabel rowLabel = CreateLabel(label, 25, StsColors.gold, HorizontalAlignment.Left);
        rowLabel.CustomMinimumSize = new Vector2(260f, 58f);
        row.AddChild(rowLabel);
        return row;
    }

    private static HFlowContainer CreateInventoryPreview()
    {
        HFlowContainer preview = new()
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            MouseFilter = MouseFilterEnum.Pass
        };
        preview.AddThemeConstantOverride("h_separation", 18);
        preview.AddThemeConstantOverride("v_separation", 14);
        return preview;
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

    private static void AddToolSpacer(Control row)
    {
        row.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
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

    private static NLoadoutSettingsActionButton AddSettingsActionButton(
        Control parent,
        string id,
        string label,
        float width,
        Action action,
        bool danger = false)
    {
        NLoadoutSettingsActionButton button = CreateSettingsActionButton(id, label, width, danger);
        button.Connect(
            NClickableControl.SignalName.Released,
            Callable.From<NClickableControl>(_ => action()));
        parent.AddChild(button);
        return button;
    }

    private static NLoadoutSettingsActionButton CreateSettingsActionButton(
        string id,
        string label,
        float width,
        bool danger = false)
    {
        NLoadoutSettingsActionButton button = new()
        {
            CustomMinimumSize = new Vector2(width, 58f),
            UseDangerColor = danger
        };
        button.Init(id, label);
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
