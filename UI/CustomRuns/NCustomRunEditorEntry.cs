#nullable enable

namespace Loadout.UI.CustomRuns;

using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Loadout.Services.Compatibility;
using Loadout.Services.CustomRuns.Models;
using Loadout.Services.CustomRuns.Networking;
using Loadout.UI.Managers;
using Loadout.UI.Screens.Controls;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Entities.UI;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Platform;

public static class NCustomRunEditorEntry
{
    internal const string NodeName = "LoadoutCustomRunEditorEntry";
    internal const string OverlayNodeName = "LoadoutCustomRunStateOverlay";
    internal const string StatusNodeName = "LoadoutCustomRunStatus";
    internal const string RoleDropdownNodeName = "LoadoutCustomRunRoleDropdown";
    internal const string PlayerDropdownNodeName = "LoadoutCustomRunPlayerDropdown";
    internal const string RoleLockButtonNodeName = "LoadoutCustomRunRoleLockButton";
    private const string PlayerRoleLabelNodeName = "LoadoutCustomRunPlayerRoleLabel";
    private static readonly Dictionary<Control, ulong> SelectedAssignmentPlayers = [];
    private static readonly HashSet<StartRunLobby> AwaitingRoleLocks = [];
    private static readonly Dictionary<StartRunLobby, string> PendingDefinitionIds = [];

    public static void AttachTo(Control screen, StartRunLobby? lobby)
    {
        if (lobby is null)
        {
            DetachFrom(screen, null);
            return;
        }

        bool canManageCustomRuns = lobby.NetService.Type != NetGameType.Client;
        NLoadoutSettingsActionButton? button = screen.GetNodeOrNull<NLoadoutSettingsActionButton>(NodeName);
        if (!canManageCustomRuns)
        {
            button?.QueueFree();
            button = null;
        }
        else if (button is null)
        {
            button = new NLoadoutSettingsActionButton
            {
                Name = NodeName,
                CustomMinimumSize = new Vector2(360f, 64f),
                UseRainbowColor = true,
                ZIndex = 24
            };
            button.Init("custom_run_editor", LocMan.Loc("CUSTOM_RUNS", "Custom Runs").ToUpperInvariant());
            screen.AddChild(button);
            button.Connect(
                NClickableControl.SignalName.Released,
                Callable.From<NClickableControl>(_ => OnButtonPressed(screen, lobby)));
        }
        if (button is not null)
            PositionButton(button);

        MegaLabel? statusLabel = screen.GetNodeOrNull<MegaLabel>(StatusNodeName);
        if (statusLabel is null)
        {
            statusLabel = CreateStatusLabel();
            screen.AddChild(statusLabel);
        }
        PositionStatusLabel(statusLabel);

        EnsureRoleControls(screen, lobby);

        NCustomRunCharacterSelectOverlay? overlay =
            screen.GetNodeOrNull<NCustomRunCharacterSelectOverlay>(OverlayNodeName);
        if (overlay is null)
        {
            overlay = new NCustomRunCharacterSelectOverlay { Name = OverlayNodeName };
            overlay.Init(screen, lobby);
            screen.AddChild(overlay);
        }
        else
        {
            overlay.RefreshLoadedRun();
        }

        RefreshAttachedState(screen, lobby, CustomRunLobbyService.GetLoadedDefinition(lobby));

        if (button is not null && screen.GetNodeOrNull<Control>("ConfirmButton") is { } confirmButton)
        {
            button.FocusNeighborTop = confirmButton.GetPath();
            confirmButton.FocusNeighborBottom = button.GetPath();
        }
    }

