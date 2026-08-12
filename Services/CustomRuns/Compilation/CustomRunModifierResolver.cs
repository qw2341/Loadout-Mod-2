#nullable enable

namespace Loadout.Services.CustomRuns.Compilation;

using System;
using System.Collections.Generic;
using System.Linq;
using Loadout.Services.CustomRuns.Models;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Modifiers;

public static class CustomRunModifierResolver
{
    public static RunModifierDefinition ToDefinition(ModifierModel modifier)
    {
        return new RunModifierDefinition
        {
            ModelId = modifier.Id.ToString(),
            CharacterModelId = modifier is CharacterCards characterCards
                ? characterCards.CharacterModel.ToString()
                : null
        };
    }

    public static bool TryResolve(RunModifierDefinition definition, out ModifierModel modifier)
    {
        modifier = null!;
        ModifierModel? canonical = ModelDb.GoodModifiers
            .Concat(ModelDb.BadModifiers)
            .FirstOrDefault(candidate => ModelIdMatches(candidate, definition.ModelId));
        if (canonical is null)
            return false;

        ModifierModel mutable = canonical.ToMutable();
        if (mutable is CharacterCards characterCards)
        {
            CharacterModel? character = ModelDb.AllCharacters.FirstOrDefault(candidate =>
                ModelIdMatches(candidate, definition.CharacterModelId));
            if (character is null)
                return false;
            characterCards.CharacterModel = character.Id;
        }

        modifier = mutable;
        return true;
    }

    public static IReadOnlyList<ModifierModel> ResolveAll(IEnumerable<RunModifierDefinition> definitions)
    {
        return definitions
            .Select(definition => TryResolve(definition, out ModifierModel modifier) ? modifier : null)
            .Where(modifier => modifier is not null)
            .Cast<ModifierModel>()
            .ToList();
    }

    public static bool ContainsMutuallyExclusiveModifiers(IReadOnlyList<ModifierModel> modifiers)
    {
        return ModelDb.MutuallyExclusiveModifiers.Any(group =>
            modifiers.Select(modifier => modifier.GetType()).Distinct().Count(type =>
                group.Any(groupModifier => groupModifier.GetType() == type)) > 1);
    }

    private static bool ModelIdMatches(AbstractModel model, string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
               && (string.Equals(model.Id.ToString(), value, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(model.Id.Entry, value, StringComparison.OrdinalIgnoreCase));
    }
}
