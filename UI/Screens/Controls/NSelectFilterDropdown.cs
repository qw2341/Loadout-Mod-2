#nullable enable

namespace Loadout.UI.Screens.Controls;

using Godot;
using Loadout.UI;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using System;
using System.Collections.Generic;
using System.Linq;

public readonly record struct SelectDropdownOption(string Id, string Label);

public partial class NSelectFilterDropdown : NDropdown
{
    private const float DropdownHeight = 600f;
    private const float DropdownWidth = 320f;

    private readonly List<SelectDropdownOption> _options = new();
    private readonly Dictionary<NSelectDropdownItem, string> _optionIdsByItem = new();
    private string _groupLabel = string.Empty;
    private string _selectedOptionId = string.Empty;
    private bool _isReady;
    private bool _isOpen;
    private Control? _container;
    private Control? _dropdownContainer;
    private Control? _dropdownOriginalParent;
    private int _dropdownOriginalIndex;
    private NButton? _dismisser;
    private bool _containerPressStarted;
    private ulong _pendingFallbackReleaseFrame;
    private ulong _lastToggleFrame;

    public event Action<string>? OptionSelected;

    public override void _Ready()
    {
        BuildControlTree();
        AssignOwnerRecursive(this, this);
        ConnectSignals();
        _container = GetNode<Control>("Container");
        _dropdownContainer = GetNode<Control>("%DropdownContainer");
        _dismisser = GetNode<NButton>("%Dismisser");
        _dismisser.Connect(NClickableControl.SignalName.Released, Callable.From<NButton>(_ => CloseSelectDropdown()));
        _isReady = true;
        ApplyOptions();
        SetEnabled(true);
    }

    public override void _ExitTree()
    {
        CloseSelectDropdown();
    }

    public override void _Process(double delta)
    {
        if (_isOpen)
            PositionDropdownContainer();
    }

    public override void _Input(InputEvent inputEvent)
    {
        base._Input(inputEvent);

        if (!_isOpen || inputEvent is not InputEventMouseButton { Pressed: true } mouseButton)
            return;

        Vector2 globalPosition = mouseButton.GlobalPosition;
        bool insideButton = GetGlobalRect().HasPoint(globalPosition);
        bool insideDropdown = _dropdownContainer?.GetGlobalRect().HasPoint(globalPosition) ?? false;
        if (!insideButton && !insideDropdown)
            CloseSelectDropdown();
    }

    public void SetOptions(string groupLabel, IEnumerable<SelectDropdownOption> options, string selectedOptionId)
    {
        _groupLabel = groupLabel;
        _selectedOptionId = selectedOptionId;
        _options.Clear();
        _options.AddRange(options);

        if (_isReady)
            ApplyOptions();
    }

    public void SetSelectedOption(string selectedOptionId)
    {
        _selectedOptionId = selectedOptionId;

        if (_isReady)
            RefreshCurrentOptionLabel();
    }

    protected override void OnRelease()
    {
        ToggleSelectDropdown();
    }

    private void ToggleSelectDropdown()
    {
        _lastToggleFrame = Engine.GetProcessFrames();

        if (_isOpen)
            CloseSelectDropdown();
        else
            OpenSelectDropdown();
    }

    private void OnContainerGuiInput(InputEvent inputEvent)
    {
        if (inputEvent is not InputEventMouseButton { ButtonIndex: MouseButton.Left } mouseButton)
            return;

        if (mouseButton.Pressed)
        {
            _containerPressStarted = true;
            return;
        }

        if (!_containerPressStarted)
            return;

        _containerPressStarted = false;
        _pendingFallbackReleaseFrame = Engine.GetProcessFrames();
        Callable.From(ToggleFromContainerReleaseIfNeeded).CallDeferred();
    }

    private void ToggleFromContainerReleaseIfNeeded()
    {
        if (_lastToggleFrame == _pendingFallbackReleaseFrame)
            return;

        ToggleSelectDropdown();
    }

