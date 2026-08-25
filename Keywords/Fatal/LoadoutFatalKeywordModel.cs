#nullable enable

namespace Loadout.Keywords;

using System;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;

public abstract class LoadoutFatalKeywordModel : LoadoutKeywordModel
{
    public override LoadoutKeywordPresentation Presentation =>
        LoadoutKeywordPresentation.DescriptionOnly;

    public override LoadoutKeywordEditorSection EditorSection =>
        LoadoutKeywordEditorSection.Fatal;

    public override bool HasFatalEffect => true;

    protected static decimal GetTotalAmount(
        CardModel card,
        string name,
        int fatalCount)
    {
        if (fatalCount <= 0
            || !LoadoutKeywordRegistry.TryGetValue(
                card,
                name,
                out DynamicVar amount))
        {
            return 0m;
        }

        return (decimal)Math.Max(0, amount.IntValue) * fatalCount;
    }
}
