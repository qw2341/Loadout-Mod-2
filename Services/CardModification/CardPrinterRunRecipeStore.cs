#nullable enable

namespace Loadout.Services.CardModification;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Text.Json.Serialization;
using Godot;
using Loadout.Patches.Cards.CardModification;
using Loadout.Services.Actions;
using Loadout.Services.CardPortraits;
using Loadout.Services.Saving;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

public sealed record CardPrinterRunRecipe(
    CardModificationDelta Delta,
    string? TemporaryPortraitReference)
{
    public CardPrinterRunRecipe Copy() =>
        new(Delta.Clone(), TemporaryPortraitReference);
}

public static class CardPrinterRunRecipeStore
{
    private const int CurrentSchemaVersion = 1;
    private const string RunDirectory = "loadout/card_printer";
    private const string RunFilePrefix = "card_printer_recipes";
    private const int MaxDeltaJsonLength = 256 * 1024;
    private const int MaxPortraitReferenceLength = 2048;

    private static readonly object Gate = new();
    private static readonly Dictionary<ModelId, CardModel> DisplayCache = [];
    private static RecipeSaveData _save = new();
    private static bool _registered;
    private static bool _loaded;
    private static long? _loadedRunStartTime;
    private static long _revision;

    public static event Action<ModelId>? Changed;

    public static long Revision
    {
        get
        {
            lock (Gate)
                return _revision;
        }
    }

    public static void Register()
    {
        if (_registered)
            return;

        _registered = true;
        RunManager.Instance.RunStarted += OnRunStarted;
        SaveManager.Instance.ProfileIdChanged += OnProfileIdChanged;
        CardModificationRuntime.PermanentCardDisplayChanged += OnPermanentCardDisplayChanged;
        EnsureLoaded();
    }

    public static void OnRunCleaningUp()
    {
        lock (Gate)
        {
            _loaded = false;
            _loadedRunStartTime = null;
            _save = new RecipeSaveData();
            DisplayCache.Clear();
            _revision++;
        }
    }

    public static bool TryGet(ModelId cardId, out CardPrinterRunRecipe recipe)
    {
        EnsureLoaded();
        lock (Gate)
        {
            if (_save.Recipes.TryGetValue(NormalizeCardId(cardId), out RecipeEntry? entry)
                && TryCreateRecipe(entry, out CardPrinterRunRecipe? found))
            {
                recipe = found!.Copy();
                return true;
            }
        }

        recipe = new CardPrinterRunRecipe(new CardModificationDelta(), null);
        return false;
    }

    public static CardModel GetEffectiveCardForDisplay(CardModel canonical)
    {
        Register();
        if (!TryGet(canonical.Id, out CardPrinterRunRecipe recipe))
            return CardModificationRuntime.GetPermanentCardForDisplay(canonical);

        Player? owner = GetLocalPlayer();
        if (owner is null)
            return CardModificationRuntime.GetPermanentCardForDisplay(canonical);

        lock (Gate)
        {
            if (DisplayCache.TryGetValue(canonical.Id, out CardModel? cached)
                && ReferenceEquals(cached.Owner, owner))
                return cached;
        }

        CardModel display = CreateDetachedCard(canonical, owner, recipe);
        lock (Gate)
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
        EnsureLoaded();
        if (LoadoutModelRegistry.ResolveCard(cardId) is null)
            return false;
        if (!string.IsNullOrWhiteSpace(temporaryPortraitReference)
            && temporaryPortraitReference.Length > MaxPortraitReferenceLength)
        {
            return false;
        }

        delta ??= new CardModificationDelta();
        delta = delta.Clone();
        delta.Normalize();
        if (!CardModificationRuntime.IsValidPrinterDelta(cardId, delta))
            return false;
        string deltaJson = delta.IsEmpty ? string.Empty : CardModificationCodec.SerializeDelta(delta);
        if (deltaJson.Length > MaxDeltaJsonLength)
            return false;
        temporaryPortraitReference = NormalizePortraitReference(temporaryPortraitReference);
        if (temporaryPortraitReference is not null
            && !CardPortraitRuntime.IsValidTemporaryReference(temporaryPortraitReference))
        {
            return false;
        }

        string key = NormalizeCardId(cardId);
        bool changed;
        lock (Gate)
        {
            if (delta.IsEmpty && temporaryPortraitReference is null)
            {
                changed = _save.Recipes.Remove(key);
            }
            else
            {
                RecipeEntry next = new()
                {
                    Delta = delta.Clone(),
                    TemporaryPortraitReference = temporaryPortraitReference
                };
                changed = !_save.Recipes.TryGetValue(key, out RecipeEntry? current)
                          || !string.Equals(
                              CardModificationCodec.SerializeDelta(current.Delta ?? new CardModificationDelta()),
                              deltaJson,
                              StringComparison.Ordinal)
                          || !string.Equals(current.TemporaryPortraitReference, next.TemporaryPortraitReference, StringComparison.Ordinal);
                if (changed)
                    _save.Recipes[key] = next;
            }

            if (!changed)
                return false;

            DisplayCache.Remove(cardId);
            SaveLocked();
            _revision++;
        }

        Changed?.Invoke(cardId);
        return true;
    }