    public static void DetachFrom(Control? screen, StartRunLobby? lobby)
    {
        if (screen is not null)
            ClearPlayerRoleLabels(screen);
        screen?.GetNodeOrNull<NLoadoutSettingsActionButton>(NodeName)?.QueueFree();
        screen?.GetNodeOrNull<NCustomRunCharacterSelectOverlay>(OverlayNodeName)?.QueueFree();
        screen?.GetNodeOrNull<MegaLabel>(StatusNodeName)?.QueueFree();
        screen?.GetNodeOrNull<NLoadoutDropdown>(RoleDropdownNodeName)?.QueueFree();
        screen?.GetNodeOrNull<NLoadoutDropdown>(PlayerDropdownNodeName)?.QueueFree();
        screen?.GetNodeOrNull<NLoadoutSettingsActionButton>(RoleLockButtonNodeName)?.QueueFree();
        if (screen is not null)
            SelectedAssignmentPlayers.Remove(screen);

        if (lobby is null)
            return;
        AwaitingRoleLocks.Remove(lobby);
        PendingDefinitionIds.Remove(lobby);

        if (!lobby.IsAboutToBeginGame()
            && lobby.NetService.Type != MegaCrit.Sts2.Core.Multiplayer.Game.NetGameType.Client)
            CustomRunLobbyService.ClearLoadedDefinition(lobby, out _);
        NCustomRunEditorScreen.CloseForLobby(lobby);
        NCustomRunLibraryScreen.CloseForLobby(lobby);
    }

    private static void OnButtonPressed(Control screen, StartRunLobby lobby)
    {
        if (IsLocalPlayerReady(lobby))
        {
            ShowAttachedStatus(
                screen,
                LocMan.Loc("CUSTOM_RUN_UNREADY_BEFORE_SETTINGS", "Unready before changing Custom Run settings."),
                error: true);
            return;
        }
        if (CustomRunLobbyService.GetLoadedDefinition(lobby) is not null
            && lobby.NetService.Type != MegaCrit.Sts2.Core.Multiplayer.Game.NetGameType.Client)
        {
            CustomRunLobbyService.ClearLoadedDefinition(lobby, out _);
            return;
        }

        NCustomRunLibraryScreen.Open(screen, lobby);
    }

    internal static void RefreshAttachedState(
        Control? screen,
        StartRunLobby? lobby,
        CustomRunDefinition? definition)
    {
        if (screen is null)
            return;

        if (lobby is not null)
        {
            if (definition is null)
            {
                AwaitingRoleLocks.Remove(lobby);
                PendingDefinitionIds.Remove(lobby);
            }
            else if (!PendingDefinitionIds.TryGetValue(lobby, out string? pendingDefinitionId)
                     || !string.Equals(pendingDefinitionId, definition.Id, StringComparison.Ordinal))
            {
                AwaitingRoleLocks.Remove(lobby);
                PendingDefinitionIds[lobby] = definition.Id;
            }
        }

        bool loaded = definition is not null;
        NLoadoutSettingsActionButton? entryButton = screen.GetNodeOrNull<NLoadoutSettingsActionButton>(NodeName);
        entryButton?.Init("custom_run_editor", loaded
            ? LocMan.Loc("CUSTOM_RUN_CANCEL_RUN", "Cancel Run").ToUpperInvariant()
            : LocMan.Loc("CUSTOM_RUNS", "Custom Runs").ToUpperInvariant());
        if (lobby is not null)
            entryButton?.SetEnabled(lobby.NetService.Type != NetGameType.Client && !IsLocalPlayerReady(lobby));

        MegaLabel? statusLabel = screen.GetNodeOrNull<MegaLabel>(StatusNodeName);
        if (statusLabel is null)
            return;
        statusLabel.Visible = loaded;
        statusLabel.Text = loaded
            ? LocMan.Loc("CUSTOM_RUN_LOADED", "Custom Run loaded  •  {0}", CustomRunUiText.DefinitionName(definition!)).ToUpperInvariant()
            : string.Empty;
        statusLabel.TooltipText = loaded ? CustomRunUiText.DefinitionName(definition!) : string.Empty;
        statusLabel.AddThemeColorOverride("font_color", StsColors.gold);
        if (lobby is not null)
            RefreshRoleControls(screen, lobby, definition);
    }

