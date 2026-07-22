#nullable enable

namespace Loadout.UI.Screens.Controls;

using Godot;
using Loadout.UI;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Audio;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using System;
using System.Collections.Generic;
using System.Linq;

public readonly record struct LoadoutDropdownOption(
    string Id,
    string Label,
    Func<IReadOnlyList<IHoverTip>>? HoverTipsFactory = null,
    Texture2D? Icon = null);

public partial class NLoadoutDropdown : NDropdown
{
    private const float DefaultDropdownWidth = 320f;
    private const float DefaultItemHeight = 44f;
    private const int DefaultMaxVisibleItems = 8;
    private const float DefaultButtonHeight = 52f;
    private const float ScrollbarLaneWidth = 42f;

    private readonly List<LoadoutDropdownOption> _items = new();
    private readonly Dictionary<NSelectDropdownItem, string> _itemIdsByNode = new();
    private string _labelPrefix = string.Empty;
    private string _selectedItemId = string.Empty;
    private bool _isReady;
    private bool _isOpen;
    private Control? _container;
    private Control? _dropdownContainer;
    private Control? _dropdownOriginalParent;
    private ColorRect? _buttonHoverHighlight;
    private TextureRect? _currentOptionIcon;
    private int _dropdownOriginalIndex;
    private NButton? _dismisser;
    private bool _containerPressStarted;
    private ulong _pendingFallbackReleaseFrame;
    private ulong _lastToggleFrame;
    private bool _isButtonHovered;
    private bool _signalsConnected;

    public float DropdownWidth { get; set; } = DefaultDropdownWidth;
    public float ItemHeight { get; set; } = DefaultItemHeight;
    public int MaxVisibleItems { get; set; } = DefaultMaxVisibleItems;
    public float ButtonHeight { get; set; } = DefaultButtonHeight;
    public bool UseFullScreenDismisser { get; set; } = true;
    public int LabelMinFontSize { get; set; } = 18;
    public int LabelMaxFontSize { get; set; } = 28;
    public int ItemFontSize { get; set; } = 24;

    public event Action<string>? SelectedItemChanged;

    public override void _Ready()
    {
        BuildControlTree();
        AssignOwnerRecursive(this, this);
        ConnectSignals();
        _container = GetNode<Control>("Container");
        _dropdownContainer = GetNode<Control>("%DropdownContainer");
        _buttonHoverHighlight = GetNodeOrNull<ColorRect>("%HoverHighlight");
        _currentOptionIcon = GetNodeOrNull<TextureRect>("%CurrentOptionIcon");
        _dismisser = GetNode<NButton>("%Dismisser");
        _dismisser.Connect(NClickableControl.SignalName.Released, Callable.From<NButton>(OnDismisserReleased));
        MouseEntered += OnButtonHoverStart;
        MouseExited += OnButtonHoverEnd;
        FocusEntered += RefreshButtonHighlight;
        FocusExited += RefreshButtonHighlight;
        _signalsConnected = true;
        _isReady = true;
        ApplyItems();
        SetEnabled(true);
    }

    public override void _ExitTree()
    {
        base._ExitTree();

        CloseLoadoutDropdown(restoreFocus: false);
        if (_signalsConnected)
        {
            MouseEntered -= OnButtonHoverStart;
            MouseExited -= OnButtonHoverEnd;
            FocusEntered -= RefreshButtonHighlight;
            FocusExited -= RefreshButtonHighlight;

            if (_container is not null)
                _container.GuiInput -= OnContainerGuiInput;

            if (_dismisser is not null)
                _dismisser.Disconnect(NClickableControl.SignalName.Released, Callable.From<NButton>(OnDismisserReleased));

            _signalsConnected = false;
        }

        _itemIdsByNode.Clear();
        _dropdownOriginalParent = null;
        _dropdownContainer = null;
        _buttonHoverHighlight = null;
        _currentOptionIcon = null;
        _dismisser = null;
        _container = null;
        _isReady = false;
    }

    public override void _Process(double delta)
    {
        if (_isOpen)
            PositionDropdownContainer();
    }

