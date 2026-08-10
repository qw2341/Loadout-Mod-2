#nullable enable

namespace Loadout.UI.Screens.Controls;

using Godot;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Nodes.Screens.Settings;

public partial class NLoadoutSettingsCategoryButton : NSettingsTab
{
    private const float DefaultWidth = 256f;
    private const float DefaultHeight = 90f;

    private string _pendingLabel = string.Empty;

    public string CategoryButtonId { get; private set; } = string.Empty;

    public void Init(string id, string label)
    {
        CategoryButtonId = id;
        _pendingLabel = label;
        if (IsNodeReady())
            SetLabel(label);
    }

    public override void _Ready()
    {
        BuildControlTree();
        base._Ready();
        SetLabel(_pendingLabel);
    }

    private void BuildControlTree()
    {
        float width = CustomMinimumSize.X > 0f ? CustomMinimumSize.X : DefaultWidth;
        float height = CustomMinimumSize.Y > 0f ? CustomMinimumSize.Y : DefaultHeight;
        bool usesCustomSize = width != DefaultWidth || height != DefaultHeight;
        CustomMinimumSize = new Vector2(width, height);
        PivotOffset = new Vector2(width * 0.5f, height * 0.5f);
        FocusMode = FocusModeEnum.All;
        MouseFilter = MouseFilterEnum.Stop;

        TextureRect outline = new()
        {
            Name = "Outline",
            Visible = false,
            Modulate = new Color(0.3648f, 0.9104f, 0.96f, 0.752941f),
            Material = LoadMaterial("res://themes/canvas_item_material_additive_shared.tres"),
            Texture = LoadTexture("res://images/packed/common_ui/settings_tab_stroke.png"),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = usesCustomSize
                ? TextureRect.StretchModeEnum.Scale
                : TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = MouseFilterEnum.Ignore
        };
        outline.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(outline);

        TextureRect image = new()
        {
            Name = "TabImage",
            Texture = LoadTexture("res://images/packed/common_ui/settings_tab_selected.png"),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = usesCustomSize
                ? TextureRect.StretchModeEnum.Scale
                : TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = MouseFilterEnum.Ignore
        };
        image.SetAnchorsPreset(LayoutPreset.FullRect);
        if (ResourceLoader.Exists("res://shaders/hsv.gdshader"))
        {
            ShaderMaterial material = new()
            {
                ResourceLocalToScene = true,
                Shader = GD.Load<Shader>("res://shaders/hsv.gdshader")
            };
            material.SetShaderParameter("h", 1f);
            material.SetShaderParameter("s", 1f);
            material.SetShaderParameter("v", 0.9f);
            image.Material = material;
        }
        AddChild(image);

        MegaLabel label = new()
        {
            Name = "Label",
            AutoSizeEnabled = false,
            MinFontSize = 18,
            MaxFontSize = 32,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore
        };
        label.SetAnchorsPreset(LayoutPreset.FullRect);
        label.OffsetLeft = 18f;
        label.OffsetTop = -5f;
        label.OffsetRight = -18f;
        label.OffsetBottom = 5f;
        label.AddThemeFontOverride("font", LoadFont("res://themes/kreon_bold_glyph_space_two.tres"));
        label.AddThemeFontSizeOverride("font_size", 32);
        AddChild(label);
    }

    private static Texture2D? LoadTexture(string path)
    {
        string localPath = path.Replace("res://images/", "res://Loadout/images/");
        if (ResourceLoader.Exists(localPath))
            return GD.Load<Texture2D>(localPath);
        return ResourceLoader.Exists(path) ? GD.Load<Texture2D>(path) : null;
    }

    private static Font? LoadFont(string path)
    {
        return ResourceLoader.Exists(path) ? GD.Load<Font>(path) : null;
    }

    private static Material? LoadMaterial(string path)
    {
        return ResourceLoader.Exists(path) ? GD.Load<Material>(path) : null;
    }
}
