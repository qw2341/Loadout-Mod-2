#nullable enable

namespace Loadout.Config;

using System;
using System.Reflection;
using BaseLib.Config;
using BaseLib.Config.UI;
using Godot;
using Loadout.Patches.Cards;
using Loadout.Services.Configuration;

public enum LoadoutSkin
{
    Default,
    Isaac,
    Legacy,
    STS1,
    XGGG
}

public enum LoadoutPanelAnimation
{
    YellowGlowPulse,
    DockMagnify
}

public sealed class LoadoutModConfig : SimpleModConfig
{
    public static bool EnableDeckLoadoutScreen
    {
        get => LoadoutConfigService.EnableDeckLoadoutScreen;
        set => LoadoutConfigService.EnableDeckLoadoutScreen = value;
    }

    public static LoadoutSkin PanelSkin
    {
        get => LoadoutConfigService.ActiveSkinId.ToLowerInvariant() switch
        {
            "isaac" => LoadoutSkin.Isaac,
            "legacy" => LoadoutSkin.Legacy,
            "sts1" => LoadoutSkin.STS1,
            "xggg" => LoadoutSkin.XGGG,
            _ => LoadoutSkin.Default
        };
        set => LoadoutConfigService.ActiveSkinId = value switch
        {
            LoadoutSkin.Isaac => "isaac",
            LoadoutSkin.Legacy => "legacy",
            LoadoutSkin.STS1 => "sts1",
            LoadoutSkin.XGGG => "xggg",
            _ => "default"
        };
    }

    public static LoadoutPanelAnimation PanelAnimation
    {
        get => LoadoutConfigService.ActiveAnimationId.Equals("dock_magnify", StringComparison.OrdinalIgnoreCase)
            ? LoadoutPanelAnimation.DockMagnify
            : LoadoutPanelAnimation.YellowGlowPulse;
        set => LoadoutConfigService.ActiveAnimationId = value == LoadoutPanelAnimation.DockMagnify
            ? "dock_magnify"
            : "yellow_glow_pulse";
    }

    public override void SetupConfigUI(Control optionContainer)
    {
        AddPreviewLifetime(optionContainer);

        optionContainer.AddChild(CreateSectionHeader(GetLabelText("LoadoutPanelSection"), alignToTop: true));
        AddOptionRow(optionContainer, nameof(EnableDeckLoadoutScreen), CreateRawTickboxControl);
        AddOptionRow(optionContainer, nameof(PanelSkin), CreateRawDropdownControl);
        AddOptionRow(optionContainer, nameof(PanelAnimation), CreateRawDropdownControl);

        optionContainer.AddChild(CreateSectionHeader(GetLabelText("CardModificationsSection")));
        var resetStatus = CreateRawLabelControl(GetLabelText("ResetStatusReady"), 22);
        resetStatus.Name = "PermanentCardModificationResetStatus";
        resetStatus.CustomMinimumSize = new Vector2(0f, 44f);
        resetStatus.HorizontalAlignment = HorizontalAlignment.Center;

        optionContainer.AddChild(CreateButton(
            "PermanentCardModifications",
            "ResetAllPermanentCardModifications",
            () => ResetAllPermanentCardModifications(resetStatus)));
        optionContainer.AddChild(resetStatus);

        SetupFocusNeighbors(optionContainer);
    }

    private void ResetAllPermanentCardModifications(MegaCrit.Sts2.addons.mega_text.MegaRichTextLabel status)
    {
        try
        {
            int removedCount = CardModificationPatcher.GetPermanentModificationCount();
            CardModificationPatcher.ResetAllPermanent();
            status.Text = GetLabelText(removedCount > 0
                ? "ResetStatusSucceeded"
                : "ResetStatusNothingToReset");
            status.AddThemeColorOverride("default_color", new Color("85D98B"));
        }
        catch (Exception exception)
        {
            status.Text = GetLabelText("ResetStatusFailed");
            status.AddThemeColorOverride("default_color", new Color("F07C72"));
            GD.PushError($"Loadout: failed to reset permanent card modifications. {exception}");
        }
    }

    private void AddOptionRow(
        Control optionContainer,
        string propertyName,
        Func<PropertyInfo, Control> controlFactory)
    {
        PropertyInfo property = GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Static)
                                ?? throw new MissingMemberException(GetType().FullName, propertyName);
        Control control = controlFactory(property);
        Control label = CreateRawLabelControl(GetLabelText(propertyName), 28);
        optionContainer.AddChild(new NConfigOptionRow(ModPrefix, propertyName, label, control));
    }

    private static void AddPreviewLifetime(Control optionContainer)
    {
        Control lifetime = new()
        {
            Name = "LoadoutPanelConfigPreviewLifetime",
            Visible = false,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            CustomMinimumSize = Vector2.Zero
        };
        lifetime.TreeEntered += () => LoadoutConfigService.SetConfigPanelPreviewVisible(true);
        lifetime.TreeExiting += () => LoadoutConfigService.SetConfigPanelPreviewVisible(false);
        optionContainer.AddChild(lifetime);
    }
}
