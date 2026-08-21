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
    private sealed record CardPortraitIdentity(string Value);

    private static ConditionalWeakTable<CardModel, CardPortraitIdentity> Identities = new();
    private static readonly Dictionary<string, CardPortraitReference> ReferencesByIdentity =
        new(StringComparer.Ordinal);

    public static bool HasAny => ReferencesByIdentity.Count > 0;

    public static string GetOrCreateIdentity(CardModel card)
    {
        if (card.IsCanonical)
            throw new InvalidOperationException("A temporary portrait cannot be attached to a canonical card model.");

        if (Identities.TryGetValue(card, out CardPortraitIdentity? identity))
            return identity.Value;

        string value = Guid.NewGuid().ToString("N");
        Identities.Add(card, new CardPortraitIdentity(value));
        return value;
    }

    public static bool TryGet(CardModel card, [NotNullWhen(true)] out CardPortraitReference? reference)
    {
        if (!card.IsCanonical
            && Identities.TryGetValue(card, out CardPortraitIdentity? identity)
            && ReferencesByIdentity.TryGetValue(identity.Value, out reference))
        {
            return true;
        }

        reference = null;
        return false;
    }

    public static bool Set(CardModel card, CardPortraitReference reference)
    {
        if (card.IsCanonical)
            return false;

        if (Identities.TryGetValue(card, out CardPortraitIdentity? identity))
        {
            if (string.Equals(identity.Value, reference.CardInstanceId, StringComparison.Ordinal)
                && ReferencesByIdentity.TryGetValue(identity.Value, out CardPortraitReference? current)
                && current == reference)
            {
                return false;
            }

            if (!string.Equals(identity.Value, reference.CardInstanceId, StringComparison.Ordinal))
            {
                Identities.Remove(card);
                Identities.Add(card, new CardPortraitIdentity(reference.CardInstanceId));
            }
        }
        else
        {
            Identities.Add(card, new CardPortraitIdentity(reference.CardInstanceId));
        }

        ReferencesByIdentity[reference.CardInstanceId] = reference;
        return true;
    }

    public static bool Clear(CardModel card)
    {
        if (!Identities.TryGetValue(card, out CardPortraitIdentity? identity))
            return false;

        return ReferencesByIdentity.Remove(identity.Value);
    }

    public static void Copy(CardModel source, CardModel destination)
    {
        if (source.IsCanonical || destination.IsCanonical)
            return;

        if (!Identities.TryGetValue(source, out CardPortraitIdentity? identity)
            && (source.DeckVersion is not CardModel deckCard
                || !Identities.TryGetValue(deckCard, out identity)))
        {
            return;
        }

        if (Identities.TryGetValue(destination, out CardPortraitIdentity? destinationIdentity))
        {
            if (string.Equals(destinationIdentity.Value, identity.Value, StringComparison.Ordinal))
                return;
            Identities.Remove(destination);
        }

        Identities.Add(destination, identity);
    }

    public static bool SharesIdentity(CardModel first, CardModel second)
    {
        return !first.IsCanonical
            && !second.IsCanonical
            && Identities.TryGetValue(first, out CardPortraitIdentity? firstIdentity)
            && Identities.TryGetValue(second, out CardPortraitIdentity? secondIdentity)
            && string.Equals(firstIdentity.Value, secondIdentity.Value, StringComparison.Ordinal);
    }

    public static void ClearAll()
    {
        Identities = new ConditionalWeakTable<CardModel, CardPortraitIdentity>();
        ReferencesByIdentity.Clear();
    }
}

internal static class CardPortraitRuntime
{
    private const long MaxOutputPixelsAcrossFrames = ImageAnimationSizing.MaxOutputPixelsAcrossFrames;
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
        if (temporaryOwner.IsCanonical)
            return false;

        string cardInstanceId = CardPortraitFields.GetOrCreateIdentity(temporaryOwner);
        CardPortraitFields.TryGet(temporaryOwner, out CardPortraitReference? previous);
        if (!CardPortraitStore.RegisterTemporary(
                card.Id,
                cardInstanceId,
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
        CardPortraitDynamicPatches.RefreshTemporary(temporaryOwner, previous?.PortraitId);
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
        CardPortraitDynamicPatches.RefreshModelId(card.Id);
        CardModificationRuntime.NotifyPermanentCardVisualChanged(card.Id);
        return true;
    }

    public static bool ResetTemporary(CardModel card)
    {
        Register();
        CardModel temporaryOwner = GetTemporaryOwner(card);
        if (!CardPortraitFields.TryGet(temporaryOwner, out CardPortraitReference? previous))
        {
            CardPortraitDynamicPatches.RefreshModelId(card.Id);
            return false;
        }
        if (!CardPortraitFields.Clear(temporaryOwner))
            return false;

        RemoveCachedPortrait(previous.PortraitId);
        CardPortraitDynamicPatches.RefreshTemporary(temporaryOwner, previous.PortraitId);
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
            CardPortraitDynamicPatches.RefreshModelId(card.Id);
            CardModificationRuntime.NotifyPermanentCardVisualChanged(card.Id);
        }
        if (previousTemporary is not null)
            RemoveCachedPortrait(previousTemporary.PortraitId);
        if (temporaryChanged)
            CardPortraitDynamicPatches.RefreshTemporary(temporaryOwner, previousTemporary?.PortraitId);
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
            CardModificationRuntime.ShouldUseAncientRendering(card));
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
                CardModificationRuntime.ShouldUseAncientRendering(card));
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

        Vector2I outputSize = ImageAnimationSizing.GetOutputSize(
            frame.OutputSize,
            source.Frames.Count);
        long outputPixels = (long)outputSize.X * outputSize.Y * source.Frames.Count;
        if (outputPixels > MaxOutputPixelsAcrossFrames)
            throw new InvalidDataException("The animated portrait exceeds the decoded frame budget.");

        List<Texture2D> textures = new(source.Frames.Count);
        List<double> durations = new(source.Frames.Count);
        foreach (ImageMediaFrame mediaFrame in source.Frames)
        {
            Image fitted = CenterCover(mediaFrame.Image, outputSize);
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
        CardPortraitDynamicPatches.RefreshModelId(cardId);
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
            CardPortraitDynamicPatches.RefreshModelId(cardId);
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