    public override void _Input(InputEvent inputEvent)
    {
        base._Input(inputEvent);

        if (!_isOpen || inputEvent is not InputEventMouseButton mouseButton)
            return;

        Vector2 globalPosition = mouseButton.GlobalPosition;
        bool insideButton = GetGlobalRect().HasPoint(globalPosition);
        bool insideDropdown = _dropdownContainer?.GetGlobalRect().HasPoint(globalPosition) ?? false;

        if (insideDropdown
            && mouseButton.Pressed
            && _dropdownContainer is NLoadoutDropdownContainer dropdownContainer
            && dropdownContainer.TryScrollFromInput(inputEvent))
        {
            GetViewport().SetInputAsHandled();
            return;
        }

        if (!mouseButton.Pressed)
            return;

        if (!insideButton && !insideDropdown)
            CloseLoadoutDropdown(restoreFocus: false);
    }

    public void SetItems(string labelPrefix, IEnumerable<LoadoutDropdownOption> items, string selectedItemId)
    {
        _labelPrefix = labelPrefix;
        _selectedItemId = selectedItemId;
        _items.Clear();
        _items.AddRange(items);

        if (_isReady)
            ApplyItems();
    }

    public void SetSelectedItem(string selectedItemId)
    {
        _selectedItemId = selectedItemId;

        if (_isReady)
            RefreshCurrentItemLabel();
    }

    protected override void OnRelease()
    {
        ToggleLoadoutDropdown();
    }

    private void ToggleLoadoutDropdown()
    {
        _lastToggleFrame = Engine.GetProcessFrames();

        if (_isOpen)
            CloseLoadoutDropdown();
        else
            OpenLoadoutDropdown();
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

        ToggleLoadoutDropdown();
    }

    private void OnDismisserReleased(NButton _)
    {
        CloseLoadoutDropdown(restoreFocus: false);
    }

    private void ApplyItems()
    {
        foreach (Node child in _dropdownItems.GetChildren())
        {
            _dropdownItems.RemoveChild(child);
            child.QueueFree();
        }

        _itemIdsByNode.Clear();

        foreach (LoadoutDropdownOption option in _items)
        {
            NSelectDropdownItem item = new()
            {
                Name = MakeSafeNodeName($"Option_{option.Id}"),
                CustomMinimumSize = new Vector2(DropdownWidth - 4f, ItemHeight),
                Size = new Vector2(DropdownWidth - 4f, ItemHeight),
                FocusMode = FocusModeEnum.All,
                MouseFilter = MouseFilterEnum.Stop
            };
            item.FontSize = ItemFontSize;
            item.Init(option.Id, option.Label, option.Icon);
            item.SetHoverTipsFactory(option.HoverTipsFactory);
            item.Connect(NDropdownItem.SignalName.Selected, Callable.From<NDropdownItem>(OnDropdownItemSelected));
            _dropdownItems.AddChild(item);
            _itemIdsByNode[item] = option.Id;
        }

        RefreshDropdownLayout();

        RefreshCurrentItemLabel();
        PositionDropdownContainer();
    }

    private void OnDropdownItemSelected(NDropdownItem dropdownItem)
    {
        if (dropdownItem is not NSelectDropdownItem selectItem || !_itemIdsByNode.TryGetValue(selectItem, out string? itemId))
            return;

        _selectedItemId = itemId;
        RefreshCurrentItemLabel();
        SelectedItemChanged?.Invoke(itemId);
        CloseLoadoutDropdown(restoreFocus: false);
    }

    private void RefreshCurrentItemLabel()
    {
        if (_currentOptionLabel is null)
            return;

        if (_items.Count == 0)
        {
            RefreshCurrentItemIcon(null);
            _currentOptionLabel.SetTextAutoSize(_labelPrefix);
            return;
        }

        LoadoutDropdownOption selectedItem = _items.FirstOrDefault(item => item.Id == _selectedItemId);
        if (string.IsNullOrWhiteSpace(selectedItem.Id))
        {
            selectedItem = _items[0];
            _selectedItemId = selectedItem.Id;
        }

        string label = string.IsNullOrWhiteSpace(_labelPrefix)
            ? selectedItem.Label
            : $"{_labelPrefix}: {selectedItem.Label}";
        RefreshCurrentItemIcon(selectedItem.Icon);
        _currentOptionLabel.SetTextAutoSize(label);
    }

