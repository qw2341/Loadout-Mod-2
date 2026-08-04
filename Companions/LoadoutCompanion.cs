#nullable enable

namespace Loadout.Companions;

using Godot;

public abstract class LoadoutCompanion
{
    public abstract string CompanionId { get; }
    public abstract string DisplayName { get; }
    public abstract string TooltipDescription { get; }
    public abstract string SpritePath { get; }

    public virtual string ConfigLocalizationKey => $"Companion{GetType().Name.Replace(nameof(LoadoutCompanion), string.Empty)}";
    public virtual string NameLocalizationKey => $"{ConfigLocalizationKey}Name";
    public virtual string TooltipLocalizationKey => $"{ConfigLocalizationKey}Description";
    public virtual Rect2? SpriteRegion => null;
    public virtual bool IsCustom => false;
    public virtual bool UsesLocalizedConfigText => true;
    public virtual bool IsGameplayAffecting => false;
    public virtual Color? SelectionColor => IsGameplayAffecting ? new Color("EFC851") : null;
}
