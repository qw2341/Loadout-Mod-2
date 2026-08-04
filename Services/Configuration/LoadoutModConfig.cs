#nullable enable

namespace Loadout.Config;

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using BaseLib.Config;
using BaseLib.Config.UI;
using Godot;
using Loadout.Companions;
using Loadout.Services.CardModification;
using Loadout.Patches.Cards.CardModification;
using Loadout.Services.Configuration;
using Loadout.Services.RelicModification;
using Loadout.UI.ImageEditing;
using Loadout.UI.Screens.Controls;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
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

    private NLoadoutDropdown? _companionDropdown;
    private Control? _deleteCustomCompanionButton;

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
        AddCompanionActions(optionContainer);

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
        _companionDropdown = dropdown;
        RefreshCompanionDropdown();
        dropdown.SelectedItemChanged += selectedId =>
        {
            Companion = selectedId;
            Changed();
            RefreshCustomCompanionDeleteVisibility();
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

    private List<LoadoutDropdownOption> BuildCompanionOptions()
    {
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
            string name = companion.UsesLocalizedConfigText
                ? GetLabelText(companion.NameLocalizationKey)
                : companion.DisplayName;
            string description = companion.UsesLocalizedConfigText
                ? GetLabelText(companion.TooltipLocalizationKey)
                : FormatLabelText("CustomCompanionDescription", companion.DisplayName);
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

        return options;
    }

    private void RefreshCompanionDropdown()
    {
        if (_companionDropdown is { } dropdown && GodotObject.IsInstanceValid(dropdown))
            dropdown.SetItems(string.Empty, BuildCompanionOptions(), Companion);
    }

    private void AddCompanionActions(Control optionContainer)
    {
        HBoxContainer actions = new()
        {
            Name = "CustomCompanionActions",
            Alignment = BoxContainer.AlignmentMode.End,
            CustomMinimumSize = new Vector2(0f, BaseLibDropdownHeight),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        actions.AddThemeConstantOverride("separation", 12);

        Control createButton = CreateRawButtonControl(
            GetLabelText("CreateCustomCompanion"),
            () => TaskHelper.RunSafely(CreateCustomCompanionAsync()));
        createButton.Name = "CreateCustomCompanion";
        createButton.CustomMinimumSize = new Vector2(BaseLibDropdownWidth, BaseLibDropdownHeight);
        createButton.SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd;
        actions.AddChild(createButton);

        _deleteCustomCompanionButton = CreateRawButtonControl(
            GetLabelText("DeleteCustomCompanion"),
            () => TaskHelper.RunSafely(DeleteSelectedCustomCompanionAsync()));
        _deleteCustomCompanionButton.Name = "DeleteCustomCompanion";
        _deleteCustomCompanionButton.CustomMinimumSize = new Vector2(BaseLibDropdownWidth, BaseLibDropdownHeight);
        _deleteCustomCompanionButton.SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd;
        actions.AddChild(_deleteCustomCompanionButton);
        optionContainer.AddChild(actions);
        RefreshCustomCompanionDeleteVisibility();
    }

    private async Task CreateCustomCompanionAsync()
    {
        string suffix = Guid.NewGuid().ToString("N");
        string companionId = $"custom-{suffix}";
        string imageFileName = $"{suffix}.png";
        ImageEditRequest request = new(
            ImageEditFramePresets.Companion,
            CustomCompanionStore.DirectoryPath,
            imageFileName,
            GetLabelText("CreateCustomCompanion"),
            AllowDisplayNameEditing: true,
            InitialOpenDirectory: GetDefaultImageOpenDirectory());

        ImageEditResult result = await ImageEditorService.PickAndEditAsync(request);
        if (result.Status == ImageEditStatus.Cancelled)
            return;
        if (!result.Saved || string.IsNullOrWhiteSpace(result.SavedPath))
        {
            if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
                GD.PushWarning($"Loadout: custom companion image editing failed. {result.ErrorMessage}");
            return;
        }

        string displayName = string.IsNullOrWhiteSpace(result.DisplayName)
            ? GetLabelText("CustomCompanionDefaultName")
            : result.DisplayName.Trim();
        if (!CustomCompanionStore.TryAdd(
                companionId,
                displayName,
                Path.GetFileName(result.SavedPath),
                out CustomLoadoutCompanion? companion,
                out string? error)
            || companion is null
            || !LoadoutCompanionRegistry.AddCustomCompanion(companion))
        {
            if (companion is not null)
            {
                if (!CustomCompanionStore.TryRemove(companionId, out string? rollbackError))
                    GD.PushError($"Loadout: failed to roll back custom companion '{companionId}'. {rollbackError}");
            }
            else
            {
                TryDeleteNewImage(result.SavedPath);
            }
            GD.PushError($"Loadout: failed to register custom companion '{companionId}'. {error ?? "The companion registry rejected the new entry."}");
            return;
        }

        Companion = companion.CompanionId;
        Changed();
        RefreshCompanionDropdown();
        RefreshCustomCompanionDeleteVisibility();
    }

    private async Task DeleteSelectedCustomCompanionAsync()
    {
        LoadoutCompanion? companion = LoadoutCompanionRegistry.GetCompanion(Companion);
        if (companion is null || !companion.IsCustom)
        {
            RefreshCustomCompanionDeleteVisibility();
            return;
        }

        NModalContainer? modalContainer = NModalContainer.Instance;
        if (modalContainer is null
            || !GodotObject.IsInstanceValid(modalContainer)
            || modalContainer.OpenModal is not null)
        {
            GD.PushWarning("Loadout: custom companion deletion confirmation could not open because the modal UI is unavailable or busy.");
            return;
        }

        NGenericPopup? popup = NGenericPopup.Create();
        if (popup is null)
        {
            GD.PushWarning("Loadout: custom companion deletion confirmation popup could not be created.");
            return;
        }

        LocString body = new("settings_ui", "LOADOUT-DELETE_CUSTOM_COMPANION_CONFIRM_BODY.title");
        body.Add("Name", companion.DisplayName);
        modalContainer.Add(popup);
        bool confirmed = await popup.WaitForConfirmation(
            body,
            new LocString("settings_ui", "LOADOUT-DELETE_CUSTOM_COMPANION_CONFIRM_TITLE.title"),
            new LocString("settings_ui", "LOADOUT-DELETE_CUSTOM_COMPANION_NO.title"),
            new LocString("settings_ui", "LOADOUT-DELETE_CUSTOM_COMPANION_YES.title"));
        if (!confirmed)
            return;

        if (!CustomCompanionStore.TryRemove(companion.CompanionId, out string? error))
        {
            GD.PushError($"Loadout: failed to delete custom companion '{companion.CompanionId}'. {error}");
            return;
        }

        LoadoutCompanionRegistry.RemoveCustomCompanion(companion.CompanionId);
        Companion = LoadoutCompanionRegistry.NoneId;
        Changed();
        RefreshCompanionDropdown();
        RefreshCustomCompanionDeleteVisibility();
        if (!string.IsNullOrWhiteSpace(error))
            GD.PushWarning($"Loadout: custom companion was removed with a file cleanup warning. {error}");
    }

    private void RefreshCustomCompanionDeleteVisibility()
    {
        if (_deleteCustomCompanionButton is { } button && GodotObject.IsInstanceValid(button))
            button.Visible = LoadoutCompanionRegistry.GetCompanion(Companion)?.IsCustom == true;
    }

    private static string? GetDefaultImageOpenDirectory()
    {
        string pictures = System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyPictures);
        if (!string.IsNullOrWhiteSpace(pictures) && Directory.Exists(pictures))
            return pictures;

        string home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        return !string.IsNullOrWhiteSpace(home) && Directory.Exists(home) ? home : null;
    }

    private string FormatLabelText(string key, string companionName)
    {
        return GetLabelText(key).Replace("{Name}", companionName, StringComparison.Ordinal);
    }

    private static void TryDeleteNewImage(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception exception)
        {
            GD.PushWarning($"Loadout: failed to clean up unregistered custom companion image '{path}'. {exception.Message}");
        }
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
