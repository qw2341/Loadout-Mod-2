#nullable enable

namespace Loadout.UI.CustomRuns;

using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Loadout.Services.Compatibility;
using Loadout.Services.CustomRuns.Models;
using Loadout.Services.CustomRuns.Networking;
using Loadout.UI.Screens.Controls;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Entities.UI;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;

public static class NCustomRunEditorEntry
{
    internal const string NodeName = "LoadoutCustomRunEditorEntry";
    internal const string OverlayNodeName = "LoadoutCustomRunStateOverlay";
    internal const string StatusNodeName = "LoadoutCustomRunStatus";
    internal const string RoleDropdownNodeName = "LoadoutCustomRunRoleDropdown";
    internal const string PlayerDropdownNodeName = "LoadoutCustomRunPlayerDropdown";
    private const string PlayerRoleLabelNodeName = "LoadoutCustomRunPlayerRole";
    private static readonly Dictionary<Control, ulong> SelectedAssignmentPlayers = [];
    private static readonly Dictionary<StartRunLobby, string?> PendingLocalRoles = [];
    private static readonly HashSet<StartRunLobby> AwaitingRoleLocks = [];
    private static readonly Dictionary<StartRunLobby, string> PendingDefinitionIds = [];

    public static void AttachTo(Control screen, StartRunLobby? lobby)
    {
        if (lobby is null)
        {
            DetachFrom(screen, null);
            return;
        }

        NLoadoutSettingsActionButton? button =
            screen.GetNodeOrNull<NLoadoutSettingsActionButton>(NodeName);
        if (button is null)
        {
            button = new NLoadoutSettingsActionButton
            {
                Name = NodeName,
                CustomMinimumSize = new Vector2(360f, 64f),
                UseRainbowColor = true,
                ZIndex = 24
            };
            button.Init("custom_run_editor", "CUSTOM RUNS");
            screen.AddChild(button);
            button.Connect(
                NClickableControl.SignalName.Released,
                Callable.From<NClickableControl>(_ => OnButtonPressed(screen, lobby)));
        }
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

        if (screen.GetNodeOrNull<Control>("ConfirmButton") is { } confirmButton)
        {
            button.FocusNeighborTop = confirmButton.GetPath();
            confirmButton.FocusNeighborBottom = button.GetPath();
        }
    }

