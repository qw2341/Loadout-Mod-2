#nullable enable

namespace Loadout.UI;

using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Loadout.PanelItems;
using Loadout.Services.Loadouts;
using Loadout.Services.Targets;
using Loadout.UI.Managers;
using Loadout.UI.Screens.Controls;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Runs;

public partial class NDeckLoadoutPanel : Control
{
    private const string NodeName = "LoadoutDeckLoadoutPanel";
    private const string NoSelectionId = "__none__";
    private const string TargetKey = "deck_loadout_apply";
    private const float PanelWidth = 245f;
    private const float PanelHeight = 420f;
    private const float DropdownHeight = 38f;
    private const float ButtonHeight = 26f;

    private readonly Dictionary<string, LoadoutCatalogEntry> _entriesByOptionId = new(StringComparer.Ordinal);

    private Player? _player;
    private VBoxContainer? _content;
    private NLoadoutDropdown? _loadoutDropdown;
    private NLoadoutDropdown? _targetDropdown;
    private LineEdit? _nameInput;
    private MegaLabel? _emptyLoadoutLabel;
    private MegaLabel? _statusLabel;
    private string _selectedOptionId = NoSelectionId;
    private bool _built;
    private bool _eventsBound;

    public static void AttachTo(Control deckScreen, Player? player)
    {
        if (deckScreen.GetNodeOrNull<NDeckLoadoutPanel>(NodeName) is { } existing)
        {
            existing.SetPlayer(player);
            return;
        }

        NDeckLoadoutPanel panel = new()
        {
            Name = NodeName
        };
        panel.SetPlayer(player);
        deckScreen.AddChild(panel);
    }

