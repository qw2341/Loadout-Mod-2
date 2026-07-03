#nullable enable

namespace Loadout.UI.Screens.Controls;

using Godot;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.CommonUi;

public static class NLoadoutBackButtonFactory
{
    public static NBackButton Create()
    {
        NBackButton backButton = new()
        {
            FocusMode = Control.FocusModeEnum.All,
            MouseFilter = Control.MouseFilterEnum.Stop,
            PivotOffset = new Vector2(20f, 40f)
        };
        backButton.SetAnchorsPreset(Control.LayoutPreset.BottomLeft);
        backButton.OffsetLeft = -40f;
        backButton.OffsetTop = -354f;
        backButton.OffsetRight = 160f;
        backButton.OffsetBottom = -244f;

        TextureRect shadow = new()
        {
            Name = "Shadow",
            Modulate = new Color(0f, 0f, 0f, 0.25098f),
            Texture = LoadGameTexture("res://images/atlases/ui_atlas.sprites/back_button.tres"),
            ExpandMode = TextureRect.ExpandModeEnum.FitWidthProportional,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        shadow.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        shadow.OffsetLeft = -9f;
        shadow.OffsetTop = -1f;
        shadow.OffsetRight = 58f;
        shadow.OffsetBottom = 39f;
        backButton.AddChild(shadow);

        TextureRect outline = new()
        {
            Name = "Outline",
            Modulate = Colors.Transparent,
            Texture = LoadGameTexture("res://images/atlases/compressed.sprites/back_button_outline.tres"),
            ExpandMode = TextureRect.ExpandModeEnum.FitWidthProportional,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        outline.Material = LoadGameMaterial("res://themes/canvas_item_material_additive_shared.tres");
        outline.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        outline.OffsetLeft = -24f;
        outline.OffsetTop = -16f;
        outline.OffsetRight = 49f;
        outline.OffsetBottom = 30f;
        backButton.AddChild(outline);

        TextureRect image = new()
        {
            Name = "Image",
            Texture = LoadGameTexture("res://images/atlases/ui_atlas.sprites/back_button.tres"),
            ExpandMode = TextureRect.ExpandModeEnum.FitWidthProportional,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        image.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        image.OffsetLeft = -21f;
        image.OffsetTop = -13f;
        image.OffsetRight = 46f;
        image.OffsetBottom = 27f;
        backButton.AddChild(image);

        TextureRect icon = new()
        {
            Name = "Icon",
            Modulate = StsColors.cream,
            Texture = LoadGameTexture("res://images/atlases/compressed.sprites/back_button_arrow.tres"),
            ExpandMode = TextureRect.ExpandModeEnum.FitWidthProportional,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Position = new Vector2(88f, 28f),
            Size = new Vector2(80f, 80f)
        };
        image.AddChild(icon);

        TextureRect controllerIcon = new()
        {
            Name = "ControllerIcon",
            UniqueNameInOwner = true,
            Visible = false,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        controllerIcon.SetAnchorsPreset(Control.LayoutPreset.Center);
        controllerIcon.OffsetLeft = -219f;
        controllerIcon.OffsetTop = -48f;
        controllerIcon.OffsetRight = 21f;
        controllerIcon.OffsetBottom = 72f;
        controllerIcon.Scale = new Vector2(0.5f, 0.5f);
        controllerIcon.PivotOffset = new Vector2(256f, 128f);
        backButton.AddChild(controllerIcon);

        AssignOwnerRecursive(backButton, backButton);
        ResetVisualState(backButton);
        return backButton;
    }

    public static void ResetVisualState(Node button)
    {
        if (button is Control control)
            control.Scale = Vector2.One;

        if (button.GetNodeOrNull<CanvasItem>("Image") is { } image)
            image.Modulate = Colors.White;

        if (button.GetNodeOrNull<CanvasItem>("Outline") is { } outline)
            outline.Modulate = Colors.Transparent;
    }

    private static Texture2D? LoadGameTexture(string path)
    {
        string localPath = path
            .Replace("res://images/atlases/", "res://Loadout/images/atlases/")
            .Replace("res://images/packed/common_ui/", "res://Loadout/images/atlases/ui_atlas.sprites/");

        if (ResourceLoader.Exists(localPath))
            return GD.Load<Texture2D>(localPath);

        return ResourceLoader.Exists(path) ? GD.Load<Texture2D>(path) : null;
    }

    private static Material? LoadGameMaterial(string path)
    {
        string localPath = path.Replace("res://themes/", "res://Loadout/themes/default/");
        if (ResourceLoader.Exists(localPath))
            return GD.Load<Material>(localPath);

        return ResourceLoader.Exists(path) ? GD.Load<Material>(path) : null;
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
