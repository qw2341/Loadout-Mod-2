#nullable enable

namespace Loadout.UI.CustomRuns;

using System;
using Godot;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Helpers;

public partial class NCustomRunDragVisual : Control
{
    private static CanvasItem? _activeInsertionIndicator;
    private static Control? _activeInsertionOwner;

    public static void Show(string title)
    {
        NLoadoutPanelRoot? root = NLoadoutPanelRoot.Instance;
        if (root is null)
            return;
        NCustomRunDragVisual visual = new();
        visual._title = title;
        root.HostDragVisual(visual);
    }

    public static void ShowInsertion(Control owner, CanvasItem indicator)
    {
        if (!ReferenceEquals(_activeInsertionIndicator, indicator))
        {
            HideInsertion();
            _activeInsertionIndicator = indicator;
            _activeInsertionOwner = owner;
        }
        indicator.Visible = true;
    }

    public static void HideInsertion(Control? owner = null)
    {
        if (owner is not null && !ReferenceEquals(owner, _activeInsertionOwner))
            return;
        if (GodotObject.IsInstanceValid(_activeInsertionIndicator))
            _activeInsertionIndicator.Visible = false;
        _activeInsertionIndicator = null;
        _activeInsertionOwner = null;
    }

    public static void Clear()
    {
        HideInsertion();
        NLoadoutPanelRoot.Instance?.ClearDragVisual();
    }

    private string _title = string.Empty;

    public override void _Ready()
    {
        CustomMinimumSize = new Vector2(420f, 58f);
        Size = CustomMinimumSize;
        MouseFilter = MouseFilterEnum.Ignore;
        ZIndex = 1;

        ColorRect accent = new()
        {
            Color = new Color(0.94f, 0.72f, 0.2f, 0.95f),
            MouseFilter = MouseFilterEnum.Ignore
        };
        accent.SetAnchorsPreset(LayoutPreset.LeftWide);
        accent.OffsetRight = 5f;
        AddChild(accent);

        ColorRect underline = new()
        {
            Color = new Color(0.94f, 0.72f, 0.2f, 0.72f),
            MouseFilter = MouseFilterEnum.Ignore
        };
        underline.SetAnchorsPreset(LayoutPreset.BottomWide);
        underline.OffsetTop = -2f;
        AddChild(underline);

        MegaLabel title = CreateLabel(_title, 25, StsColors.gold, true);
        title.SetAnchorsPreset(LayoutPreset.FullRect);
        title.OffsetLeft = 20f;
        title.OffsetTop = 0f;
        title.OffsetRight = -12f;
        title.OffsetBottom = -3f;
        AddChild(title);

        SetProcess(true);
        UpdatePosition();
    }

    public override void _Process(double delta)
    {
        UpdatePosition();
    }

    private void UpdatePosition()
    {
        Viewport? viewport = GetViewport();
        if (viewport is null)
            return;
        Vector2 viewportSize = viewport.GetVisibleRect().Size;
        Vector2 desired = viewport.GetMousePosition() + new Vector2(26f, 22f);
        Position = new Vector2(
            Mathf.Clamp(desired.X, 12f, Math.Max(12f, viewportSize.X - Size.X - 12f)),
            Mathf.Clamp(desired.Y, 12f, Math.Max(12f, viewportSize.Y - Size.Y - 12f)));
    }

    private static MegaLabel CreateLabel(string text, int fontSize, Color color, bool bold)
    {
        MegaLabel label = new()
        {
            Text = text,
            AutoSizeEnabled = false,
            MinFontSize = Math.Max(12, fontSize - 5),
            MaxFontSize = fontSize,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            MouseFilter = MouseFilterEnum.Ignore
        };
        string fontPath = bold
            ? "res://themes/kreon_bold_glyph_space_one.tres"
            : "res://themes/kreon_regular_shared.tres";
        if (ResourceLoader.Exists(fontPath))
            label.AddThemeFontOverride("font", GD.Load<Font>(fontPath));
        label.AddThemeFontSizeOverride("font_size", fontSize);
        label.AddThemeColorOverride("font_color", color);
        label.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f, 0.82f));
        label.AddThemeConstantOverride("outline_size", bold ? 8 : 5);
        return label;
    }
}