    private void RefreshCurrentItemIcon(Texture2D? icon)
    {
        if (_currentOptionIcon is null || _currentOptionLabel is null)
            return;

        bool hasIcon = icon is not null;
        _currentOptionIcon.Texture = icon;
        _currentOptionIcon.Visible = hasIcon;
        _currentOptionLabel.OffsetLeft = hasIcon ? 52f : 0f;
        _currentOptionLabel.OffsetRight = hasIcon ? -52f : 0f;
    }

    private void OpenLoadoutDropdown()
    {
        if (_dropdownContainer is null)
            return;

        _isOpen = true;
        RefreshButtonHighlight();
        if (UseFullScreenDismisser)
        {
            _dismisser?.SetEnabled(true);
            _dismisser?.Show();
        }
        else
        {
            _dismisser?.SetEnabled(false);
            _dismisser?.Hide();
        }

        Control? layer = NLoadoutPanelRoot.Instance?.DropdownLayer;
        if (layer is not null && _dropdownContainer.GetParent() != layer)
        {
            _dropdownOriginalParent = _dropdownContainer.GetParent<Control>();
            _dropdownOriginalIndex = _dropdownOriginalParent?.GetChildren().ToList().IndexOf(_dropdownContainer) ?? -1;
            _dropdownOriginalParent?.RemoveChild(_dropdownContainer);
            layer.AddChild(_dropdownContainer);
        }

        RefreshDropdownLayout();

        _dropdownContainer.Visible = true;
        _dropdownContainer.MoveToFront();
        PositionDropdownContainer();

        foreach (NDropdownItem item in _dropdownItems.GetChildren().OfType<NDropdownItem>())
            item.UnhoverSelection();

        _dropdownItems.GetChildren().OfType<NDropdownItem>().FirstOrDefault()?.TryGrabFocus();
    }

    public void CloseLoadoutDropdown(bool restoreFocus = true)
    {
        _isOpen = false;
        _dismisser?.Hide();
        foreach (NSelectDropdownItem item in _dropdownItems.GetChildren().OfType<NSelectDropdownItem>())
            item.ClearHoverTips();

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
        if (restoreFocus && IsInsideTree() && Visible)
        {
            GrabFocus();
        }
        else
        {
            RefreshButtonHoverFromMouse();
            if (HasFocus())
                ReleaseFocus();
        }

        RefreshButtonHighlight();
    }

    private void OnButtonHoverStart()
    {
        if (!_isButtonHovered)
            SfxCmd.Play(FmodSfx.uiHover);

        _isButtonHovered = true;
        RefreshButtonHighlight();
    }

    private void OnButtonHoverEnd()
    {
        _isButtonHovered = false;
        RefreshButtonHighlight();
    }

    private void RefreshButtonHighlight()
    {
        if (_buttonHoverHighlight is not null && GodotObject.IsInstanceValid(_buttonHoverHighlight))
            _buttonHoverHighlight.Visible = _isOpen || _isButtonHovered || HasFocus();
    }

    private void RefreshButtonHoverFromMouse()
    {
        Viewport? viewport = GetViewport();
        _isButtonHovered = viewport is not null && GetGlobalRect().HasPoint(viewport.GetMousePosition());
    }

    private void PositionDropdownContainer()
    {
        if (_dropdownContainer is null)
            return;

        Vector2 globalPosition = GlobalPosition + new Vector2((Size.X - DropdownWidth) * 0.5f, Size.Y + 2f);
        float viewportHeight = GetViewportRect().Size.Y;
        float availableHeight = MathF.Max(ItemHeight, viewportHeight - globalPosition.Y - 24f);
        RefreshDropdownLayout(availableHeight);
        float currentHeight = _dropdownContainer.Size.Y > 0f ? _dropdownContainer.Size.Y : GetDesiredDropdownHeight(availableHeight);

        _dropdownContainer.SetAnchorsPreset(LayoutPreset.TopLeft);
        _dropdownContainer.GlobalPosition = globalPosition;
        _dropdownContainer.Size = new Vector2(DropdownWidth, MathF.Min(currentHeight, availableHeight));
        _dropdownContainer.CustomMinimumSize = _dropdownContainer.Size;
    }

