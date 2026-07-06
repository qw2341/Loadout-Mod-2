using Loadout.UI.Managers;

namespace Loadout.Helpers;

public class CommonLoc
{
    public static string Block =>
        LocMan.GameLoc("static_hover_tips", "BLOCK.title", "Block");
    public static string Damage => LocMan.Loc("DAMAGE","Damage");
}