#nullable enable

namespace Loadout.Services.CardModification;

using System;
using System.Collections.Generic;
using System.Reflection;
using Loadout.Patches.Cards.CardModification;
using Loadout.Services.Actions;
using Loadout.Services.CardPortraits;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

public sealed record CardPrinterRunRecipe(
    CardModificationDelta Delta,
    string? TemporaryPortraitReference);

public static class CardPrinterRunRecipeStore
{
    private static readonly FieldInfo? RunStartTimeField = typeof(RunManager).GetField(
        "_startTime",
        BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly Dictionary<ModelId, CardPrinterRunRecipe> Recipes = [];
    private static readonly Dictionary<ModelId, CardModel> DisplayCache = [];
    private static bool _registered;
    private static long? _runStartTime;
    private static long _revision;

    public static event Action<ModelId>? Changed;

    public static long Revision => _revision;

    public static void Register()
    {
        if (_registered)
            return;

        _registered = true;
        RunManager.Instance.RunStarted += OnRunStarted;
        CardModificationRuntime.PermanentCardDisplayChanged += OnPermanentCardDisplayChanged;
        _runStartTime = GetRunStartTime();
    }

    public static void OnRunCleaningUp() => DisplayCache.Clear();

    public static bool TryGet(ModelId cardId, out CardPrinterRunRecipe recipe)
    {
        return Recipes.TryGetValue(cardId, out recipe!);
    }

    public static CardModel GetEffectiveCardForDisplay(CardModel canonical)
    {
        if (!Recipes.TryGetValue(canonical.Id, out CardPrinterRunRecipe? recipe))
            return CardModificationRuntime.GetPermanentCardForDisplay(canonical);

        Player? owner = GetLocalPlayer();
        if (owner is null)
            return CardModificationRuntime.GetPermanentCardForDisplay(canonical);

        if (DisplayCache.TryGetValue(canonical.Id, out CardModel? cached)
            && ReferenceEquals(cached.Owner, owner))
        {
            return cached;
        }

        CardModel display = CreateDetachedCard(canonical, owner, recipe);
        DisplayCache[canonical.Id] = display;
        return display;
    }

    public static CardModel CreateDetachedCard(
        CardModel canonical,
        Player owner,
        CardPrinterRunRecipe? recipe = null)
    {
        CardModel source = LoadoutModelRegistry.ResolveCard(canonical.Id) ?? canonical;
        CardModel detached = source.ToMutable();
        detached.Owner = owner;
        if (recipe is not null)
        {
            CardModificationFields.SetDelta(detached, recipe.Delta);
            CardModificationRuntime.ReapplyTemporaryDelta(detached);
            if (!string.IsNullOrWhiteSpace(recipe.TemporaryPortraitReference))
                CardPortraitRuntime.TryApplyTemporaryReference(detached, recipe.TemporaryPortraitReference);
        }
        return detached;
    }

    public static bool SetFromEditor(CardModel card, CardModificationSpec desired)
    {
        CardModificationDelta delta = CardModificationRuntime.CreateTemporaryDelta(card, desired);
        string? portraitReference = CardPortraitRuntime.TryExportTemporaryReference(card, out string? token)
            ? token
            : null;
        return Set(card.Id, delta, portraitReference);
    }

    public static bool Set(
        ModelId cardId,
        CardModificationDelta? delta,
        string? temporaryPortraitReference = null)
    {
        delta ??= new CardModificationDelta();
        temporaryPortraitReference = string.IsNullOrWhiteSpace(temporaryPortraitReference)
            ? null
            : temporaryPortraitReference;
        if (delta.IsEmpty && temporaryPortraitReference is null)
            return Reset(cardId);

        Recipes[cardId] = new CardPrinterRunRecipe(delta.Clone(), temporaryPortraitReference);
        DisplayCache.Remove(cardId);
        _revision++;
        Changed?.Invoke(cardId);
        return true;
    }

    public static bool Reset(ModelId cardId)
    {
        if (!Recipes.Remove(cardId))
            return false;

        DisplayCache.Remove(cardId);
        _revision++;
        Changed?.Invoke(cardId);
        return true;
    }

    private static void OnRunStarted(RunState _)
    {
        long? startTime = GetRunStartTime();
        if (_runStartTime != startTime)
            Recipes.Clear();
        _runStartTime = startTime;
        DisplayCache.Clear();
    }

    private static long? GetRunStartTime()
    {
        try
        {
            return RunStartTimeField?.GetValue(RunManager.Instance) is long startTime
                ? startTime
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static void OnPermanentCardDisplayChanged(ModelId cardId) =>
        DisplayCache.Remove(cardId);

    private static Player? GetLocalPlayer()
    {
        try
        {
            return RunManager.Instance.IsInProgress
                ? LocalContext.GetMe(RunManager.Instance.DebugOnlyGetState())
                : null;
        }
        catch
        {
            return null;
        }
    }
}
