using Loadout.UI.Managers;

namespace Loadout.Helpers;

public static class CommonLoc
{
    public static string Block =>
        LocMan.GameLoc("static_hover_tips", "BLOCK.title", "Block");

    public static string CalculatedBlock =>
        LocMan.Loc("DYNAMIC_VAR_CALCULATED_BLOCK", "Calculated Block");

    public static string CalculatedDamage =>
        LocMan.Loc("DYNAMIC_VAR_CALCULATED_DAMAGE", "Calculated Damage");

    public static string CalculationBase =>
        LocMan.Loc("DYNAMIC_VAR_CALCULATION_BASE", "Base Value");

    public static string CalculationExtra =>
        LocMan.Loc("DYNAMIC_VAR_CALCULATION_EXTRA", "Extra Value");

    public static string Cards =>
        LocMan.Loc("DYNAMIC_VAR_CARDS", "Cards");

    public static string Damage => LocMan.Loc("DYNAMIC_VAR_DAMAGE", "Damage");

    public static string Energy =>
        LocMan.GameLoc("static_hover_tips", "ENERGY.title", "Energy");

    public static string ExtraDamage =>
        LocMan.Loc("DYNAMIC_VAR_EXTRA_DAMAGE", "Extra Damage");

    public static string Forge =>
        LocMan.GameLoc("static_hover_tips", "FORGE.title", "Forge");

    public static string Gold =>
        LocMan.GameLoc("static_hover_tips", "MONEY_POUCH.title", "Gold");

    public static string Heal =>
        LocMan.Loc("DYNAMIC_VAR_HEAL", "Heal");

    public static string HpLoss =>
        LocMan.Loc("DYNAMIC_VAR_HP_LOSS", "HP Loss");

    public static string IfUpgraded =>
        LocMan.Loc("DYNAMIC_VAR_IF_UPGRADED", "If Upgraded");

    public static string MaxHp =>
        LocMan.Loc("DYNAMIC_VAR_MAX_HP", "Max HP");

    public static string OstyDamage =>
        LocMan.Loc("DYNAMIC_VAR_OSTY_DAMAGE", "Osty Damage");

    public static string Repeat =>
        LocMan.GameLoc("static_hover_tips", "REPLAY_STATIC.title", "Replay");

    public static string Stars =>
        LocMan.GameLoc("static_hover_tips", "STAR_COUNT.title", "Stars");

    public static string Summon =>
        LocMan.GameLoc("static_hover_tips", "SUMMON_STATIC.title", "Summon");
    public static string Colorless => LocMan.Loc("COLORLESS", "Colorless");
    public static string Curse => LocMan.Loc("CURSE", "Curse");
    public static string Event => LocMan.Loc("EVENT", "Event");
    public static string Quest => LocMan.Loc("QUEST", "Quest");
    public static string Status => LocMan.Loc("STATUS", "Status");
    public static string Token => LocMan.Loc("TOKEN", "Token");
}