    public static bool Reset(ModelId cardId)
    {
        EnsureLoaded();
        bool changed;
        lock (Gate)
        {
            changed = _save.Recipes.Remove(NormalizeCardId(cardId));
            if (!changed)
                return false;
            DisplayCache.Remove(cardId);
            SaveLocked();
            _revision++;
        }

        Changed?.Invoke(cardId);
        return true;
    }

    private static void OnRunStarted(RunState _) => Reload();

    private static void OnProfileIdChanged(int _) => Reload();

    private static void OnPermanentCardDisplayChanged(ModelId cardId)
    {
        lock (Gate)
            DisplayCache.Remove(cardId);
    }

    private static void Reload()
    {
        lock (Gate)
        {
            _loaded = false;
            _loadedRunStartTime = null;
            DisplayCache.Clear();
            _revision++;
        }
        EnsureLoaded();
    }

    private static void EnsureLoaded()
    {
        long? runStartTime = SaveUtility.GetCurrentRunStartTime();
        lock (Gate)
        {
            if (_loaded && _loadedRunStartTime == runStartTime)
                return;

            _loaded = true;
            _loadedRunStartTime = runStartTime;
            DisplayCache.Clear();
            if (!runStartTime.HasValue)
            {
                _save = new RecipeSaveData();
                return;
            }

            string path = GetRunPath(runStartTime.Value);
            SaveUtility.LoadResult<RecipeSaveData> loaded = SaveUtility.LoadProfileJson(
                path,
                new RecipeSaveData { RunStartTime = runStartTime.Value });
            bool stale = loaded.Loaded && loaded.Value.RunStartTime != runStartTime.Value;
            _save = stale
                ? new RecipeSaveData { RunStartTime = runStartTime.Value }
                : Normalize(loaded.Value, runStartTime.Value);
            if (loaded.Loaded && (stale || loaded.Value.SchemaVersion != CurrentSchemaVersion))
                SaveLocked();
        }
    }

    private static RecipeSaveData Normalize(RecipeSaveData save, long runStartTime)
    {
        RecipeSaveData normalized = new()
        {
            SchemaVersion = CurrentSchemaVersion,
            RunStartTime = runStartTime
        };
        foreach ((string rawId, RecipeEntry? entry) in save.Recipes ?? [])
        {
            if (entry is null
                || !LoadoutModelRegistry.TryResolveWireId(rawId, out ModelId cardId)
                || LoadoutModelRegistry.ResolveCard(cardId) is null
                || !TryCreateRecipe(entry, out CardPrinterRunRecipe? recipe))
            {
                continue;
            }

            normalized.Recipes[NormalizeCardId(cardId)] = new RecipeEntry
            {
                Delta = recipe!.Delta.Clone(),
                TemporaryPortraitReference = recipe.TemporaryPortraitReference
            };
        }
        return normalized;
    }

    private static bool TryCreateRecipe(RecipeEntry entry, out CardPrinterRunRecipe? recipe)
    {
        recipe = null;
        CardModificationDelta delta;
        try
        {
            delta = entry.Delta?.Clone() ?? new CardModificationDelta();
            delta.Normalize();
            if (CardModificationCodec.SerializeDelta(delta).Length > MaxDeltaJsonLength)
                return false;
        }
        catch
        {
            return false;
        }
        if (!string.IsNullOrWhiteSpace(entry.TemporaryPortraitReference)
            && entry.TemporaryPortraitReference.Length > MaxPortraitReferenceLength)
            return false;
        string? portrait = NormalizePortraitReference(entry.TemporaryPortraitReference);
        if (portrait is not null && !CardPortraitRuntime.IsValidTemporaryReference(portrait))
            return false;
        if (delta.IsEmpty && portrait is null)
            return false;
        recipe = new CardPrinterRunRecipe(delta, portrait);
        return true;
    }

    private static string NormalizeCardId(ModelId cardId) =>
        LoadoutModelIdSafety.ToWireString(cardId);

    private static string? NormalizePortraitReference(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxPortraitReferenceLength)
            return null;
        return value.Trim();
    }

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

    private static string GetRunPath(long runStartTime) =>
        SaveUtility.GetRunSidecarPath(RunDirectory, RunFilePrefix, runStartTime);

    private static void SaveLocked()
    {
        if (!_loadedRunStartTime.HasValue)
            return;
        _save.SchemaVersion = CurrentSchemaVersion;
        _save.RunStartTime = _loadedRunStartTime.Value;
        SaveUtility.SaveProfileJson(GetRunPath(_loadedRunStartTime.Value), _save);
    }

    private sealed class RecipeEntry
    {
        [JsonPropertyName("delta")]
        public CardModificationDelta Delta { get; set; } = new();

        [JsonPropertyName("portrait")]
        public string? TemporaryPortraitReference { get; set; }
    }

    private struct RecipeSaveData : ISerializable
    {
        public RecipeSaveData()
        {
        }

        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        [JsonPropertyName("runStartTime")]
        public long RunStartTime { get; set; }

        [JsonPropertyName("recipes")]
        public Dictionary<string, RecipeEntry> Recipes { get; set; } = new(StringComparer.Ordinal);

        public void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            info.AddValue(nameof(SchemaVersion), SchemaVersion);
            info.AddValue(nameof(RunStartTime), RunStartTime);
            info.AddValue(nameof(Recipes), Recipes);
        }
    }
}
