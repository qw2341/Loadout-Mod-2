#nullable enable

namespace Loadout.Companions;

using Godot;
using MegaCrit.Sts2.Core.Models;

public abstract class LoadoutCompanion : AbstractModel
{
    public abstract string CompanionId { get; }
    public abstract string DisplayName { get; }
    public abstract string TooltipDescription { get; }
    public abstract string SpritePath { get; }

    public virtual string ConfigLocalizationKey => $"Companion{GetType().Name.Replace(nameof(LoadoutCompanion), string.Empty)}";
    public virtual string NameLocalizationKey => $"{ConfigLocalizationKey}Name";
    public virtual string TooltipLocalizationKey => $"{ConfigLocalizationKey}Description";
    public virtual Rect2? SpriteRegion => null;
    public virtual bool IsGameplayAffecting => false;
    public virtual Color? SelectionColor => IsGameplayAffecting ? new Color("EFC851") : null;

    public ulong OwnerNetId { get; private set; }

    public sealed override bool ShouldReceiveCombatHooks => false;

    public LoadoutCompanion CreateForOwner(ulong ownerNetId)
    {
        LoadoutCompanion companion = (LoadoutCompanion)MutableClone();
        companion.OwnerNetId = ownerNetId;
        return companion;
    }

    public void Peek(double seconds = 1.5)
    {
        LoadoutCompanionRegistry.RequestPresentation(this, null, seconds);
    }

    public void Say(string text, double seconds = 2.0)
    {
        if (!string.IsNullOrWhiteSpace(text))
            LoadoutCompanionRegistry.RequestPresentation(this, text, seconds);
    }
}

public readonly record struct LoadoutCompanionPresentationRequest(
    LoadoutCompanion Companion,
    string? Text,
    double Seconds);
