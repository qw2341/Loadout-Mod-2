#nullable enable

namespace Loadout.UI.ImageEditing;

using System;
using System.Collections.Generic;
using Godot;
using MegaCrit.Sts2.Core.Entities.Cards;

public static class ImageEditFramePresets
{
    private const string AttackBorderPath = "res://images/atlases/ui_atlas.sprites/card/card_portrait_border_attack_s.tres";
    private const string SkillBorderPath = "res://images/atlases/ui_atlas.sprites/card/card_portrait_border_skill_s.tres";
    private const string PowerBorderPath = "res://images/atlases/ui_atlas.sprites/card/card_portrait_border_power_s.tres";
    private const string AncientMaskPath = "res://images/atlases/compressed.sprites/card_template/ancient_portrait_mask_large.tres";
    private const string AncientOverlayPath = "res://images/atlases/compressed.sprites/card_template/ancient_card_border.tres";

    private static readonly Dictionary<string, ImageEditFrameDefinition> CachedFrames = new(StringComparer.Ordinal);

    public static ImageEditFrameDefinition Companion =>
        GetOrCreate("companion", () => ImageEditFrameDefinition.Rectangle("companion", new Vector2I(192, 224)));

    public static ImageEditFrameDefinition AttackCard => CreateCardFrame("card_attack", AttackBorderPath);

    public static ImageEditFrameDefinition SkillCard => CreateCardFrame("card_skill", SkillBorderPath);

    public static ImageEditFrameDefinition PowerCard => CreateCardFrame("card_power", PowerBorderPath);

    public static ImageEditFrameDefinition OtherCard => CreateCardFrame("card_other", SkillBorderPath);

    public static ImageEditFrameDefinition AncientCard => GetOrCreate("card_ancient", () =>
    {
        Texture2D? maskTexture = LoadTexture(AncientMaskPath);
        Image? mask = ExtractReadableImage(maskTexture);
        mask = NormalizeMask(mask, new Vector2I(606, 852));
        return new ImageEditFrameDefinition(
            "card_ancient",
            new Vector2I(606, 852),
            mask,
            LoadTexture(AncientOverlayPath),
            BakeMaskIntoOutput: false);
    });

    public static ImageEditFrameDefinition ForCard(CardType type, bool ancient = false)
    {
        if (ancient)
            return AncientCard;

        return type switch
        {
            CardType.Attack => AttackCard,
            CardType.Power => PowerCard,
            CardType.Skill => SkillCard,
            _ => OtherCard
        };
    }

    public static ImageEditFrameDefinition CustomMask(
        string id,
        Vector2I outputSize,
        Image alphaMask,
        Texture2D? previewOverlay = null,
        bool bakeMaskIntoOutput = true)
    {
        ArgumentNullException.ThrowIfNull(alphaMask);
        return new ImageEditFrameDefinition(
            id,
            outputSize,
            NormalizeMask(alphaMask.Duplicate() as Image, outputSize),
            previewOverlay,
            bakeMaskIntoOutput);
    }

    private static ImageEditFrameDefinition CreateCardFrame(string id, string borderPath)
    {
        return GetOrCreate(id, () =>
        {
            Texture2D? border = LoadTexture(borderPath);
            Image? mask = CreateInteriorMask(ExtractReadableImage(border), new Vector2I(1000, 760));
            return new ImageEditFrameDefinition(
                id,
                new Vector2I(1000, 760),
                mask,
                border,
                BakeMaskIntoOutput: false);
        });
    }

    private static ImageEditFrameDefinition GetOrCreate(string id, Func<ImageEditFrameDefinition> factory)
    {
        if (!CachedFrames.TryGetValue(id, out ImageEditFrameDefinition? frame))
        {
            frame = factory();
            CachedFrames[id] = frame;
        }

        return frame;
    }

    private static Texture2D? LoadTexture(string path)
    {
        return ResourceLoader.Exists(path)
            ? ResourceLoader.Load<Texture2D>(path, null, ResourceLoader.CacheMode.Reuse)
            : null;
    }

