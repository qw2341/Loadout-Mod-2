#nullable enable

namespace Loadout.Keywords;

using System;
using Loadout.Patches.Cards.CardModification;
using Loadout.Services.CardModification;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;

public abstract class LoadoutImprovementKeywordModel : LoadoutKeywordModel
{
    private const string DamageVarName = "Damage";

    public override LoadoutKeywordPresentation Presentation =>
        LoadoutKeywordPresentation.DescriptionOnly;

    public override LoadoutKeywordEditorSection EditorSection =>
        LoadoutKeywordEditorSection.Improvement;

    protected static decimal GetAmount(CardModel card, string name)
    {
        return LoadoutKeywordRegistry.TryGetValue(card, name, out DynamicVar value)
            ? Math.Max(0m, value.BaseValue)
            : 0m;
    }

    protected static bool IncreaseDamage(CardModel card, decimal amount)
    {
        if (amount <= 0m
            || card.IsCanonical
            || !card.DynamicVars.TryGetValue(DamageVarName, out DynamicVar? damage)
            || damage is not DamageVar)
        {
            return false;
        }

        damage.BaseValue += amount;
        return true;
    }

    protected static void IncreaseDamagePermanently(
        CardModel card,
        decimal amount)
    {
        IncreasePersistentCopy(card, amount);
        if (card.DeckVersion is CardModel deckCard
            && !ReferenceEquals(card, deckCard))
        {
            IncreasePersistentCopy(deckCard, amount);
        }
    }

    private static void IncreasePersistentCopy(CardModel card, decimal amount)
    {
        if (!IncreaseDamage(card, amount))
            return;

        CardModificationDelta delta =
            CardModificationFields.TryGet(card, out CardModificationCardData data)
                ? data.Delta.Clone()
                : new CardModificationDelta();
        delta.DynamicVarDeltas.TryGetValue(
            DamageVarName,
            out decimal existingIncrease);
        delta.DynamicVarDeltas[DamageVarName] = existingIncrease + amount;
        CardModificationFields.SetDelta(card, delta);
    }
}