    public static void DetachFrom(Control? screen, StartRunLobby? lobby)
    {
        screen?.GetNodeOrNull<NLoadoutSettingsActionButton>(NodeName)?.QueueFree();
        screen?.GetNodeOrNull<NCustomRunCharacterSelectOverlay>(OverlayNodeName)?.QueueFree();
        screen?.GetNodeOrNull<MegaLabel>(StatusNodeName)?.QueueFree();
        screen?.GetNodeOrNull<NLoadoutDropdown>(RoleDropdownNodeName)?.QueueFree();
        screen?.GetNodeOrNull<NLoadoutDropdown>(PlayerDropdownNodeName)?.QueueFree();
        if (screen is not null)
            SelectedAssignmentPlayers.Remove(screen);

        if (lobby is null)
            return;
        PendingLocalRoles.Remove(lobby);
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
                PendingLocalRoles.Remove(lobby);
                AwaitingRoleLocks.Remove(lobby);
                PendingDefinitionIds.Remove(lobby);
            }
            else if (!PendingDefinitionIds.TryGetValue(lobby, out string? pendingDefinitionId)
                     || !string.Equals(pendingDefinitionId, definition.Id, StringComparison.Ordinal))
            {
                PendingLocalRoles.Remove(lobby);
                AwaitingRoleLocks.Remove(lobby);
                PendingDefinitionIds[lobby] = definition.Id;
            }
        }

        bool loaded = definition is not null;
        screen.GetNodeOrNull<NLoadoutSettingsActionButton>(NodeName)
            ?.Init("custom_run_editor", loaded ? "CANCEL RUN" : "CUSTOM RUNS");

        MegaLabel? statusLabel = screen.GetNodeOrNull<MegaLabel>(StatusNodeName);
        if (statusLabel is null)
            return;
        statusLabel.Visible = loaded;
        statusLabel.Text = loaded
            ? $"CUSTOM RUN LOADED  •  {GetDefinitionName(definition!)}"
            : string.Empty;
        statusLabel.TooltipText = loaded ? GetDefinitionName(definition!) : string.Empty;
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
                bool accepted;
                string error;
                if (definition.RoleAssignmentMode == RoleAssignmentMode.HostAssigns)
                {
                    ulong playerId = SelectedAssignmentPlayers.GetValueOrDefault(screen, lobby.NetService.NetId);
                    accepted = CustomRunRoleAssignmentService.AssignAsHost(lobby, playerId, roleId, out error);
                }
                else
                {
                    PendingLocalRoles[lobby] = roleId;
                    accepted = true;
                    error = string.Empty;
                    RefreshRoleControls(screen, lobby, definition);
                    screen.GetNodeOrNull<NCustomRunCharacterSelectOverlay>(OverlayNodeName)?.RefreshRoleGate();
                }
                if (!accepted)
                    ShowAttachedStatus(screen, error, error: true);
            };
            screen.AddChild(roles);
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
        if (playerDropdown is null || roleDropdown is null)
            return;
        PositionRoleControls(screen);

        bool visible = definition is { Roles.Count: > 0 };
        bool hostCanAssign = visible
                             && lobby.NetService.Type != NetGameType.Client
                             && definition?.RoleAssignmentMode == RoleAssignmentMode.HostAssigns;
        playerDropdown.Visible = hostCanAssign;
        roleDropdown.Visible = visible;
        if (!visible || definition is null)
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

        playerDropdown.SetItems("PLAYER  ",
            players.Select(player => new LoadoutDropdownOption(
                player.PlayerId.ToString(),
                player.PlayerId == lobby.NetService.NetId
                    ? $"Player {player.SlotId + 1} (You)"
                    : $"Player {player.SlotId + 1}")),
            selectedPlayer.ToString());
        playerDropdown.SetEnabled(hostCanAssign);

        if (definition.RoleAssignmentMode == RoleAssignmentMode.Random)
        {
            roleDropdown.SetItems(string.Empty,
                [new LoadoutDropdownOption(string.Empty, "Roles resolve on embark")],
                string.Empty);
            roleDropdown.SetEnabled(false);
            RefreshPlayerRoleLabels(screen, lobby, definition);
            return;
        }

        string selectedRoleId = definition.RoleAssignmentMode == RoleAssignmentMode.PlayersChoose
                                && selectedPlayer == lobby.NetService.NetId
                                && PendingLocalRoles.TryGetValue(lobby, out string? pendingRoleId)
            ? pendingRoleId ?? string.Empty
            : CustomRunRoleAssignmentService.GetRoleId(lobby, selectedPlayer) ?? string.Empty;
        IReadOnlyDictionary<ulong, string?> assignments = CustomRunRoleAssignmentService.GetAssignments(lobby);
        List<LoadoutDropdownOption> options =
        [
            new LoadoutDropdownOption(string.Empty, definition.DefaultRoleName)
        ];
        foreach (RoleDefinition role in definition.Roles)
        {
            int occupied = assignments.Count(pair => string.Equals(pair.Value, role.Id, StringComparison.Ordinal));
            int occupiedByOthers = assignments.Count(pair =>
                pair.Key != selectedPlayer && string.Equals(pair.Value, role.Id, StringComparison.Ordinal));
            if (role.MaximumPlayers == 0
                || occupiedByOthers < role.MaximumPlayers
                || string.Equals(selectedRoleId, role.Id, StringComparison.Ordinal))
            {
                string required = role.MinimumPlayers > 0 ? " *" : string.Empty;
                string progress = role.MinimumPlayers > 0 ? $" ({occupied}/{role.MinimumPlayers})" : string.Empty;
                string maximum = role.MaximumPlayers > 0 ? $" - MAX {role.MaximumPlayers}" : string.Empty;
                options.Add(new LoadoutDropdownOption(role.Id, $"{role.Name}{required}{progress}{maximum}"));
            }
        }
        roleDropdown.SetItems("ROLE  ", options, selectedRoleId);
        bool selectedReady = players.FirstOrDefault(player => player.PlayerId == selectedPlayer)?.IsReady == true;
        bool canChoose = definition.RoleAssignmentMode == RoleAssignmentMode.PlayersChoose
            ? selectedPlayer == lobby.NetService.NetId
            : hostCanAssign;
        roleDropdown.SetEnabled(canChoose && !selectedReady && !AwaitingRoleLocks.Contains(lobby));
        RefreshPlayerRoleLabels(screen, lobby, definition);
    }

    internal static bool TryHandleRoleConfirmation(
        Control screen,
        StartRunLobby lobby,
        out bool awaitingHost,
        out string error)
    {
        awaitingHost = false;
        error = string.Empty;
        CustomRunDefinition? definition = CustomRunLobbyService.GetLoadedDefinition(lobby);
        if (definition is null || definition.Roles.Count == 0 || definition.RoleAssignmentMode == RoleAssignmentMode.Random)
            return true;

        StartRunLobbyPlayerInfo? localPlayer = Sts2Compatibility.EnumerateStartRunLobbyPlayers(lobby)
            .FirstOrDefault(player => player.PlayerId == lobby.NetService.NetId);
        if (localPlayer?.IsReady == true)
            return true;

        if (definition.RoleAssignmentMode == RoleAssignmentMode.PlayersChoose)
        {
            string? selectedRoleId = PendingLocalRoles.TryGetValue(lobby, out string? pending)
                ? pending
                : CustomRunRoleAssignmentService.GetRoleId(lobby, lobby.NetService.NetId);
            if (CustomRunRoleAssignmentService.IsRoleAtCapacity(
                    lobby,
                    definition,
                    lobby.NetService.NetId,
                    selectedRoleId))
            {
                error = "That role is already at maximum capacity.";
                return false;
            }

            if (lobby.NetService.Type == NetGameType.Client)
            {
                if (AwaitingRoleLocks.Contains(lobby))
                {
                    awaitingHost = true;
                    return false;
                }
                if (!CustomRunRoleAssignmentService.RequestLocalRole(lobby, selectedRoleId, out error))
                    return false;
                AwaitingRoleLocks.Add(lobby);
                awaitingHost = true;
                RefreshRoleControls(screen, lobby, definition);
                return false;
            }

            if (!CustomRunRoleAssignmentService.RequestLocalRole(lobby, selectedRoleId, out error))
                return false;
            PendingLocalRoles.Remove(lobby);
        }

        if (lobby.NetService.Type == NetGameType.Client)
            return true;
        if (definition.RoleAssignmentMode == RoleAssignmentMode.PlayersChoose)
        {
            bool everyPlayerLocked = Sts2Compatibility.EnumerateStartRunLobbyPlayerIds(lobby)
                .All(playerId => CustomRunRoleAssignmentService.HasLockedSelection(lobby, playerId));
            if (!everyPlayerLocked)
            {
                error = "Wait for every player to lock in a role.";
                return false;
            }
        }
        if (!CustomRunRoleAssignmentService.AreMinimumsSatisfied(lobby, definition))
        {
            error = "The required role minimums have not been filled.";
            return false;
        }
        return true;
    }

    internal static bool IsRoleConfirmationBlocked(StartRunLobby lobby, CustomRunDefinition definition)
    {
        StartRunLobbyPlayerInfo? localPlayer = Sts2Compatibility.EnumerateStartRunLobbyPlayers(lobby)
            .FirstOrDefault(player => player.PlayerId == lobby.NetService.NetId);
        if (localPlayer?.IsReady == true || definition.Roles.Count == 0
            || definition.RoleAssignmentMode == RoleAssignmentMode.Random)
        {
            return false;
        }

        string? proposedRoleId = PendingLocalRoles.TryGetValue(lobby, out string? pending)
            ? pending
            : CustomRunRoleAssignmentService.GetRoleId(lobby, lobby.NetService.NetId);
        if (definition.RoleAssignmentMode == RoleAssignmentMode.PlayersChoose
            && CustomRunRoleAssignmentService.IsRoleAtCapacity(
                lobby,
                definition,
                lobby.NetService.NetId,
                proposedRoleId))
        {
            return true;
        }
        if (lobby.NetService.Type == NetGameType.Client)
            return AwaitingRoleLocks.Contains(lobby);
        if (definition.RoleAssignmentMode == RoleAssignmentMode.PlayersChoose)
        {
            bool othersLocked = Sts2Compatibility.EnumerateStartRunLobbyPlayerIds(lobby)
                .Where(playerId => playerId != lobby.NetService.NetId)
                .All(playerId => CustomRunRoleAssignmentService.HasLockedSelection(lobby, playerId));
            if (!othersLocked)
                return true;
            return !CustomRunRoleAssignmentService.AreMinimumsSatisfied(
                lobby,
                definition,
                lobby.NetService.NetId,
                proposedRoleId);
        }
        return definition.RoleAssignmentMode == RoleAssignmentMode.HostAssigns
               && !CustomRunRoleAssignmentService.AreMinimumsSatisfied(lobby, definition);
    }

    internal static void CompleteLocalRoleLock(Control? screen, StartRunLobby lobby, bool accepted)
    {
        AwaitingRoleLocks.Remove(lobby);
        if (accepted)
            PendingLocalRoles.Remove(lobby);
        if (screen is not null)
            RefreshRoleControls(screen, lobby, CustomRunLobbyService.GetLoadedDefinition(lobby));
    }

    private static void RefreshPlayerRoleLabels(
        Control screen,
        StartRunLobby lobby,
        CustomRunDefinition definition)
    {
        IReadOnlyDictionary<ulong, string?> assignments = CustomRunRoleAssignmentService.GetAssignments(lobby);
        foreach (Node node in screen.FindChildren("*", "NRemoteLobbyPlayer", recursive: true, owned: false))
        {
            if (node is not NRemoteLobbyPlayer playerNode || node is not Control playerControl)
                continue;
            MegaLabel? label = playerControl.GetNodeOrNull<MegaLabel>(PlayerRoleLabelNodeName);
            if (label is null)
            {
                label = CreatePlayerRoleLabel();
                playerControl.AddChild(label);
            }
            bool locked = CustomRunRoleAssignmentService.HasLockedSelection(lobby, playerNode.PlayerId);
            assignments.TryGetValue(playerNode.PlayerId, out string? roleId);
            string roleName = definition.RoleAssignmentMode == RoleAssignmentMode.Random
                ? "Random role"
                : !locked && definition.RoleAssignmentMode == RoleAssignmentMode.PlayersChoose
                ? "Choosing role..."
                : roleId is null
                    ? definition.DefaultRoleName
                    : definition.Roles.FirstOrDefault(role => role.Id == roleId)?.Name ?? "Unknown Role";
            label.Text = roleName;
        }
    }

    private static MegaLabel CreatePlayerRoleLabel()
    {
        MegaLabel label = CreateStatusLabel();
        label.Name = PlayerRoleLabelNodeName;
        label.Visible = true;
        label.MinFontSize = 15;
        label.MaxFontSize = 20;
        label.HorizontalAlignment = HorizontalAlignment.Left;
        label.AnchorLeft = 1f;
        label.AnchorTop = 0f;
        label.AnchorRight = 1f;
        label.AnchorBottom = 1f;
        label.OffsetLeft = 12f;
        label.OffsetRight = 300f;
        label.OffsetTop = 0f;
        label.OffsetBottom = 0f;
        label.AddThemeColorOverride("font_color", StsColors.gold);
        return label;
    }

    private static void ClearPlayerRoleLabels(Control screen)
    {
        foreach (Node node in screen.FindChildren(PlayerRoleLabelNodeName, string.Empty, recursive: true, owned: false))
            node.QueueFree();
    }

    private static NLoadoutDropdown CreateRoleDropdown(string name)
    {
        return new NLoadoutDropdown
        {
            Name = name,
            CustomMinimumSize = new Vector2(360f, 54f),
            DropdownWidth = 360f,
            MaxVisibleItems = 6,
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
        PositionRoleDropdown(confirm, roles, 0);
        if (screen.GetNodeOrNull<NLoadoutDropdown>(PlayerDropdownNodeName) is { } players)
            PositionRoleDropdown(confirm, players, 1);
    }

    private static void PositionRoleDropdown(Control confirm, Control dropdown, int rowAboveConfirm)
    {
        const float width = 360f;
        const float height = 54f;
        const float gap = 10f;
        dropdown.AnchorLeft = confirm.AnchorLeft;
        dropdown.AnchorTop = confirm.AnchorTop;
        dropdown.AnchorRight = confirm.AnchorRight;
        dropdown.AnchorBottom = confirm.AnchorBottom;
        dropdown.OffsetRight = confirm.OffsetRight;
        dropdown.OffsetLeft = dropdown.OffsetRight - width;
        dropdown.OffsetBottom = confirm.OffsetTop - gap - rowAboveConfirm * (height + gap);
        dropdown.OffsetTop = dropdown.OffsetBottom - height;
        dropdown.Size = new Vector2(width, height);
        dropdown.PivotOffset = dropdown.Size * 0.5f;
        dropdown.GrowHorizontal = Control.GrowDirection.Both;
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

    private static string GetDefinitionName(CustomRunDefinition definition)
    {
        return string.IsNullOrWhiteSpace(definition.Name) ? "Unnamed Custom Run" : definition.Name;
    }
}
