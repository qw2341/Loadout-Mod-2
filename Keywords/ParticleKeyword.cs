#nullable enable

namespace Loadout.Keywords;

using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;

public sealed class ParticleKeyword : LoadoutKeywordModel
{
    public static ParticleKeyword Instance { get; } = new();

    private ParticleKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.Particle;

    public override string StorageKey => LoadoutKeywords.ParticleKey;

    public override string TitleLocKey => "LOADOUT-PARTICLE.title";

    public override LoadoutKeywordPresentation Presentation =>
        LoadoutKeywordPresentation.DescriptionOnly;

    public override string? CardTextLocKey => "LOADOUT-PARTICLE.cardText";
}
