#nullable enable

namespace Loadout.UI.Screens.Controls;

using Godot;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.CommonUi;

public partial class NSelectDropdownItem : NDropdownItem
{
    public string OptionId { get; private set; } = string.Empty;
    private string _pendingLabel = "DropdownItem";
    private ColorRect? _highlight;
    private bool _signalsConnected;

    public void Init(string optionId, string label)
    {
        OptionId = optionId;
        _pendingLabel = label;

        if (IsNodeReady())
            Text = label;
    }

    public override void _Ready()
    {
        EnsureControlTree();
        base._Ready();
        MouseEntered += ShowHoverHighlight;
        MouseExited += HideHoverHighlight;
        FocusEntered += ShowHoverHighlight;
        FocusExited += HideHoverHighlight;
        _signalsConnected = true;
        Text = _pendingLabel;
    }

    public override void _ExitTree()
    {
        base._ExitTree();

        if (!_signalsConnected)
            return;

        MouseEntered -= ShowHoverHighlight;
        MouseExited -= HideHoverHighlight;
        FocusEntered -= ShowHoverHighlight;
        FocusExited -= HideHoverHighlight;
        _signalsConnected = false;
    }

    public override void _GuiInput(InputEvent inputEvent)
    {
        base._GuiInput(inputEvent);

        if (GetParent()?.GetParent() is not NLoadoutDropdownContainer dropdownContainer)
            return;

        if (dropdownContainer.TryScrollFromInput(inputEvent))
            GetViewport().SetInputAsHandled();
    }

    private void EnsureControlTree()
    {
        _highlight = GetNodeOrNull<ColorRect>("Highlight");
        if (_highlight is null)
        {
            ColorRect highlight = new()
            {
                Name = "Highlight",
                Visible = false,
                Color = new Color(0.172549f, 0.345098f, 0.439216f, 1f),
                MouseFilter = MouseFilterEnum.Ignore
            };
            highlight.SetAnchorsPreset(LayoutPreset.FullRect);
            AddChild(highlight);
            _highlight = highlight;
        }

        if (GetNodeOrNull<MegaLabel>("Label") is not null)
            return;

        MegaLabel label = new()
        {
            Name = "Label",
            Text = "DropdownItem",
            AutoSizeEnabled = false,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore
        };
        label.SetAnchorsPreset(LayoutPreset.FullRect);
        label.AddThemeColorOverride("font_color", StsColors.cream);
        label.AddThemeColorOverride("font_shadow_color", new Color(0f, 0f, 0f, 0.12549f));
        label.AddThemeConstantOverride("shadow_offset_x", 3);
        label.AddThemeConstantOverride("shadow_offset_y", 2);
        label.AddThemeFontOverride("font", LoadFont("res://themes/kreon_bold_glyph_space_one.tres"));
        label.AddThemeFontSizeOverride("font_size", 24);
        AddChild(label);
    }

    private static Font? LoadFont(string path)
    {
        string localPath = path.Replace("res://themes/", "res://Loadout/themes/default/");
        if (ResourceLoader.Exists(localPath))
            return GD.Load<Font>(localPath);

        return ResourceLoader.Exists(path) ? GD.Load<Font>(path) : null;
    }

    private void ShowHoverHighlight()
    {
        if (_highlight is not null && GodotObject.IsInstanceValid(_highlight))
            _highlight.Visible = true;
    }

    private void HideHoverHighlight()
    {
        if (_highlight is not null && GodotObject.IsInstanceValid(_highlight))
            _highlight.Visible = false;
    }
}
