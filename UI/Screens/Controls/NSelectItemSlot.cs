#nullable enable

namespace Loadout.UI.Screens.Controls;

using BaseLib.Utils;
using Godot;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.Pooling;
using System;

public partial class NSelectItemSlot : Control, IPoolable
{
    private static GeneratedNodePool<NSelectItemSlot>? _pool;
    private static bool _useRegisteredNodePool;

    private Control? _content;

    public Action<Control>? ContentReady { get; set; }

    public static NSelectItemSlot GetPooled()
    {
        if (_pool is not null)
            return _pool.Get();

        if (_useRegisteredNodePool)
            return NodePool.Get<NSelectItemSlot>();

        try
        {
            _pool = GeneratedNodePool.Init(() => new NSelectItemSlot(), prewarmCount: 0);
            return _pool.Get();
        }
        catch (InvalidOperationException)
        {
            // This can happen after a hot reload when NodePool still owns the registration.
            _useRegisteredNodePool = true;
            return NodePool.Get<NSelectItemSlot>();
        }
    }

    public void SetContent(Control content, Vector2 slotSize)
    {
        if (_content is not null && GodotObject.IsInstanceValid(_content) && _content != content)
            ReleaseContent();

        CustomMinimumSize = slotSize;
        Size = slotSize;
        MouseFilter = MouseFilterEnum.Ignore;
        ClipContents = false;

        content.GetParent()?.RemoveChild(content);
        _content = content;
        AddChild(content);
        CenterContent();

        if (IsNodeReady())
            ContentReady?.Invoke(content);
    }

    public override void _Ready()
    {
        CenterContent();

        if (_content is not null)
            ContentReady?.Invoke(_content);
    }

    public override void _Notification(int what)
    {
        base._Notification(what);

        if (what == NotificationResized)
            CenterContent();
    }

    public void OnInstantiated()
    {
        ContentReady = null;
        ResetVisualState();
    }

    public void OnReturnedFromPool()
    {
        ContentReady = null;
        ResetVisualState();
        Visible = true;
    }

    public void OnFreedToPool()
    {
        ContentReady = null;
        ReleaseContent();
        ResetVisualState();
        Visible = false;
    }

    public void ReleaseContentToPool()
    {
        ReleaseContent();
    }

    private void ReleaseContent()
    {
        if (_content is null)
            return;

        Control content = _content;
        _content = null;

        if (!GodotObject.IsInstanceValid(content))
            return;

        content.GetParent()?.RemoveChild(content);
        content.QueueFreeSafely();
    }

    private void ResetVisualState()
    {
        Modulate = Colors.White;
        SelfModulate = Colors.White;
        Scale = Vector2.One;
        Rotation = 0f;
        PivotOffset = Vector2.Zero;
        ZIndex = 0;
        MouseFilter = MouseFilterEnum.Ignore;
        ClipContents = false;
    }

    private void CenterContent()
    {
        if (_content is null || !GodotObject.IsInstanceValid(_content))
            return;

        Vector2 contentSize = _content.CustomMinimumSize;
        if (contentSize.X <= 0f || contentSize.Y <= 0f)
            contentSize = _content.GetCombinedMinimumSize();

        if (contentSize.X <= 0f || contentSize.Y <= 0f)
            contentSize = _content.Size;

        _content.Position = (CustomMinimumSize - contentSize) * 0.5f;
    }
}