    private static void EnsureRoleControls(Control screen, StartRunLobby lobby)
    {
        if (screen.GetNodeOrNull<NLoadoutDropdown>(PlayerDropdownNodeName) is null)
        {
            NLoadoutDropdown players = CreateRoleDropdown(PlayerDropdownNodeName);
            players.SelectedItemChanged += selected =>
            {
                if (ulong.TryParse(selected, out ulong playerId))
                    SelectedAssignmentPlayers[screen] = playerId;
                RefreshRoleControls(screen, lobby, CustomRunLobbyService.GetLoadedDefinition(lobby));
            };
            screen.AddChild(players);
        }

        if (screen.GetNodeOrNull<NLoadoutDropdown>(RoleDropdownNodeName) is null)
        {
            NLoadoutDropdown roles = CreateRoleDropdown(RoleDropdownNodeName);
            roles.SelectedItemChanged += selected =>
            {
                string? roleId = string.IsNullOrWhiteSpace(selected) ? null : selected;
                CustomRunDefinition? definition = CustomRunLobbyService.GetLoadedDefinition(lobby);
                if (definition is null)
                    return;
                ulong playerId = definition.RoleAssignmentMode == RoleAssignmentMode.HostAssigns
                    ? SelectedAssignmentPlayers.GetValueOrDefault(screen, lobby.NetService.NetId)
                    : lobby.NetService.NetId;
                if (CustomRunRoleAssignmentService.HasLockedSelection(lobby, playerId))
                    return;
                string error = string.Empty;
                bool accepted = definition.RoleAssignmentMode switch
                {
                    RoleAssignmentMode.HostAssigns =>
                        CustomRunRoleAssignmentService.PreviewAsHost(lobby, playerId, roleId, out error),
                    RoleAssignmentMode.PlayersChoose =>
                        CustomRunRoleAssignmentService.RequestLocalRolePreview(lobby, roleId, out error),
                    _ => false
                };
                if (!accepted)
                    ShowAttachedStatus(screen, error, error: true);
                RefreshRoleControls(screen, lobby, definition);
            };
            screen.AddChild(roles);
        }

        if (screen.GetNodeOrNull<NLoadoutSettingsActionButton>(RoleLockButtonNodeName) is null)
        {
            NLoadoutSettingsActionButton roleLock = new()
            {
                Name = RoleLockButtonNodeName,
                CustomMinimumSize = new Vector2(360f, 54f),
                Size = new Vector2(360f, 54f),
                ZIndex = 24
            };
            roleLock.Init("custom_run_role_lock", LocMan.Loc("CUSTOM_RUN_LOCK_IN_ROLE", "Lock In Role").ToUpperInvariant());
            roleLock.Connect(
                NClickableControl.SignalName.Released,
                Callable.From<NClickableControl>(_ => OnRoleLockPressed(screen, lobby)));
            screen.AddChild(roleLock);
        }
        PositionRoleControls(screen);
    }

