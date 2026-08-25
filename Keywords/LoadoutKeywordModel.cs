#nullable enable

namespace Loadout.Keywords;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;

public enum LoadoutKeywordTextPosition
{
    Before,
    After
}

public enum LoadoutKeywordPresentation
{
    Normal,
    DescriptionOnly
}

public enum LoadoutKeywordEditorSection
{
    Default,
    Basic,
    Restrictive,
    Improvement
}

public sealed record LoadoutKeywordDynamicVarDefinition(
    string Name,
    decimal DefaultValue,
    int Minimum,
    int Maximum,
    string LabelLocKey,
    Func<string, decimal, DynamicVar>? Factory = null)
{
    public DynamicVar Create() =>
        Factory?.Invoke(Name, DefaultValue)
        ?? new DynamicVar(Name, DefaultValue);
}

/// <summary>
/// Common API for Loadout-owned card keywords. Keyword-specific files provide
/// only their metadata and behavior; shared registration, dynamic-variable,
/// description, and play-dispatch code consumes this model.
/// </summary>
public abstract class LoadoutKeywordModel
{
    public abstract CardKeyword Keyword { get; }

    public abstract string StorageKey { get; }

    public abstract string TitleLocKey { get; }

    public virtual LoadoutKeywordPresentation Presentation =>
        LoadoutKeywordPresentation.Normal;

    public virtual LoadoutKeywordEditorSection EditorSection =>
        LoadoutKeywordEditorSection.Default;

    public virtual string? CardTextLocKey => null;

    public virtual LoadoutKeywordTextPosition TextPosition =>
        LoadoutKeywordTextPosition.After;

    public virtual IReadOnlyList<LoadoutKeywordDynamicVarDefinition> DynamicVars => [];

    public bool ShowKeywordHoverTip =>
        Presentation == LoadoutKeywordPresentation.Normal;

    public virtual bool HasOnPlayEffect => false;

    public virtual int OnPlayPriority => 0;

    public virtual bool HasUnblockedDamageEffect => false;

    public virtual bool HasFatalEffect => false;

    public virtual bool HasTurnEndInHandEffect => false;

    public virtual bool HasPlayRestriction => false;

    public virtual bool ChangesTargeting => false;

    public bool IsEnabled(
        CardModel card,
        IReadOnlyDictionary<string, bool>? overrides = null)
    {
        return overrides?.TryGetValue(StorageKey, out bool enabled) == true
            ? enabled
            : LoadoutKeywords.Has(card, Keyword);
    }

    public string GetTitle()
    {
        return new LocString("card_keywords", TitleLocKey).GetFormattedText();
    }

    public string GetCardText(CardModel card)
    {
        if (Presentation != LoadoutKeywordPresentation.DescriptionOnly
            || string.IsNullOrWhiteSpace(CardTextLocKey))
        {
            return string.Empty;
        }

        LocString cardText = new("card_keywords", CardTextLocKey);
        card.DynamicVars.AddTo(cardText);
        AddCardTextVariables(card, cardText);
        return cardText.GetFormattedText();
    }

    protected virtual void AddCardTextVariables(CardModel card, LocString cardText)
    {
    }

    /// <summary>
    /// Captures state immediately before the card's concrete OnPlay body.
    /// The native BeforeCardPlayed hooks have already completed at this point.
    /// </summary>
    public virtual object? CaptureBeforeOnPlay(CardModel card, CardPlay cardPlay)
    {
        return null;
    }

    /// <summary>
    /// Runs after the card's concrete OnPlay task completes successfully.
    /// </summary>
    public virtual Task AfterOnPlay(
        CardModel card,
        PlayerChoiceContext choiceContext,
        CardPlay cardPlay,
        object? capturedState)
    {
        return Task.CompletedTask;
    }

    public virtual Task AfterUnblockedDamageDealt(
        CardModel card,
        decimal unblockedDamage)
    {
        return Task.CompletedTask;
    }

    public virtual void AfterFatal(CardModel card, int fatalCount)
    {
    }

    public virtual Task AfterTurnEndInHand(
        CardModel card,
        PlayerChoiceContext choiceContext)
    {
        return Task.CompletedTask;
    }

    internal static MethodInfo GetDescriptionTarget()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        MethodInfo? method = typeof(CardModel)
            .GetMethods(flags)
            .SingleOrDefault(candidate =>
            {
                if (!string.Equals(
                        candidate.Name,
                        nameof(CardModel.GetDescriptionForPile),
                        StringComparison.Ordinal)
                    || candidate.ReturnType != typeof(string))
                {
                    return false;
                }

                ParameterInfo[] parameters = candidate.GetParameters();
                return parameters.Length == 3
                       && parameters[0].ParameterType == typeof(PileType)
                       && parameters[2].ParameterType == typeof(Creature);
            });

        return method ?? throw new MissingMethodException(
            typeof(CardModel).FullName,
            "private GetDescriptionForPile(PileType, DescriptionPreviewType, Creature)");
    }

    internal static IEnumerable<MethodBase> GetCardPropertyGetters(
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
            if (getter is not null
                && !getter.IsStatic
                && !getter.IsAbstract
                && getter.GetMethodBody() is not null)
            {
                targets.Add(getter);
            }
        }

        return targets;
    }
}
