#nullable enable

namespace Loadout.Services.CardPortraits;

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.CompilerServices;
using Godot;
using Loadout.UI.ImageEditing;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;

internal sealed record CardPortraitTextureSequence(
    string PortraitId,
    string CardModelId,
    string FrameId,
    IReadOnlyList<Texture2D> Frames,
    IReadOnlyList<double> Durations);

internal static class CardPortraitFields
{
    private static ConditionalWeakTable<CardModel, CardPortraitReference> References = new();

    public static bool TryGet(CardModel card, [NotNullWhen(true)] out CardPortraitReference? reference) =>
        References.TryGetValue(card, out reference);

    public static bool Set(CardModel card, CardPortraitReference reference)
    {
        if (References.TryGetValue(card, out CardPortraitReference? current) && current == reference)
            return false;

        References.Remove(card);
        References.Add(card, reference);
        return true;
    }

    public static bool Clear(CardModel card) => References.Remove(card);

    public static void Copy(CardModel source, CardModel destination)
    {
        if (!References.TryGetValue(source, out CardPortraitReference? reference))
            return;

        References.Remove(destination);
        References.Add(destination, reference);
    }

    public static void ClearAll()
    {
        References = new ConditionalWeakTable<CardModel, CardPortraitReference>();
    }
}

internal static class CardPortraitRuntime
{
    private const long MaxOutputPixelsAcrossFrames = 48L * 1024L * 1024L;

    private static readonly Dictionary<string, CardPortraitTextureSequence> SequenceCache =
        new(StringComparer.Ordinal);
    private static readonly HashSet<string> WarnedAssets = new(StringComparer.OrdinalIgnoreCase);
    private static bool _registered;

    public static event Action<CardModel>? TemporaryChanged;
    public static event Action<ModelId>? PermanentChanged;

    public static void Register()
    {
        if (_registered)
            return;

        _registered = true;
        CardPortraitStore.PermanentChanged += OnPermanentChanged;
        CardPortraitStore.PermanentReloaded += OnPermanentReloaded;
        if (CardPortraitStore.HasAnyPermanent)
            CardPortraitDynamicPatches.EnsureInstalled();
    }

    public static void Unregister()
    {
        if (!_registered)
            return;

        CardPortraitStore.PermanentChanged -= OnPermanentChanged;
        CardPortraitStore.PermanentReloaded -= OnPermanentReloaded;
        SequenceCache.Clear();
        WarnedAssets.Clear();
        CardPortraitFields.ClearAll();
        CardPortraitDynamicPatches.Clear();
        CardPortraitStore.Unregister();
        _registered = false;
    }

    public static bool TryResolve(CardModel card, out CardPortraitTextureSequence sequence)
    {
        Register();
        if (CardPortraitFields.TryGet(card, out CardPortraitReference? reference)
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

        CardPortraitFields.Set(card, reference);
        CardPortraitDynamicPatches.EnsureInstalled();
        TemporaryChanged?.Invoke(card);
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

        bool temporaryChanged = CardPortraitFields.Clear(card);
        CardPortraitDynamicPatches.EnsureInstalled();
        if (temporaryChanged)
            TemporaryChanged?.Invoke(card);
        return true;
    }

    public static bool ResetTemporary(CardModel card)
    {
        Register();
        if (!CardPortraitFields.Clear(card))
            return false;

        TemporaryChanged?.Invoke(card);
        return true;
    }

    public static bool ResetPermanent(CardModel card)
    {
        Register();
        bool temporaryChanged = CardPortraitFields.Clear(card);
        bool permanentChanged = CardPortraitStore.ResetPermanent(card.Id);
        if (temporaryChanged)
            TemporaryChanged?.Invoke(card);
        return temporaryChanged || permanentChanged;
    }

    public static bool HasTemporary(CardModel card) => CardPortraitFields.TryGet(card, out _);

    public static void DeleteTemporaryRun(long runStartTime)
    {
        SequenceCache.Clear();
        CardPortraitStore.DeleteTemporaryRun(runStartTime);
    }

    private static bool TryLoadSequence(
        CardPortraitAsset asset,
        CardModel card,
        out CardPortraitTextureSequence sequence)
    {
        ImageEditFrameDefinition frame = ImageEditFramePresets.ForCard(
            card.Type,
            card.Rarity == CardRarity.Ancient);
        string cacheKey = $"{asset.Record.PortraitId}|{frame.Id}|{asset.GlobalPath}";
        if (SequenceCache.TryGetValue(cacheKey, out sequence!))
            return true;
        if (WarnedAssets.Contains(cacheKey))
        {
            sequence = null!;
            return false;
        }

        try
        {
            if (frame.AlphaMask is null || frame.AlphaMask.IsEmpty())
                throw new InvalidDataException($"The native mask '{frame.Id}' is unavailable.");

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
                Image masked = PrepareMaskedFrame(mediaFrame.Image, frame);
                textures.Add(ImageTexture.CreateFromImage(masked));
                durations.Add(Math.Clamp(mediaFrame.DurationSeconds, 0.02, 10.0));
            }

            sequence = new CardPortraitTextureSequence(
                asset.Record.PortraitId,
                asset.Record.CardModelId,
                frame.Id,
                textures,
                durations);
            SequenceCache[cacheKey] = sequence;
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

    private static Image PrepareMaskedFrame(Image source, ImageEditFrameDefinition frame)
    {
        Image fitted = CenterCover(source, frame.OutputSize);
        Image mask = frame.AlphaMask!.Duplicate() as Image
            ?? throw new InvalidOperationException("Could not duplicate the portrait mask.");
        if (mask.GetWidth() != frame.OutputSize.X || mask.GetHeight() != frame.OutputSize.Y)
        {
            mask.Resize(frame.OutputSize.X, frame.OutputSize.Y, Image.Interpolation.Lanczos);
        }

        fitted.Convert(Image.Format.Rgba8);
        mask.Convert(Image.Format.Rgba8);
        byte[] pixels = fitted.GetData();
        byte[] maskPixels = mask.GetData();
        for (int index = 3; index < pixels.Length; index += 4)
            pixels[index] = (byte)((pixels[index] * maskPixels[index] + 127) / 255);
        return Image.CreateFromData(
            fitted.GetWidth(),
            fitted.GetHeight(),
            false,
            Image.Format.Rgba8,
            pixels);
    }

    private static Image CenterCover(Image source, Vector2I outputSize)
    {
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
        output.BlitRect(
            input,
            new Rect2I(sourcePosition, outputSize),
            Vector2I.Zero);
        return output;
    }

    private static void OnPermanentChanged(ModelId cardId)
    {
        RemoveCachedSequences(cardId.ToString());
        CardPortraitDynamicPatches.EnsureInstalled();
        PermanentChanged?.Invoke(cardId);
    }

    private static void OnPermanentReloaded(IReadOnlyList<ModelId> changedIds)
    {
        SequenceCache.Clear();
        WarnedAssets.Clear();
        if (CardPortraitStore.HasAnyPermanent)
            CardPortraitDynamicPatches.EnsureInstalled();
        foreach (ModelId cardId in changedIds)
            PermanentChanged?.Invoke(cardId);
    }

    private static void RemoveCachedSequences(string cardModelId)
    {
        List<string> keys = [];
        foreach ((string key, CardPortraitTextureSequence value) in SequenceCache)
        {
            if (string.Equals(value.CardModelId, cardModelId, StringComparison.Ordinal))
                keys.Add(key);
        }
        foreach (string key in keys)
        {
            SequenceCache.Remove(key);
            WarnedAssets.Remove(key);
        }
    }
}
