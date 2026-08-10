#nullable enable

namespace Loadout.UI.CustomRuns;

using Godot;
using Loadout.Services.CustomRuns.Networking;
using Loadout.UI.Screens.Controls;
using MegaCrit.Sts2.Core.Entities.UI;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;

public static class NCustomRunEditorEntry
{
    internal const string NodeName = "LoadoutCustomRunEditorEntry";
    internal const string OverlayNodeName = "LoadoutCustomRunStateOverlay";

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
}