    private void ApplyOptions()
    {
        foreach (Node child in _dropdownItems.GetChildren())
        {
            _dropdownItems.RemoveChild(child);
            child.QueueFree();
        }

        _optionIdsByItem.Clear();

        foreach (SelectDropdownOption option in _options)
        {
            NSelectDropdownItem item = new()
            {
                Name = MakeSafeNodeName($"Option_{option.Id}"),
                CustomMinimumSize = new Vector2(DropdownWidth - 4f, 44f),
                FocusMode = FocusModeEnum.All,
                MouseFilter = MouseFilterEnum.Stop
            };
            item.Init(option.Id, option.Label);
            item.Connect(NDropdownItem.SignalName.Selected, Callable.From<NDropdownItem>(OnDropdownItemSelected));
            _dropdownItems.AddChild(item);
            _optionIdsByItem[item] = option.Id;
        }

        if (_dropdownItems.GetParent() is NDropdownContainer dropdownContainer)
            dropdownContainer.RefreshLayout();

        RefreshCurrentOptionLabel();
        PositionDropdownContainer();
    }

    private void OnDropdownItemSelected(NDropdownItem dropdownItem)
    {
        if (dropdownItem is not NSelectDropdownItem selectItem || !_optionIdsByItem.TryGetValue(selectItem, out string? optionId))
            return;

        _selectedOptionId = optionId;
        RefreshCurrentOptionLabel();
        OptionSelected?.Invoke(optionId);
        CloseSelectDropdown();
    }

    private void RefreshCurrentOptionLabel()
    {
        if (_currentOptionLabel is null)
            return;

        if (_options.Count == 0)
        {
            _currentOptionLabel.SetTextAutoSize(_groupLabel);
            return;
        }

        SelectDropdownOption selectedOption = _options.FirstOrDefault(option => option.Id == _selectedOptionId);
        if (string.IsNullOrWhiteSpace(selectedOption.Id))
            selectedOption = _options[0];

        string label = string.IsNullOrWhiteSpace(_groupLabel)
            ? selectedOption.Label
            : $"{_groupLabel}: {selectedOption.Label}";
        _currentOptionLabel.SetTextAutoSize(label);
    }

    private void OpenSelectDropdown()
    {
        if (_dropdownContainer is null)
            return;

        _isOpen = true;
        _dismisser?.SetEnabled(true);
        _dismisser?.Show();

        Control? layer = NLoadoutPanelRoot.Instance?.DropdownLayer;
        if (layer is not null && _dropdownContainer.GetParent() != layer)
        {
            _dropdownOriginalParent = _dropdownContainer.GetParent<Control>();
            _dropdownOriginalIndex = _dropdownOriginalParent?.GetChildren().ToList().IndexOf(_dropdownContainer) ?? -1;
            _dropdownOriginalParent?.RemoveChild(_dropdownContainer);
            layer.AddChild(_dropdownContainer);
        }

        _dropdownContainer.Visible = true;
        _dropdownContainer.MoveToFront();
        PositionDropdownContainer();

        foreach (NDropdownItem item in _dropdownItems.GetChildren().OfType<NDropdownItem>())
            item.UnhoverSelection();

        _dropdownItems.GetChildren().OfType<NDropdownItem>().FirstOrDefault()?.TryGrabFocus();
    }

    private void CloseSelectDropdown()
    {
        _isOpen = false;
        _dismisser?.Hide();

        if (_dropdownContainer is not null)
            _dropdownContainer.Visible = false;

        if (_dropdownContainer is not null
            && _dropdownOriginalParent is not null
            && GodotObject.IsInstanceValid(_dropdownOriginalParent)
            && _dropdownContainer.GetParent() != _dropdownOriginalParent)
        {
            _dropdownContainer.GetParent()?.RemoveChild(_dropdownContainer);
            _dropdownOriginalParent.AddChild(_dropdownContainer);
            if (_dropdownOriginalIndex >= 0 && _dropdownOriginalIndex < _dropdownOriginalParent.GetChildCount())
                _dropdownOriginalParent.MoveChild(_dropdownContainer, _dropdownOriginalIndex);
        }

        _dropdownOriginalParent = null;
        GrabFocus();
    }

    private void PositionDropdownContainer()
    {
        if (_dropdownContainer is null)
            return;

        Vector2 globalPosition = GlobalPosition + new Vector2((Size.X - DropdownWidth) * 0.5f, Size.Y + 2f);
        float viewportHeight = GetViewportRect().Size.Y;
        float maxHeight = MathF.Min(DropdownHeight, MathF.Max(120f, viewportHeight - globalPosition.Y - 24f));

        _dropdownContainer.GlobalPosition = globalPosition;
        _dropdownContainer.Size = new Vector2(DropdownWidth, maxHeight);
    }

