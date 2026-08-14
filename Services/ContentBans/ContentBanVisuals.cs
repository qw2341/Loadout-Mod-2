#nullable enable

namespace Loadout.Services.ContentBans;

using Godot;
using System.Collections.Generic;

internal static class ContentBanVisuals
{
    private const string OverlayName = "LoadoutContentBanSlash";
    private const string SlashPath = "res://images/atlases/ui_atlas.sprites/card/card_unplayable_icon.tres";
    private static readonly Dictionary<ulong, Tween> WiggleTweens = [];
    private static Texture2D? _slashTexture;
    private static ShaderMaterial? _runMaterial;

    internal static void Refresh(Control holder, ContentBanTarget target)
    {
        if (!GodotObject.IsInstanceValid(holder))
            return;

        ContentBanScope scope = ContentBanService.GetScope(target);
        TextureRect? overlay = holder.GetNodeOrNull<TextureRect>(OverlayName);
        if (scope == ContentBanScope.None)
        {
            if (overlay is not null)
                overlay.Visible = false;
            return;
        }

        overlay ??= CreateOverlay(holder);
        LayoutOverlay(holder, overlay);
        overlay.Texture = _slashTexture ??= GD.Load<Texture2D>(SlashPath);
        overlay.Material = scope == ContentBanScope.Run ? GetRunMaterial() : null;
        overlay.Modulate = Colors.White;
        overlay.Visible = true;
        holder.MoveChild(overlay, holder.GetChildCount() - 1);
    }

    internal static void Wiggle(Control holder)
    {
        if (!GodotObject.IsInstanceValid(holder))
            return;

        ulong id = holder.GetInstanceId();
        if (WiggleTweens.Remove(id, out Tween? current) && GodotObject.IsInstanceValid(current))
            current.Kill();

        holder.PivotOffset = holder.Size * 0.5f;
        holder.RotationDegrees = 0f;
        Tween tween = holder.CreateTween();
        WiggleTweens[id] = tween;
        tween.TweenProperty(holder, "rotation_degrees", -2.25f, 0.045);
        tween.TweenProperty(holder, "rotation_degrees", 2.25f, 0.075);
        tween.TweenProperty(holder, "rotation_degrees", -1.4f, 0.065);
        tween.TweenProperty(holder, "rotation_degrees", 1.4f, 0.055);
        tween.TweenProperty(holder, "rotation_degrees", 0f, 0.045);
        tween.Finished += () =>
        {
            WiggleTweens.Remove(id);
            if (GodotObject.IsInstanceValid(holder))
                holder.RotationDegrees = 0f;
        };
    }

    private static TextureRect CreateOverlay(Control holder)
    {
        TextureRect overlay = new()
        {
            Name = OverlayName,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            ZIndex = 1000,
            ShowBehindParent = false
        };
        holder.AddChild(overlay);
        return overlay;
    }

    private static void LayoutOverlay(Control holder, TextureRect overlay)
    {
        Control? hitbox = holder.GetNodeOrNull<Control>("Hitbox");
        if (hitbox is not null && hitbox.Size.X > 0f && hitbox.Size.Y > 0f)
        {
            overlay.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
            overlay.Position = hitbox.Position;
            overlay.Size = hitbox.Size;
            return;
        }

        if (holder.Size.X > 0f && holder.Size.Y > 0f)
            overlay.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
    }

    private static ShaderMaterial GetRunMaterial()
    {
        if (_runMaterial is not null)
            return _runMaterial;

        Shader shader = new()
        {
            Code = "shader_type canvas_item; void fragment(){ vec4 source = texture(TEXTURE, UV); COLOR = vec4(0.247, 0.549, 1.0, source.a * 0.75); }"
        };
        _runMaterial = new ShaderMaterial { Shader = shader };
        return _runMaterial;
    }
}
