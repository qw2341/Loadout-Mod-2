#nullable enable

namespace Loadout.Config;

using System;
using System.Collections.Generic;
using System.Reflection;
using BaseLib.Config;
using BaseLib.Config.UI;
using Godot;
using Loadout.Companions;
using Loadout.Services.CardModification;
using Loadout.Patches.Cards.CardModification;
using Loadout.Services.Configuration;
using Loadout.Services.RelicModification;
using Loadout.UI.Screens.Controls;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Runs;

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
    private const float BaseLibDropdownWidth = 324f;
    private const float BaseLibDropdownHeight = 64f;

    private static readonly PropertyInfo HoverTipTitleProperty =
        typeof(HoverTip).GetProperty(nameof(HoverTip.Title), BindingFlags.Instance | BindingFlags.Public)
        ?? throw new MissingMemberException(typeof(HoverTip).FullName, nameof(HoverTip.Title));

    public static bool EnableDeckLoadoutScreen
    {
        get => LoadoutConfigService.EnableDeckLoadoutScreen;
        set => LoadoutConfigService.EnableDeckLoadoutScreen = value;
    }

    public static bool EnableCreatureManipulationPanel
    {
        get => LoadoutConfigService.EnableCreatureManipulationPanel;
        set => LoadoutConfigService.EnableCreatureManipulationPanel = value;
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

    public static string Companion
    {
        get => LoadoutConfigService.ActiveCompanionId;
        set => LoadoutConfigService.ActiveCompanionId = value;
    }

    public override void SetupConfigUI(Control optionContainer)
    {
        AddPreviewLifetime(optionContainer);

        optionContainer.AddChild(CreateSectionHeader(GetLabelText("LoadoutPanelSection"), alignToTop: true));
        AddOptionRow(optionContainer, nameof(EnableDeckLoadoutScreen), CreateRawTickboxControl);
        AddOptionRow(optionContainer, nameof(EnableCreatureManipulationPanel), CreateRawTickboxControl);
        AddOptionRow(optionContainer, nameof(PanelSkin), CreateRawDropdownControl);
        AddOptionRow(optionContainer, nameof(PanelAnimation), CreateRawDropdownControl);
        AddOptionRow(optionContainer, nameof(Companion), CreateCompanionDropdownControl);

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

        optionContainer.AddChild(CreateSectionHeader(GetLabelText("RelicModificationsSection")));
        var relicResetStatus = CreateRawLabelControl(GetLabelText("ResetStatusReady"), 22);
        relicResetStatus.Name = "PermanentRelicModificationResetStatus";
        relicResetStatus.CustomMinimumSize = new Vector2(0f, 44f);
        relicResetStatus.HorizontalAlignment = HorizontalAlignment.Center;

        optionContainer.AddChild(CreateButton(
            "PermanentRelicModifications",
            "ResetAllPermanentRelicModifications",
            () => ResetAllPermanentRelicModifications(relicResetStatus)));
        optionContainer.AddChild(relicResetStatus);

        SetupFocusNeighbors(optionContainer);
    }

    private void ResetAllPermanentCardModifications(MegaCrit.Sts2.addons.mega_text.MegaRichTextLabel status)
    {
        try
        {
            int removedCount = CardModificationRuntime.GetPermanentModificationCount();
            CardModificationRuntime.ResetAllPermanent();
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

    private void ResetAllPermanentRelicModifications(MegaCrit.Sts2.addons.mega_text.MegaRichTextLabel status)
    {
        try
        {
            int removedCount = RelicModificationStateService.GetPermanentModificationCount();
            RelicModificationStateService.ResetAllPermanent();
            status.Text = GetLabelText(removedCount > 0
                ? "RelicResetStatusSucceeded"
                : "RelicResetStatusNothingToReset");
            status.AddThemeColorOverride("default_color", new Color("85D98B"));
        }
        catch (Exception exception)
        {
            status.Text = GetLabelText("RelicResetStatusFailed");
            status.AddThemeColorOverride("default_color", new Color("F07C72"));
            GD.PushError($"Loadout: failed to reset permanent relic modifications. {exception}");
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

    private Control CreateCompanionDropdownControl(PropertyInfo _)
    {
        LoadoutCompanionRegistry.Initialize();
        HoverTip noneHoverTip = CreateCompanionHoverTip(
            GetLabelText("CompanionNoneName"),
            GetLabelText("CompanionNoneDescription"));

        List<LoadoutDropdownOption> options =
        [
            new LoadoutDropdownOption(
                LoadoutCompanionRegistry.NoneId,
                GetLabelText("CompanionNoneName"),
                () => [noneHoverTip],
                TextColor: StsColors.cream)
        ];

        foreach (LoadoutCompanion companion in LoadoutCompanionRegistry.Companions)
        {
            string name = GetLabelText(companion.NameLocalizationKey);
            string description = GetLabelText(companion.TooltipLocalizationKey);
            Texture2D? icon = LoadoutCompanionRegistry.GetTexture(companion);
            Color textColor = companion.SelectionColor ?? StsColors.cream;
            HoverTip hoverTip = CreateCompanionHoverTip(name, description, icon);

            options.Add(new LoadoutDropdownOption(
                companion.CompanionId,
                name,
                () => [hoverTip],
                icon,
                textColor));
        }

        NLoadoutDropdown dropdown = new()
        {
            Name = "CompanionDropdown",
            DropdownWidth = BaseLibDropdownWidth,
            ButtonHeight = BaseLibDropdownHeight,
            CustomMinimumSize = new Vector2(BaseLibDropdownWidth, BaseLibDropdownHeight),
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd,
            SizeFlagsVertical = Control.SizeFlags.Fill,
            ExpandToAvailableWidth = false
        };
        dropdown.SetItems(string.Empty, options, Companion);
        dropdown.SelectedItemChanged += selectedId =>
        {
            Companion = selectedId;
            Changed();
        };
        Control positioner = new()
        {
            CustomMinimumSize = new Vector2(BaseLibDropdownWidth, BaseLibDropdownHeight),
            FocusMode = Control.FocusModeEnum.None,
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd,
            SizeFlagsVertical = Control.SizeFlags.Fill,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        positioner.AddChild(dropdown);
        return positioner;
    }

    private static HoverTip CreateCompanionHoverTip(string title, string description, Texture2D? icon = null)
    {
        HoverTip hoverTip = new(new LocString("static_hover_tips", "SETTINGS.title"), description, icon);
        object boxedHoverTip = hoverTip;
        HoverTipTitleProperty.SetValue(boxedHoverTip, title);
        return (HoverTip)boxedHoverTip;
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