    private static Image? ExtractReadableImage(Texture2D? texture)
    {
        if (texture is null)
            return null;

        try
        {
            if (texture is AtlasTexture atlasTexture && atlasTexture.Atlas is { } atlas)
            {
                Image? atlasImage = MakeReadable(atlas.GetImage());
                if (atlasImage is null)
                    return null;

                Rect2 region = atlasTexture.Region;
                Rect2I sourceRect = new(
                    Mathf.RoundToInt(region.Position.X),
                    Mathf.RoundToInt(region.Position.Y),
                    Mathf.RoundToInt(region.Size.X),
                    Mathf.RoundToInt(region.Size.Y));
                Rect2I atlasBounds = new(Vector2I.Zero, atlasImage.GetSize());
                sourceRect = sourceRect.Intersection(atlasBounds);
                if (sourceRect.Size.X <= 0 || sourceRect.Size.Y <= 0)
                    return null;

                Image regionImage = atlasImage.GetRegion(sourceRect);
                Rect2 margin = atlasTexture.Margin;
                Vector2I destination = new(
                    Mathf.RoundToInt(margin.Position.X),
                    Mathf.RoundToInt(margin.Position.Y));
                Vector2I outputSize = new(
                    Mathf.Max(1, sourceRect.Size.X + Mathf.RoundToInt(margin.Size.X)),
                    Mathf.Max(1, sourceRect.Size.Y + Mathf.RoundToInt(margin.Size.Y)));
                if (destination == Vector2I.Zero && outputSize == sourceRect.Size)
                    return regionImage;

                Image output = Image.CreateEmpty(outputSize.X, outputSize.Y, false, Image.Format.Rgba8);
                output.Fill(Colors.Transparent);
                output.BlitRect(regionImage, new Rect2I(Vector2I.Zero, regionImage.GetSize()), destination);
                return output;
            }

            return MakeReadable(texture.GetImage());
        }
        catch (Exception exception)
        {
            GD.PushWarning($"Loadout: could not read native image-edit frame texture '{texture.ResourcePath}'. {exception.Message}");
            return null;
        }
    }

    private static Image? MakeReadable(Image? image)
    {
        if (image is null || image.IsEmpty())
            return null;

        if (image.IsCompressed())
        {
            Error error = image.Decompress();
            if (error != Error.Ok)
            {
                GD.PushWarning($"Loadout: could not decompress a native image-edit frame texture ({error}).");
                return null;
            }
        }

        image.Convert(Image.Format.Rgba8);
        return image;
    }

    private static Image? CreateInteriorMask(Image? borderImage, Vector2I outputSize)
    {
        if (borderImage is null || borderImage.IsEmpty())
            return null;

        Image source = borderImage.Duplicate() as Image ?? borderImage;
        source.Convert(Image.Format.Rgba8);
        int width = source.GetWidth();
        int height = source.GetHeight();
        int startX = width / 2;
        int startY = height / 2;

        if (source.GetPixel(startX, startY).A > 0.35f)
            return null;

        Image mask = Image.CreateEmpty(width, height, false, Image.Format.Rgba8);
        mask.Fill(Colors.Transparent);
        bool[] visited = new bool[width * height];
        int[] queue = new int[width * height];
        int head = 0;
        int tail = 0;
        queue[tail++] = startY * width + startX;
        visited[queue[0]] = true;

        while (head < tail)
        {
            int index = queue[head++];
            int x = index % width;
            int y = index / width;
            if (source.GetPixel(x, y).A > 0.35f)
                continue;

            mask.SetPixel(x, y, Colors.White);
            Enqueue(x - 1, y);
            Enqueue(x + 1, y);
            Enqueue(x, y - 1);
            Enqueue(x, y + 1);
        }

        mask.Resize(outputSize.X, outputSize.Y, Image.Interpolation.Lanczos);
        return mask;

        void Enqueue(int x, int y)
        {
            if (x < 0 || x >= width || y < 0 || y >= height)
                return;

            int index = y * width + x;
            if (visited[index])
                return;

            visited[index] = true;
            queue[tail++] = index;
        }
    }

    private static Image? NormalizeMask(Image? mask, Vector2I outputSize)
    {
        if (mask is null || mask.IsEmpty())
            return null;

        mask.Convert(Image.Format.Rgba8);
        if (mask.GetWidth() != outputSize.X || mask.GetHeight() != outputSize.Y)
            mask.Resize(outputSize.X, outputSize.Y, Image.Interpolation.Lanczos);
        return mask;
    }
}
