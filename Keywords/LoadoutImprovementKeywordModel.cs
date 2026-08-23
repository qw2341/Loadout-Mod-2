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
        return IncreaseDamage(card, amount, persistentDelta: null);
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
        CardModificationDelta delta =
            CardModificationFields.TryGet(card, out CardModificationCardData data)
                ? data.Delta.Clone()
                : new CardModificationDelta();
        if (IncreaseDamage(card, amount, delta))
            CardModificationFields.SetDelta(card, delta);
    }

    private static bool IncreaseDamage(
        CardModel card,
        decimal amount,
        CardModificationDelta? persistentDelta)
    {
        if (amount <= 0m || card.IsCanonical)
            return false;

        bool changed = false;
        foreach ((string name, DynamicVar dynamicVar) in card.DynamicVars)
        {
            if (dynamicVar is not DamageVar damage)
                continue;

            damage.BaseValue += amount;
            changed = true;
            if (persistentDelta is null)
                continue;

            persistentDelta.DynamicVarDeltas.TryGetValue(
                name,
                out decimal existingIncrease);
            persistentDelta.DynamicVarDeltas[name] = existingIncrease + amount;
        }

        return changed;
    }
}
