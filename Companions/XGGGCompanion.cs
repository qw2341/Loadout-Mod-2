#nullable enable

namespace Loadout.Companions;

using Godot;

public sealed class XGGGCompanion : LoadoutCompanion
{
    public override string CompanionId => "xggg";
    public override string DisplayName => "XGGG";
    public override string TooltipDescription => "A cosmetic companion that peeks out from the Loadout panel button.";
    public override string SpritePath => "res://Loadout/images/companions/XGGG.png";
    public override string ConfigLocalizationKey => "CompanionXggg";
    public override Rect2? SpriteRegion => new Rect2(42f, 38f, 44f, 49f);
}
