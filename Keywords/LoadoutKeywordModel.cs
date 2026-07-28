#nullable enable

namespace Loadout.Keywords;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;

public enum LoadoutKeywordTextPosition
{
    Before,
    After
}

public sealed record LoadoutKeywordDynamicVarDefinition(
    string Name,
    decimal DefaultValue,
    int Minimum,
    int Maximum,
    string LabelLocKey);

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

    public virtual IReadOnlyList<LoadoutKeywordDynamicVarDefinition> DynamicVars => [];

    public virtual bool ShowKeywordHoverTip => true;

    public virtual bool HasOnPlayEffect => false;

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
        CardPlay cardPlay,
        object? capturedState)
    {
        return Task.CompletedTask;
    }
}

/// <summary>
/// Parent for keywords that do not render a keyword heading or hover tooltip.
/// Their localized text is appended to the card description and may use any
/// dynamic variables declared by the child model.
/// </summary>
public abstract class LoadoutDescriptionKeywordModel : LoadoutKeywordModel
{
    public abstract string CardTextLocKey { get; }

    public virtual LoadoutKeywordTextPosition TextPosition =>
        LoadoutKeywordTextPosition.After;

    public sealed override bool ShowKeywordHoverTip => false;

    public string GetCardText(CardModel card)
    {
        LocString cardText = new("card_keywords", CardTextLocKey);
        card.DynamicVars.AddTo(cardText);
        return cardText.GetFormattedText();
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
}
