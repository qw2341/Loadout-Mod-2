#nullable enable

namespace Loadout.Services.CardPortraits;

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.CompilerServices;
using Godot;
using Loadout.Patches.Cards.CardModification;
using Loadout.UI.ImageEditing;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;

internal sealed record CardPortraitTextureSequence(
    string PortraitId,
    ModelId CardModelId,
    string FrameId,
    long? RunStartTime,
    IReadOnlyList<Texture2D> Frames,
    IReadOnlyList<double> Durations,
    long ApproximateBytes);

internal static class CardPortraitFields
{
    private static ConditionalWeakTable<CardModel, CardPortraitReference> References = new();
    private static int _referenceCount;

    public static bool HasAny => _referenceCount > 0;

    public static bool TryGet(CardModel card, [NotNullWhen(true)] out CardPortraitReference? reference) =>
        References.TryGetValue(card, out reference);

    public static bool Set(CardModel card, CardPortraitReference reference)
    {
        if (References.TryGetValue(card, out CardPortraitReference? current))
        {
            if (current == reference)
                return false;
            References.Remove(card);
        }
        else
        {
            _referenceCount++;
        }

        References.Add(card, reference);
        return true;
    }

    public static bool Clear(CardModel card)
    {
        if (!References.Remove(card))
            return false;
        _referenceCount = Math.Max(0, _referenceCount - 1);
        return true;
    }

    public static void Copy(CardModel source, CardModel destination)
    {
        if (!References.TryGetValue(source, out CardPortraitReference? reference)
            && (source.DeckVersion is not CardModel deckCard
                || !References.TryGetValue(deckCard, out reference)))
        {
            return;
        }

        Set(destination, reference);
    }

    public static void ClearAll()
    {
        References = new ConditionalWeakTable<CardModel, CardPortraitReference>();
        _referenceCount = 0;
    }
}

internal static class CardPortraitRuntime
{
    private const long MaxOutputPixelsAcrossFrames = 48L * 1024L * 1024L;
    private const long SequenceCacheByteBudget = 512L * 1024L * 1024L;

    private static readonly Dictionary<CardPortraitCacheKey, CardPortraitCacheEntry> SequenceCache = [];
    private static readonly LinkedList<CardPortraitCacheKey> SequenceLru = [];
    private static readonly HashSet<CardPortraitCacheKey> WarnedAssets = [];
    private static long _sequenceCacheBytes;
    private static bool _registered;

    public static void Register()
    {
        if (_registered)
            return;

        _registered = true;
        CardPortraitStore.PermanentChanged += OnPermanentChanged;
        CardPortraitStore.PermanentReloaded += OnPermanentReloaded;
        if (CardPortraitStore.HasAnyPermanent)
            CardPortraitDynamicPatches.EnsureVisualInstalled();
    }

    public static void Unregister()
    {
        if (!_registered)
            return;

        CardPortraitStore.PermanentChanged -= OnPermanentChanged;
        CardPortraitStore.PermanentReloaded -= OnPermanentReloaded;
        ClearSequenceCache();
        WarnedAssets.Clear();
        CardPortraitFields.ClearAll();
        CardPortraitDynamicPatches.Clear();
        CardPortraitStore.Unregister();
        _registered = false;
    }

    public static bool HasOverride(CardModel card)
    {
        Register();
        return CardPortraitFields.TryGet(GetTemporaryOwner(card), out _)
            || CardPortraitStore.HasPermanent(card.Id);
    }

    public static bool TryResolve(CardModel card, out CardPortraitTextureSequence sequence)
    {
        Register();
        if (CardPortraitFields.TryGet(GetTemporaryOwner(card), out CardPortraitReference? reference)
            && CardPortraitStore.TryGetTemporary(reference, out CardPortraitAsset temporary)
            && TryLoadSequence(temporary, card, out sequence))
        {
            return true;
        }

        if (CardPortraitStore.TryGetPermanent(card.Id, out CardPortraitAsset permanent)
            && TryLoadSequence(permanent, card, out sequence))
        {
            return true;
        }

        sequence = null!;
        return false;
    }

