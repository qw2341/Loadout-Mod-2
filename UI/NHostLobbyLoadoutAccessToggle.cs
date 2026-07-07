#nullable enable

namespace Loadout.UI;

using Godot;
using Loadout.Services.Loadouts;
using Loadout.UI.Managers;
using Loadout.UI.Screens.Controls;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;

public static class NHostLobbyLoadoutAccessToggle
{
    private const string NodeName = "LoadoutGuestPanelAccessToggle";
    private const float ToggleWidth = 360f;
    private const float ToggleHeight = 44f;
    private const float ConfirmGap = 10f;

    public static void AttachTo(Control screen, StartRunLobby? lobby)
    {
        if (screen is null)
            return;

        if (lobby is null || lobby.NetService.Type != NetGameType.Host || !lobby.NetService.Type.IsMultiplayer())
        {
            DetachFrom(screen);
            return;
        }

        NLoadoutToggle toggle = screen.GetNodeOrNull<NLoadoutToggle>(NodeName) ?? CreateToggle(screen);
        toggle.SetChecked(LoadoutPanelAccessService.HostAllowsGuests);
        PositionAboveConfirmButton(screen, toggle);
        toggle.Visible = true;
    }

    public static void DetachFrom(Control screen)
    {
        NLoadoutToggle? toggle = screen?.GetNodeOrNull<NLoadoutToggle>(NodeName);
        if (toggle is null)
            return;

        toggle.QueueFree();
    }

    private static NLoadoutToggle CreateToggle(Control screen)
    {
        NLoadoutToggle toggle = new()
        {
            Name = NodeName,
            CustomMinimumSize = new Vector2(ToggleWidth, ToggleHeight),
            ZIndex = 20
        };
        toggle.Init(
            "allow_guest_loadout_panel",
            LocMan.Loc("ALLOW_GUEST_LOADOUT_PANEL", "Allow guests to use Loadout Panel"),
            LoadoutPanelAccessService.HostAllowsGuests);
        toggle.Connect(
            NLoadoutToggle.SignalName.Toggled,
            Callable.From<NLoadoutToggle>(changed => LoadoutPanelAccessService.SetHostAllowsGuests(changed.IsChecked)));
        screen.AddChild(toggle);
        return toggle;
    }

    private static void PositionAboveConfirmButton(Control screen, Control toggle)
    {
        Control? confirmButton = screen.GetNodeOrNull<Control>("ConfirmButton")
            ?? screen.GetNodeOrNull<Control>("%ConfirmButton");
        if (confirmButton is null)
            return;

        toggle.AnchorLeft = confirmButton.AnchorLeft;
        toggle.AnchorTop = confirmButton.AnchorTop;
        toggle.AnchorRight = confirmButton.AnchorRight;
        toggle.AnchorBottom = confirmButton.AnchorBottom;
        toggle.OffsetRight = confirmButton.OffsetRight;
        toggle.OffsetLeft = toggle.OffsetRight - 2f * ToggleWidth;
        toggle.OffsetBottom = confirmButton.OffsetTop - ConfirmGap;
        toggle.OffsetTop = toggle.OffsetBottom - ToggleHeight;
        toggle.Size = new Vector2(ToggleWidth, ToggleHeight);
        toggle.PivotOffset = toggle.Size * 0.5f;
    }
}