    private static void RefreshRoleControls(
        Control screen,
        StartRunLobby lobby,
        CustomRunDefinition? definition)
    {
        NLoadoutDropdown? playerDropdown = screen.GetNodeOrNull<NLoadoutDropdown>(PlayerDropdownNodeName);
        NLoadoutDropdown? roleDropdown = screen.GetNodeOrNull<NLoadoutDropdown>(RoleDropdownNodeName);
        NLoadoutSettingsActionButton? roleLock =
            screen.GetNodeOrNull<NLoadoutSettingsActionButton>(RoleLockButtonNodeName);
        if (playerDropdown is null || roleDropdown is null || roleLock is null)
            return;
        PositionRoleControls(screen);

        bool hasManualRoles = definition is { Roles.Count: > 0 }
                              && definition.RoleAssignmentMode != RoleAssignmentMode.Random;
        bool hostCanAssign = hasManualRoles
                             && lobby.NetService.Type != NetGameType.Client
                             && definition!.RoleAssignmentMode == RoleAssignmentMode.HostAssigns;
        bool localCanChoose = hasManualRoles
                              && definition!.RoleAssignmentMode == RoleAssignmentMode.PlayersChoose;
        playerDropdown.Visible = hostCanAssign;
        roleDropdown.Visible = hostCanAssign || localCanChoose;
        roleLock.Visible = hostCanAssign || localCanChoose;
        if (!hasManualRoles || definition is null)
        {
            ClearPlayerRoleLabels(screen);
            return;
        }

        List<StartRunLobbyPlayerInfo> players = Sts2Compatibility.EnumerateStartRunLobbyPlayers(lobby)
            .OrderBy(player => player.SlotId)
            .ThenBy(player => player.PlayerId)
            .ToList();
        ulong selectedPlayer = hostCanAssign
            ? SelectedAssignmentPlayers.GetValueOrDefault(screen, lobby.NetService.NetId)
            : lobby.NetService.NetId;
        if (players.All(player => player.PlayerId != selectedPlayer))
            selectedPlayer = lobby.NetService.NetId;
        SelectedAssignmentPlayers[screen] = selectedPlayer;

        playerDropdown.SetItems(LocMan.Loc("CUSTOM_RUN_PLAYER", "Player").ToUpperInvariant() + "  ",
            players.Select(player => new LoadoutDropdownOption(
                player.PlayerId.ToString(),
                GetPlayerName(lobby, player))),
            selectedPlayer.ToString());
        playerDropdown.SetEnabled(hostCanAssign && !CustomRunRoleAssignmentService.IsHostReady(lobby));

        bool locked = CustomRunRoleAssignmentService.HasLockedSelection(lobby, selectedPlayer);
        if (!locked
            && lobby.NetService.Type == NetGameType.Singleplayer
            && !CustomRunRoleAssignmentService.HasPendingSelection(lobby, selectedPlayer)
            && definition.Roles.FirstOrDefault(role => role.MinimumPlayers > 0) is { } requiredRole)
        {
            bool accepted = definition.RoleAssignmentMode switch
            {
                RoleAssignmentMode.HostAssigns =>
                    CustomRunRoleAssignmentService.AssignAsHost(lobby, selectedPlayer, requiredRole.Id, out _),
                RoleAssignmentMode.PlayersChoose =>
                    CustomRunRoleAssignmentService.RequestLocalRoleLock(lobby, requiredRole.Id, out _),
                _ => false
            };
            if (accepted)
                locked = true;
        }
        string selectedRoleId = locked
            ? CustomRunRoleAssignmentService.GetRoleId(lobby, selectedPlayer) ?? string.Empty
            : CustomRunRoleAssignmentService.GetPendingRoleId(lobby, selectedPlayer) ?? string.Empty;
        IReadOnlyDictionary<ulong, string?> assignments = CustomRunRoleAssignmentService.GetAssignments(lobby);
        List<LoadoutDropdownOption> options =
        [
            new LoadoutDropdownOption(string.Empty, CustomRunUiText.DefaultRoleName(definition.DefaultRoleName))
        ];
        foreach (RoleDefinition role in definition.Roles)
        {
            int occupied = assignments.Count(pair => string.Equals(pair.Value, role.Id, StringComparison.Ordinal));
            int occupiedByOthers = assignments.Count(pair =>
                pair.Key != selectedPlayer && string.Equals(pair.Value, role.Id, StringComparison.Ordinal));
            bool available = role.MaximumPlayers == 0
                             || occupiedByOthers < role.MaximumPlayers
                             || locked && string.Equals(selectedRoleId, role.Id, StringComparison.Ordinal);
            string required = role.MinimumPlayers > 0 ? " *" : string.Empty;
            string progress = role.MinimumPlayers > 0 ? $" ({occupied}/{role.MinimumPlayers})" : string.Empty;
            string maximum = role.MaximumPlayers > 0
                ? LocMan.Loc("CUSTOM_RUN_MAX_SUFFIX", " - MAX {0}", role.MaximumPlayers)
                : string.Empty;
            options.Add(new LoadoutDropdownOption(
                role.Id,
                $"{CustomRunUiText.RoleName(role)}{required}{progress}{maximum}",
                TextColor: available ? null : new Color(0.48f, 0.48f, 0.48f),
                Enabled: available));
        }
        roleDropdown.SetItems(LocMan.Loc("CUSTOM_RUN_ROLE", "Role").ToUpperInvariant() + "  ", options, selectedRoleId);
        bool selectedReady = players.FirstOrDefault(player => player.PlayerId == selectedPlayer)?.IsReady == true;
        bool awaiting = AwaitingRoleLocks.Contains(lobby);
        bool hostReady = CustomRunRoleAssignmentService.IsHostReady(lobby);
        bool canChoose = hostCanAssign || localCanChoose && selectedPlayer == lobby.NetService.NetId;
        bool selectedAtCapacity = CustomRunRoleAssignmentService.IsRoleAtCapacity(
            lobby,
            definition,
            selectedPlayer,
            string.IsNullOrWhiteSpace(selectedRoleId) ? null : selectedRoleId);
        roleDropdown.SetEnabled(canChoose && !locked && !selectedReady && !awaiting && !hostReady);
        roleLock.Init(
            "custom_run_role_lock",
            locked
                ? LocMan.Loc("CUSTOM_RUN_UNLOCK_ROLE", "Unlock Role").ToUpperInvariant()
                : LocMan.Loc("CUSTOM_RUN_LOCK_IN_ROLE", "Lock In Role").ToUpperInvariant());
        roleLock.SetEnabled(canChoose
                            && !selectedReady
                            && !awaiting
                            && !hostReady
                            && (locked || !selectedAtCapacity));
        RefreshPlayerRoleLabels(screen, lobby, definition);
    }