    private void RefreshDropdownLayout(float? availableHeight = null)
    {
        if (_dropdownContainer is null)
            return;

        float maxHeight = GetMaxDropdownHeight();
        if (availableHeight is { } constrainedHeight)
            maxHeight = MathF.Min(maxHeight, MathF.Max(ItemHeight, constrainedHeight));

        if (_dropdownContainer is NLoadoutDropdownContainer dropdownContainer)
        {
            dropdownContainer.SetMaxHeight(maxHeight);
        }
        else
        {
            _dropdownContainer.Size = new Vector2(DropdownWidth, GetDesiredDropdownHeight(maxHeight));
            _dropdownContainer.CustomMinimumSize = _dropdownContainer.Size;
        }

        _dropdownContainer.SetAnchorsPreset(LayoutPreset.TopLeft);
        _dropdownContainer.Position = new Vector2(0f, ButtonHeight + 2f);
        _dropdownContainer.Size = new Vector2(DropdownWidth, _dropdownContainer.Size.Y);
        _dropdownContainer.CustomMinimumSize = _dropdownContainer.Size;
    }

    private float GetMaxDropdownHeight()
    {
        return MathF.Max(1, MaxVisibleItems) * MathF.Max(1f, ItemHeight);
    }

    private float GetDesiredDropdownHeight(float maxHeight)
    {
        float itemCountHeight = MathF.Max(1, _items.Count) * MathF.Max(1f, ItemHeight);
        return MathF.Min(maxHeight, itemCountHeight);
    }