    private void BuildControlTree()
    {
        CustomMinimumSize = new Vector2(256f, 52f);
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        FocusMode = FocusModeEnum.All;
        MouseFilter = MouseFilterEnum.Stop;

        Control container = new()
        {
            Name = "Container",
            MouseFilter = MouseFilterEnum.Pass
        };
        container.SetAnchorsPreset(LayoutPreset.FullRect);
        container.GuiInput += OnContainerGuiInput;
        AddChild(container);

        NButton dismisser = new()
        {
            Name = "Dismisser",
            UniqueNameInOwner = true,
            Visible = false,
            CustomMinimumSize = new Vector2(3000f, 2000f),
            MouseFilter = MouseFilterEnum.Stop
        };
        dismisser.SetAnchorsPreset(LayoutPreset.Center);
        dismisser.OffsetLeft = -1500f;
        dismisser.OffsetTop = -1000f;
        dismisser.OffsetRight = 1500f;
        dismisser.OffsetBottom = 1000f;
        container.AddChild(dismisser);

        Control currentOption = new()
        {
            Name = "CurrentOption",
            MouseFilter = MouseFilterEnum.Ignore
        };
        currentOption.SetAnchorsPreset(LayoutPreset.FullRect);
        container.AddChild(currentOption);

        ColorRect highlight = new()
        {
            Name = "Highlight",
            UniqueNameInOwner = true,
            Color = new Color(0.172549f, 0.262745f, 0.309804f, 1f),
            MouseFilter = MouseFilterEnum.Ignore
        };
        highlight.SetAnchorsPreset(LayoutPreset.FullRect);
        currentOption.AddChild(highlight);

        MegaLabel label = new()
        {
            Name = "Label",
            UniqueNameInOwner = true,
            Text = "Filter",
            MinFontSize = 18,
            MaxFontSize = 28,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore
        };
        label.SetAnchorsPreset(LayoutPreset.FullRect);
        label.AddThemeColorOverride("font_color", StsColors.gold);
        label.AddThemeColorOverride("font_shadow_color", new Color(0f, 0f, 0f, 0.25098f));
        label.AddThemeConstantOverride("shadow_offset_x", 3);
        label.AddThemeConstantOverride("shadow_offset_y", 2);
        label.AddThemeFontOverride("font", LoadFont("res://themes/kreon_bold_glyph_space_one.tres"));
        label.AddThemeFontSizeOverride("font_size", 28);
        currentOption.AddChild(label);

        TextureRect arrow = new()
        {
            Name = "Arrow",
            UniqueNameInOwner = true,
            Rotation = -Mathf.Pi * 0.5f,
            PivotOffset = new Vector2(16f, 13f),
            Texture = LoadTexture("res://images/packed/common_ui/settings_tiny_left_arrow.png")
                ?? LoadTexture("res://images/atlases/ui_atlas.sprites/settings_tiny_left_arrow.tres"),
            ExpandMode = TextureRect.ExpandModeEnum.FitWidthProportional,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = MouseFilterEnum.Ignore
        };
        arrow.SetAnchorsPreset(LayoutPreset.CenterRight);
        arrow.OffsetLeft = -39f;
        arrow.OffsetTop = -15f;
        arrow.OffsetRight = -13f;
        arrow.OffsetBottom = 11f;
        container.AddChild(arrow);

        NDropdownContainer dropdownContainer = CreateDropdownContainer();
        container.AddChild(dropdownContainer);
    }