    private static void OnRoleLockPressed(Control screen, StartRunLobby lobby)
    {
        CustomRunDefinition? definition = CustomRunLobbyService.GetLoadedDefinition(lobby);
        if (definition is null || definition.Roles.Count == 0)
            return;

        ulong playerId = definition.RoleAssignmentMode == RoleAssignmentMode.HostAssigns
            ? SelectedAssignmentPlayers.GetValueOrDefault(screen, lobby.NetService.NetId)
            : lobby.NetService.NetId;
        bool locked = CustomRunRoleAssignmentService.HasLockedSelection(lobby, playerId);
        string? selectedRoleId = locked
            ? CustomRunRoleAssignmentService.GetRoleId(lobby, playerId)
            : CustomRunRoleAssignmentService.GetPendingRoleId(lobby, playerId);
        if (!locked && CustomRunRoleAssignmentService.IsRoleAtCapacity(lobby, definition, playerId, selectedRoleId))
        {
            ShowAttachedStatus(
                screen,
                LocMan.Loc("CUSTOM_RUN_ROLE_AT_CAPACITY", "That role is already at maximum capacity."),
                error: true);
            return;
        }

        bool accepted;
        string error;
        if (definition.RoleAssignmentMode == RoleAssignmentMode.HostAssigns)
        {
            accepted = locked
                ? CustomRunRoleAssignmentService.UnlockAsHost(lobby, playerId, out error)
                : CustomRunRoleAssignmentService.AssignAsHost(lobby, playerId, selectedRoleId, out error);
        }
        else if (definition.RoleAssignmentMode == RoleAssignmentMode.PlayersChoose)
        {
            accepted = locked
                ? CustomRunRoleAssignmentService.RequestLocalRoleUnlock(lobby, out error)
                : CustomRunRoleAssignmentService.RequestLocalRoleLock(lobby, selectedRoleId, out error);
            if (accepted && lobby.NetService.Type == NetGameType.Client)
            {
                AwaitingRoleLocks.Add(lobby);
            }
        }
        else
        {
            return;
        }

        if (!accepted)
        {
            ShowAttachedStatus(screen, error, error: true);
        }
        RefreshRoleControls(screen, lobby, definition);
    }

    internal static bool TryHandleRoleConfirmation(StartRunLobby lobby, out string error)
    {
        error = string.Empty;
        CustomRunDefinition? definition = CustomRunLobbyService.GetLoadedDefinition(lobby);
        if (definition is null || definition.Roles.Count == 0 || definition.RoleAssignmentMode == RoleAssignmentMode.Random)
            return true;

        if (lobby.NetService.Type == NetGameType.Client)
            return true;
        if (!CustomRunRoleAssignmentService.AreAllPlayersLocked(lobby))
        {
            error = LocMan.Loc("CUSTOM_RUN_WAIT_FOR_ROLE_LOCKS", "Wait for every player to lock in a role.");
            return false;
        }
        if (!CustomRunRoleAssignmentService.AreMinimumsSatisfied(lobby, definition))
        {
            error = LocMan.Loc("CUSTOM_RUN_ROLE_MINIMUMS_NOT_FILLED", "The required role minimums have not been filled.");
            return false;
        }
        return true;
    }

