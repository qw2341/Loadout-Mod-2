using System;
using Godot;
using Loadout.UI.Screens;
using MegaCrit.Sts2.Core.Localization;

namespace Loadout.UI.Managers;

public class LocMan
{
    public static string SScreenLoc(string key, string fallback)
    {
        return SelectScreenLoc.Text(key, fallback);
    }

    public static string GameLoc(string table, string key, string fallback)
    {
        try
        {
            return LocString.Exists(table, key)
                ? new LocString(table, key).GetFormattedText()
                : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    public static string SafeFormatLocString(LocString locString, string fallback)
    {
        try
        {
            return locString.GetFormattedText();
        }
        catch (Exception exception)
        {
            GD.PushWarning($"LoadoutPanel: could not format loc string '{locString.LocTable}.{locString.LocEntryKey}'. {exception.Message}");
            return fallback;
        }
    }
}