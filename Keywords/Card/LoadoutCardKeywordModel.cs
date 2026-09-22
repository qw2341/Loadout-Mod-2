#nullable enable

namespace Loadout.Keywords;

using System.Collections.Generic;
using BaseLib.Cards.Variables;
using MegaCrit.Sts2.Core.Models;

public abstract class LoadoutCardKeywordModel : LoadoutKeywordModel
{
    public static IReadOnlyList<LoadoutCardKeywordModel> All => Models.Value;

    private static class Models
    {
        public static readonly IReadOnlyList<LoadoutCardKeywordModel> Value = [AddCardKeyword.Instance];
    }

    public override LoadoutKeywordPresentation Presentation => LoadoutKeywordPresentation.DescriptionOnly;
    public override LoadoutKeywordEditorSection EditorSection => LoadoutKeywordEditorSection.Card;
    public override LoadoutKeywordEditorControlKind EditorControlKind => LoadoutKeywordEditorControlKind.RepeatableCard;
    public override bool HasOnPlayEffect => true;
    public virtual string AmountLabelLocKey => "CARD_MOD_ADD_CARD_AMOUNT";
    public virtual string CardLabelLocKey => "CARD_MOD_ADD_CARD_CARD";

    protected static IReadOnlyList<LoadoutKeywordDynamicVarDefinition> CreateDisplayVariables(
        string name, string label, string key) =>
    [
        new(name, 0m, 0, int.MaxValue, label,
            (varName, _) => new DisplayVar<CardModel>(varName,
                card => LoadoutCardKeywordState.FormatEntries(card, key)),
            EditorVisible: false)
    ];
}