    internal static bool IsRoleConfirmationBlocked(StartRunLobby lobby, CustomRunDefinition definition)
    {
        return lobby.NetService.Type != NetGameType.Client
               && definition.Roles.Count > 0
               && definition.RoleAssignmentMode != RoleAssignmentMode.Random
               && (!CustomRunRoleAssignmentService.AreAllPlayersLocked(lobby)
                   || !CustomRunRoleAssignmentService.AreMinimumsSatisfied(lobby, definition));
    }

    internal static string? GetEffectiveLocalRoleId(StartRunLobby lobby)
    {
        return CustomRunRoleAssignmentService.GetRoleId(lobby, lobby.NetService.NetId);
    }

    internal static void CompleteLocalRoleAction(Control? screen, StartRunLobby lobby, bool accepted)
    {
        AwaitingRoleLocks.Remove(lobby);
        if (screen is not null)
            RefreshRoleControls(screen, lobby, CustomRunLobbyService.GetLoadedDefinition(lobby));
    }

    private static void RefreshPlayerRoleLabels(
        Control screen,
        StartRunLobby lobby,
        CustomRunDefinition definition)
    {
        IReadOnlyDictionary<ulong, string?> assignments = CustomRunRoleAssignmentService.GetAssignments(lobby);
        IReadOnlyDictionary<ulong, string?> pendingSelections =
            CustomRunRoleAssignmentService.GetPendingSelections(lobby);
        foreach (NRemoteLobbyPlayer playerNode in EnumerateRemoteLobbyPlayers(screen))
        {
            if (playerNode.GetNodeOrNull<MegaLabel>("%NameplateLabel") is not { } nameplate)
                continue;

            bool locked = CustomRunRoleAssignmentService.HasLockedSelection(lobby, playerNode.PlayerId);
            bool pending = !locked && pendingSelections.ContainsKey(playerNode.PlayerId);
            string? roleId = locked
                ? assignments.GetValueOrDefault(playerNode.PlayerId)
                : pendingSelections.GetValueOrDefault(playerNode.PlayerId);
            MegaLabel? roleLabel = nameplate.GetParentOrNull<Control>()?
                .GetNodeOrNull<MegaLabel>(PlayerRoleLabelNodeName);
            if (definition.RoleAssignmentMode == RoleAssignmentMode.Random || !locked && !pending)
            {
                if (roleLabel is not null)
                    roleLabel.Visible = false;
                continue;
            }

            roleLabel ??= CreatePlayerRoleLabel(nameplate);
            roleLabel.Visible = true;
            roleLabel.Modulate = new Color(1f, 1f, 1f, locked ? 1f : 0.5f);
            PositionPlayerRoleLabel(nameplate, roleLabel);
            roleLabel.SetTextAutoSize($"\u2022  {GetRoleName(definition, roleId)}");
        }
    }

    private static IEnumerable<NRemoteLobbyPlayer> EnumerateRemoteLobbyPlayers(Control screen)
    {
        return screen.FindChildren("*", "Control", recursive: true, owned: false)
            .OfType<NRemoteLobbyPlayer>();
    }

    private static MegaLabel CreatePlayerRoleLabel(MegaLabel nameplate)
    {
        MegaLabel roleLabel = new()
        {
            Name = PlayerRoleLabelNodeName,
            AutoSizeEnabled = true,
            MinFontSize = nameplate.MinFontSize,
            MaxFontSize = nameplate.MaxFontSize,
            VerticalAlignment = nameplate.VerticalAlignment,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            ZIndex = 1
        };
        roleLabel.AddThemeFontOverride("font", nameplate.GetThemeFont("font"));
        roleLabel.AddThemeFontSizeOverride("font_size", nameplate.GetThemeFontSize("font_size"));
        roleLabel.AddThemeColorOverride("font_color", nameplate.GetThemeColor("font_color"));
        roleLabel.AddThemeColorOverride("font_shadow_color", nameplate.GetThemeColor("font_shadow_color"));
        roleLabel.AddThemeColorOverride("font_outline_color", nameplate.GetThemeColor("font_outline_color"));
        roleLabel.AddThemeConstantOverride("shadow_offset_x", nameplate.GetThemeConstant("shadow_offset_x"));
        roleLabel.AddThemeConstantOverride("shadow_offset_y", nameplate.GetThemeConstant("shadow_offset_y"));
        roleLabel.AddThemeConstantOverride("outline_size", nameplate.GetThemeConstant("outline_size"));
        nameplate.GetParent().AddChild(roleLabel);
        return roleLabel;
    }

