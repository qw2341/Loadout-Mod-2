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
        return IncreaseValues<DamageVar>(card, amount, persistentDelta: null);
    }

    protected static bool MultiplyDamage(CardModel card, decimal multiplier)
    {
        return MultiplyValues<DamageVar>(card, multiplier);
    }

    protected static void IncreaseDamagePermanently(
        CardModel card,
        decimal amount)
    {
        IncreaseValuesPermanently<DamageVar>(card, amount);
    }

    protected static bool IncreaseBlock(CardModel card, decimal amount)
    {
        return IncreaseValues<BlockVar>(card, amount, persistentDelta: null);
    }

    protected static bool MultiplyBlock(CardModel card, decimal multiplier)
    {
        return MultiplyValues<BlockVar>(card, multiplier);
    }

    protected static void IncreaseBlockPermanently(
        CardModel card,
        decimal amount)
    {
        IncreaseValuesPermanently<BlockVar>(card, amount);
    }

    protected static bool IncreaseOtherVariables(CardModel card, decimal amount)
    {
        return IncreaseMatchingValues(
            card,
            amount,
            persistentDelta: null,
            IsOtherNumericVariable);
    }

    protected static bool MultiplyOtherVariables(
        CardModel card,
        decimal multiplier)
    {
        return MultiplyMatchingValues(card, multiplier, IsOtherNumericVariable);
    }

    protected static void IncreaseOtherVariablesPermanently(
        CardModel card,
        decimal amount)
    {
        IncreaseMatchingValuesPermanently(card, amount, IsOtherNumericVariable);
    }

    private static void IncreaseValuesPermanently<TVar>(
        CardModel card,
        decimal amount)
        where TVar : DynamicVar
    {
        IncreasePersistentCopy<TVar>(card, amount);
        if (card.DeckVersion is CardModel deckCard
            && !ReferenceEquals(card, deckCard))
        {
            IncreasePersistentCopy<TVar>(deckCard, amount);
        }
    }

    private static void IncreasePersistentCopy<TVar>(
        CardModel card,
        decimal amount)
        where TVar : DynamicVar
    {
        CardModificationDelta delta =
            CardModificationFields.TryGet(card, out CardModificationCardData data)
                ? data.Delta.Clone()
                : new CardModificationDelta();
        if (IncreaseValues<TVar>(card, amount, delta))
            CardModificationFields.SetDelta(card, delta);
    }

    private static bool IncreaseValues<TVar>(
        CardModel card,
        decimal amount,
        CardModificationDelta? persistentDelta)
        where TVar : DynamicVar
    {
        if (amount <= 0m || card.IsCanonical)
            return false;

        bool changed = false;
        foreach ((string name, DynamicVar dynamicVar) in card.DynamicVars)
        {
            if (dynamicVar is not TVar value)
                continue;

            value.BaseValue += amount;
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

    private static bool MultiplyValues<TVar>(
        CardModel card,
        decimal multiplier)
        where TVar : DynamicVar
    {
        if (multiplier < 0m || card.IsCanonical)
            return false;

        bool changed = false;
        foreach (DynamicVar dynamicVar in card.DynamicVars.Values)
        {
            if (dynamicVar is not TVar value)
                continue;

            value.BaseValue *= multiplier;
            changed = true;
        }

        return changed;
    }

    private static void IncreaseMatchingValuesPermanently(
        CardModel card,
        decimal amount,
        Func<string, DynamicVar, bool> predicate)
    {
        IncreasePersistentMatchingCopy(card, amount, predicate);
        if (card.DeckVersion is CardModel deckCard
            && !ReferenceEquals(card, deckCard))
        {
            IncreasePersistentMatchingCopy(deckCard, amount, predicate);
        }
    }

    private static void IncreasePersistentMatchingCopy(
        CardModel card,
        decimal amount,
        Func<string, DynamicVar, bool> predicate)
    {
        CardModificationDelta delta =
            CardModificationFields.TryGet(card, out CardModificationCardData data)
                ? data.Delta.Clone()
                : new CardModificationDelta();
        if (IncreaseMatchingValues(card, amount, delta, predicate))
            CardModificationFields.SetDelta(card, delta);
    }

    private static bool IncreaseMatchingValues(
        CardModel card,
        decimal amount,
        CardModificationDelta? persistentDelta,
        Func<string, DynamicVar, bool> predicate)
    {
        if (amount <= 0m || card.IsCanonical)
            return false;

        bool changed = false;
        foreach ((string name, DynamicVar value) in card.DynamicVars)
        {
            if (!predicate(name, value))
                continue;

            value.BaseValue += amount;
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

    private static bool MultiplyMatchingValues(
        CardModel card,
        decimal multiplier,
        Func<string, DynamicVar, bool> predicate)
    {
        if (multiplier < 0m || card.IsCanonical)
            return false;

        bool changed = false;
        foreach ((string name, DynamicVar value) in card.DynamicVars)
        {
            if (!predicate(name, value))
                continue;

            value.BaseValue *= multiplier;
            changed = true;
        }

        return changed;
    }

    private static bool IsOtherNumericVariable(
        string name,
        DynamicVar value)
    {
        // Keep the improvement controls stable and leave damage/block to their
        // dedicated keyword families. Calculated and non-numeric vars are not
        // independent card values, so mutating them would double-count or
        // corrupt description grammar.
        if (name.StartsWith("LoadoutImprovement", StringComparison.Ordinal))
            return false;

        return value is not DamageVar
               and not BlockVar
               and not CalculatedVar
               and not CalculationBaseVar
               and not CalculationExtraVar
               and not BoolVar
               and not StringVar
               and not IfUpgradedVar;
    }
}
