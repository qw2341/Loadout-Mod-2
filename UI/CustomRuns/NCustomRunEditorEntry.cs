#nullable enable

namespace Loadout.UI.CustomRuns;

using Godot;
using Loadout.Services.CustomRuns.Networking;
using Loadout.UI.Screens.Controls;
using MegaCrit.Sts2.Core.Entities.UI;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;

public static class NCustomRunEditorEntry
{
    private const string NodeName = "LoadoutCustomRunEditorEntry";

    public static void AttachTo(Control screen, StartRunLobby? lobby)
    {
        if (lobby is null)
        {
            DetachFrom(screen, null);
            return;
        }

        CustomRunLobbyService.RegisterLobby(lobby);
        NLoadoutActionButton? existing = screen.GetNodeOrNull<NLoadoutActionButton>(NodeName);
        if (existing is not null)
        {
            PositionButton(existing);
            return;
        }

        NLoadoutActionButton button = new()
        {
            Name = NodeName,
            CustomMinimumSize = new Vector2(350f, 48f),
            ZIndex = 24
        };
        button.Init(
            "custom_run_editor",
            lobby.NetService.Type == NetGameType.Client ? "VIEW CUSTOM RUN" : "CUSTOM RUN EDITOR");
        screen.AddChild(button);
        PositionButton(button);
        button.Connect(
            NClickableControl.SignalName.Released,
            Callable.From<NClickableControl>(_ => NCustomRunEditorScreen.Open(screen, lobby)));

        if (screen.GetNodeOrNull<Control>("ConfirmButton") is { } confirmButton)
        {
            button.FocusNeighborRight = confirmButton.GetPath();
            confirmButton.FocusNeighborLeft = button.GetPath();
        }
    }

    public static void DetachFrom(Control? screen, StartRunLobby? lobby)
    {
        if (screen is not null)
        {
            NLoadoutActionButton? button = screen.GetNodeOrNull<NLoadoutActionButton>(NodeName);
            button?.QueueFree();
        }

        if (lobby is null)
            return;
        NCustomRunEditorScreen.CloseForLobby(lobby);
        CustomRunLobbyService.UnregisterLobby(lobby);
    }

    private static void PositionButton(Control button)
    {
        button.AnchorLeft = 1f;
        button.AnchorTop = 1f;
        button.AnchorRight = 1f;
        button.AnchorBottom = 1f;
        button.OffsetLeft = -570f;
        button.OffsetTop = -337f;
        button.OffsetRight = -210f;
        button.OffsetBottom = -289f;
        button.GrowHorizontal = Control.GrowDirection.Begin;
        button.GrowVertical = Control.GrowDirection.Begin;
        button.PivotOffset = button.Size * 0.5f;
    }
}
