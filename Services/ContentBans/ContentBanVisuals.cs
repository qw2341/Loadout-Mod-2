#nullable enable

namespace Loadout.Services.ContentBans;

using Godot;
using System;
using System.Collections.Generic;

internal static class ContentBanVisuals
{
    private const string OverlayName = "LoadoutContentBanSlash";
    private const string NativeSlashPath = "res://images/atlases/ui_atlas.sprites/card/card_unplayable_icon.tres";
    private const string CardSlashPath = "res://Loadout/images/ui/red_slash_high_res.png";
    private static readonly Dictionary<ulong, WiggleState> WiggleTweens = [];
    private static Texture2D? _nativeSlashTexture;
    private static Texture2D? _cardSlashTexture;
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
        overlay.Texture = target.Kind == ContentBanKind.Card
            ? _cardSlashTexture ??= GD.Load<Texture2D>(CardSlashPath)
            : _nativeSlashTexture ??= GD.Load<Texture2D>(NativeSlashPath);
        overlay.StretchMode = target.Kind == ContentBanKind.Card
            ? TextureRect.StretchModeEnum.KeepAspectCentered
            : TextureRect.StretchModeEnum.Scale;
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
        if (WiggleTweens.Remove(id, out WiggleState? current))
        {
            if (GodotObject.IsInstanceValid(current.Tween))
                current.Tween.Kill();
            RestoreWiggleState(holder, current);
        }

        Vector2 originalPivot = holder.PivotOffset;
        float originalRotation = holder.RotationDegrees;
        holder.PivotOffset = GetLocalBounds(holder).GetCenter();
        holder.RotationDegrees = originalRotation;
        Tween tween = holder.CreateTween();
        WiggleState state = new(tween, originalPivot, originalRotation);
        WiggleTweens[id] = state;
        tween.TweenProperty(holder, "rotation_degrees", originalRotation - 2.25f, 0.045);
        tween.TweenProperty(holder, "rotation_degrees", originalRotation + 2.25f, 0.075);
        tween.TweenProperty(holder, "rotation_degrees", originalRotation - 1.4f, 0.065);
        tween.TweenProperty(holder, "rotation_degrees", originalRotation + 1.4f, 0.055);
        tween.TweenProperty(holder, "rotation_degrees", originalRotation, 0.045);
        tween.Finished += () =>
        {
            if (!WiggleTweens.TryGetValue(id, out WiggleState? active) || !ReferenceEquals(active, state))
                return;
            WiggleTweens.Remove(id);
            RestoreWiggleState(holder, state);
        };
    }

    internal static bool ContainsPoint(Control holder, Vector2 globalPoint)
    {
        if (!GodotObject.IsInstanceValid(holder))
            return false;
        Control? hitbox = holder.GetNodeOrNull<Control>("Hitbox");
        return hitbox is not null && hitbox.IsVisibleInTree()
            ? hitbox.GetGlobalRect().HasPoint(globalPoint)
            : holder.GetGlobalRect().HasPoint(globalPoint);
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
        Rect2 bounds = GetLocalBounds(holder);
        if (bounds.Size.X > 0f && bounds.Size.Y > 0f)
        {
            overlay.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
            overlay.Position = bounds.Position;
            overlay.Size = bounds.Size;
            return;
        }

        if (holder.Size.X > 0f && holder.Size.Y > 0f)
            overlay.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
    }

    private static Rect2 GetLocalBounds(Control holder)
    {
        Control? hitbox = holder.GetNodeOrNull<Control>("Hitbox");
        return hitbox is not null && hitbox.Size.X > 0f && hitbox.Size.Y > 0f
            ? new Rect2(hitbox.Position, hitbox.Size)
            : new Rect2(Vector2.Zero, holder.Size);
    }

    private static void RestoreWiggleState(Control holder, WiggleState state)
    {
        if (!GodotObject.IsInstanceValid(holder))
            return;
        holder.RotationDegrees = state.OriginalRotation;
        holder.PivotOffset = state.OriginalPivot;
    }

    private sealed record WiggleState(Tween Tween, Vector2 OriginalPivot, float OriginalRotation);

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
