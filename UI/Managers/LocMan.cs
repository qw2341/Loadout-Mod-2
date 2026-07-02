using System;
using Godot;
using MegaCrit.Sts2.Core.Localization;

namespace Loadout.UI.Managers;

public static class LocMan
{
    private const string DefaultLocale = "en";
    private static bool _loaded;

    public static void Load()
    {
        if (_loaded)
            return;

        LoadTranslation("res://Loadout/localization/uitext.en.translation");
        LoadTranslation("res://Loadout/localization/uitext.zh_CN.translation");

        _loaded = true;
    }

    private static void LoadTranslation(string path)
    {
        if (!ResourceLoader.Exists(path))
        {
            GD.PushWarning($"LoadoutPanel: translation file not found: {path}");
            return;
        }

        Translation translation = ResourceLoader.Load<Translation>(path);

        if (translation == null)
        {
            GD.PushWarning($"LoadoutPanel: failed to load translation file: {path}");
            return;
        }

        TranslationServer.AddTranslation(translation);
        GD.Print($"LoadoutPanel: loaded translation file: {path}");
    }

    public static string Text(string key, string fallback)
    {
        Load();

        try
        {
            string translated = TranslationServer.Translate(key).ToString();

            if (!string.IsNullOrEmpty(translated) && translated != key)
                return translated;
        }
        catch
        {
            // Ignore and use fallback.
        }

        return fallback;
    }

    public static string Text(string key, string fallback, params object[] args)
    {
        string format = Text(key, fallback);

        try
        {
            return string.Format(format, args);
        }
        catch
        {
            return fallback;
        }
    }

    public static string Loc(string key, string fallback)
    {
        return Text(key, fallback);
    }

    public static string Loc(string key, string fallback, params object[] args)
    {
        return Text(key, fallback, args);
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
            GD.PushWarning(
                $"LoadoutPanel: could not format loc string '{locString.LocTable}.{locString.LocEntryKey}'. {exception.Message}"
            );

            return fallback;
        }
    }
}