    private void BuildControlTree()
    {
        if (GetNodeOrNull<Control>("Container") is not null)
            return;

        float requestedWidth = CustomMinimumSize.X > 0f ? CustomMinimumSize.X : 256f;
        CustomMinimumSize = new Vector2(requestedWidth, ButtonHeight);
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

        ColorRect baseFill = new()
        {
            Name = "Highlight",
            UniqueNameInOwner = true,
            Color = new Color(0.172549f, 0.262745f, 0.309804f, 1f),
            MouseFilter = MouseFilterEnum.Ignore
        };
        baseFill.SetAnchorsPreset(LayoutPreset.FullRect);
        currentOption.AddChild(baseFill);

        ColorRect hoverHighlight = new()
        {
            Name = "HoverHighlight",
            UniqueNameInOwner = true,
            Visible = false,
            Color = new Color(0.215686f, 0.411765f, 0.501961f, 0.82f),
            MouseFilter = MouseFilterEnum.Ignore
        };
        hoverHighlight.SetAnchorsPreset(LayoutPreset.FullRect);
        currentOption.AddChild(hoverHighlight);

        TextureRect currentOptionIcon = new()
        {
            Name = "CurrentOptionIcon",
            UniqueNameInOwner = true,
            Visible = false,
            ExpandMode = TextureRect.ExpandModeEnum.FitWidthProportional,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = MouseFilterEnum.Ignore
        };
        currentOptionIcon.SetAnchorsPreset(LayoutPreset.CenterLeft);
        currentOptionIcon.OffsetLeft = 8f;
        currentOptionIcon.OffsetTop = -20f;
        currentOptionIcon.OffsetRight = 48f;
        currentOptionIcon.OffsetBottom = 20f;
        currentOption.AddChild(currentOptionIcon);

        MegaLabel label = new()
        {
            Name = "Label",
            UniqueNameInOwner = true,
            Text = "Select",
            MinFontSize = LabelMinFontSize,
            MaxFontSize = LabelMaxFontSize,
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
        label.AddThemeFontSizeOverride("font_size", LabelMaxFontSize);
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

        NLoadoutDropdownContainer dropdownContainer = CreateDropdownContainer();
        container.AddChild(dropdownContainer);
    }

    private NLoadoutDropdownContainer CreateDropdownContainer()
    {
        float maxHeight = GetMaxDropdownHeight();
        NLoadoutDropdownContainer dropdownContainer = new()
        {
            Name = "DropdownContainer",
            UniqueNameInOwner = true,
            Visible = false,
            ClipContents = true,
            CustomMinimumSize = new Vector2(DropdownWidth, GetDesiredDropdownHeight(maxHeight)),
            Size = new Vector2(DropdownWidth, GetDesiredDropdownHeight(maxHeight)),
            MouseFilter = MouseFilterEnum.Stop
        };
        dropdownContainer.SetAnchorsPreset(LayoutPreset.TopLeft);
        dropdownContainer.Position = new Vector2(0f, ButtonHeight + 2f);

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

        Control scrollbar = new()
        {
            Name = "Scrollbar",
            MouseFilter = MouseFilterEnum.Stop
        };
        scrollbar.SetAnchorsPreset(LayoutPreset.RightWide);
        scrollbar.AnchorLeft = 1f;
        scrollbar.OffsetLeft = -ScrollbarLaneWidth;
        scrollbar.OffsetRight = 0f;
        dropdownContainer.AddChild(scrollbar);

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
        train.SetAnchorsPreset(LayoutPreset.TopWide);
        train.OffsetLeft = 4f;
        train.OffsetTop = 9f;
        train.OffsetRight = -8f;
        train.OffsetBottom = 97f;
        train.PivotOffset = new Vector2(14f, 44f);
        scrollbar.AddChild(train);
        dropdownContainer.SetMaxHeight(maxHeight);

        return dropdownContainer;
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

public partial class NLoadoutDropdownContainer : Control
{
    private const float ScrollbarPadding = 9f;
    private const float MinimumTrainHeight = 32f;

    private VBoxContainer? _dropdownItems;
    private Control? _scrollbar;
    private Control? _train;
    private float _maxHeight = 264f;
    private float _contentHeight;
    private float _targetItemsY;
    private bool _isDraggingTrain;
    private bool _signalsConnected;

    public override void _EnterTree()
    {
        BindNodes();
        ConnectContainerSignals();
    }

    public override void _Ready()
    {
        BindNodes();
        ConnectContainerSignals();
        RefreshLayout();
    }

    public override void _ExitTree()
    {
        if (_signalsConnected)
        {
            if (_scrollbar is not null)
                _scrollbar.GuiInput -= OnScrollbarGuiInput;

            VisibilityChanged -= OnVisibilityChanged;
            _signalsConnected = false;
        }

        _isDraggingTrain = false;
    }

    public override void _Process(double delta)
    {
        if (!IsVisibleInTree() || _dropdownItems is null || !IsScrollbarNeeded())
            return;

        float currentY = _dropdownItems.Position.Y;
        if (!Mathf.IsEqualApprox(currentY, _targetItemsY))
        {
            currentY = Mathf.Lerp(currentY, _targetItemsY, (float)delta * 15f);
            if (Mathf.Abs(currentY - _targetItemsY) < 0.5f)
                currentY = _targetItemsY;

            _dropdownItems.Position = new Vector2(_dropdownItems.Position.X, currentY);
            UpdateScrollbarTrain();
        }
    }

    public override void _Input(InputEvent inputEvent)
    {
        if (!IsVisibleInTree() || !_isDraggingTrain)
            return;

        if (inputEvent is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false })
        {
            _isDraggingTrain = false;
            GetViewport().SetInputAsHandled();
            return;
        }

        if (inputEvent is InputEventMouseMotion motion)
        {
            DragScrollbarBy(motion.Relative.Y);
            GetViewport().SetInputAsHandled();
        }
    }

    public override void _GuiInput(InputEvent inputEvent)
    {
        if (TryScrollFromInput(inputEvent))
            GetViewport().SetInputAsHandled();
    }

    public void SetMaxHeight(float maxHeight)
    {
        _maxHeight = MathF.Max(1f, maxHeight);

        if (IsNodeReady())
            RefreshLayout();
    }

    public void RefreshLayout()
    {
        if (_dropdownItems is null)
            return;

        _contentHeight = 0f;
        foreach (Node child in _dropdownItems.GetChildren())
        {
            if (child is not Control control)
                continue;

            float childHeight = control.Size.Y > 0f ? control.Size.Y : control.CustomMinimumSize.Y;
            _contentHeight += MathF.Max(0f, childHeight);
        }

        bool needsScrollbar = IsScrollbarNeeded();
        Size = new Vector2(Size.X, needsScrollbar ? _maxHeight : _contentHeight);
        CustomMinimumSize = new Vector2(CustomMinimumSize.X, Size.Y);

        if (_scrollbar is not null)
            _scrollbar.Visible = needsScrollbar;

        if (!needsScrollbar)
        {
            _targetItemsY = 0f;
            _dropdownItems.Position = new Vector2(_dropdownItems.Position.X, 0f);
        }
        else
        {
            _targetItemsY = ClampItemsY(_targetItemsY);
            _dropdownItems.Position = new Vector2(_dropdownItems.Position.X, ClampItemsY(_dropdownItems.Position.Y));
        }

        UpdateScrollbarTrain();
    }

    public bool TryScrollFromInput(InputEvent inputEvent)
    {
        float drag = ScrollHelper.GetDragForScrollEvent(inputEvent);
        if (Mathf.IsZeroApprox(drag) || !IsScrollbarNeeded())
            return false;

        ScrollItemsBy(drag);
        return true;
    }

    private void OnVisibilityChanged()
    {
        if (!Visible)
            return;

        _isDraggingTrain = false;
        RefreshLayout();
    }

    private void BindNodes()
    {
        _dropdownItems = GetNodeOrNull<VBoxContainer>("VBoxContainer");
        _scrollbar = GetNodeOrNull<Control>("Scrollbar");
        _train = GetNodeOrNull<Control>("Scrollbar/Train");
    }

    private void ConnectContainerSignals()
    {
        if (_signalsConnected)
            return;

        if (_scrollbar is not null)
            _scrollbar.GuiInput += OnScrollbarGuiInput;

        VisibilityChanged += OnVisibilityChanged;
        _signalsConnected = true;
    }

    private void OnScrollbarGuiInput(InputEvent inputEvent)
    {
        if (!IsScrollbarNeeded())
            return;

        if (inputEvent is InputEventMouseButton { ButtonIndex: MouseButton.Left } mouseButton)
        {
            _isDraggingTrain = mouseButton.Pressed;
            return;
        }

        if (_isDraggingTrain && inputEvent is InputEventMouseMotion motion)
        {
            DragScrollbarBy(motion.Relative.Y);
            return;
        }

        float drag = ScrollHelper.GetDragForScrollEvent(inputEvent);
        if (!Mathf.IsZeroApprox(drag))
            ScrollItemsBy(drag);
    }

    private void DragScrollbarBy(float relativeY)
    {
        float trainRange = GetTrainTravelRange();
        if (trainRange <= 0f)
            return;

        float contentRange = _contentHeight - Size.Y;
        _targetItemsY = ClampItemsY(_targetItemsY - relativeY * contentRange / trainRange);
        if (_dropdownItems is not null)
            _dropdownItems.Position = new Vector2(_dropdownItems.Position.X, _targetItemsY);
        UpdateScrollbarTrain();
    }

    private void ScrollItemsBy(float deltaY)
    {
        if (!IsScrollbarNeeded())
            return;

        _targetItemsY = ClampItemsY(_targetItemsY + deltaY);
    }

    private bool IsScrollbarNeeded()
    {
        return _contentHeight > _maxHeight;
    }

    private float ClampItemsY(float value)
    {
        return Mathf.Clamp(value, MathF.Min(0f, Size.Y - _contentHeight), 0f);
    }

    private float GetTrainHeight()
    {
        if (_contentHeight <= 0f)
            return MinimumTrainHeight;

        return Mathf.Clamp((Size.Y - ScrollbarPadding * 2f) * Size.Y / _contentHeight, MinimumTrainHeight, Size.Y - ScrollbarPadding * 2f);
    }

    private float GetTrainTravelRange()
    {
        return MathF.Max(0f, Size.Y - ScrollbarPadding * 2f - GetTrainHeight());
    }

    private void UpdateScrollbarTrain()
    {
        if (_train is null)
            return;

        float trainHeight = GetTrainHeight();
        float contentRange = MathF.Max(1f, _contentHeight - Size.Y);
        float scrollPercentage = IsScrollbarNeeded()
            ? Mathf.Clamp(-(_dropdownItems?.Position.Y ?? _targetItemsY) / contentRange, 0f, 1f)
            : 0f;

        _train.Size = new Vector2(_train.Size.X, trainHeight);
        _train.Position = new Vector2(_train.Position.X, ScrollbarPadding + scrollPercentage * GetTrainTravelRange());
    }
}

public partial class NSelectFilterDropdown : NLoadoutDropdown
{
}
