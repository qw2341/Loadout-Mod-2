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
    private const string PanelToggleNodeName = "LoadoutGuestPanelAccessToggle";
    private const string DebugConsoleToggleNodeName = "LoadoutGuestDebugConsoleAccessToggle";
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

        NLoadoutToggle panelToggle = screen.GetNodeOrNull<NLoadoutToggle>(PanelToggleNodeName)
            ?? CreatePanelToggle(screen);
        panelToggle.SetChecked(LoadoutPanelAccessService.HostAllowsGuests);
        PositionAboveConfirmButton(screen, panelToggle, 0);
        panelToggle.Visible = true;

        NLoadoutToggle debugConsoleToggle = screen.GetNodeOrNull<NLoadoutToggle>(DebugConsoleToggleNodeName)
            ?? CreateDebugConsoleToggle(screen);
        debugConsoleToggle.SetChecked(LoadoutPanelAccessService.HostAllowsGuestDebugConsole);
        PositionAboveConfirmButton(screen, debugConsoleToggle, 1);
        debugConsoleToggle.Visible = true;
    }

    public static void DetachFrom(Control screen)
    {
        screen?.GetNodeOrNull<NLoadoutToggle>(PanelToggleNodeName)?.QueueFree();
        screen?.GetNodeOrNull<NLoadoutToggle>(DebugConsoleToggleNodeName)?.QueueFree();
    }

    private static NLoadoutToggle CreatePanelToggle(Control screen)
    {
        NLoadoutToggle toggle = new()
        {
            Name = PanelToggleNodeName,
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

    private static NLoadoutToggle CreateDebugConsoleToggle(Control screen)
    {
        NLoadoutToggle toggle = new()
        {
            Name = DebugConsoleToggleNodeName,
            CustomMinimumSize = new Vector2(ToggleWidth, ToggleHeight),
            ZIndex = 20
        };
        toggle.Init(
            "allow_guest_debug_console",
            LocMan.Loc("ALLOW_GUEST_DEBUG_CONSOLE", "Allow others to use debug console"),
            LoadoutPanelAccessService.HostAllowsGuestDebugConsole);
        toggle.Connect(
            NLoadoutToggle.SignalName.Toggled,
            Callable.From<NLoadoutToggle>(changed =>
                LoadoutPanelAccessService.SetHostAllowsGuestDebugConsole(changed.IsChecked)));
        screen.AddChild(toggle);
        return toggle;
    }

    private static void PositionAboveConfirmButton(Control screen, Control toggle, int row)
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
        toggle.OffsetLeft = toggle.OffsetRight - 1.5f * ToggleWidth;
        toggle.OffsetBottom = confirmButton.OffsetTop - ConfirmGap - row * (ToggleHeight + ConfirmGap);
        toggle.OffsetTop = toggle.OffsetBottom - ToggleHeight;
        toggle.Size = new Vector2(ToggleWidth, ToggleHeight);
        toggle.PivotOffset = toggle.Size * 0.5f;
    }
}
