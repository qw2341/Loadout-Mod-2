#nullable enable

namespace Loadout.Config;

using System;
using System.Linq;
using System.Reflection;
using BaseLib.Config;
using BaseLib.Config.UI;
using Godot;

public static class LoadoutBaseLibBootstrap
{
    private static bool _initialized;

    public static void Initialize()
    {
        if (_initialized)
            return;

        ModConfigRegistry.Register("Loadout", new LoadoutModConfig());
        _initialized = true;
    }
}

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
        get => LoadoutBridge.GetBool(nameof(EnableDeckLoadoutScreen), fallback: true);
        set => LoadoutBridge.Set(nameof(EnableDeckLoadoutScreen), value);
    }

    public static LoadoutSkin PanelSkin
    {
        get => LoadoutBridge.GetString("ActiveSkinId", "default").ToLowerInvariant() switch
        {
            "isaac" => LoadoutSkin.Isaac,
            "legacy" => LoadoutSkin.Legacy,
            "sts1" => LoadoutSkin.STS1,
            "xggg" => LoadoutSkin.XGGG,
            _ => LoadoutSkin.Default
        };
        set => LoadoutBridge.Set("ActiveSkinId", value switch
        {
            LoadoutSkin.Isaac => "isaac",
            LoadoutSkin.Legacy => "legacy",
            LoadoutSkin.STS1 => "sts1",
            LoadoutSkin.XGGG => "xggg",
            _ => "default"
        });
    }

    public static LoadoutPanelAnimation PanelAnimation
    {
        get => LoadoutBridge.GetString("ActiveAnimationId", "yellow_glow_pulse").ToLowerInvariant() switch
        {
            "dock_magnify" => LoadoutPanelAnimation.DockMagnify,
            _ => LoadoutPanelAnimation.YellowGlowPulse
        };
        set => LoadoutBridge.Set("ActiveAnimationId", value == LoadoutPanelAnimation.DockMagnify
            ? "dock_magnify"
            : "yellow_glow_pulse");
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
            () =>
            {
                try
                {
                    int removedCount = LoadoutBridge.ResetAllPermanentCardModifications();
                    resetStatus.Text = GetLabelText(removedCount > 0
                        ? "ResetStatusSucceeded"
                        : "ResetStatusNothingToReset");
                    resetStatus.AddThemeColorOverride("default_color", new Color("85D98B"));
                }
                catch (Exception exception)
                {
                    resetStatus.Text = GetLabelText("ResetStatusFailed");
                    resetStatus.AddThemeColorOverride("default_color", new Color("F07C72"));
                    GD.PushError($"Loadout: failed to reset permanent card modifications. {exception}");
                }
            }));
        optionContainer.AddChild(resetStatus);

        SetupFocusNeighbors(optionContainer);
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
        lifetime.TreeEntered += () => LoadoutBridge.SetConfigPanelPreviewVisible(true);
        lifetime.TreeExiting += () => LoadoutBridge.SetConfigPanelPreviewVisible(false);
        optionContainer.AddChild(lifetime);
    }
}

internal static class LoadoutBridge
{
    private const string ServiceTypeName = "Loadout.Services.Configuration.LoadoutConfigService";
    private const string CardModificationServiceTypeName =
        "Loadout.Services.CardModification.CardModificationStateService";

    public static bool GetBool(string propertyName, bool fallback)
    {
        object? value = GetProperty(propertyName)?.GetValue(null);
        return value is bool result ? result : fallback;
    }

    public static string GetString(string propertyName, string fallback)
    {
        return GetProperty(propertyName)?.GetValue(null)?.ToString() ?? fallback;
    }

    public static void Set(string propertyName, object value)
    {
        GetProperty(propertyName)?.SetValue(null, value);
    }

    public static int ResetAllPermanentCardModifications()
    {
        Type? service = FindType(CardModificationServiceTypeName);
        MethodInfo? reset = service?.GetMethod("ResetAllPermanent", BindingFlags.Public | BindingFlags.Static);
        if (reset is null)
            throw new MissingMethodException(CardModificationServiceTypeName, "ResetAllPermanent");

        MethodInfo? getCount = service?.GetMethod(
            "GetPermanentModificationCount",
            BindingFlags.Public | BindingFlags.Static);
        int removedCount = getCount?.Invoke(null, null) is int count ? count : 0;
        reset.Invoke(null, null);
        return removedCount;
    }

    public static void SetConfigPanelPreviewVisible(bool visible)
    {
        Type? service = FindType(ServiceTypeName);
        MethodInfo? setVisible = service?.GetMethod(
            "SetConfigPanelPreviewVisible",
            BindingFlags.Public | BindingFlags.Static);
        setVisible?.Invoke(null, [visible]);
    }

    private static PropertyInfo? GetProperty(string propertyName)
    {
        return FindType(ServiceTypeName)?.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Static);
    }

    private static Type? FindType(string fullName)
    {
        return AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType(fullName, throwOnError: false))
            .FirstOrDefault(type => type is not null);
    }
}
