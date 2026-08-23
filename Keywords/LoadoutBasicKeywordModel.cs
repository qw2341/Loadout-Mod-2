#nullable enable

namespace Loadout.Keywords;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;

public abstract class LoadoutBasicKeywordModel : LoadoutKeywordModel
{
    public override LoadoutKeywordPresentation Presentation =>
        LoadoutKeywordPresentation.DescriptionOnly;

    public override LoadoutKeywordEditorSection EditorSection =>
        LoadoutKeywordEditorSection.Basic;

    public override bool HasOnPlayEffect => true;

    protected static DynamicVar GetAmount(CardModel card, string name)
    {
        return LoadoutKeywordRegistry.TryGetValue(card, name, out DynamicVar value)
            ? value
            : new DynamicVar(name, 0m);
    }

    protected static int GetClampedHandCount(
        CardModel card,
        string variableName)
    {
        int requested = Math.Max(0, GetAmount(card, variableName).IntValue);
        int available = PileType.Hand.GetPile(card.Owner).Cards.Count;
        return Math.Min(requested, available);
    }

    internal static IEnumerable<MethodBase> GetPropertyGetters(
        string propertyName)
    {
        HashSet<MethodBase> targets = [];
        const BindingFlags flags = BindingFlags.Instance
                                   | BindingFlags.Public
                                   | BindingFlags.NonPublic
                                   | BindingFlags.DeclaredOnly;
        foreach (Type type in ModelDb.AllCards
                     .Select(card => card.GetType())
                     .Append(typeof(CardModel))
                     .Distinct())
        {
            MethodInfo? getter = type.GetProperty(propertyName, flags)?.GetMethod;
            if (getter is not null && !getter.IsStatic)
                targets.Add(getter);
        }

        return targets;
    }
}
