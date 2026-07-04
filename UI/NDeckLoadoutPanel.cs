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
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Runs;

public partial class NDeckLoadoutPanel : PanelContainer
{
    private const string NodeName = "LoadoutDeckLoadoutPanel";
    private const string NoSelectionId = "__none__";
    private const string TargetKey = "deck_loadout_apply";
    private const float PanelWidth = 360f;
    private const float ButtonHeight = 42f;

    private readonly Dictionary<string, LoadoutCatalogEntry> _entriesByOptionId = new(StringComparer.Ordinal);

    private Player? _player;
    private VBoxContainer? _content;
    private NLoadoutDropdown? _loadoutDropdown;
    private NLoadoutDropdown? _targetDropdown;
    private LineEdit? _nameInput;
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
        MouseFilter = MouseFilterEnum.Stop;
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

        StyleBoxFlat panelStyle = new()
        {
            BgColor = new Color(0.035f, 0.028f, 0.024f, 0.88f),
            BorderColor = StsColors.gold,
            CornerRadiusBottomLeft = 4,
            CornerRadiusBottomRight = 4,
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4
        };
        panelStyle.SetBorderWidthAll(2);
        panelStyle.SetContentMarginAll(10f);
        AddThemeStyleboxOverride("panel", panelStyle);

        MarginContainer margin = new()
        {
            Name = "MarginContainer",
            MouseFilter = MouseFilterEnum.Pass
        };
        margin.AddThemeConstantOverride("margin_left", 10);
        margin.AddThemeConstantOverride("margin_top", 10);
        margin.AddThemeConstantOverride("margin_right", 10);
        margin.AddThemeConstantOverride("margin_bottom", 10);
        AddChild(margin);

        _content = new VBoxContainer
        {
            Name = "Content",
            MouseFilter = MouseFilterEnum.Pass
        };
        _content.AddThemeConstantOverride("separation", 8);
        margin.AddChild(_content);

        _content.AddChild(CreateLabel(LocMan.Loc("DECK_LOADOUTS_TITLE", "Loadouts"), 28, StsColors.gold));

        _loadoutDropdown = new NLoadoutDropdown
        {
            Name = "LoadoutDropdown",
            CustomMinimumSize = new Vector2(PanelWidth - 40f, 52f),
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        _loadoutDropdown.SelectedItemChanged += OnLoadoutSelected;
        _content.AddChild(_loadoutDropdown);

        _targetDropdown = new NLoadoutDropdown
        {
            Name = "TargetDropdown",
            CustomMinimumSize = new Vector2(PanelWidth - 40f, 52f),
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        _targetDropdown.SelectedItemChanged += OnTargetSelected;
        _content.AddChild(_targetDropdown);

        _nameInput = CreateNameInput();
        _content.AddChild(_nameInput);

        _content.AddChild(CreateButton("save_cards", LocMan.Loc("SAVE_CARDS_LOADOUT", "Save Cards"), () => SaveCurrent(LoadoutKind.Cards), "CardPrinter.png"));
        _content.AddChild(CreateButton("save_relics", LocMan.Loc("SAVE_RELICS_LOADOUT", "Save Relics"), () => SaveCurrent(LoadoutKind.Relics), "LoadoutBag.png"));
        _content.AddChild(CreateButton("save_both", LocMan.Loc("SAVE_CARDS_RELICS_LOADOUT", "Save Cards + Relics"), () => SaveCurrent(LoadoutKind.CardsAndRelics), "AllInOneBag.png"));
        _content.AddChild(CreateButton("apply", LocMan.Loc("APPLY_LOADOUT", "Apply"), ApplySelected, "LoadoutCauldron.png"));
        _content.AddChild(CreateButton("rename", LocMan.Loc("RENAME_LOADOUT", "Rename"), RenameSelected));
        _content.AddChild(CreateButton("delete", LocMan.Loc("DELETE_LOADOUT", "Delete"), DeleteSelected, "TrashBin.png"));
        _content.AddChild(CreateButton("copy", LocMan.Loc("COPY_LOADOUT", "Copy"), CopySelected));
        _content.AddChild(CreateButton("import", LocMan.Loc("IMPORT_LOADOUT", "Import"), ImportFromClipboard));

        _statusLabel = CreateLabel(string.Empty, 17, StsColors.cream);
        _statusLabel.CustomMinimumSize = new Vector2(PanelWidth - 40f, 56f);
        _content.AddChild(_statusLabel);
    }

    private void ApplyPanelLayout()
    {
        AnchorLeft = 0f;
        AnchorTop = 0f;
        AnchorRight = 0f;
        AnchorBottom = 1f;
        OffsetLeft = 14f;
        OffsetTop = 156f;
        OffsetRight = 14f + PanelWidth;
        OffsetBottom = -96f;
        CustomMinimumSize = new Vector2(PanelWidth, 0f);
    }

    private LineEdit CreateNameInput()
    {
        LineEdit lineEdit = new()
        {
            Name = "NameInput",
            PlaceholderText = LocMan.Loc("LOADOUT_NAME_PLACEHOLDER", "Loadout name"),
            CustomMinimumSize = new Vector2(PanelWidth - 40f, 42f),
            MouseFilter = MouseFilterEnum.Stop
        };
        lineEdit.AddThemeFontOverride("font", CommonHelpers.LoadGameFont("res://themes/kreon_regular_glyph_space_one.tres"));
        lineEdit.AddThemeFontSizeOverride("font_size", 21);
        lineEdit.AddThemeColorOverride("font_color", StsColors.cream);
        lineEdit.AddThemeColorOverride("font_placeholder_color", new Color(0.95f, 0.9f, 0.75f, 0.55f));
        return lineEdit;
    }

    private NLoadoutActionButton CreateButton(string id, string label, Action action, string? iconFileName = null)
    {
        NLoadoutActionButton button = new()
        {
            Name = CommonHelpers.MakeSafeNodeName($"{id}_button"),
            CustomMinimumSize = new Vector2(PanelWidth - 40f, ButtonHeight),
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        button.Init(id, label, iconFileName is null ? null : CommonHelpers.LoadActionButtonIcon(iconFileName));
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
            options.Add(new LoadoutDropdownOption(NoSelectionId, LocMan.Loc("NO_LOADOUTS", "No Loadouts")));

        if (!_entriesByOptionId.ContainsKey(_selectedOptionId))
            _selectedOptionId = options[0].Id;

        _loadoutDropdown.SetItems(LocMan.Loc("LOADOUT_SELECT_LABEL", "Loadout"), options, _selectedOptionId);
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
        _targetDropdown.SetItems(LocMan.Loc("LOADOUT_TARGET", "Target"), options, selected.ToOptionId());
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
        SavedLoadout saved = LoadoutStorageService.Upsert(loadout);
        _selectedOptionId = $"local:{saved.Id}";
        RefreshAll();
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
            ? LocMan.Loc("LOADOUT_COPIED", "Copied loadout.")
            : LocMan.Loc("LOADOUT_COPY_FAILED", "Could not copy loadout."));
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
}
