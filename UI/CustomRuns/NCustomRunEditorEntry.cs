#nullable enable

namespace Loadout.UI.CustomRuns;

using Godot;
using Loadout.UI.Screens.Controls;
using MegaCrit.Sts2.Core.Entities.UI;
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

        NLoadoutSettingsActionButton? existing = screen.GetNodeOrNull<NLoadoutSettingsActionButton>(NodeName);
        if (existing is not null)
        {
            PositionButton(existing);
            return;
        }

        NLoadoutSettingsActionButton button = new()
        {
            Name = NodeName,
            CustomMinimumSize = new Vector2(360f, 64f),
            UseRainbowColor = true,
            ZIndex = 24
        };
        button.Init(
            "custom_run_editor",
            "CUSTOM RUNS");
        screen.AddChild(button);
        PositionButton(button);
        button.Connect(
            NClickableControl.SignalName.Released,
            Callable.From<NClickableControl>(_ => NCustomRunLibraryScreen.Open(screen, lobby)));

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
            NLoadoutSettingsActionButton? button = screen.GetNodeOrNull<NLoadoutSettingsActionButton>(NodeName);
            button?.QueueFree();
        }

        if (lobby is null)
            return;
        NCustomRunEditorScreen.CloseForLobby(lobby);
        NCustomRunLibraryScreen.CloseForLobby(lobby);
    }

    private static void PositionButton(Control button)
    {
        button.AnchorLeft = 1f;
        button.AnchorTop = 1f;
        button.AnchorRight = 1f;
        button.AnchorBottom = 1f;
        button.AnchorLeft = 0.5f;
        button.AnchorRight = 0.5f;
        button.OffsetLeft = -180f;
        button.OffsetTop = -72f;
        button.OffsetRight = 180f;
        button.OffsetBottom = -8f;
        button.GrowHorizontal = Control.GrowDirection.Both;
        button.GrowVertical = Control.GrowDirection.Begin;
        button.PivotOffset = button.Size * 0.5f;
    }
}
