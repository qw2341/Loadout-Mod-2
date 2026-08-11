#nullable enable

namespace Loadout.UI.CustomRuns;

using System;
using Godot;
using Loadout.PanelItems;
using MegaCrit.Sts2.Core.Audio;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;

public partial class NCustomRunCharacterFilterButton : Button
{
    private const string OutlinePath =
        "res://images/packed/character_select/char_select_outline.png";
    private const string MaskPath =
        "res://images/packed/character_select/char_select_button_mask.png";
    private static readonly StyleBoxEmpty EmptyStyle = new();
    private static readonly StringName SaturationParameter = "s";
    private static readonly StringName ValueParameter = "v";

    private TextureRect? _icon;
    private TextureRect? _outline;
    private ShaderMaterial? _iconMaterial;
    private Action<NCustomRunCharacterFilterButton>? _pressedAction;
    private bool _hovered;

    public CharacterModel Character { get; private set; } = null!;
    public bool IsRandom { get; private set; }
    public bool IsSelected { get; private set; }

    public void Init(
        CharacterModel character,
        bool selected,
        Action<NCustomRunCharacterFilterButton> pressedAction)
    {
        Character = character;
        IsRandom = character.GetType().Name.Contains("Random", StringComparison.OrdinalIgnoreCase)
                   || character.Id.Entry.Contains("RANDOM", StringComparison.OrdinalIgnoreCase);
        _pressedAction = pressedAction;
        Name = $"CharacterRestriction_{character.Id.Entry}";
        CustomMinimumSize = new Vector2(100f, 148f);
        Size = CustomMinimumSize;
        PivotOffset = CustomMinimumSize * 0.5f;
        SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
        SizeFlagsVertical = SizeFlags.ShrinkCenter;
        FocusMode = FocusModeEnum.All;
        MouseFilter = MouseFilterEnum.Stop;
        TooltipText = character.Id.Entry;
        AddThemeStyleboxOverride("normal", EmptyStyle);
        AddThemeStyleboxOverride("hover", EmptyStyle);
        AddThemeStyleboxOverride("pressed", EmptyStyle);
        AddThemeStyleboxOverride("focus", EmptyStyle);
        AddThemeStyleboxOverride("disabled", EmptyStyle);
        BuildVisualTree(character);
        SetSelected(selected);
        Pressed += OnPressed;
        MouseEntered += OnMouseEntered;
        MouseExited += OnMouseExited;
        FocusEntered += OnFocusEntered;
        FocusExited += OnFocusExited;
        CommonHelpers.AttachHoverTips(
            this,
            [new HoverTip(
                new LocString("characters", character.CharacterSelectTitle),
                new LocString("characters", character.CharacterSelectDesc))]);
    }

    public void SetSelected(bool selected)
    {
        IsSelected = selected;
        RefreshVisualState();
    }

    private void BuildVisualTree(CharacterModel character)
    {
        TextureRect shadow = new()
        {
            Texture = LoadTexture(MaskPath),
            Position = new Vector2(12f, 19f),
            Size = new Vector2(88f, 130f),
            Modulate = new Color(0f, 0f, 0f, 0.25f),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = MouseFilterEnum.Ignore
        };
        AddChild(shadow);

        _outline = new TextureRect
        {
            Texture = LoadTexture(OutlinePath),
            Position = Vector2.Zero,
            Size = new Vector2(100f, 148f),
            SelfModulate = new Color(StsColors.gold, 0.9f),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = MouseFilterEnum.Ignore
        };
        AddChild(_outline);

        Texture2D? icon = null;
        try
        {
            icon = character.CharacterSelectIcon;
        }
        catch (Exception exception)
        {
            GD.PushWarning($"Loadout Custom Run: character icon '{character.Id}' failed. {exception.Message}");
        }

        _icon = new TextureRect
        {
            Texture = icon,
            Position = new Vector2(6f, 9f),
            Size = new Vector2(88f, 130f),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = MouseFilterEnum.Ignore
        };
        if (ResourceLoader.Exists("res://shaders/hsv.gdshader"))
        {
            _iconMaterial = new ShaderMaterial
            {
                ResourceLocalToScene = true,
                Shader = GD.Load<Shader>("res://shaders/hsv.gdshader")
            };
            _icon.Material = _iconMaterial;
        }
        AddChild(_icon);
    }

    private void OnPressed()
    {
        _pressedAction?.Invoke(this);
    }

    private void OnMouseEntered()
    {
        if (!_hovered && !Disabled)
            SfxCmd.Play(FmodSfx.uiHover);
        _hovered = true;
        Scale = Vector2.One * 1.08f;
        RefreshVisualState();
    }

    private void OnMouseExited()
    {
        _hovered = false;
        if (!HasFocus())
            Scale = Vector2.One;
        RefreshVisualState();
    }

    private void OnFocusEntered()
    {
        Scale = Vector2.One * 1.08f;
        RefreshVisualState();
    }

    private void OnFocusExited()
    {
        if (!_hovered)
            Scale = Vector2.One;
        RefreshVisualState();
    }

    private void RefreshVisualState()
    {
        bool bright = IsSelected || _hovered;
        if (_outline is not null)
            _outline.Visible = IsSelected;
        if (_iconMaterial is not null)
        {
            _iconMaterial.SetShaderParameter(SaturationParameter, bright ? 1f : 0.2f);
            _iconMaterial.SetShaderParameter(ValueParameter, bright ? 1.1f : 0.45f);
        }
        else if (_icon is not null)
        {
            _icon.SelfModulate = bright ? Colors.White : new Color(0.42f, 0.42f, 0.42f, 1f);
        }
    }

    private static Texture2D? LoadTexture(string path)
    {
        return ResourceLoader.Exists(path) ? GD.Load<Texture2D>(path) : null;
    }
}