    private static void PositionPlayerRoleLabel(MegaLabel nameplate, MegaLabel roleLabel)
    {
        const float gap = 12f;
        Font font = nameplate.GetThemeFont("font");
        int fontSize = nameplate.GetThemeFontSize("font_size");
        float nameWidth = font.GetStringSize(nameplate.Text, HorizontalAlignment.Left, -1f, fontSize).X;
        roleLabel.AnchorLeft = nameplate.AnchorLeft;
        roleLabel.AnchorTop = nameplate.AnchorTop;
        roleLabel.AnchorRight = nameplate.AnchorRight;
        roleLabel.AnchorBottom = nameplate.AnchorBottom;
        roleLabel.OffsetLeft = nameplate.OffsetLeft + nameWidth + gap;
        roleLabel.OffsetTop = nameplate.OffsetTop;
        roleLabel.OffsetRight = nameplate.OffsetRight;
        roleLabel.OffsetBottom = nameplate.OffsetBottom;
    }

    private static void ClearPlayerRoleLabels(Control screen)
    {
        foreach (NRemoteLobbyPlayer playerNode in EnumerateRemoteLobbyPlayers(screen))
        {
            MegaLabel? nameplate = playerNode.GetNodeOrNull<MegaLabel>("%NameplateLabel");
            nameplate?.GetParentOrNull<Control>()?
                .GetNodeOrNull<MegaLabel>(PlayerRoleLabelNodeName)?
                .QueueFree();
        }
    }

    private static NLoadoutDropdown CreateRoleDropdown(string name)
    {
        return new NLoadoutDropdown
        {
            Name = name,
            CustomMinimumSize = new Vector2(360f, 54f),
            Size = new Vector2(360f, 54f),
            DropdownWidth = 360f,
            MaxVisibleItems = 6,
            ExpandToAvailableWidth = false,
            ZIndex = 24
        };
    }

    private static void PositionRoleControls(Control screen)
    {
        Control? confirm = screen.GetNodeOrNull<Control>("ConfirmButton")
            ?? screen.GetNodeOrNull<Control>("%ConfirmButton");
        NLoadoutDropdown? roles = screen.GetNodeOrNull<NLoadoutDropdown>(RoleDropdownNodeName);
        if (confirm is null || roles is null)
            return;
        PositionRoleDropdown(confirm, roles, 2);
        if (screen.GetNodeOrNull<NLoadoutSettingsActionButton>(RoleLockButtonNodeName) is { } roleLock)
            PositionRoleDropdown(confirm, roleLock, 1);
        if (screen.GetNodeOrNull<NLoadoutDropdown>(PlayerDropdownNodeName) is { } players)
            PositionRoleDropdown(confirm, players, 3);
    }

    private static void PositionRoleDropdown(Control confirm, Control dropdown, int rowAboveConfirm)
    {
        const float width = 360f;
        const float height = 54f;
        const float gap = 10f;
        const float rightMargin = 40f;
        dropdown.AnchorLeft = 1f;
        dropdown.AnchorTop = confirm.AnchorTop;
        dropdown.AnchorRight = 1f;
        dropdown.AnchorBottom = confirm.AnchorBottom;
        dropdown.OffsetRight = -rightMargin;
        dropdown.OffsetLeft = dropdown.OffsetRight - width;
        dropdown.OffsetBottom = confirm.OffsetTop - gap - rowAboveConfirm * (height + gap);
        dropdown.OffsetTop = dropdown.OffsetBottom - height;
        dropdown.CustomMinimumSize = new Vector2(width, height);
        dropdown.Size = new Vector2(width, height);
        dropdown.PivotOffset = dropdown.Size * 0.5f;
        dropdown.GrowHorizontal = Control.GrowDirection.Begin;
        dropdown.GrowVertical = Control.GrowDirection.Begin;
    }