    public static bool SaveTemporary(
        CardModel card,
        CardPortraitSaveTarget target,
        ImageEditFrameDefinition frame,
        ImageMediaDocument document,
        string savedPath)
    {
        Register();
        CardModel temporaryOwner = GetTemporaryOwner(card);
        CardPortraitFields.TryGet(temporaryOwner, out CardPortraitReference? previous);
        if (!CardPortraitStore.RegisterTemporary(
                card.Id,
                target,
                frame,
                document,
                savedPath,
                out CardPortraitReference reference))
        {
            return false;
        }

        CardPortraitFields.Set(temporaryOwner, reference);
        if (previous is not null)
            RemoveCachedPortrait(previous.PortraitId);
        if (CardPortraitStore.TryGetTemporary(reference, out CardPortraitAsset asset))
            CacheDocument(asset, card, document);
        CardPortraitDynamicPatches.EnsureTemporaryInstalled();
        CardPortraitDynamicPatches.RefreshTemporary(temporaryOwner);
        return true;
    }

    public static bool SavePermanent(
        CardModel card,
        CardPortraitSaveTarget target,
        ImageEditFrameDefinition frame,
        ImageMediaDocument document,
        string savedPath)
    {
        Register();
        if (!CardPortraitStore.RegisterPermanent(card.Id, target, frame, document, savedPath))
            return false;

        CardModel temporaryOwner = GetTemporaryOwner(card);
        CardPortraitFields.TryGet(temporaryOwner, out CardPortraitReference? previousTemporary);
        CardPortraitFields.Clear(temporaryOwner);
        RemoveCachedSequences(card.Id);
        if (previousTemporary is not null)
            RemoveCachedPortrait(previousTemporary.PortraitId);
        if (CardPortraitStore.TryGetPermanent(card.Id, out CardPortraitAsset asset))
            CacheDocument(asset, card, document);
        CardPortraitDynamicPatches.EnsureVisualInstalled();
        CardPortraitDynamicPatches.RefreshPermanent(card.Id);
        CardModificationRuntime.NotifyPermanentCardVisualChanged(card.Id);
        return true;
    }

    public static bool ResetTemporary(CardModel card)
    {
        Register();
        CardModel temporaryOwner = GetTemporaryOwner(card);
        if (!CardPortraitFields.TryGet(temporaryOwner, out CardPortraitReference? previous)
            || !CardPortraitFields.Clear(temporaryOwner))
        {
            return false;
        }

        RemoveCachedPortrait(previous.PortraitId);
        CardPortraitDynamicPatches.RefreshTemporary(temporaryOwner);
        ReconcilePatches();
        return true;
    }

    public static bool ResetPermanent(CardModel card)
    {
        Register();
        CardModel temporaryOwner = GetTemporaryOwner(card);
        CardPortraitFields.TryGet(temporaryOwner, out CardPortraitReference? previousTemporary);
        bool temporaryChanged = CardPortraitFields.Clear(temporaryOwner);
        bool permanentChanged = CardPortraitStore.ResetPermanent(card.Id);
        if (!permanentChanged)
        {
            RemoveCachedSequences(card.Id);
            CardPortraitDynamicPatches.RefreshPermanent(card.Id);
            CardModificationRuntime.NotifyPermanentCardVisualChanged(card.Id);
        }
        if (previousTemporary is not null)
            RemoveCachedPortrait(previousTemporary.PortraitId);
        if (temporaryChanged)
            CardPortraitDynamicPatches.RefreshTemporary(temporaryOwner);
        ReconcilePatches();
        return temporaryChanged || permanentChanged;
    }

    public static bool HasTemporary(CardModel card) =>
        CardPortraitFields.TryGet(GetTemporaryOwner(card), out _);

    public static void DeleteTemporaryRun(long runStartTime)
    {
        RemoveCachedTemporaryRun(runStartTime);
        CardPortraitFields.ClearAll();
        CardPortraitStore.DeleteTemporaryRun(runStartTime);
        ReconcilePatches();
    }

