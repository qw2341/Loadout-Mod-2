#nullable enable

namespace Loadout.UI.Screens.Controls;

using Godot;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;

public static class NLoadoutNativeScrollbar
{
    public const float Width = 48f;
    public const float EndCapSize = 48f;

    public static NScrollbar Create()
    {
        NScrollbar scrollbar = new()
        {
            MinValue = 0,
            MaxValue = 100,
            Step = 1,
            MouseFilter = Control.MouseFilterEnum.Stop
        };
        TextureRect trackBody = new()
        {
            Name = "TrackBody",
            Modulate = new Color(0.164706f, 0.290196f, 0.321569f, 1f),
            Texture = LoadTexture(
                "res://images/atlases/ui_atlas.sprites/scrollbar_track_center.tres"),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        trackBody.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        scrollbar.AddChild(trackBody);

        TextureRect trackTop = new()
        {
            Name = "TrackTop",
            Modulate = trackBody.Modulate,
            Texture = LoadTexture(
                "res://images/atlases/ui_atlas.sprites/scrollbar_track_edge2.tres"),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        trackTop.SetAnchorsPreset(Control.LayoutPreset.TopWide);
        trackTop.OffsetTop = -EndCapSize;
        scrollbar.AddChild(trackTop);

        TextureRect trackBottom = new()
        {
            Name = "TrackBot",
            Modulate = trackBody.Modulate,
            Texture = trackTop.Texture,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            FlipV = true,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        trackBottom.SetAnchorsPreset(Control.LayoutPreset.BottomWide);
        trackBottom.OffsetBottom = EndCapSize;
        scrollbar.AddChild(trackBottom);

        TextureRect handle = new()
        {
            Name = "Handle",
            UniqueNameInOwner = true,
            Texture = LoadTexture(
                "res://images/atlases/ui_atlas.sprites/scrollbar_train_large.tres"),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            PivotOffset = new Vector2(36f, 36f),
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        handle.Position = new Vector2(-12f, -36f);
        handle.Size = new Vector2(72f, 72f);
        scrollbar.AddChild(handle);
        AssignOwnerRecursive(scrollbar, scrollbar);
        return scrollbar;
    }

    private static Texture2D? LoadTexture(string path)
    {
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
