#nullable enable

namespace Loadout.UI.ImageEditing;

using System;
using System.Collections.Generic;
using Godot;

public enum ImageEditorTool
{
    Pan,
    Erase,
    Restore
}

public enum ImageEditorBrushMode
{
    Brush,
    Fill
}

public partial class NImageEditorCanvas : Control
{
    private const int UndoLimit = 8;
    private const long HistoryByteBudget = 96L * 1024L * 1024L;
    private const int MaxWorkingDimension = 1280;
    private const float CheckerSize = 18f;

    private static readonly Color CheckerLight = new("4A4A4A");
    private static readonly Color CheckerDark = new("303030");
    private static readonly Color FrameOutline = new("D9AD46");

    private readonly List<EditorSnapshot> _undo = [];
    private readonly List<EditorSnapshot> _redo = [];
    private readonly ShaderMaterial _previewMaterial = CreatePreviewMaterial();
    private readonly List<Image> _originalFrames = [];
    private readonly List<Image> _workingFrames = [];
    private readonly List<Image> _backgroundPreviewFrames = [];
    private readonly List<ImageTexture> _workingTextures = [];
    private readonly List<ImageTexture> _backgroundPreviewTextures = [];

    private double[] _frameDurations = [];
    private ImageTexture? _maskTexture;
    private ImageEditFrameDefinition _frame = null!;
    private ColorRect _imageDisplay = null!;
    private TextureRect _overlay = null!;
    private ReferenceRect _frameOutline = null!;
    private Line2D _brushCursor = null!;
    private Timer _backgroundPreviewTimer = null!;
    private Timer _animationTimer = null!;
    private bool _initialized;
    private bool _dragging;
    private bool _strokeStarted;
    private bool _pointerInside;
    private bool _backgroundPreviewActive;
    private bool _allowAlphaEditing = true;
    private int _currentFrameIndex;
    private Vector2 _lastPointer;
    private Vector2 _pointerPosition;
    private Vector2 _offset;
    private float _zoom;
    private float _fitZoom;
    private float _rotationRadians;
    private float _brushSize = 42f;
    private float _backgroundTolerance = 0.14f;
    private ImageEditorTool _tool = ImageEditorTool.Pan;
    private ImageEditorBrushMode _brushMode = ImageEditorBrushMode.Brush;

    public event Action<float>? RelativeZoomChanged;
    public event Action<float>? RotationDegreesChanged;
    public event Action<bool, bool>? HistoryAvailabilityChanged;

    public ImageEditorTool Tool
    {
        get => _tool;
        set
        {
            _tool = value;
            RefreshBrushCursor();
        }
    }

    public ImageEditorBrushMode BrushMode
    {
        get => _brushMode;
        set
        {
            _brushMode = value;
            RefreshBrushCursor();
        }
    }

    public float BrushSize
    {
        get => _brushSize;
        set
        {
            _brushSize = Mathf.Clamp(value, 4f, 160f);
            RefreshBrushCursor();
        }
    }

    public float BackgroundTolerance
    {
        get => _backgroundTolerance;
        set
        {
            float next = Mathf.Clamp(value, 0.01f, 0.5f);
            if (Mathf.IsEqualApprox(next, _backgroundTolerance))
                return;
            _backgroundTolerance = next;
            if (_initialized && IsNodeReady())
                _backgroundPreviewTimer.Start();
        }
    }

    public float RelativeZoom => _fitZoom <= 0f ? 1f : _zoom / _fitZoom;

    public float ImageRotationDegrees => Mathf.RadToDeg(_rotationRadians);

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Stop;
        FocusMode = FocusModeEnum.All;
        ClipContents = true;
        CustomMinimumSize = new Vector2(700f, 560f);
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        SizeFlagsVertical = SizeFlags.ExpandFill;

        _imageDisplay = new ColorRect
        {
            Name = "EditedImage",
            MouseFilter = MouseFilterEnum.Ignore,
            Material = _previewMaterial
        };
        _imageDisplay.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(_imageDisplay);

        _overlay = new TextureRect
        {
            Name = "FrameOverlay",
            MouseFilter = MouseFilterEnum.Ignore,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale
        };
        AddChild(_overlay);

        _frameOutline = new ReferenceRect
        {
            Name = "FrameOutline",
            MouseFilter = MouseFilterEnum.Ignore,
            BorderColor = FrameOutline,
            BorderWidth = 3f,
            EditorOnly = false
        };
        AddChild(_frameOutline);

        _brushCursor = new Line2D
        {
            Name = "BrushCursor",
            DefaultColor = Colors.Black,
            Width = 2f,
            Closed = true,
            Antialiased = true,
            Visible = false
        };
        AddChild(_brushCursor);

        _backgroundPreviewTimer = new Timer
        {
            Name = "BackgroundPreviewDebounce",
            OneShot = true,
            WaitTime = 0.06
        };
        _backgroundPreviewTimer.Timeout += RefreshBackgroundPreview;
        AddChild(_backgroundPreviewTimer);