    private static bool TryLoadSequence(
        CardPortraitAsset asset,
        CardModel card,
        out CardPortraitTextureSequence sequence)
    {
        ImageEditFrameDefinition frame = ImageEditFramePresets.ForCard(
            card.Type,
            card.Rarity == CardRarity.Ancient);
        CardPortraitCacheKey cacheKey = new(asset.Record.PortraitId, frame.Id, asset.GlobalPath);
        if (TryGetCachedSequence(cacheKey, out CardPortraitTextureSequence? cachedSequence))
        {
            sequence = cachedSequence;
            return true;
        }
        if (WarnedAssets.Contains(cacheKey))
        {
            sequence = null!;
            return false;
        }

        try
        {
            ImageMediaMetadata metadata = ImageMediaLoader.ReadMetadata(asset.GlobalPath);
            long decodedPixels = (long)metadata.Width * metadata.Height * metadata.FrameCount;
            if (metadata.Width != asset.Record.Width
                || metadata.Height != asset.Record.Height
                || (metadata.FrameCount > 1) != asset.Record.Animated)
            {
                throw new InvalidDataException("The portrait metadata does not match its saved asset.");
            }
            if (decodedPixels > MaxOutputPixelsAcrossFrames)
                throw new InvalidDataException("The portrait exceeds the decoded frame budget.");

            ImageMediaDocument source = ImageMediaLoader.LoadDocumentFromFile(asset.GlobalPath);
            sequence = CreateSequence(asset, card, frame, source);
            AddCachedSequence(cacheKey, sequence);
            return true;
        }
        catch (Exception exception)
        {
            if (WarnedAssets.Add(cacheKey))
                GD.PushWarning($"CardPortrait: ignored '{asset.GlobalPath}'. {exception.Message}");
            sequence = null!;
            return false;
        }
    }

    private static void CacheDocument(
        CardPortraitAsset asset,
        CardModel card,
        ImageMediaDocument document)
    {
        try
        {
            ImageEditFrameDefinition frame = ImageEditFramePresets.ForCard(
                card.Type,
                card.Rarity == CardRarity.Ancient);
            CardPortraitCacheKey key = new(asset.Record.PortraitId, frame.Id, asset.GlobalPath);
            AddCachedSequence(key, CreateSequence(asset, card, frame, document));
            WarnedAssets.Remove(key);
        }
        catch (Exception exception)
        {
            GD.PushWarning($"CardPortrait: could not cache '{asset.GlobalPath}'. {exception.Message}");
        }
    }

    private static CardPortraitTextureSequence CreateSequence(
        CardPortraitAsset asset,
        CardModel card,
        ImageEditFrameDefinition frame,
        ImageMediaDocument source)
    {
        if (!string.Equals(asset.Record.CardModelId, card.Id.ToString(), StringComparison.Ordinal)
            || source.Width != asset.Record.Width
            || source.Height != asset.Record.Height
            || source.IsAnimated != asset.Record.Animated)
        {
            throw new InvalidDataException("The portrait metadata does not match its saved asset.");
        }

        long outputPixels = (long)frame.OutputSize.X * frame.OutputSize.Y * source.Frames.Count;
        if (outputPixels > MaxOutputPixelsAcrossFrames)
            throw new InvalidDataException("The animated portrait exceeds the decoded frame budget.");

        List<Texture2D> textures = new(source.Frames.Count);
        List<double> durations = new(source.Frames.Count);
        foreach (ImageMediaFrame mediaFrame in source.Frames)
        {
            Image fitted = CenterCover(mediaFrame.Image, frame.OutputSize);
            textures.Add(ImageTexture.CreateFromImage(fitted));
            durations.Add(Math.Clamp(mediaFrame.DurationSeconds, 0.02, 10.0));
        }

        return new CardPortraitTextureSequence(
            asset.Record.PortraitId,
            card.Id,
            frame.Id,
            asset.Record.RunStartTime,
            textures,
            durations,
            outputPixels * 4L);
    }

    private static Image CenterCover(Image source, Vector2I outputSize)
    {
        if (source.GetWidth() == outputSize.X
            && source.GetHeight() == outputSize.Y
            && source.GetFormat() == Image.Format.Rgba8)
        {
            return source;
        }

        Image input = source.Duplicate() as Image
            ?? throw new InvalidOperationException("Could not duplicate the portrait frame.");
        input.Convert(Image.Format.Rgba8);
        if (input.GetWidth() == outputSize.X && input.GetHeight() == outputSize.Y)
            return input;

        float scale = Mathf.Max(
            (float)outputSize.X / input.GetWidth(),
            (float)outputSize.Y / input.GetHeight());
        int scaledWidth = Mathf.Max(outputSize.X, Mathf.CeilToInt(input.GetWidth() * scale));
        int scaledHeight = Mathf.Max(outputSize.Y, Mathf.CeilToInt(input.GetHeight() * scale));
        input.Resize(scaledWidth, scaledHeight, Image.Interpolation.Lanczos);
        Image output = Image.CreateEmpty(outputSize.X, outputSize.Y, false, Image.Format.Rgba8);
        output.Fill(Colors.Transparent);
        Vector2I sourcePosition = new(
            Mathf.Max(0, (scaledWidth - outputSize.X) / 2),
            Mathf.Max(0, (scaledHeight - outputSize.Y) / 2));
        output.BlitRect(input, new Rect2I(sourcePosition, outputSize), Vector2I.Zero);
        return output;
    }

