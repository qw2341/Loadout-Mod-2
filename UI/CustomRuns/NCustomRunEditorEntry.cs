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

public static class NCustomRunEditorEntry
{
    internal const string NodeName = "LoadoutCustomRunEditorEntry";
    internal const string OverlayNodeName = "LoadoutCustomRunStateOverlay";
    internal const string StatusNodeName = "LoadoutCustomRunStatus";
    internal const string RoleDropdownNodeName = "LoadoutCustomRunRoleDropdown";
    internal const string PlayerDropdownNodeName = "LoadoutCustomRunPlayerDropdown";
    private static readonly Dictionary<Control, ulong> SelectedAssignmentPlayers = [];

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
            PositionRoleDropdown(players, -370f);
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
            PositionRoleDropdown(roles, -306f);
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
                    accepted = CustomRunRoleAssignmentService.RequestLocalRole(lobby, roleId, out error);
                }
                if (!accepted)
                    ShowAttachedStatus(screen, error, error: true);
            };
            screen.AddChild(roles);
        }
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

        bool visible = definition is { Roles.Count: > 0 };
        bool hostCanAssign = visible
                             && lobby.NetService.Type != NetGameType.Client
                             && definition?.RoleAssignmentMode == RoleAssignmentMode.HostAssigns;
        playerDropdown.Visible = hostCanAssign;
        roleDropdown.Visible = visible;
        if (!visible || definition is null)
            return;

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
            return;
        }

        string selectedRoleId = CustomRunRoleAssignmentService.GetRoleId(lobby, selectedPlayer) ?? string.Empty;
        IReadOnlyDictionary<ulong, string?> assignments = CustomRunRoleAssignmentService.GetAssignments(lobby);
        List<LoadoutDropdownOption> options = [new LoadoutDropdownOption(string.Empty, "No Role")];
        foreach (RoleDefinition role in definition.Roles)
        {
            int occupied = assignments.Count(pair => string.Equals(pair.Value, role.Id, StringComparison.Ordinal));
            int occupiedByOthers = assignments.Count(pair =>
                pair.Key != selectedPlayer && string.Equals(pair.Value, role.Id, StringComparison.Ordinal));
            if (occupiedByOthers < role.MaximumPlayers || string.Equals(selectedRoleId, role.Id, StringComparison.Ordinal))
                options.Add(new LoadoutDropdownOption(role.Id, $"{role.Name}  ({occupied}/{role.MaximumPlayers})"));
        }
        roleDropdown.SetItems("ROLE  ", options, selectedRoleId);
        bool selectedReady = players.FirstOrDefault(player => player.PlayerId == selectedPlayer)?.IsReady == true;
        bool canChoose = definition.RoleAssignmentMode == RoleAssignmentMode.PlayersChoose
            ? selectedPlayer == lobby.NetService.NetId
            : hostCanAssign;
        roleDropdown.SetEnabled(canChoose && !selectedReady);
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

    private static void PositionRoleDropdown(Control dropdown, float top)
    {
        dropdown.AnchorLeft = 1f;
        dropdown.AnchorTop = 1f;
        dropdown.AnchorRight = 1f;
        dropdown.AnchorBottom = 1f;
        dropdown.OffsetLeft = -400f;
        dropdown.OffsetTop = top;
        dropdown.OffsetRight = -40f;
        dropdown.OffsetBottom = top + 54f;
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

    private static string GetDefinitionName(CustomRunDefinition definition)
    {
        return string.IsNullOrWhiteSpace(definition.Name) ? "Unnamed Custom Run" : definition.Name;
    }
}
