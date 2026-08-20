#nullable enable

namespace Loadout.UI.ImageEditing;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using Loadout.UI.Managers;
using Loadout.UI.Screens.Controls;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;

public partial class NImageEditorModal : Control, IScreenContext
{
    private readonly TaskCompletionSource<EditorSessionResult> _completion = new();

    private ImageEditRequest _request = null!;
    private ImageMediaDocument _source = null!;
    private PanelContainer _editorPanel = null!;
    private NImageEditorCanvas _canvas = null!;
    private LineEdit? _nameEdit;
    private MegaLabel _toolLabel = null!;
    private HSlider _zoomSlider = null!;
    private HSlider? _rotationSlider;
    private MegaLabel? _rotationLabel;
    private Control _saveButton = null!;
    private NCard? _cardPreview;
    private TextureRect? _cardPreviewPortrait;
    private Texture2D? _cardPreviewOriginalTexture;
    private Material? _cardPreviewOriginalMaterial;
    private ShaderMaterial? _cardPreviewMaterial;
    private bool _initialized;
    private bool _uiBuilt;
    private bool _completed;

    [Export]
    public bool UseLoadoutScreenChrome { get; set; }

    public Control? DefaultFocusedControl => _saveButton;

    public Task<EditorSessionResult> Completion => _completion.Task;

    public void Initialize(Image source, ImageEditRequest request)
    {
        Initialize(ImageMediaDocument.FromImage(source), request);
    }

