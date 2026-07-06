using System;
using System.Linq;
using System.Reflection;
using Godot;
using Loadout.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;

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

    static bool TryGetPowerVarTitle(object dynamicVar, out string title)
    {
        title = null;

        if (!TryGetPowerVarGenericType(dynamicVar, out Type powerType))
            return false;

        PowerModel powerModel = GetPowerModel(powerType);

        if (powerModel == null)
            return false;

        title = powerModel.Title.GetFormattedText();
        return true;
    }

    static bool TryGetPowerVarGenericType(object obj, out Type powerType)
    {
        powerType = null;

        if (obj == null)
            return false;

        Type type = obj.GetType();

        while (type != null)
        {
            if (type.IsGenericType &&
                type.GetGenericTypeDefinition() == typeof(PowerVar<>))
            {
                powerType = type.GetGenericArguments()[0];
                return true;
            }

            type = type.BaseType;
        }

        return false;
    }

    static PowerModel GetPowerModel(Type powerType)
    {
        MethodInfo method = typeof(ModelDb)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(m =>
                m.Name == "Power" &&
                m.IsGenericMethodDefinition &&
                m.GetGenericArguments().Length == 1 &&
                m.GetParameters().Length == 0
            );

        MethodInfo closedMethod = method.MakeGenericMethod(powerType);

        object result = closedMethod.Invoke(null, null);

        return result as PowerModel;
    }
    
    public static string DynamicVarLoc(DynamicVar dynamicVar)
    {

        if (TryGetPowerVarTitle(dynamicVar,out string loc))
        {
            return loc;
        }

        return dynamicVar switch
        {
            BlockVar _ => CommonLoc.Block,
            CalculatedBlockVar _ => CommonLoc.CalculatedBlock,
            CalculatedDamageVar _ => CommonLoc.CalculatedDamage,
            CalculationBaseVar _ => CommonLoc.CalculationBase,
            CalculationExtraVar _ => CommonLoc.CalculationExtra,
            CardsVar _ => CommonLoc.Cards,
            DamageVar _ => CommonLoc.Damage,
            EnergyVar _ => CommonLoc.Energy,
            ExtraDamageVar _ => CommonLoc.ExtraDamage,
            ForgeVar _ => CommonLoc.Forge,
            GoldVar _ => CommonLoc.Gold,
            HealVar _ => CommonLoc.Heal,
            HpLossVar _ => CommonLoc.HpLoss,
            IfUpgradedVar _ => CommonLoc.IfUpgraded,
            MaxHpVar _ => CommonLoc.MaxHp,
            OstyDamageVar _ => CommonLoc.OstyDamage,
            RepeatVar _ => CommonLoc.Repeat,
            StarsVar _ => CommonLoc.Stars,
            SummonVar _ => CommonLoc.Summon,
            _ => Loc($"DYNAMIC_VAR_{dynamicVar.Name.ToUpper()}",dynamicVar.Name)

        };
    }
}
