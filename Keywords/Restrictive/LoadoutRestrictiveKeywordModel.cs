#nullable enable

namespace Loadout.Keywords;

using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;

public abstract class LoadoutRestrictiveKeywordModel : LoadoutKeywordModel
{
    public override LoadoutKeywordPresentation Presentation =>
        LoadoutKeywordPresentation.DescriptionOnly;

    public override LoadoutKeywordEditorSection EditorSection =>
        LoadoutKeywordEditorSection.Restrictive;

    protected static DynamicVar GetAmount(CardModel card, string name)
    {
        return LoadoutKeywordRegistry.TryGetValue(card, name, out DynamicVar value)
            ? value
            : new DynamicVar(name, 0m);
    }
}