    public void Initialize(ImageMediaDocument source, ImageEditRequest request)
    {
        _source = source;
        _request = request;
        _initialized = true;
        if (IsNodeReady())
            BuildUi();
    }

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        OffsetLeft = 0f;
        OffsetTop = 0f;
        OffsetRight = 0f;
        OffsetBottom = 0f;
        MouseFilter = MouseFilterEnum.Stop;
        FocusMode = FocusModeEnum.All;
        Resized += LayoutEditorPanel;
        if (!_initialized)
            throw new InvalidOperationException("The image editor modal must be initialized before entering the tree.");
        BuildUi();
    }

    public override void _ExitTree()
    {
        Resized -= LayoutEditorPanel;
        ReleaseCardPreview();
        if (!_completed)
        {
            _completed = true;
            _completion.TrySetResult(new EditorSessionResult(false, null, null, null));
        }
        base._ExitTree();
    }

    public override void _UnhandledInput(InputEvent inputEvent)
    {
        if (inputEvent is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape })
        {
            Cancel();
            GetViewport().SetInputAsHandled();
        }
    }

    private void BuildUi()
    {
        if (_uiBuilt)
            return;
        _uiBuilt = true;

        _editorPanel = new PanelContainer
        {
            Name = "EditorPanel",
            MouseFilter = MouseFilterEnum.Stop,
            GrowHorizontal = GrowDirection.Both,
            GrowVertical = GrowDirection.Both
        };
        StyleBoxFlat panelStyle = new()
        {
            BgColor = UseLoadoutScreenChrome ? Colors.Transparent : new Color("171A22F5"),
            BorderColor = UseLoadoutScreenChrome ? Colors.Transparent : new Color("B88B38"),
            BorderWidthLeft = UseLoadoutScreenChrome ? 0 : 3,
            BorderWidthTop = UseLoadoutScreenChrome ? 0 : 3,
            BorderWidthRight = UseLoadoutScreenChrome ? 0 : 3,
            BorderWidthBottom = UseLoadoutScreenChrome ? 0 : 3,
            CornerRadiusTopLeft = 8,
            CornerRadiusTopRight = 8,
            CornerRadiusBottomLeft = 8,
            CornerRadiusBottomRight = 8
        };
        _editorPanel.AddThemeStyleboxOverride("panel", panelStyle);
        Control editorMount = UseLoadoutScreenChrome
            ? GetNodeOrNull<Control>("%EditorMount") ?? this
            : this;
        _editorPanel.SetAnchorsPreset(
            UseLoadoutScreenChrome ? LayoutPreset.FullRect : LayoutPreset.Center);
        editorMount.AddChild(_editorPanel);

        MarginContainer contentMargin = new();
        contentMargin.AddThemeConstantOverride("margin_left", 24);
        contentMargin.AddThemeConstantOverride("margin_top", 18);
        contentMargin.AddThemeConstantOverride("margin_right", 24);
        contentMargin.AddThemeConstantOverride("margin_bottom", 18);
        _editorPanel.AddChild(contentMargin);

        VBoxContainer root = new();
        root.AddThemeConstantOverride("separation", 12);
        contentMargin.AddChild(root);
        root.AddChild(CreateLabel(_request.Title, 34, StsColors.gold, HorizontalAlignment.Center));

        if (_request.AllowDisplayNameEditing)
            root.AddChild(CreateNameRow());

        HBoxContainer body = new()
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        body.AddThemeConstantOverride("separation", 18);
        root.AddChild(body);

        _canvas = new NImageEditorCanvas { Name = "Canvas" };
        _canvas.Initialize(_source, _request.Frame, _request.AllowAlphaEditing);
        _canvas.RelativeZoomChanged += OnCanvasZoomChanged;
        _canvas.RotationDegreesChanged += OnCanvasRotationChanged;
        body.AddChild(_canvas);
        if (UseLoadoutScreenChrome)
        {
            Control toolsMount = GetNodeOrNull<Control>("%ToolsMount")
                ?? throw new InvalidOperationException("The card portrait editor screen is missing its tools mount.");
            Control tools = CreateToolsPanel();
            tools.SetAnchorsPreset(LayoutPreset.FullRect);
            toolsMount.AddChild(tools);
            BuildCardPreview();
            BuildLoadoutScreenActions();
        }
        else
        {
            body.AddChild(CreateToolsPanel());
            root.AddChild(CreateBottomButtons());
        }

        LayoutEditorPanel();
        Callable.From(() => _saveButton.GrabFocus()).CallDeferred();
    }

    private void LayoutEditorPanel()
    {
        if (_editorPanel is null || !GodotObject.IsInstanceValid(_editorPanel))
            return;

        if (UseLoadoutScreenChrome)
        {
            _editorPanel.SetAnchorsPreset(LayoutPreset.FullRect);
            _editorPanel.OffsetLeft = 0f;
            _editorPanel.OffsetTop = 0f;
            _editorPanel.OffsetRight = 0f;
            _editorPanel.OffsetBottom = 0f;
            return;
        }

        Vector2 availableSize = new(
            Mathf.Max(1f, Size.X - 108f),
            Mathf.Max(1f, Size.Y - 72f));
        Vector2 panelSize = new(
            Mathf.Min(1540f, availableSize.X),
            Mathf.Min(920f, availableSize.Y));
        _editorPanel.SetAnchorsPreset(LayoutPreset.Center);
        _editorPanel.OffsetLeft = panelSize.X * -0.5f;
        _editorPanel.OffsetTop = panelSize.Y * -0.5f;
        _editorPanel.OffsetRight = panelSize.X * 0.5f;
        _editorPanel.OffsetBottom = panelSize.Y * 0.5f;
    }

    private Control CreateNameRow()
    {
        HBoxContainer row = new()
        {
            CustomMinimumSize = new Vector2(0f, 48f)
        };
        row.AddThemeConstantOverride("separation", 12);
        MegaLabel label = CreateLabel(EditorText("IMAGE_EDITOR_NAME", "Name"), 23, StsColors.cream);
        label.CustomMinimumSize = new Vector2(130f, 44f);
        row.AddChild(label);

        _nameEdit = new LineEdit
        {
            Text = _request.InitialDisplayName?.Trim() ?? string.Empty,
            PlaceholderText = EditorText("IMAGE_EDITOR_NAME_PLACEHOLDER", "Custom companion name"),
            MaxLength = 80,
            CustomMinimumSize = new Vector2(360f, 44f),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            FocusMode = FocusModeEnum.All
        };
        Font? font = LoadFont("res://themes/kreon_regular_shared.tres");
        if (font is not null)
            _nameEdit.AddThemeFontOverride("font", font);
        _nameEdit.AddThemeFontSizeOverride("font_size", 22);
        row.AddChild(_nameEdit);
        return row;
    }

    private Control CreateToolsPanel()
    {
        VBoxContainer tools = new()
        {
            Name = "Tools",
            CustomMinimumSize = new Vector2(275f, 0f),
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        tools.AddThemeConstantOverride("separation", 7);

        _toolLabel = CreateLabel(EditorText("IMAGE_EDITOR_TOOL_PAN", "Tool: Move"), 22, StsColors.gold, HorizontalAlignment.Center);
        tools.AddChild(_toolLabel);
        tools.AddChild(CreateToolButton("pan", EditorText("IMAGE_EDITOR_PAN", "Move image"), ImageEditorTool.Pan));

        if (_request.AllowAlphaEditing)
        {
            tools.AddChild(CreateToolButton("erase", EditorText("IMAGE_EDITOR_ERASE", "Eraser"), ImageEditorTool.Erase));
            tools.AddChild(CreateToolButton("restore", EditorText("IMAGE_EDITOR_RESTORE", "Restore"), ImageEditorTool.Restore));

            string brushModePrefix = EditorText("IMAGE_EDITOR_BRUSH_MODE", "Brush mode");
            string brushModeBrush = EditorText("IMAGE_EDITOR_BRUSH_MODE_BRUSH", "Brush");
            string brushModeFill = EditorText("IMAGE_EDITOR_BRUSH_MODE_FILL", "Fill");
            MegaLabel brushModeLabel = CreateSliderLabel($"{brushModePrefix}: {brushModeBrush}");
            tools.AddChild(brushModeLabel);
            NLoadoutActionButton brushModeButton = CreateButton(
                "brush_mode",
                brushModeBrush,
                () =>
                {
                    _canvas.BrushMode = ImageEditorBrushMode.Brush;
                    brushModeLabel.SetTextAutoSize($"{brushModePrefix}: {brushModeBrush}");
                });
            NLoadoutActionButton fillModeButton = CreateButton(
                "fill_mode",
                brushModeFill,
                () =>
                {
                    _canvas.BrushMode = ImageEditorBrushMode.Fill;
                    brushModeLabel.SetTextAutoSize($"{brushModePrefix}: {brushModeFill}");
                });
            tools.AddChild(CreateCompactButtonRow(brushModeButton, fillModeButton));
        }

        tools.AddChild(CreateSliderLabel(EditorText("IMAGE_EDITOR_ZOOM", "Zoom")));
        _zoomSlider = CreateSlider(0.1, 12.0, 0.01, 1.0);
        _zoomSlider.DragStarted += _canvas.BeginTransformEdit;
        _zoomSlider.ValueChanged += value => _canvas.SetRelativeZoom((float)value);
        tools.AddChild(_zoomSlider);

        if (_request.AllowRotation)
            AddRotationControls(tools);

        if (_request.AllowAlphaEditing)
        {
            tools.AddChild(CreateSliderLabel(EditorText("IMAGE_EDITOR_BRUSH_SIZE", "Brush size")));
            HSlider brushSlider = CreateSlider(4.0, 160.0, 2.0, 42.0);
            brushSlider.ValueChanged += value => _canvas.BrushSize = (float)value;
            tools.AddChild(brushSlider);

            tools.AddChild(CreateSliderLabel(EditorText("IMAGE_EDITOR_TOLERANCE", "Background tolerance")));
            HSlider toleranceSlider = CreateSlider(0.01, 0.5, 0.01, 0.14);
            toleranceSlider.ValueChanged += value => _canvas.BackgroundTolerance = (float)value;
            tools.AddChild(toleranceSlider);

            tools.AddChild(CreateButton("auto_remove", EditorText("IMAGE_EDITOR_AUTO_REMOVE", "Apply background removal"), _canvas.RemoveBackground));
        }
        NLoadoutActionButton undoButton = CreateButton("undo", EditorText("IMAGE_EDITOR_UNDO", "Undo"), _canvas.Undo);
        NLoadoutActionButton redoButton = CreateButton("redo", EditorText("IMAGE_EDITOR_REDO", "Redo"), _canvas.Redo);
        _canvas.HistoryAvailabilityChanged += (canUndo, canRedo) =>
        {
            undoButton.SetEnabled(canUndo);
            redoButton.SetEnabled(canRedo);
        };
        tools.AddChild(CreateCompactButtonRow(undoButton, redoButton));
        Callable.From(undoButton.Disable).CallDeferred();
        Callable.From(redoButton.Disable).CallDeferred();
        tools.AddChild(CreateButton("fit", EditorText("IMAGE_EDITOR_FIT", "Fit image"), () =>
        {
            _canvas.BeginTransformEdit();
            _canvas.FitToFrame();
        }));
        tools.AddChild(CreateButton("reset", EditorText("IMAGE_EDITOR_RESET", "Reset image"), _canvas.ResetAll));

        MegaLabel instructions = CreateLabel(
            _request.AllowAlphaEditing
                ? EditorText("IMAGE_EDITOR_INSTRUCTIONS", "Drag to move. Brush beyond the frame edge for softer cuts. Use the mouse wheel to zoom.")
                : EditorText("IMAGE_EDITOR_CARD_INSTRUCTIONS", "Drag to move. Use the mouse wheel to zoom, then rotate or fit the image."),
            18,
            new Color("C9C2B3"),
            HorizontalAlignment.Center);
        instructions.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        instructions.SizeFlagsVertical = SizeFlags.ExpandFill;
        instructions.VerticalAlignment = VerticalAlignment.Bottom;
        tools.AddChild(instructions);
        return tools;
    }

    private Control CreateBottomButtons()
    {
        HBoxContainer row = new()
        {
            CustomMinimumSize = new Vector2(0f, 54f),
            Alignment = BoxContainer.AlignmentMode.End
        };
        row.AddThemeConstantOverride("separation", 14);
        IReadOnlyList<ImageEditSaveOption> saveOptions = ImageEditorService.GetSaveOptions(_request);
        float buttonWidth = saveOptions.Count == 1 ? 220f : 200f;
        NLoadoutActionButton cancel = CreateButton("cancel", EditorText("IMAGE_EDITOR_CANCEL", "Cancel"), Cancel);
        cancel.CustomMinimumSize = new Vector2(buttonWidth, 48f);
        row.AddChild(cancel);

        Control previous = cancel;
        foreach (ImageEditSaveOption option in saveOptions)
        {
            NLoadoutActionButton save = CreateButton(option.Id, option.Label, () => Save(option.Id));
            save.CustomMinimumSize = new Vector2(buttonWidth, 48f);
            row.AddChild(save);
            previous.FocusNeighborRight = previous.GetPathTo(save);
            save.FocusNeighborLeft = save.GetPathTo(previous);
            previous = save;
            _saveButton ??= save;
        }
        return row;
    }

    private void BuildLoadoutScreenActions()
    {
        Control? backMount = GetNodeOrNull<Control>("%BackButtonMount");
        Control? saveMount = GetNodeOrNull<Control>("%SaveButtonMount");
        if (backMount is null || saveMount is null)
            throw new InvalidOperationException("The card portrait editor screen is missing its navigation mounts.");

        NBackButton back = NLoadoutBackButtonFactory.Create();
        back.Name = "BackButton";
        back.Connect(
            NClickableControl.SignalName.Released,
            Callable.From<NClickableControl>(_ => Cancel()));
        backMount.AddChild(back);
        Callable.From(back.Enable).CallDeferred();

        IReadOnlyList<ImageEditSaveOption> saveOptions = ImageEditorService.GetSaveOptions(_request);
        List<NConfirmButton> buttons = [];
        for (int index = 0; index < saveOptions.Count; index++)
        {
            ImageEditSaveOption option = saveOptions[index];
            NConfirmButton button = NLoadoutConfirmButtonFactory.Create();
            button.Name = $"SaveButton{index}";
            button.OverrideHotkeys([]);
            float shiftUp = (saveOptions.Count - 1 - index) * 180f;
            button.OffsetTop -= shiftUp;
            button.OffsetBottom -= shiftUp;
            button.Connect(
                NClickableControl.SignalName.Released,
                Callable.From<NClickableControl>(_ => Save(option.Id)));

            MegaLabel label = CreateLabel(option.Label, 22, StsColors.cream, HorizontalAlignment.Center);
            label.Name = "ActionLabel";
            label.Position = new Vector2(-60f, -42f);
            label.Size = new Vector2(320f, 42f);
            button.AddChild(label);

            saveMount.AddChild(button);
            buttons.Add(button);
            Callable.From(button.Enable).CallDeferred();
            _saveButton ??= button;
        }

        if (buttons.Count == 0)
            throw new InvalidOperationException("The card portrait editor screen requires a save action.");

        back.FocusNeighborRight = back.GetPathTo(buttons[0]);
        buttons[0].FocusNeighborLeft = buttons[0].GetPathTo(back);
        for (int index = 1; index < buttons.Count; index++)
        {
            buttons[index - 1].FocusNeighborRight = buttons[index - 1].GetPathTo(buttons[index]);
            buttons[index].FocusNeighborLeft = buttons[index].GetPathTo(buttons[index - 1]);
        }
    }

    private void BuildCardPreview()
    {
        if (_request.CardPreviewModel is not { } model)
            return;

        Control? mount = GetNodeOrNull<Control>("%CardPreviewMount");
        if (mount is null)
            throw new InvalidOperationException("The card portrait editor screen is missing its card preview mount.");

        NCard? card = NCard.Create(model);
        if (card is null)
            return;

        _cardPreview = card;
        mount.AddChild(card);
        card.SetAnchorsPreset(LayoutPreset.Center);
        card.Position = Vector2.Zero;
        card.Scale = Vector2.One;
        card.MouseFilter = MouseFilterEnum.Ignore;
        card.UpdateVisuals(PileType.None, CardPreviewMode.Normal);

        _cardPreviewPortrait = model.Rarity == CardRarity.Ancient
            ? card.GetNodeOrNull<TextureRect>("%AncientPortrait")
            : card.GetNodeOrNull<TextureRect>("%Portrait");
        if (_cardPreviewPortrait is null)
            return;

        _cardPreviewOriginalTexture = _cardPreviewPortrait.Texture;
        _cardPreviewOriginalMaterial = _cardPreviewPortrait.Material;
        Image previewSurface = Image.CreateEmpty(
            _request.Frame.OutputSize.X,
            _request.Frame.OutputSize.Y,
            false,
            Image.Format.Rgba8);
        previewSurface.Fill(Colors.White);
        _cardPreviewPortrait.Texture = ImageTexture.CreateFromImage(previewSurface);
        _cardPreviewMaterial = _canvas.CreateOutputPreviewMaterial();
        _cardPreviewPortrait.Material = _cardPreviewMaterial;
        _canvas.PreviewChanged += RefreshCardPreviewMaterial;
        Callable.From(RefreshCardPreviewMaterial).CallDeferred();
    }

    private void RefreshCardPreviewMaterial()
    {
        if (_cardPreviewMaterial is null
            || _cardPreviewPortrait is null
            || !GodotObject.IsInstanceValid(_cardPreviewPortrait))
        {
            return;
        }

        _canvas.UpdateOutputPreviewMaterial(_cardPreviewMaterial);
        _cardPreviewPortrait.Material = _cardPreviewMaterial;
    }

    private void ReleaseCardPreview()
    {
        if (_canvas is not null && GodotObject.IsInstanceValid(_canvas))
            _canvas.PreviewChanged -= RefreshCardPreviewMaterial;

        if (_cardPreviewPortrait is not null && GodotObject.IsInstanceValid(_cardPreviewPortrait))
        {
            _cardPreviewPortrait.Texture = _cardPreviewOriginalTexture;
            _cardPreviewPortrait.Material = _cardPreviewOriginalMaterial;
        }

        if (_cardPreview is not null && GodotObject.IsInstanceValid(_cardPreview))
            _cardPreview.QueueFreeSafely();

        _cardPreview = null;
        _cardPreviewPortrait = null;
        _cardPreviewOriginalTexture = null;
        _cardPreviewOriginalMaterial = null;
        _cardPreviewMaterial = null;
    }

    private void AddRotationControls(VBoxContainer tools)
    {
        string rotationText = EditorText("IMAGE_EDITOR_ROTATION", "Rotation");
        _rotationLabel = CreateSliderLabel($"{rotationText}: 0°");
        tools.AddChild(_rotationLabel);

        _rotationSlider = CreateSlider(-180.0, 180.0, 1.0, 0.0);
        _rotationSlider.DragStarted += _canvas.BeginTransformEdit;
        _rotationSlider.ValueChanged += value => _canvas.SetImageRotationDegrees((float)value);
        tools.AddChild(_rotationSlider);

        NLoadoutActionButton rotateLeft = CreateButton(
            "rotate_left",
            EditorText("IMAGE_EDITOR_ROTATE_LEFT", "-90°"),
            () => RotateBy(-90f));
        NLoadoutActionButton rotationReset = CreateButton(
            "rotation_reset",
            EditorText("IMAGE_EDITOR_ROTATION_RESET", "0°"),
            () => SetImageRotation(0f));
        NLoadoutActionButton rotateRight = CreateButton(
            "rotate_right",
            EditorText("IMAGE_EDITOR_ROTATE_RIGHT", "+90°"),
            () => RotateBy(90f));
        tools.AddChild(CreateCompactButtonRow(rotateLeft, rotationReset, rotateRight));
    }

    private void RotateBy(float degrees)
    {
        SetImageRotation(_canvas.ImageRotationDegrees + degrees);
    }

    private void SetImageRotation(float degrees)
    {
        _canvas.BeginTransformEdit();
        _canvas.SetImageRotationDegrees(degrees);
    }

    private NLoadoutActionButton CreateToolButton(string id, string label, ImageEditorTool tool)
    {
        return CreateButton(id, label, () =>
        {
            _canvas.Tool = tool;
            _toolLabel.SetTextAutoSize(tool switch
            {
                ImageEditorTool.Erase => EditorText("IMAGE_EDITOR_TOOL_ERASE", "Tool: Eraser"),
                ImageEditorTool.Restore => EditorText("IMAGE_EDITOR_TOOL_RESTORE", "Tool: Restore"),
                _ => EditorText("IMAGE_EDITOR_TOOL_PAN", "Tool: Move")
            });
        });
    }

    private static NLoadoutActionButton CreateButton(string id, string label, Action onReleased)
    {
        NLoadoutActionButton button = new()
        {
            CustomMinimumSize = new Vector2(0f, 46f),
            FocusMode = FocusModeEnum.All,
            MouseFilter = MouseFilterEnum.Stop
        };
        button.Init(id, label);
        button.Connect(NClickableControl.SignalName.Released, Callable.From<NButton>(_ => onReleased()));
        return button;
    }

    private static HBoxContainer CreateCompactButtonRow(params NLoadoutActionButton[] buttons)
    {
        HBoxContainer row = new()
        {
            CustomMinimumSize = new Vector2(0f, 46f)
        };
        row.AddThemeConstantOverride("separation", 8);
        foreach (NLoadoutActionButton button in buttons)
        {
            button.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            row.AddChild(button);
        }
        return row;
    }

    private static MegaLabel CreateSliderLabel(string text)
    {
        MegaLabel label = CreateLabel(text, 18, StsColors.cream);
        label.CustomMinimumSize = new Vector2(0f, 26f);
        label.VerticalAlignment = VerticalAlignment.Bottom;
        return label;
    }

    private static HSlider CreateSlider(double minimum, double maximum, double step, double value)
    {
        return new HSlider
        {
            MinValue = minimum,
            MaxValue = maximum,
            Step = step,
            Value = value,
            Scrollable = false,
            FocusMode = FocusModeEnum.All,
            CustomMinimumSize = new Vector2(0f, 30f)
        };
    }

    private static MegaLabel CreateLabel(
        string text,
        int fontSize,
        Color color,
        HorizontalAlignment alignment = HorizontalAlignment.Left)
    {
        MegaLabel label = new()
        {
            Text = text,
            AutoSizeEnabled = false,
            MinFontSize = Math.Max(12, fontSize - 6),
            MaxFontSize = fontSize,
            HorizontalAlignment = alignment,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore
        };
        label.AddThemeColorOverride("font_color", color);
        Font? font = LoadFont("res://themes/kreon_bold_shared.tres");
        if (font is not null)
            label.AddThemeFontOverride("font", font);
        label.AddThemeFontSizeOverride("font_size", fontSize);
        return label;
    }

    private static Font? LoadFont(string path)
    {
        return ResourceLoader.Exists(path) ? ResourceLoader.Load<Font>(path) : null;
    }

    private void OnCanvasZoomChanged(float relativeZoom)
    {
        if (_zoomSlider is not null && !Mathf.IsEqualApprox((float)_zoomSlider.Value, relativeZoom))
            _zoomSlider.SetValueNoSignal(relativeZoom);
    }

    private void OnCanvasRotationChanged(float degrees)
    {
        if (_rotationSlider is not null && !Mathf.IsEqualApprox((float)_rotationSlider.Value, degrees))
            _rotationSlider.SetValueNoSignal(degrees);
        _rotationLabel?.SetTextAutoSize(
            $"{EditorText("IMAGE_EDITOR_ROTATION", "Rotation")}: {Mathf.RoundToInt(degrees)}°");
    }

    private void Save(string saveOptionId)
    {
        string? name = _nameEdit?.Text.Trim();
        if (_request.AllowDisplayNameEditing && string.IsNullOrWhiteSpace(name))
        {
            _nameEdit?.GrabFocus();
            return;
        }

        try
        {
            Complete(new EditorSessionResult(true, _canvas.RenderOutputDocument(), name, saveOptionId));
        }
        catch (Exception exception)
        {
            GD.PushError($"Loadout: image editor output rendering failed. {exception}");
            Complete(new EditorSessionResult(false, null, name, null, exception.Message));
        }
    }

    private void Cancel()
    {
        Complete(new EditorSessionResult(false, null, null, null));
    }

    private void Complete(EditorSessionResult result)
    {
        if (_completed)
            return;

        _completed = true;
        try
        {
            ReleaseCardPreview();
            if (UseLoadoutScreenChrome)
            {
                NLoadoutPanelRoot.Instance?.RemoveScreen(this);
                QueueFree();
            }
            else if (NModalContainer.Instance is { } modalContainer
                && GodotObject.IsInstanceValid(modalContainer)
                && ReferenceEquals(modalContainer.OpenModal, this))
            {
                modalContainer.Clear();
            }
            else
            {
                QueueFree();
            }
        }
        finally
        {
            _completion.TrySetResult(result);
        }
    }

    public readonly record struct EditorSessionResult(
        bool Accepted,
        ImageMediaDocument? Document,
        string? DisplayName,
        string? SaveOptionId,
        string? ErrorMessage = null);

    private static string EditorText(string key, string fallback)
    {
        return LocMan.GameLoc("settings_ui", $"LOADOUT-{key}.title", fallback);
    }
}