    internal static void ShowAttachedStatus(Control? screen, string text, bool error)
    {
        MegaLabel? statusLabel = screen?.GetNodeOrNull<MegaLabel>(StatusNodeName);
        if (statusLabel is null)
            return;
        statusLabel.Visible = true;
        statusLabel.Text = text;
        statusLabel.TooltipText = text;
        statusLabel.AddThemeColorOverride(
            "font_color",
            error ? new Color(1f, 0.58f, 0.48f) : StsColors.gold);
    }

    private static void PositionButton(Control button)
    {
        button.AnchorLeft = 1f;
        button.AnchorTop = 1f;
        button.AnchorRight = 1f;
        button.AnchorBottom = 1f;
        button.OffsetLeft = -400f;
        button.OffsetTop = -226f;
        button.OffsetRight = -40f;
        button.OffsetBottom = -162f;
        button.GrowHorizontal = Control.GrowDirection.Begin;
        button.GrowVertical = Control.GrowDirection.Begin;
        button.PivotOffset = button.Size * 0.5f;
    }

    private static MegaLabel CreateStatusLabel()
    {
        MegaLabel label = new()
        {
            Name = StatusNodeName,
            AutoSizeEnabled = false,
            MinFontSize = 18,
            MaxFontSize = 27,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Visible = false,
            ZIndex = 24
        };
        label.AddThemeFontOverride(
            "font",
            GD.Load<Font>("res://themes/kreon_bold_glyph_space_two.tres"));
        label.AddThemeFontSizeOverride("font_size", 27);
        label.AddThemeConstantOverride("outline_size", 8);
        label.AddThemeColorOverride("font_outline_color", Colors.Black);
        return label;
    }

    private static void PositionStatusLabel(Control label)
    {
        label.AnchorLeft = 0.5f;
        label.AnchorTop = 1f;
        label.AnchorRight = 0.5f;
        label.AnchorBottom = 1f;
        label.OffsetLeft = -480f;
        label.OffsetTop = -72f;
        label.OffsetRight = 480f;
        label.OffsetBottom = -18f;
        label.GrowHorizontal = Control.GrowDirection.Both;
        label.GrowVertical = Control.GrowDirection.Begin;
    }

    private static string GetRoleName(CustomRunDefinition definition, string? roleId)
    {
        if (roleId is null)
            return CustomRunUiText.DefaultRoleName(definition.DefaultRoleName);
        RoleDefinition? role = definition.Roles.FirstOrDefault(candidate => candidate.Id == roleId);
        return role is null
            ? LocMan.Loc("CUSTOM_RUN_UNKNOWN_ROLE", "Unknown Role")
            : CustomRunUiText.RoleName(role);
    }

    private static string GetPlayerName(StartRunLobby lobby, StartRunLobbyPlayerInfo player)
    {
        string name = lobby.NetService.Type == NetGameType.Singleplayer
            ? LocMan.Loc("LOADOUT_TARGET_PLAYER_FALLBACK", "Player {0}", player.SlotId + 1)
            : PlatformUtil.GetPlayerNameRaw(lobby.NetService.Platform, player.PlayerId);
        if (string.IsNullOrWhiteSpace(name))
            name = LocMan.Loc("LOADOUT_TARGET_PLAYER_FALLBACK", "Player {0}", player.SlotId + 1);
        return player.PlayerId == lobby.NetService.NetId
            ? LocMan.Loc("CUSTOM_RUN_PLAYER_YOU", "{0} (You)", name)
            : name;
    }

    private static bool IsLocalPlayerReady(StartRunLobby lobby)
    {
        return Sts2Compatibility.EnumerateStartRunLobbyPlayers(lobby)
            .FirstOrDefault(player => player.PlayerId == lobby.NetService.NetId)?.IsReady == true;
    }

}
