#nullable enable

namespace Loadout.UI.ImageEditing;

using System;
using Godot;
using MegaCrit.Sts2.Core.Entities.Cards;

public static class ImageEditFramePresets
{
    public static ImageEditFrameDefinition Companion =>
        ImageEditFrameDefinition.Rectangle("companion", new Vector2I(192, 224));

    public static ImageEditFrameDefinition AttackCard =>
        ImageEditFrameDefinition.Rectangle("card_attack", new Vector2I(1000, 760));

    public static ImageEditFrameDefinition SkillCard =>
        ImageEditFrameDefinition.Rectangle("card_skill", new Vector2I(1000, 760));

    public static ImageEditFrameDefinition PowerCard =>
        ImageEditFrameDefinition.Rectangle("card_power", new Vector2I(1000, 760));

    public static ImageEditFrameDefinition OtherCard =>
        ImageEditFrameDefinition.Rectangle("card_other", new Vector2I(1000, 760));

    public static ImageEditFrameDefinition AncientCard =>
        ImageEditFrameDefinition.Rectangle("card_ancient", new Vector2I(606, 852));

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
