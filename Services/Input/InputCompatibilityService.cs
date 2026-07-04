#nullable enable

namespace Loadout.Services.Input;

using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.ControllerInput;
using MegaCrit.Sts2.Core.Saves;

public static class InputCompatibilityService
{
    private static readonly IReadOnlyDictionary<string, string> LegacyControllerActions =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["controller_l_stick_press"] = Controller.joystickPress,
            ["controller_l_stick_left"] = Controller.joystickLeft,
            ["controller_l_stick_right"] = Controller.joystickRight,
            ["controller_l_stick_up"] = Controller.joystickUp,
            ["controller_l_stick_down"] = Controller.joystickDown,
            ["controller_d_pad_left"] = Controller.dPadWest,
            ["controller_d_pad_right"] = Controller.dPadEast,
            ["controller_d_pad_up"] = Controller.dPadNorth,
            ["controller_d_pad_down"] = Controller.dPadSouth
        };

    private static readonly ISet<string> SupportedControllerMappingKeys =
        new HashSet<string>(MegaInput.AllInputs, StringComparer.Ordinal);

    private static bool _registered;

    public static void Register()
    {
        if (_registered)
            return;

        _registered = true;
        SanitizeSettingsControllerMapping();
    }

    private static void SanitizeSettingsControllerMapping()
    {
        SettingsSave settings = SaveManager.Instance.SettingsSave;
        Dictionary<string, string> mapping = settings.ControllerMapping ?? new Dictionary<string, string>();
        Dictionary<string, string> sanitized = new(mapping, StringComparer.Ordinal);
        bool changed = !ReferenceEquals(settings.ControllerMapping, mapping);

        foreach ((string input, string action) in mapping.ToArray())
        {
            if (!SupportedControllerMappingKeys.Contains(input))
            {
                sanitized.Remove(input);
                changed = true;
                continue;
            }

            string actionName = NormalizeLegacyControllerAction(action);
            if (!InputMap.HasAction(actionName))
            {
                sanitized.Remove(input);
                changed = true;
                continue;
            }

            if (!string.Equals(action, actionName, StringComparison.Ordinal))
            {
                sanitized[input] = actionName;
                changed = true;
            }
        }

        if (!changed)
            return;

        settings.ControllerMapping = sanitized;
        SaveManager.Instance.SaveSettings();
        MainFile.Logger.Info("[Loadout] Sanitized stale controller mapping entries.");
    }

    private static string NormalizeLegacyControllerAction(string action)
    {
        return LegacyControllerActions.TryGetValue(action, out string? canonical)
            ? canonical
            : action;
    }
}
