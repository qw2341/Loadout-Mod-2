#nullable enable

namespace Loadout.UI.Screens.Controls;

using Godot;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.CommonUi;

public static class NLoadoutConfirmButtonFactory
{
    public static NConfirmButton Create()
    {
        NConfirmButton confirmButton = new()
        {
            FocusMode = Control.FocusModeEnum.All,
            MouseFilter = Control.MouseFilterEnum.Stop,
            PivotOffset = new Vector2(180f, 40f)
        };
        confirmButton.SetAnchorsPreset(Control.LayoutPreset.BottomRight);
        confirmButton.OffsetLeft = -160f;
        confirmButton.OffsetTop = -354f;
        confirmButton.OffsetRight = 40f;
        confirmButton.OffsetBottom = -244f;

        TextureRect shadow = CreateTextureRect(
            "Shadow",
            LoadTexture("res://images/atlases/ui_atlas.sprites/confirm_button.tres"));
        shadow.Modulate = new Color(0f, 0f, 0f, 0.25098f);
        shadow.OffsetLeft = -41f;
        shadow.OffsetTop = -1f;
        shadow.OffsetRight = 26f;
        shadow.OffsetBottom = 39f;
        confirmButton.AddChild(shadow);

        TextureRect outline = CreateTextureRect(
            "Outline",
            LoadTexture("res://images/atlases/compressed.sprites/confirm_button_outline.tres"));
        outline.Modulate = new Color(0.941176f, 0.705882f, 0f, 0.752941f);
        outline.OffsetLeft = -56f;
        outline.OffsetTop = -16f;
        outline.OffsetRight = 17f;
        outline.OffsetBottom = 30f;
        confirmButton.AddChild(outline);

        TextureRect image = CreateTextureRect(
            "Image",
            LoadTexture("res://images/atlases/ui_atlas.sprites/confirm_button.tres"));
        image.ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize;
        image.OffsetLeft = -53f;
        image.OffsetTop = -13f;
        image.OffsetRight = 14f;
        image.OffsetBottom = 27f;
        confirmButton.AddChild(image);

        TextureRect icon = CreateTextureRect(
            "Icon",
            LoadTexture("res://images/atlases/compressed.sprites/confirm_button_tick.tres"));
        icon.Modulate = StsColors.cream;
        icon.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
        icon.OffsetLeft = 88f;
        icon.OffsetTop = 28f;
        icon.OffsetRight = 168f;
        icon.OffsetBottom = 108f;
        image.AddChild(icon);

        TextureRect controllerIcon = new()
        {
            Name = "ControllerIcon",
            UniqueNameInOwner = true,
            Visible = false,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Scale = new Vector2(0.5f, 0.5f),
            PivotOffset = new Vector2(256f, 128f)
        };
        controllerIcon.SetAnchorsPreset(Control.LayoutPreset.Center);
        controllerIcon.OffsetLeft = -142.5f;
        controllerIcon.OffsetTop = -64f;
        controllerIcon.OffsetRight = 97.5f;
        controllerIcon.OffsetBottom = 56f;
        image.AddChild(controllerIcon);

        AssignOwnerRecursive(confirmButton, confirmButton);
        return confirmButton;
    }

    private static TextureRect CreateTextureRect(string name, Texture2D? texture)
    {
        TextureRect textureRect = new()
        {
            Name = name,
            Texture = texture,
            ExpandMode = TextureRect.ExpandModeEnum.FitWidthProportional,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        textureRect.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        return textureRect;
    }

    private static Texture2D? LoadTexture(string path)
    {
        string localPath = path.Replace("res://images/atlases/", "res://Loadout/images/atlases/");
        if (ResourceLoader.Exists(localPath))
            return GD.Load<Texture2D>(localPath);
        return ResourceLoader.Exists(path) ? GD.Load<Texture2D>(path) : null;
    }

    private static void AssignOwnerRecursive(Node root, Node owner)
    {
        foreach (Node child in root.GetChildren())
        {
            child.Owner = owner;
            AssignOwnerRecursive(child, owner);
        }
    }
}
