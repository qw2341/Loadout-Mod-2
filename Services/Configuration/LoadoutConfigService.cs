#nullable enable

namespace Loadout.Services.Configuration;

using System;
using Godot;
using Loadout.UI;
using Loadout.UI.Managers;

public static class LoadoutConfigService
{
    private static bool _enableDeckLoadoutScreen = true;

    public static event Action? DeckLoadoutScreenVisibilityChanged;

    public static bool EnableDeckLoadoutScreen
    {
        get => _enableDeckLoadoutScreen;
        set
        {
            if (_enableDeckLoadoutScreen == value)
                return;

            _enableDeckLoadoutScreen = value;
            DeckLoadoutScreenVisibilityChanged?.Invoke();
        }
    }

    public static string ActiveSkinId
    {
        get => LoadoutSkinManager.ActiveSkinId;
        set => LoadoutSkinManager.SetActiveSkin(value);
    }

    public static string ActiveAnimationId
    {
        get => LoadoutPanelItemAnimationManager.ActiveAnimationId;
        set => LoadoutPanelItemAnimationManager.SetActiveAnimation(value);
    }

    public static void SetConfigPanelPreviewVisible(bool visible)
    {
        if (visible && Engine.GetMainLoop() is SceneTree tree)
            NLoadoutPanelRoot.GetOrAttach(tree);

        NLoadoutPanel.SetConfigPreviewVisible(visible);
    }
}