    private static bool TryGetCachedSequence(
        CardPortraitCacheKey key,
        [NotNullWhen(true)] out CardPortraitTextureSequence? sequence)
    {
        if (!SequenceCache.TryGetValue(key, out CardPortraitCacheEntry? entry))
        {
            sequence = null;
            return false;
        }

        SequenceLru.Remove(entry.Node);
        SequenceLru.AddLast(entry.Node);
        sequence = entry.Sequence;
        return true;
    }

    private static void AddCachedSequence(CardPortraitCacheKey key, CardPortraitTextureSequence sequence)
    {
        RemoveCachedKey(key);
        LinkedListNode<CardPortraitCacheKey> node = SequenceLru.AddLast(key);
        SequenceCache[key] = new CardPortraitCacheEntry(sequence, node);
        _sequenceCacheBytes += sequence.ApproximateBytes;
        while (_sequenceCacheBytes > SequenceCacheByteBudget && SequenceCache.Count > 1)
        {
            LinkedListNode<CardPortraitCacheKey>? oldest = SequenceLru.First;
            if (oldest is null)
                break;
            RemoveCachedKey(oldest.Value);
        }
    }

    private static void RemoveCachedKey(CardPortraitCacheKey key)
    {
        if (!SequenceCache.Remove(key, out CardPortraitCacheEntry? entry))
            return;
        SequenceLru.Remove(entry.Node);
        _sequenceCacheBytes = Math.Max(0, _sequenceCacheBytes - entry.Sequence.ApproximateBytes);
    }

    private static void RemoveCachedPortrait(string portraitId)
    {
        RemoveCachedSequences(sequence =>
            string.Equals(sequence.PortraitId, portraitId, StringComparison.Ordinal));
    }

    private static void RemoveCachedTemporaryRun(long runStartTime)
    {
        RemoveCachedSequences(sequence => sequence.RunStartTime == runStartTime);
    }

    private static void OnPermanentChanged(ModelId cardId)
    {
        RemoveCachedSequences(cardId);
        CardPortraitDynamicPatches.RefreshPermanent(cardId);
        CardModificationRuntime.NotifyPermanentCardVisualChanged(cardId);
        ReconcilePatches();
    }

    private static void OnPermanentReloaded(IReadOnlyList<ModelId> changedIds)
    {
        if (CardPortraitStore.HasAnyPermanent)
            CardPortraitDynamicPatches.EnsureVisualInstalled();
        foreach (ModelId cardId in changedIds)
        {
            RemoveCachedSequences(cardId);
            CardPortraitDynamicPatches.RefreshPermanent(cardId);
            CardModificationRuntime.NotifyPermanentCardVisualChanged(cardId);
        }
        ReconcilePatches();
    }

    private static void ReconcilePatches()
    {
        if (!CardPortraitStore.HasAnyPermanent && !CardPortraitFields.HasAny)
            CardPortraitDynamicPatches.Clear();
    }

    private static void RemoveCachedSequences(ModelId cardModelId)
    {
        RemoveCachedSequences(sequence => sequence.CardModelId.Equals(cardModelId));
    }

    private static void RemoveCachedSequences(Func<CardPortraitTextureSequence, bool> predicate)
    {
        List<CardPortraitCacheKey> keys = [];
        foreach ((CardPortraitCacheKey key, CardPortraitCacheEntry entry) in SequenceCache)
        {
            if (predicate(entry.Sequence))
                keys.Add(key);
        }
        foreach (CardPortraitCacheKey key in keys)
        {
            RemoveCachedKey(key);
            WarnedAssets.Remove(key);
        }
    }

    private static void ClearSequenceCache()
    {
        SequenceCache.Clear();
        SequenceLru.Clear();
        _sequenceCacheBytes = 0;
    }

    private static CardModel GetTemporaryOwner(CardModel card) => card.DeckVersion ?? card;

    private readonly record struct CardPortraitCacheKey(
        string PortraitId,
        string FrameId,
        string GlobalPath);

    private sealed record CardPortraitCacheEntry(
        CardPortraitTextureSequence Sequence,
        LinkedListNode<CardPortraitCacheKey> Node);
}
