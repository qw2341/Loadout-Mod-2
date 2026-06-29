#nullable enable

namespace Loadout.UI.Screens.Controls;

using Godot;
using System;

public partial class NSelectItemSlot : Control
{
    private Control? _content;

    public Action<Control>? ContentReady { get; set; }

    public void SetContent(Control content, Vector2 slotSize)
    {
        CustomMinimumSize = slotSize;
        Size = slotSize;
        MouseFilter = MouseFilterEnum.Ignore;
        ClipContents = false;

        _content?.GetParent()?.RemoveChild(_content);
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

    private void CenterContent()
    {
        if (_content is null)
            return;

        Vector2 contentSize = _content.CustomMinimumSize;
        if (contentSize.X <= 0f || contentSize.Y <= 0f)
            contentSize = _content.GetCombinedMinimumSize();

        if (contentSize.X <= 0f || contentSize.Y <= 0f)
            contentSize = _content.Size;

        _content.Position = (CustomMinimumSize - contentSize) * 0.5f;
    }
}