    private static NDropdownContainer CreateDropdownContainer()
    {
        NDropdownContainer dropdownContainer = new()
        {
            Name = "DropdownContainer",
            UniqueNameInOwner = true,
            Visible = false,
            ClipContents = true,
            CustomMinimumSize = new Vector2(DropdownWidth, DropdownHeight),
            MouseFilter = MouseFilterEnum.Stop
        };
        dropdownContainer.SetAnchorsPreset(LayoutPreset.TopWide);
        dropdownContainer.OffsetLeft = 0f;
        dropdownContainer.OffsetTop = 54f;
        dropdownContainer.OffsetRight = 0f;
        dropdownContainer.OffsetBottom = 54f + DropdownHeight;

        ColorRect background = new()
        {
            Name = "ColorRect",
            Color = new Color(0.0705882f, 0.129412f, 0.160784f, 1f),
            MouseFilter = MouseFilterEnum.Ignore
        };
        background.SetAnchorsPreset(LayoutPreset.FullRect);
        dropdownContainer.AddChild(background);

        VBoxContainer items = new()
        {
            Name = "VBoxContainer",
            MouseFilter = MouseFilterEnum.Ignore
        };
        items.SetAnchorsPreset(LayoutPreset.FullRect);
        items.AddThemeConstantOverride("separation", 0);
        dropdownContainer.AddChild(items);

        NDropdownScrollbar scrollbar = new()
        {
            Name = "Scrollbar",
            MouseFilter = MouseFilterEnum.Stop
        };
        scrollbar.SetAnchorsPreset(LayoutPreset.RightWide);
        scrollbar.AnchorLeft = 0.875f;
        dropdownContainer.AddChild(scrollbar);

        Control track = new()
        {
            Name = "Track",
            Modulate = new Color(0f, 0f, 0f, 0.501961f),
            MouseFilter = MouseFilterEnum.Ignore
        };
        track.SetAnchorsPreset(LayoutPreset.FullRect);
        track.OffsetTop = 5f;
        track.OffsetBottom = 5f;
        scrollbar.AddChild(track);

        AddSmallScrollbarTrack(track, "TrackTop", false);
        AddSmallScrollbarTrack(track, "TrackBody", false, body: true);
        AddSmallScrollbarTrack(track, "TrackBot", true);

        NinePatchRect train = new()
        {
            Name = "Train",
            Modulate = StsColors.quarterTransparentWhite,
            Texture = LoadTexture("res://images/packed/common_ui/small_scrollbar_train.png")
                ?? LoadTexture("res://images/atlases/ui_atlas.sprites/small_scrollbar_train.tres"),
            PatchMarginTop = 20,
            PatchMarginBottom = 20,
            MouseFilter = MouseFilterEnum.Ignore
        };
        train.SetAnchorsPreset(LayoutPreset.CenterRight);
        train.OffsetLeft = 4f;
        train.OffsetTop = -291f;
        train.OffsetRight = -8f;
        train.OffsetBottom = -203f;
        train.PivotOffset = new Vector2(14f, 44f);
        scrollbar.AddChild(train);

        return dropdownContainer;
    }

    private static void AddSmallScrollbarTrack(Control parent, string name, bool flipV, bool body = false)
    {
        TextureRect track = new()
        {
            Name = name,
            Texture = LoadTexture(body
                ? "res://images/packed/common_ui/small_scrollbar_track_center.png"
                : "res://images/packed/common_ui/small_scrollbar_track_edge.png")
                ?? LoadTexture(body
                    ? "res://images/atlases/ui_atlas.sprites/small_scrollbar_track_center.tres"
                    : "res://images/atlases/ui_atlas.sprites/small_scrollbar_track_edge.tres"),
            ExpandMode = body ? TextureRect.ExpandModeEnum.IgnoreSize : TextureRect.ExpandModeEnum.FitWidth,
            FlipV = flipV,
            MouseFilter = MouseFilterEnum.Ignore
        };

        if (body)
        {
            track.SetAnchorsPreset(LayoutPreset.FullRect);
            track.OffsetLeft = 8f;
            track.OffsetRight = -8f;
            track.OffsetTop = 32f;
            track.OffsetBottom = -32f;
        }
        else if (flipV)
        {
            track.SetAnchorsPreset(LayoutPreset.BottomWide);
            track.OffsetLeft = 8f;
            track.OffsetRight = -8f;
            track.OffsetTop = -41f;
            track.OffsetBottom = -9f;
        }
        else
        {
            track.SetAnchorsPreset(LayoutPreset.TopWide);
            track.OffsetLeft = 8f;
            track.OffsetRight = -8f;
            track.OffsetBottom = 32f;
        }

        parent.AddChild(track);
    }

    private static Font? LoadFont(string path)
    {
        string localPath = path.Replace("res://themes/", "res://Loadout/themes/default/");
        if (ResourceLoader.Exists(localPath))
            return GD.Load<Font>(localPath);

        return ResourceLoader.Exists(path) ? GD.Load<Font>(path) : null;
    }

    private static Texture2D? LoadTexture(string path)
    {
        string localPath = path
            .Replace("res://images/atlases/", "res://Loadout/images/atlases/")
            .Replace("res://images/packed/common_ui/", "res://Loadout/images/atlases/ui_atlas.sprites/");

        if (ResourceLoader.Exists(localPath))
            return GD.Load<Texture2D>(localPath);

        return ResourceLoader.Exists(path) ? GD.Load<Texture2D>(path) : null;
    }

    private static string MakeSafeNodeName(string value)
    {
        string safe = System.Text.RegularExpressions.Regex.Replace(value, @"[^A-Za-z0-9_]", "_");
        return string.IsNullOrWhiteSpace(safe) ? "DropdownOption" : safe;
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
