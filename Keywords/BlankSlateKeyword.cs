#nullable enable

namespace Loadout.Keywords;

using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;

public sealed class BlankSlateKeyword : LoadoutKeywordModel
{
    public static BlankSlateKeyword Instance { get; } = new();

    private BlankSlateKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.BlankSlate;

    public override string StorageKey => LoadoutKeywords.BlankSlateKey;

    public override string TitleLocKey => "LOADOUT-BLANK_SLATE.title";

    public override LoadoutKeywordPresentation Presentation =>
        LoadoutKeywordPresentation.DescriptionOnly;

    public override LoadoutKeywordEditorSection EditorSection =>
        LoadoutKeywordEditorSection.Basic;

    public override bool TransformsBaseDescription => true;

    public override int BaseDescriptionPriority => int.MinValue;

    public override bool SuppressesOriginalOnPlay => true;

    public override string TransformBaseDescription(
        CardModel card,
        string description)
    {
        return string.Empty;
    }
}