        _animationTimer = new Timer
        {
            Name = "AnimationTimer",
            OneShot = true
        };
        _animationTimer.Timeout += AdvanceAnimationFrame;
        AddChild(_animationTimer);

        MouseEntered += OnMouseEntered;
        MouseExited += OnMouseExited;
        Resized += LayoutPreview;
        if (_initialized)
            ApplyInitializedState();
    }

    public override void _ExitTree()
    {
        MouseEntered -= OnMouseEntered;
        MouseExited -= OnMouseExited;
        Resized -= LayoutPreview;
        if (_backgroundPreviewTimer is not null)
            _backgroundPreviewTimer.Timeout -= RefreshBackgroundPreview;
        if (_animationTimer is not null)
            _animationTimer.Timeout -= AdvanceAnimationFrame;
        _undo.Clear();
        _redo.Clear();
        base._ExitTree();
    }

    public override void _Draw()
    {
        Rect2 frameRect = GetFrameRect();
        int columns = Mathf.CeilToInt(frameRect.Size.X / CheckerSize);
        int rows = Mathf.CeilToInt(frameRect.Size.Y / CheckerSize);
        for (int y = 0; y < rows; y++)
        {
            for (int x = 0; x < columns; x++)
            {
                Vector2 position = frameRect.Position + new Vector2(x * CheckerSize, y * CheckerSize);
                Vector2 size = new(
                    Mathf.Min(CheckerSize, frameRect.End.X - position.X),
                    Mathf.Min(CheckerSize, frameRect.End.Y - position.Y));
                DrawRect(new Rect2(position, size), (x + y) % 2 == 0 ? CheckerLight : CheckerDark);
            }
        }
    }

    public override void _GuiInput(InputEvent inputEvent)
    {
        if (!_initialized)
            return;

        if (inputEvent is InputEventMouse mouse)
        {
            _pointerPosition = mouse.Position;
            RefreshBrushCursor();
        }

        if (inputEvent is InputEventMouseButton mouseButton)
        {
            if (mouseButton.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown && mouseButton.Pressed)
            {
                BeginTransformEdit();
                float factor = mouseButton.ButtonIndex == MouseButton.WheelUp ? 1.12f : 1f / 1.12f;
                ZoomAt(mouseButton.Position, RelativeZoom * factor);
                AcceptEvent();
                return;
            }

            if (mouseButton.ButtonIndex == MouseButton.Left)
            {
                _dragging = mouseButton.Pressed;
                _lastPointer = mouseButton.Position;
                _strokeStarted = false;
                if (_dragging && _tool == ImageEditorTool.Pan)
                {
                    BeginTransformEdit();
                }
                else if (_dragging && _allowAlphaEditing)
                {
                    CancelBackgroundPreview();
                    if (_brushMode == ImageEditorBrushMode.Fill)
                    {
                        if (CanFillAt(mouseButton.Position))
                        {
                            PushUndo();
                            ApplyFill(mouseButton.Position);
                        }
                        _dragging = false;
                    }
                    else if (BrushTouchesImage(mouseButton.Position))
                    {
                        PushUndo();
                        _strokeStarted = true;
                        ApplyBrush(mouseButton.Position, mouseButton.Position);
                    }
                }

                AcceptEvent();
                return;
            }
        }

        if (inputEvent is not InputEventMouseMotion mouseMotion || !_dragging)
            return;

        if (_tool == ImageEditorTool.Pan)
        {
            float displayScale = GetDisplayScale();
            if (displayScale > 0f)
            {
                _offset += (mouseMotion.Position - _lastPointer) / displayScale;
                UpdatePreviewParameters();
            }
        }
        else if (_strokeStarted)
        {
            ApplyBrush(_lastPointer, mouseMotion.Position);
        }

        _lastPointer = mouseMotion.Position;
        AcceptEvent();
    }

    public void Initialize(Image source, ImageEditFrameDefinition frame)
    {
        Initialize(ImageMediaDocument.FromImage(source), frame, allowAlphaEditing: true);
    }

    public void Initialize(ImageMediaDocument source, ImageEditFrameDefinition frame)
    {
        Initialize(source, frame, allowAlphaEditing: true);
    }

    public void Initialize(ImageMediaDocument source, ImageEditFrameDefinition frame, bool allowAlphaEditing)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.OutputSize.X <= 0 || frame.OutputSize.Y <= 0)
            throw new ArgumentException("The output frame size must be positive.", nameof(frame));

        _frame = frame;
        _allowAlphaEditing = allowAlphaEditing;
        _frameDurations = new double[source.Frames.Count];
        for (int i = 0; i < source.Frames.Count; i++)
        {
            Image working = source.Frames[i].Image.Duplicate() as Image
                ?? throw new InvalidOperationException("Could not duplicate an animation frame.");
            working.Convert(Image.Format.Rgba8);
            _workingFrames.Add(working);
            _frameDurations[i] = source.Frames[i].DurationSeconds;
        }
        DownscaleWorkingFrames(_workingFrames);
        foreach (Image working in _workingFrames)
        {
            _originalFrames.Add(working.Duplicate() as Image
                ?? throw new InvalidOperationException("Could not duplicate an original animation frame."));
        }

        CreateFrameTextures(_workingFrames, _workingTextures);
        _maskTexture = frame.AlphaMask is { } mask && !mask.IsEmpty()
            ? ImageTexture.CreateFromImage(mask)
            : null;
        _initialized = true;

        if (IsNodeReady())
            ApplyInitializedState();
    }

    public void SetRelativeZoom(float relativeZoom)
    {
        SetRelativeZoom(relativeZoom, null);
    }

    public void SetImageRotationDegrees(float degrees)
    {
        if (!_initialized)
            return;

        float normalized = Mathf.Wrap(degrees, -180f, 180f);
        if (Mathf.IsEqualApprox(normalized, ImageRotationDegrees))
            return;
        _rotationRadians = Mathf.DegToRad(normalized);
        UpdatePreviewParameters();
        RotationDegreesChanged?.Invoke(normalized);
    }

    public void BeginTransformEdit()
    {
        if (_initialized)
            PushUndo(includeAlpha: false);
    }

    public void FitToFrame()
    {
        if (!_initialized)
            return;

        Image first = _workingFrames[0];
        float cosine = Mathf.Abs(Mathf.Cos(_rotationRadians));
        float sine = Mathf.Abs(Mathf.Sin(_rotationRadians));
        _fitZoom = Mathf.Max(
            (_frame.OutputSize.X * cosine + _frame.OutputSize.Y * sine) / first.GetWidth(),
            (_frame.OutputSize.X * sine + _frame.OutputSize.Y * cosine) / first.GetHeight());
        _zoom = _fitZoom;
        Vector2 sourceCenter = new(first.GetWidth() * 0.5f, first.GetHeight() * 0.5f);
        Vector2 outputCenter = new(_frame.OutputSize.X * 0.5f, _frame.OutputSize.Y * 0.5f);
        _offset = outputCenter - sourceCenter * _zoom;
        UpdatePreviewParameters();
        RelativeZoomChanged?.Invoke(1f);
    }

    public void ResetAll()
    {
        if (!_initialized)
            return;

        CancelBackgroundPreview();
        PushUndo(includeAlpha: _allowAlphaEditing);
        for (int i = 0; i < _workingFrames.Count; i++)
            _workingFrames[i] = _originalFrames[i].Duplicate() as Image ?? _originalFrames[i];
        RefreshWorkingTextures();
        _rotationRadians = 0f;
        FitToFrame();
        RotationDegreesChanged?.Invoke(0f);
    }

    public void Undo()
    {
        if (_backgroundPreviewActive)
        {
            CancelBackgroundPreview();
            return;
        }
        if (_undo.Count == 0)
            return;

        EditorSnapshot snapshot = TakeLast(_undo);
        AddSnapshot(_redo, CaptureSnapshot(snapshot.AlphaByFrame is not null));
        RestoreSnapshot(snapshot);
        NotifyHistoryAvailability();
    }

    public void Redo()
    {
        CancelBackgroundPreview();
        if (_redo.Count == 0)
            return;

        EditorSnapshot snapshot = TakeLast(_redo);
        AddSnapshot(_undo, CaptureSnapshot(snapshot.AlphaByFrame is not null));
        RestoreSnapshot(snapshot);
        NotifyHistoryAvailability();
    }

    public void RemoveBackground()
    {
        if (!_initialized)
            return;

        if (!_backgroundPreviewActive)
            RefreshBackgroundPreview();
        if (!_backgroundPreviewActive)
            return;

        PushUndo();
        for (int i = 0; i < _workingFrames.Count; i++)
            _workingFrames[i] = _backgroundPreviewFrames[i];
        _backgroundPreviewFrames.Clear();
        _backgroundPreviewActive = false;
        RefreshWorkingTextures();
        SetDisplayedFrameTexture();
    }

    public Image RenderOutput()
    {
        return RenderOutputDocument().FirstImage;
    }

    public ImageMediaDocument RenderOutputDocument()
    {
        if (!_initialized)
            throw new InvalidOperationException("The editor canvas has not been initialized.");
        if (_backgroundPreviewActive)
            RemoveBackground();

        List<ImageMediaFrame> frames = new(_workingFrames.Count);
        for (int i = 0; i < _workingFrames.Count; i++)
            frames.Add(new ImageMediaFrame(RenderFrame(_workingFrames[i]), _frameDurations[i]));
        return new ImageMediaDocument(frames);
    }

    private Image RenderFrame(Image source)
    {
        int outputWidth = _frame.OutputSize.X;
        int outputHeight = _frame.OutputSize.Y;
        Image output = Image.CreateEmpty(outputWidth, outputHeight, false, Image.Format.Rgba8);
        output.Fill(Colors.Transparent);
        Image? mask = _frame.BakeMaskIntoOutput ? _frame.AlphaMask : null;
        Vector2 sourceCenter = new(source.GetWidth() * 0.5f, source.GetHeight() * 0.5f);
        Vector2 imageCenter = _offset + sourceCenter * _zoom;

        for (int y = 0; y < outputHeight; y++)
        {
            for (int x = 0; x < outputWidth; x++)
            {
                Vector2 outputPoint = new(x + 0.5f, y + 0.5f);
                Vector2 sourcePoint = Rotate(outputPoint - imageCenter, -_rotationRadians) / _zoom + sourceCenter;
                float sourceX = sourcePoint.X - 0.5f;
                float sourceY = sourcePoint.Y - 0.5f;
                if (sourceX < 0f || sourceY < 0f || sourceX > source.GetWidth() - 1 || sourceY > source.GetHeight() - 1)
                    continue;

                Color color = SampleBilinear(source, sourceX, sourceY);
                if (mask is not null && !mask.IsEmpty())
                {
                    int maskX = Mathf.Clamp(x * mask.GetWidth() / outputWidth, 0, mask.GetWidth() - 1);
                    int maskY = Mathf.Clamp(y * mask.GetHeight() / outputHeight, 0, mask.GetHeight() - 1);
                    color.A *= mask.GetPixel(maskX, maskY).A;
                }
                output.SetPixel(x, y, color);
            }
        }
        return output;
    }

    private void ApplyInitializedState()
    {
        SetDisplayedFrameTexture();
        _previewMaterial.SetShaderParameter("source_size", new Vector2(_workingFrames[0].GetWidth(), _workingFrames[0].GetHeight()));
        _previewMaterial.SetShaderParameter("output_size", new Vector2(_frame.OutputSize.X, _frame.OutputSize.Y));
        _previewMaterial.SetShaderParameter("has_mask", _maskTexture is not null);
        if (_maskTexture is not null)
            _previewMaterial.SetShaderParameter("mask_texture", _maskTexture);
        _overlay.Texture = _frame.PreviewOverlay;
        _overlay.Visible = _frame.PreviewOverlay is not null;
        FitToFrame();
        LayoutPreview();
        NotifyHistoryAvailability();
        RestartAnimationTimer();
    }

    private void LayoutPreview()
    {
        if (!_initialized || _imageDisplay is null || _overlay is null || _frameOutline is null)
            return;

        Rect2 frameRect = GetFrameRect();
        _overlay.Position = frameRect.Position;
        _overlay.Size = frameRect.Size;
        _frameOutline.Position = frameRect.Position;
        _frameOutline.Size = frameRect.Size;
        _previewMaterial.SetShaderParameter("canvas_size", Size);
        _previewMaterial.SetShaderParameter("frame_position", frameRect.Position);
        _previewMaterial.SetShaderParameter("display_scale", GetDisplayScale());
        QueueRedraw();
        RefreshBrushCursor();
    }

    private Rect2 GetFrameRect()
    {
        if (!_initialized)
            return new Rect2(Vector2.Zero, Size);

        Vector2 available = new(Mathf.Max(1f, Size.X - 32f), Mathf.Max(1f, Size.Y - 32f));
        float scale = Mathf.Min(available.X / _frame.OutputSize.X, available.Y / _frame.OutputSize.Y);
        Vector2 size = new(_frame.OutputSize.X * scale, _frame.OutputSize.Y * scale);
        return new Rect2((Size - size) * 0.5f, size);
    }

    private float GetDisplayScale()
    {
        Rect2 rect = GetFrameRect();
        return _frame.OutputSize.X <= 0 ? 1f : rect.Size.X / _frame.OutputSize.X;
    }

    private void ZoomAt(Vector2 localPosition, float relativeZoom)
    {
        SetRelativeZoom(relativeZoom, localPosition);
    }

    private void SetRelativeZoom(float relativeZoom, Vector2? anchorLocal)
    {
        if (!_initialized)
            return;

        relativeZoom = Mathf.Clamp(relativeZoom, 0.1f, 12f);
        float nextZoom = _fitZoom * relativeZoom;
        Vector2 outputAnchor = new(_frame.OutputSize.X * 0.5f, _frame.OutputSize.Y * 0.5f);
        if (anchorLocal is { } local)
        {
            Rect2 frameRect = GetFrameRect();
            outputAnchor = (local - frameRect.Position) / GetDisplayScale();
        }

        Image first = _workingFrames[0];
        Vector2 sourceCenter = new(first.GetWidth() * 0.5f, first.GetHeight() * 0.5f);
        Vector2 imageCenter = _offset + sourceCenter * _zoom;
        Vector2 sourceAnchor = Rotate(outputAnchor - imageCenter, -_rotationRadians) / _zoom + sourceCenter;
        _zoom = nextZoom;
        imageCenter = outputAnchor - Rotate((sourceAnchor - sourceCenter) * _zoom, _rotationRadians);
        _offset = imageCenter - sourceCenter * _zoom;
        UpdatePreviewParameters();
        RelativeZoomChanged?.Invoke(relativeZoom);
    }

    private void UpdatePreviewParameters()
    {
        if (!_initialized)
            return;

        _previewMaterial.SetShaderParameter("image_scale", _zoom);
        _previewMaterial.SetShaderParameter("image_offset", _offset);
        _previewMaterial.SetShaderParameter("image_rotation", _rotationRadians);
        RefreshBrushCursor();
    }

    private void ApplyBrush(Vector2 fromLocal, Vector2 toLocal)
    {
        Vector2 fromSource = LocalToSource(fromLocal);
        Vector2 toSource = LocalToSource(toLocal);
        float radius = GetSourceBrushRadius();
        float distance = fromSource.DistanceTo(toSource);
        int steps = Mathf.Max(1, Mathf.CeilToInt(distance / Mathf.Max(1f, radius * 0.45f)));
        for (int frameIndex = 0; frameIndex < _workingFrames.Count; frameIndex++)
        {
            for (int i = 0; i <= steps; i++)
                PaintCircle(frameIndex, fromSource.Lerp(toSource, (float)i / steps), radius);
        }
        RefreshWorkingTextures();
    }

    private void PaintCircle(int frameIndex, Vector2 center, float radius)
    {
        Image working = _workingFrames[frameIndex];
        Image original = _originalFrames[frameIndex];
        int minX = Mathf.Max(0, Mathf.FloorToInt(center.X - radius));
        int maxX = Mathf.Min(working.GetWidth() - 1, Mathf.CeilToInt(center.X + radius));
        int minY = Mathf.Max(0, Mathf.FloorToInt(center.Y - radius));
        int maxY = Mathf.Min(working.GetHeight() - 1, Mathf.CeilToInt(center.Y + radius));
        float radiusSquared = radius * radius;

        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                Vector2 delta = new(x + 0.5f - center.X, y + 0.5f - center.Y);
                if (delta.LengthSquared() > radiusSquared)
                    continue;

                Color color = working.GetPixel(x, y);
                color.A = _tool == ImageEditorTool.Erase ? 0f : original.GetPixel(x, y).A;
                working.SetPixel(x, y, color);
            }
        }
    }

    private void ApplyFill(Vector2 localPosition)
    {
        Vector2 sourcePosition = LocalToSource(localPosition);
        int sourceX = Mathf.FloorToInt(sourcePosition.X);
        int sourceY = Mathf.FloorToInt(sourcePosition.Y);
        for (int i = 0; i < _workingFrames.Count; i++)
        {
            if (_tool == ImageEditorTool.Erase)
                EraseConnectedColor(_workingFrames[i], sourceX, sourceY, _backgroundTolerance);
            else
                RestoreConnectedAlpha(_workingFrames[i], _originalFrames[i], sourceX, sourceY);
        }
        RefreshWorkingTextures();
    }

    private static void EraseConnectedColor(Image image, int startX, int startY, float tolerance)
    {
        int width = image.GetWidth();
        int height = image.GetHeight();
        Color seed = image.GetPixel(startX, startY);
        bool[] visited = new bool[width * height];
        int[] queue = new int[width * height];
        int head = 0;
        int tail = 0;
        Enqueue(startX, startY);
        while (head < tail)
        {
            int index = queue[head++];
            int x = index % width;
            int y = index / width;
            Color color = image.GetPixel(x, y);
            if (!MatchesColor(color, seed, tolerance))
                continue;
            color.A = 0f;
            image.SetPixel(x, y, color);
            Enqueue(x - 1, y);
            Enqueue(x + 1, y);
            Enqueue(x, y - 1);
            Enqueue(x, y + 1);
        }

        void Enqueue(int x, int y)
        {
            if (x < 0 || x >= width || y < 0 || y >= height)
                return;
            int index = y * width + x;
            if (visited[index])
                return;
            visited[index] = true;
            queue[tail++] = index;
        }
    }

    private static void RestoreConnectedAlpha(Image working, Image original, int startX, int startY)
    {
        int width = working.GetWidth();
        int height = working.GetHeight();
        float seedAlpha = working.GetPixel(startX, startY).A;
        bool[] visited = new bool[width * height];
        int[] queue = new int[width * height];
        int head = 0;
        int tail = 0;
        Enqueue(startX, startY);
        while (head < tail)
        {
            int index = queue[head++];
            int x = index % width;
            int y = index / width;
            Color color = working.GetPixel(x, y);
            if (Mathf.Abs(color.A - seedAlpha) > 0.01f)
                continue;
            color.A = original.GetPixel(x, y).A;
            working.SetPixel(x, y, color);
            Enqueue(x - 1, y);
            Enqueue(x + 1, y);
            Enqueue(x, y - 1);
            Enqueue(x, y + 1);
        }

        void Enqueue(int x, int y)
        {
            if (x < 0 || x >= width || y < 0 || y >= height)
                return;
            int index = y * width + x;
            if (visited[index])
                return;
            visited[index] = true;
            queue[tail++] = index;
        }
    }

    private void RefreshBackgroundPreview()
    {
        if (!_initialized)
            return;

        _backgroundPreviewFrames.Clear();
        foreach (Image working in _workingFrames)
        {
            Image preview = working.Duplicate() as Image
                ?? throw new InvalidOperationException("Could not create the background-removal preview.");
            RemoveConnectedBackground(preview, _backgroundTolerance);
            _backgroundPreviewFrames.Add(preview);
        }

        if (_backgroundPreviewTextures.Count == 0)
        {
            CreateFrameTextures(_backgroundPreviewFrames, _backgroundPreviewTextures);
        }
        else
        {
            for (int i = 0; i < _backgroundPreviewFrames.Count; i++)
                _backgroundPreviewTextures[i].Update(_backgroundPreviewFrames[i]);
        }
        _backgroundPreviewActive = true;
        SetDisplayedFrameTexture();
        NotifyHistoryAvailability();
    }

    private void CancelBackgroundPreview()
    {
        if (!_backgroundPreviewActive)
            return;
        _backgroundPreviewTimer?.Stop();
        _backgroundPreviewFrames.Clear();
        _backgroundPreviewActive = false;
        SetDisplayedFrameTexture();
        NotifyHistoryAvailability();
    }

    private static void RemoveConnectedBackground(Image image, float tolerance)
    {
        int width = image.GetWidth();
        int height = image.GetHeight();
        Color[] backgrounds =
        [
            image.GetPixel(0, 0),
            image.GetPixel(width - 1, 0),
            image.GetPixel(0, height - 1),
            image.GetPixel(width - 1, height - 1)
        ];
        bool[] visited = new bool[width * height];
        int[] queue = new int[width * height];
        int head = 0;
        int tail = 0;
        for (int x = 0; x < width; x++)
        {
            Enqueue(x, 0);
            Enqueue(x, height - 1);
        }
        for (int y = 1; y < height - 1; y++)
        {
            Enqueue(0, y);
            Enqueue(width - 1, y);
        }
        while (head < tail)
        {
            int index = queue[head++];
            int x = index % width;
            int y = index / width;
            Color color = image.GetPixel(x, y);
            if (!MatchesBackground(color, backgrounds, tolerance))
                continue;
            color.A = 0f;
            image.SetPixel(x, y, color);
            Enqueue(x - 1, y);
            Enqueue(x + 1, y);
            Enqueue(x, y - 1);
            Enqueue(x, y + 1);
        }

        void Enqueue(int x, int y)
        {
            if (x < 0 || x >= width || y < 0 || y >= height)
                return;
            int index = y * width + x;
            if (visited[index])
                return;
            visited[index] = true;
            queue[tail++] = index;
        }
    }

    private void PushUndo(bool includeAlpha = true)
    {
        AddSnapshot(_undo, CaptureSnapshot(includeAlpha));
        _redo.Clear();
        NotifyHistoryAvailability();
    }

    private EditorSnapshot CaptureSnapshot(bool includeAlpha)
    {
        byte[][]? alphaByFrame = includeAlpha ? new byte[_workingFrames.Count][] : null;
        long bytes = 0;
        if (alphaByFrame is not null)
        {
            for (int i = 0; i < _workingFrames.Count; i++)
            {
                byte[] rgba = _workingFrames[i].GetData();
                byte[] alpha = new byte[rgba.Length / 4];
                for (int pixel = 0; pixel < alpha.Length; pixel++)
                    alpha[pixel] = rgba[pixel * 4 + 3];
                alphaByFrame[i] = alpha;
                bytes += alpha.Length;
            }
        }
        return new EditorSnapshot(alphaByFrame, _zoom, _offset, _rotationRadians, bytes);
    }

    private void RestoreSnapshot(EditorSnapshot snapshot)
    {
        if (snapshot.AlphaByFrame is not null)
        {
            for (int i = 0; i < _workingFrames.Count; i++)
            {
                Image current = _workingFrames[i];
                byte[] rgba = current.GetData();
                byte[] alpha = snapshot.AlphaByFrame[i];
                for (int pixel = 0; pixel < alpha.Length; pixel++)
                    rgba[pixel * 4 + 3] = alpha[pixel];
                _workingFrames[i] = Image.CreateFromData(
                    current.GetWidth(),
                    current.GetHeight(),
                    false,
                    Image.Format.Rgba8,
                    rgba);
            }
            RefreshWorkingTextures();
        }
        _zoom = snapshot.Zoom;
        _offset = snapshot.Offset;
        _rotationRadians = snapshot.RotationRadians;
        UpdatePreviewParameters();
        RelativeZoomChanged?.Invoke(RelativeZoom);
        RotationDegreesChanged?.Invoke(ImageRotationDegrees);
    }

    private static EditorSnapshot TakeLast(List<EditorSnapshot> snapshots)
    {
        EditorSnapshot snapshot = snapshots[^1];
        snapshots.RemoveAt(snapshots.Count - 1);
        return snapshot;
    }

    private static void AddSnapshot(List<EditorSnapshot> snapshots, EditorSnapshot snapshot)
    {
        snapshots.Add(snapshot);
        while (snapshots.Count > UndoLimit)
            snapshots.RemoveAt(0);
        long bytes = 0;
        foreach (EditorSnapshot item in snapshots)
            bytes += item.ByteCount;
        while (snapshots.Count > 1 && bytes > HistoryByteBudget)
        {
            bytes -= snapshots[0].ByteCount;
            snapshots.RemoveAt(0);
        }
    }

    private void NotifyHistoryAvailability()
    {
        HistoryAvailabilityChanged?.Invoke(_undo.Count > 0 || _backgroundPreviewActive, _redo.Count > 0);
    }

    private void RefreshWorkingTextures()
    {
        for (int i = 0; i < _workingFrames.Count; i++)
            _workingTextures[i].Update(_workingFrames[i]);
    }

    private static void CreateFrameTextures(IReadOnlyList<Image> frames, List<ImageTexture> frameTextures)
    {
        frameTextures.Clear();
        foreach (Image frame in frames)
            frameTextures.Add(ImageTexture.CreateFromImage(frame));
    }

    private void AdvanceAnimationFrame()
    {
        if (_workingFrames.Count <= 1)
            return;
        _currentFrameIndex = (_currentFrameIndex + 1) % _workingFrames.Count;
        SetDisplayedFrameTexture();
        RestartAnimationTimer();
    }

    private void RestartAnimationTimer()
    {
        if (_animationTimer is null || _workingFrames.Count <= 1)
            return;
        _animationTimer.WaitTime = Math.Clamp(_frameDurations[_currentFrameIndex], 0.02, 10.0);
        _animationTimer.Start();
    }

    private void SetDisplayedFrameTexture()
    {
        if (_workingTextures.Count == 0)
            return;
        int index = Mathf.Clamp(_currentFrameIndex, 0, _workingTextures.Count - 1);
        IReadOnlyList<ImageTexture> textures = _backgroundPreviewActive
            ? _backgroundPreviewTextures
            : _workingTextures;
        _previewMaterial.SetShaderParameter("source_texture", textures[index]);
    }

    private Vector2 LocalToSource(Vector2 localPosition)
    {
        Rect2 frameRect = GetFrameRect();
        Vector2 output = (localPosition - frameRect.Position) / GetDisplayScale();
        Image first = _workingFrames[0];
        Vector2 sourceCenter = new(first.GetWidth() * 0.5f, first.GetHeight() * 0.5f);
        Vector2 imageCenter = _offset + sourceCenter * _zoom;
        return Rotate(output - imageCenter, -_rotationRadians) / _zoom + sourceCenter;
    }

    private float GetSourceBrushRadius()
    {
        return Mathf.Max(1f, _brushSize / _zoom * 0.5f);
    }

    private bool BrushTouchesImage(Vector2 localPosition)
    {
        Vector2 source = LocalToSource(localPosition);
        float radius = GetSourceBrushRadius();
        Image first = _workingFrames[0];
        return source.X + radius >= 0f
            && source.Y + radius >= 0f
            && source.X - radius <= first.GetWidth()
            && source.Y - radius <= first.GetHeight();
    }

    private bool CanFillAt(Vector2 localPosition)
    {
        Vector2 source = LocalToSource(localPosition);
        Image first = _workingFrames[0];
        return source.X >= 0f && source.Y >= 0f && source.X < first.GetWidth() && source.Y < first.GetHeight();
    }

    private void OnMouseEntered()
    {
        _pointerInside = true;
        _pointerPosition = GetLocalMousePosition();
        RefreshBrushCursor();
    }

    private void OnMouseExited()
    {
        _pointerInside = false;
        RefreshBrushCursor();
    }

    private void RefreshBrushCursor()
    {
        if (_brushCursor is null || !GodotObject.IsInstanceValid(_brushCursor))
            return;
        bool visible = _initialized && _allowAlphaEditing && _pointerInside && _tool != ImageEditorTool.Pan;
        _brushCursor.Visible = visible;
        if (!visible)
            return;

        float radius = _brushMode == ImageEditorBrushMode.Brush
            ? Mathf.Max(2f, _brushSize * GetDisplayScale() * 0.5f)
            : 9f;
        Vector2[] points = new Vector2[49];
        for (int i = 0; i < points.Length; i++)
        {
            float angle = Mathf.Tau * i / (points.Length - 1);
            points[i] = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius;
        }
        _brushCursor.Position = _pointerPosition;
        _brushCursor.Points = points;
    }

    private static bool MatchesBackground(Color color, IReadOnlyList<Color> backgrounds, float tolerance)
    {
        if (color.A <= 0.001f)
            return true;
        foreach (Color background in backgrounds)
        {
            if (MatchesColor(color, background, tolerance))
                return true;
        }
        return false;
    }

    private static bool MatchesColor(Color color, Color target, float tolerance)
    {
        float red = color.R - target.R;
        float green = color.G - target.G;
        float blue = color.B - target.B;
        return red * red + green * green + blue * blue <= tolerance * tolerance * 3f;
    }

    private static Color SampleBilinear(Image image, float x, float y)
    {
        int x0 = Mathf.FloorToInt(x);
        int y0 = Mathf.FloorToInt(y);
        int x1 = Mathf.Min(x0 + 1, image.GetWidth() - 1);
        int y1 = Mathf.Min(y0 + 1, image.GetHeight() - 1);
        float tx = x - x0;
        float ty = y - y0;
        Color top = image.GetPixel(x0, y0).Lerp(image.GetPixel(x1, y0), tx);
        Color bottom = image.GetPixel(x0, y1).Lerp(image.GetPixel(x1, y1), tx);
        return top.Lerp(bottom, ty);
    }

    private static Vector2 Rotate(Vector2 point, float radians)
    {
        float cosine = Mathf.Cos(radians);
        float sine = Mathf.Sin(radians);
        return new Vector2(
            point.X * cosine - point.Y * sine,
            point.X * sine + point.Y * cosine);
    }

    private static void DownscaleWorkingFrames(IReadOnlyList<Image> frames)
    {
        int width = frames[0].GetWidth();
        int height = frames[0].GetHeight();
        int longest = Mathf.Max(width, height);
        if (longest <= MaxWorkingDimension)
            return;

        float scale = (float)MaxWorkingDimension / longest;
        int targetWidth = Mathf.Max(1, Mathf.RoundToInt(width * scale));
        int targetHeight = Mathf.Max(1, Mathf.RoundToInt(height * scale));
        foreach (Image frame in frames)
            frame.Resize(targetWidth, targetHeight, Image.Interpolation.Lanczos);
    }

    private static ShaderMaterial CreatePreviewMaterial()
    {
        Shader shader = new()
        {
            Code = """
                shader_type canvas_item;
                uniform sampler2D source_texture : filter_linear;
                uniform sampler2D mask_texture : filter_linear;
                uniform bool has_mask = false;
                uniform vec2 source_size = vec2(1.0);
                uniform vec2 output_size = vec2(1.0);
                uniform vec2 canvas_size = vec2(1.0);
                uniform vec2 frame_position = vec2(0.0);
                uniform float display_scale = 1.0;
                uniform float image_scale = 1.0;
                uniform vec2 image_offset = vec2(0.0);
                uniform float image_rotation = 0.0;

                void fragment() {
                    vec2 canvas_pixel = UV * canvas_size;
                    vec2 output_pixel = (canvas_pixel - frame_position) / display_scale;
                    vec2 source_center = source_size * 0.5;
                    vec2 image_center = image_offset + source_center * image_scale;
                    vec2 delta = output_pixel - image_center;
                    float cosine = cos(-image_rotation);
                    float sine = sin(-image_rotation);
                    vec2 unrotated = vec2(
                        delta.x * cosine - delta.y * sine,
                        delta.x * sine + delta.y * cosine);
                    vec2 source_pixel = unrotated / image_scale + source_center;
                    vec2 source_uv = source_pixel / source_size;
                    vec4 color = vec4(0.0);
                    if (source_uv.x >= 0.0 && source_uv.y >= 0.0 && source_uv.x <= 1.0 && source_uv.y <= 1.0) {
                        color = texture(source_texture, source_uv);
                    }
                    bool inside_frame = output_pixel.x >= 0.0 && output_pixel.y >= 0.0
                        && output_pixel.x <= output_size.x && output_pixel.y <= output_size.y;
                    if (has_mask && inside_frame) {
                        vec4 mask = texture(mask_texture, output_pixel / output_size);
                        color.a *= mask.a;
                    }
                    if (!inside_frame) {
                        color.a *= 0.58;
                    }
                    COLOR = color;
                }
                """
        };
        return new ShaderMaterial
        {
            Shader = shader,
            ResourceLocalToScene = true
        };
    }

    private sealed record EditorSnapshot(
        byte[][]? AlphaByFrame,
        float Zoom,
        Vector2 Offset,
        float RotationRadians,
        long ByteCount);
}