    public static void DetachFrom(Control deckScreen)
    {
        NDeckLoadoutPanel? panel = deckScreen.GetNodeOrNull<NDeckLoadoutPanel>(NodeName);
        if (panel is null)
            return;

        deckScreen.RemoveChild(panel);
        panel.QueueFree();
    }

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        ZIndex = 240;
        BuildControlTree();
        BindEvents();
        RefreshAll();
    }

    public override void _ExitTree()
    {
        UnbindEvents();
    }

    private void SetPlayer(Player? player)
    {
        _player = player;
        if (IsNodeReady())
            RefreshAll();
    }

    private void BindEvents()
    {
        if (_eventsBound)
            return;

        LoadoutStorageService.Changed += RefreshAll;
        LoadoutHostSharingService.RemoteCatalogChanged += RefreshAll;
        LoadoutApplyService.WarningRaised += OnApplyWarningRaised;
        _eventsBound = true;
    }

    private void UnbindEvents()
    {
        if (!_eventsBound)
            return;

        LoadoutStorageService.Changed -= RefreshAll;
        LoadoutHostSharingService.RemoteCatalogChanged -= RefreshAll;
        LoadoutApplyService.WarningRaised -= OnApplyWarningRaised;
        _eventsBound = false;
    }

    private void BuildControlTree()
    {
        if (_built)
            return;

        _built = true;
        ApplyPanelLayout();

        MarginContainer margin = new()
        {
            Name = "MarginContainer",
            MouseFilter = MouseFilterEnum.Ignore
        };
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(margin);

        _content = new VBoxContainer
        {
            Name = "Content",
            MouseFilter = MouseFilterEnum.Ignore
        };
        _content.AddThemeConstantOverride("separation", 4);
        margin.AddChild(_content);

        _content.AddChild(CreateLabel(LocMan.Loc("DECK_LOADOUTS_TITLE", "Loadouts"), 23, StsColors.gold));

        _emptyLoadoutLabel = CreateLabel(LocMan.Loc("NO_SAVED_LOADOUTS", "No saved loadouts"), 16, StsColors.cream);
        _emptyLoadoutLabel.CustomMinimumSize = new Vector2(PanelWidth, 24f);
        _content.AddChild(_emptyLoadoutLabel);

        _loadoutDropdown = CreateCompactDropdown("LoadoutDropdown");
        _loadoutDropdown.SelectedItemChanged += OnLoadoutSelected;
        _content.AddChild(_loadoutDropdown);

        _targetDropdown = CreateCompactDropdown("TargetDropdown");
        _targetDropdown.SelectedItemChanged += OnTargetSelected;
        _content.AddChild(_targetDropdown);

        _nameInput = CreateNameInput();
        _content.AddChild(_nameInput);

        _content.AddChild(CreateButton("save_cards", LocMan.Loc("SAVE_CARDS_LOADOUT", "Save Cards"), () => SaveCurrent(LoadoutKind.Cards)));
        _content.AddChild(CreateButton("save_relics", LocMan.Loc("SAVE_RELICS_LOADOUT", "Save Relics"), () => SaveCurrent(LoadoutKind.Relics)));
        _content.AddChild(CreateButton("save_both", LocMan.Loc("SAVE_CARDS_RELICS_LOADOUT", "Save Cards + Relics"), () => SaveCurrent(LoadoutKind.CardsAndRelics)));
        _content.AddChild(CreateButton("apply", LocMan.Loc("APPLY_LOADOUT", "Apply"), ApplySelected));
        _content.AddChild(CreateButton("rename", LocMan.Loc("RENAME_LOADOUT", "Rename"), RenameSelected));
        _content.AddChild(CreateButton("delete", LocMan.Loc("DELETE_LOADOUT", "Delete"), DeleteSelected));
        _content.AddChild(CreateButton("export", LocMan.Loc("EXPORT_LOADOUT", "Export"), CopySelected));
        _content.AddChild(CreateButton("import", LocMan.Loc("IMPORT_LOADOUT", "Import"), ImportFromClipboard));

        _statusLabel = CreateLabel(string.Empty, 15, StsColors.cream);
        _statusLabel.CustomMinimumSize = new Vector2(PanelWidth, 44f);
        _content.AddChild(_statusLabel);
    }

    private void ApplyPanelLayout()
    {
        AnchorLeft = 0f;
        AnchorTop = 0f;
        AnchorRight = 0f;
        AnchorBottom = 0f;
        OffsetLeft = 18f;
        OffsetTop = 44f;
        OffsetRight = 18f + PanelWidth;
        OffsetBottom = 44f + PanelHeight;
        CustomMinimumSize = new Vector2(PanelWidth, PanelHeight);
        Size = CustomMinimumSize;
    }

    private static NLoadoutDropdown CreateCompactDropdown(string name)
    {
        return new NLoadoutDropdown
        {
            Name = name,
            CustomMinimumSize = new Vector2(PanelWidth, DropdownHeight),
            SizeFlagsHorizontal = SizeFlags.ShrinkBegin,
            DropdownWidth = PanelWidth,
            ItemHeight = 34f,
            MaxVisibleItems = 6,
            ButtonHeight = DropdownHeight,
            UseFullScreenDismisser = false,
            LabelMinFontSize = 13,
            LabelMaxFontSize = 17,
            ItemFontSize = 18
        };
    }

    private LineEdit CreateNameInput()
    {
        LineEdit lineEdit = new()
        {
            Name = "NameInput",
            PlaceholderText = LocMan.Loc("LOADOUT_NAME_PLACEHOLDER", "Loadout name"),
            CustomMinimumSize = new Vector2(PanelWidth, 32f),
            MouseFilter = MouseFilterEnum.Stop
        };
        lineEdit.AddThemeFontOverride("font", CommonHelpers.LoadGameFont("res://themes/kreon_regular_glyph_space_one.tres"));
        lineEdit.AddThemeFontSizeOverride("font_size", 17);
        lineEdit.AddThemeColorOverride("font_color", StsColors.cream);
        lineEdit.AddThemeColorOverride("font_placeholder_color", new Color(0.95f, 0.9f, 0.75f, 0.55f));
        return lineEdit;
    }

    private NDeckLoadoutTextAction CreateButton(string id, string label, Action action)
    {
        NDeckLoadoutTextAction button = new()
        {
            Name = CommonHelpers.MakeSafeNodeName($"{id}_button"),
            CustomMinimumSize = new Vector2(PanelWidth, ButtonHeight),
            SizeFlagsHorizontal = SizeFlags.ShrinkBegin
        };
        button.Init(id, label);
        button.Connect(NClickableControl.SignalName.Released, Callable.From<NClickableControl>(_ => action()));
        return button;
    }

    private static MegaLabel CreateLabel(string text, int fontSize, Color color)
    {
        MegaLabel label = new()
        {
            AutoSizeEnabled = true,
            MinFontSize = Math.Max(12, fontSize - 5),
            MaxFontSize = fontSize,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            MouseFilter = MouseFilterEnum.Ignore
        };
        label.AddThemeFontOverride("font", CommonHelpers.LoadGameFont("res://themes/kreon_bold_glyph_space_one.tres"));
        label.AddThemeFontSizeOverride("font_size", fontSize);
        label.AddThemeColorOverride("font_color", color);
        label.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f, 0.5f));
        label.AddThemeConstantOverride("outline_size", 10);
        label.SetTextAutoSize(text);
        return label;
    }

    private void RefreshAll()
    {
        if (!IsNodeReady())
            return;

        RefreshLoadoutOptions();
        RefreshTargetDropdown();
        RefreshSelectedName();
    }

    private void RefreshLoadoutOptions()
    {
        if (_loadoutDropdown is null)
            return;

        _entriesByOptionId.Clear();
        List<LoadoutDropdownOption> options = [];

        foreach (SavedLoadout loadout in LoadoutStorageService.GetLoadouts())
        {
            string optionId = $"local:{loadout.Id}";
            _entriesByOptionId[optionId] = new LoadoutCatalogEntry(optionId, loadout, Editable: true);
            options.Add(new LoadoutDropdownOption(optionId, FormatLoadoutOption(loadout)));
        }

        foreach (SavedLoadout loadout in LoadoutHostSharingService.GetRemoteLoadouts())
        {
            string optionId = $"remote:{loadout.Id}";
            _entriesByOptionId[optionId] = new LoadoutCatalogEntry(optionId, loadout, Editable: false);
            options.Add(new LoadoutDropdownOption(optionId, FormatLoadoutOption(loadout)));
        }

        if (options.Count == 0)
        {
            _selectedOptionId = NoSelectionId;
            _loadoutDropdown.Visible = false;
            if (_emptyLoadoutLabel is not null)
                _emptyLoadoutLabel.Visible = true;
            return;
        }

        if (!_entriesByOptionId.ContainsKey(_selectedOptionId))
            _selectedOptionId = options[0].Id;

        _loadoutDropdown.Visible = true;
        if (_emptyLoadoutLabel is not null)
            _emptyLoadoutLabel.Visible = false;
        _loadoutDropdown.SetItems(string.Empty, options, _selectedOptionId);
    }

    private string FormatLoadoutOption(SavedLoadout loadout)
    {
        string prefix = loadout.IsRemote ? $"{loadout.RemoteOwnerLabel ?? "Host"}: " : string.Empty;
        string kind = loadout.Kind switch
        {
            LoadoutKind.Relics => LocMan.Loc("LOADOUT_KIND_RELICS", "Relics"),
            LoadoutKind.CardsAndRelics => LocMan.Loc("LOADOUT_KIND_BOTH", "Cards + Relics"),
            _ => LocMan.Loc("LOADOUT_KIND_CARDS", "Cards")
        };
        return $"{prefix}{loadout.Name} ({kind})";
    }

    private void RefreshTargetDropdown()
    {
        if (_targetDropdown is null)
            return;

        if (!ShouldShowHostTargets())
        {
            _targetDropdown.Visible = false;
            Player? localPlayer = ResolveLocalPlayer();
            if (localPlayer is not null)
                LoadoutTargetService.SetSelected(TargetKey, LoadoutTargetSelection.ForPlayer(localPlayer.NetId), LoadoutTargetMode.AllPlayersAndPlayers);
            return;
        }

        IReadOnlyList<LoadoutDropdownOption> options = LoadoutTargetService.GetDropdownOptions(LoadoutTargetMode.AllPlayersAndPlayers);
        LoadoutTargetSelection selected = LoadoutTargetService.GetSelected(TargetKey, LoadoutTargetMode.AllPlayersAndPlayers);
        _targetDropdown.Visible = options.Count > 1;
        if (_targetDropdown.Visible)
            _targetDropdown.SetItems(string.Empty, options, selected.ToOptionId());
    }

    private void RefreshSelectedName()
    {
        if (_nameInput is null)
            return;

        _nameInput.Text = TryGetSelected(out LoadoutCatalogEntry entry) ? entry.Loadout.Name : string.Empty;
    }

    private void OnLoadoutSelected(string optionId)
    {
        _selectedOptionId = optionId;
        RefreshSelectedName();
    }

    private void OnTargetSelected(string optionId)
    {
        if (LoadoutTargetSelection.TryParseOptionId(optionId, out LoadoutTargetSelection selection))
            LoadoutTargetService.SetSelected(TargetKey, selection, LoadoutTargetMode.AllPlayersAndPlayers);
    }

    private void OnApplyWarningRaised(string message)
    {
        SetStatus(message);
    }

    private void SaveCurrent(LoadoutKind kind)
    {
        Player? player = _player ?? ResolveLocalPlayer();
        if (player is null)
        {
            SetStatus(LocMan.Loc("LOADOUT_NO_PLAYER", "No run player available."));
            return;
        }

        SavedLoadout loadout = LoadoutSerializationService.Capture(player, kind);
        loadout.Name = GetRequestedName(loadout.Name);
        _selectedOptionId = $"local:{loadout.Id}";
        SavedLoadout saved = LoadoutStorageService.Upsert(loadout);
        _selectedOptionId = $"local:{saved.Id}";
        RefreshAll();
        RefreshAllDeferred();
        SetStatus(LocMan.Loc("LOADOUT_SAVED", "Saved loadout."));
        LoadoutHostSharingService.BroadcastHostCatalog();
    }

    private void ApplySelected()
    {
        if (!TryGetSelected(out LoadoutCatalogEntry entry))
        {
            SetStatus(LocMan.Loc("LOADOUT_SELECT_FIRST", "Select a loadout first."));
            return;
        }

        if (!LoadoutApplyService.RequestApply(entry.Loadout, GetApplyTarget()))
        {
            SetStatus(LocMan.Loc("LOADOUT_APPLY_FAILED", "Could not apply loadout."));
            return;
        }

        SetStatus(LocMan.Loc("LOADOUT_APPLIED", "Applied loadout."));
    }

    private void RenameSelected()
    {
        if (!TryGetSelected(out LoadoutCatalogEntry entry) || !entry.Editable)
        {
            SetStatus(LocMan.Loc("LOADOUT_RENAME_LOCAL_ONLY", "Only local loadouts can be renamed."));
            return;
        }

        if (LoadoutStorageService.Rename(entry.Loadout.Id, GetRequestedName(entry.Loadout.Name)))
        {
            RefreshAll();
            RefreshAllDeferred();
            SetStatus(LocMan.Loc("LOADOUT_RENAMED", "Renamed loadout."));
            LoadoutHostSharingService.BroadcastHostCatalog();
        }
    }

    private void DeleteSelected()
    {
        if (!TryGetSelected(out LoadoutCatalogEntry entry) || !entry.Editable)
        {
            SetStatus(LocMan.Loc("LOADOUT_DELETE_LOCAL_ONLY", "Only local loadouts can be deleted."));
            return;
        }

        if (LoadoutStorageService.Delete(entry.Loadout.Id))
        {
            _selectedOptionId = NoSelectionId;
            RefreshAll();
            RefreshAllDeferred();
            SetStatus(LocMan.Loc("LOADOUT_DELETED", "Deleted loadout."));
            LoadoutHostSharingService.BroadcastHostCatalog();
        }
    }

    private void CopySelected()
    {
        if (!TryGetSelected(out LoadoutCatalogEntry entry))
        {
            SetStatus(LocMan.Loc("LOADOUT_SELECT_FIRST", "Select a loadout first."));
            return;
        }

        SetStatus(LoadoutClipboardService.Copy(entry.Loadout)
            ? LocMan.Loc("LOADOUT_EXPORTED", "Exported loadout.")
            : LocMan.Loc("LOADOUT_EXPORT_FAILED", "Could not export loadout."));
    }

    private void ImportFromClipboard()
    {
        if (!LoadoutClipboardService.TryImportFromClipboard(out SavedLoadout loadout, out string error))
        {
            SetStatus(error);
            return;
        }

        SavedLoadout imported = LoadoutStorageService.Import(loadout);
        _selectedOptionId = $"local:{imported.Id}";
        RefreshAll();
        RefreshAllDeferred();
        SetStatus(LocMan.Loc("LOADOUT_IMPORTED", "Imported loadout."));
        LoadoutHostSharingService.BroadcastHostCatalog();
    }

    private bool TryGetSelected(out LoadoutCatalogEntry entry)
    {
        return _entriesByOptionId.TryGetValue(_selectedOptionId, out entry);
    }

    private string GetRequestedName(string fallback)
    {
        string? text = _nameInput?.Text;
        return string.IsNullOrWhiteSpace(text) ? fallback : text.Trim();
    }

    private LoadoutTargetSelection GetApplyTarget()
    {
        if (ShouldShowHostTargets())
            return LoadoutTargetService.GetSelected(TargetKey, LoadoutTargetMode.AllPlayersAndPlayers);

        Player? localPlayer = ResolveLocalPlayer();
        return localPlayer is null
            ? LoadoutTargetService.GetSelected(TargetKey, LoadoutTargetMode.AllPlayersAndPlayers)
            : LoadoutTargetSelection.ForPlayer(localPlayer.NetId);
    }

    private static Player? ResolveLocalPlayer()
    {
        try
        {
            return RunManager.Instance.IsInProgress
                ? LocalContext.GetMe(RunManager.Instance.DebugOnlyGetState())
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool ShouldShowHostTargets()
    {
        try
        {
            RunState? runState = RunManager.Instance.DebugOnlyGetState();
            return RunManager.Instance.NetService.Type == NetGameType.Host
                   && !RunManager.Instance.IsSingleplayerOrFakeMultiplayer
                   && runState is not null
                   && runState.Players.Count > 1;
        }
        catch
        {
            return false;
        }
    }

    private void SetStatus(string status)
    {
        if (_statusLabel is not null)
            _statusLabel.SetTextAutoSize(status);
    }

    private void RefreshAllDeferred()
    {
        Callable.From(RefreshAll).CallDeferred();
    }
}

public partial class NDeckLoadoutTextAction : NButton
{
    private const int NormalOutlineSize = 7;
    private const int HoverOutlineSize = 10;

    private MegaLabel? _label;
    private string _pendingLabel = string.Empty;

    public string ActionButtonId { get; private set; } = string.Empty;

    public void Init(string id, string label)
    {
        ActionButtonId = id;
        _pendingLabel = label;

        if (IsNodeReady())
            ApplyLabel();
    }

    public override void _Ready()
    {
        FocusMode = FocusModeEnum.All;
        MouseFilter = MouseFilterEnum.Stop;
        BuildControlTree();
        ConnectSignals();
        ApplyLabel();
        ApplyVisualState(isHot: false, isPressed: false);
    }

    protected override void OnFocus()
    {
        base.OnFocus();
        ApplyVisualState(isHot: true, isPressed: false);
    }

    protected override void OnUnfocus()
    {
        base.OnUnfocus();
        ApplyVisualState(isHot: false, isPressed: false);
    }

    protected override void OnPress()
    {
        base.OnPress();
        ApplyVisualState(isHot: true, isPressed: true);
    }

    protected override void OnRelease()
    {
        base.OnRelease();
        ApplyVisualState(isHot: true, isPressed: false);
    }

    private void BuildControlTree()
    {
        CustomMinimumSize = new Vector2(
            CustomMinimumSize.X > 0f ? CustomMinimumSize.X : 245f,
            CustomMinimumSize.Y > 0f ? CustomMinimumSize.Y : 26f);

        if (GetNodeOrNull<MegaLabel>("Label") is { } existing)
        {
            _label = existing;
            return;
        }

        MegaLabel label = new()
        {
            Name = "Label",
            AutoSizeEnabled = true,
            MinFontSize = 13,
            MaxFontSize = 18,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore
        };
        label.SetAnchorsPreset(LayoutPreset.FullRect);
        label.AddThemeFontOverride("font", CommonHelpers.LoadGameFont("res://themes/kreon_bold_glyph_space_one.tres"));
        label.AddThemeFontSizeOverride("font_size", 18);
        AddChild(label);
        _label = label;
    }

    private void ApplyLabel()
    {
        _label?.SetTextAutoSize(_pendingLabel);
    }

    private void ApplyVisualState(bool isHot, bool isPressed)
    {
        if (_label is null)
            return;

        _label.AddThemeColorOverride("font_color", isPressed ? StsColors.cream : StsColors.gold);
        _label.AddThemeColorOverride("font_outline_color", isHot
            ? new Color(1f, 0.94f, 0.62f, 0.82f)
            : new Color(0f, 0f, 0f, 0.58f));
        _label.AddThemeConstantOverride("outline_size", isHot ? HoverOutlineSize : NormalOutlineSize);
    }
}
