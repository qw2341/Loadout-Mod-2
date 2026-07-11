#nullable enable

namespace Loadout.Patches.Configuration;

using Godot;
using HarmonyLib;
using Loadout.Services.CardModification;
using Loadout.Services.Configuration;
using Loadout.UI.Config;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;

[HarmonyPatch(typeof(NMainMenu), nameof(NMainMenu._Ready))]
public static class MainMenuOptionalBaseLibConfigPatch
{
    [HarmonyPostfix]
    public static void Postfix()
    {
        OptionalBaseLibIntegrationService.TryInitialize();
    }
}

[HarmonyPatch(typeof(NModInfoContainer), nameof(NModInfoContainer._Ready))]
public static class ModInfoContainerLoadoutResetReadyPatch
{
    internal const string ButtonName = "LoadoutResetPermanentCardModificationsButton";

    [HarmonyPostfix]
    public static void Postfix(NModInfoContainer __instance)
    {
        if (__instance.GetNodeOrNull<NLoadoutSettingsButton>(ButtonName) is not null)
            return;

        NLoadoutSettingsButton button = new()
        {
            Name = ButtonName,
            Visible = false,
            AnchorLeft = 0.5f,
            AnchorTop = 1f,
            AnchorRight = 0.5f,
            AnchorBottom = 1f,
            OffsetLeft = -260f,
            OffsetTop = -78f,
            OffsetRight = 260f,
            OffsetBottom = -14f
        };
        button.Connect(NClickableControl.SignalName.Released, Callable.From<NButton>(_ =>
        {
            CardModificationStateService.ResetAllPermanent();
            button.ShowResetComplete();
        }));
        __instance.AddChild(button);
    }
}

[HarmonyPatch(typeof(NModInfoContainer), nameof(NModInfoContainer.Fill))]
public static class ModInfoContainerLoadoutResetFillPatch
{
    [HarmonyPostfix]
    public static void Postfix(NModInfoContainer __instance, Mod mod)
    {
        NLoadoutSettingsButton? button = __instance.GetNodeOrNull<NLoadoutSettingsButton>(
            ModInfoContainerLoadoutResetReadyPatch.ButtonName);
        if (button is null)
            return;

        button.Visible = mod.manifest?.id == MainFile.ModId
                         && !OptionalBaseLibIntegrationService.IsActive;
    }
}
