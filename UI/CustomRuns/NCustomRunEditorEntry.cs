#nullable enable

namespace Loadout.UI.CustomRuns;

using System;
using Godot;
using Loadout.Services.CustomRuns.Models;
using Loadout.Services.CustomRuns.Networking;
using Loadout.UI.Screens.Controls;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Entities.UI;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;

public static class NCustomRunEditorEntry
{
    internal const string NodeName = "LoadoutCustomRunEditorEntry";
    internal const string OverlayNodeName = "LoadoutCustomRunStateOverlay";
    internal const string StatusNodeName = "LoadoutCustomRunStatus";

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

        RefreshAttachedState(screen, CustomRunLobbyService.GetLoadedDefinition(lobby));

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

    internal static void RefreshAttachedState(Control? screen, CustomRunDefinition? definition)
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
