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

public partial class NImageEditorCanvas : Control
{
    private const int UndoLimit = 8;
    private const int MaxWorkingDimension = 1280;
    private const float CheckerSize = 18f;

    private static readonly Color CheckerLight = new("4A4A4A");
    private static readonly Color CheckerDark = new("303030");
    private static readonly Color FrameOutline = new("D9AD46");

    private readonly List<EditorSnapshot> _undo = [];
    private readonly ShaderMaterial _previewMaterial = CreatePreviewMaterial();

    private Image _original = null!;
    private Image _working = null!;
    private ImageTexture _workingTexture = null!;
    private ImageTexture? _maskTexture;
    private ImageEditFrameDefinition _frame = null!;
    private ColorRect _imageDisplay = null!;
    private TextureRect _overlay = null!;
    private bool _initialized;
    private bool _dragging;
    private bool _strokeStarted;
    private Vector2 _lastPointer;
    private Vector2 _offset;
    private float _zoom;
    private float _fitZoom;
    private float _brushSize = 42f;
    private float _backgroundTolerance = 0.14f;
    private ImageEditorTool _tool = ImageEditorTool.Pan;

    public event Action<float>? RelativeZoomChanged;
    public event Action<bool>? UndoAvailabilityChanged;

    public ImageEditorTool Tool
    {
        get => _tool;
        set => _tool = value;
    }

    public float BrushSize
    {
        get => _brushSize;
        set => _brushSize = Mathf.Clamp(value, 4f, 160f);
    }

    public float BackgroundTolerance
    {
        get => _backgroundTolerance;
        set => _backgroundTolerance = Mathf.Clamp(value, 0.01f, 0.5f);
    }

    public float RelativeZoom => _fitZoom <= 0f ? 1f : _zoom / _fitZoom;

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
        AddChild(_imageDisplay);

        _overlay = new TextureRect
        {
            Name = "FrameOverlay",
            MouseFilter = MouseFilterEnum.Ignore,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale
        };
        AddChild(_overlay);

        Resized += LayoutPreview;
        if (_initialized)
            ApplyInitializedState();
    }

    public override void _ExitTree()
    {
        Resized -= LayoutPreview;
        _undo.Clear();
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

        DrawRect(frameRect, FrameOutline, filled: false, width: 3f);
    }

    public override void _GuiInput(InputEvent inputEvent)
    {
        if (!_initialized)
            return;

        if (inputEvent is InputEventMouseButton mouseButton)
        {
            if (mouseButton.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown && mouseButton.Pressed)
            {
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
                if (_dragging && _tool != ImageEditorTool.Pan && GetFrameRect().HasPoint(mouseButton.Position))
                {
                    PushUndo();
                    _strokeStarted = true;
                    ApplyBrush(mouseButton.Position, mouseButton.Position);
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
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(frame);
        if (source.IsEmpty())
            throw new ArgumentException("The source image is empty.", nameof(source));
        if (frame.OutputSize.X <= 0 || frame.OutputSize.Y <= 0)
            throw new ArgumentException("The output frame size must be positive.", nameof(frame));

        _frame = frame;
        _working = source.Duplicate() as Image ?? throw new InvalidOperationException("Could not duplicate the source image.");
        _working.Convert(Image.Format.Rgba8);
        DownscaleWorkingImage(_working);
        _original = _working.Duplicate() as Image ?? throw new InvalidOperationException("Could not duplicate the source image.");
        _workingTexture = ImageTexture.CreateFromImage(_working);
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

    public void FitToFrame()
    {
        if (!_initialized)
            return;

        _fitZoom = Mathf.Max(
            (float)_frame.OutputSize.X / _working.GetWidth(),
            (float)_frame.OutputSize.Y / _working.GetHeight());
        _zoom = _fitZoom;
        _offset = new Vector2(
            (_frame.OutputSize.X - _working.GetWidth() * _zoom) * 0.5f,
            (_frame.OutputSize.Y - _working.GetHeight() * _zoom) * 0.5f);
        UpdatePreviewParameters();
        RelativeZoomChanged?.Invoke(1f);
    }

    public void ResetAll()
    {
        if (!_initialized)
            return;

        PushUndo();
        _working = _original.Duplicate() as Image ?? _original;
        _workingTexture.Update(_working);
        FitToFrame();
    }

    public void Undo()
    {
        if (_undo.Count == 0)
            return;

        EditorSnapshot snapshot = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _working = snapshot.Image;
        _zoom = snapshot.Zoom;
        _offset = snapshot.Offset;
        _workingTexture.Update(_working);
        UpdatePreviewParameters();
        RelativeZoomChanged?.Invoke(RelativeZoom);
        UndoAvailabilityChanged?.Invoke(_undo.Count > 0);
    }

    public void RemoveBackground()
    {
        if (!_initialized)
            return;

        PushUndo();
        int width = _working.GetWidth();
        int height = _working.GetHeight();
        Color[] backgrounds =
        [
            _working.GetPixel(0, 0),
            _working.GetPixel(width - 1, 0),
            _working.GetPixel(0, height - 1),
            _working.GetPixel(width - 1, height - 1)
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
            Color color = _working.GetPixel(x, y);
            if (!MatchesBackground(color, backgrounds, _backgroundTolerance))
                continue;

            color.A = 0f;
            _working.SetPixel(x, y, color);
            Enqueue(x - 1, y);
            Enqueue(x + 1, y);
            Enqueue(x, y - 1);
            Enqueue(x, y + 1);
        }

        _workingTexture.Update(_working);

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

    public Image RenderOutput()
    {
        if (!_initialized)
            throw new InvalidOperationException("The editor canvas has not been initialized.");

        int outputWidth = _frame.OutputSize.X;
        int outputHeight = _frame.OutputSize.Y;
        Image output = Image.CreateEmpty(outputWidth, outputHeight, false, Image.Format.Rgba8);
        output.Fill(Colors.Transparent);
        Image? mask = _frame.BakeMaskIntoOutput ? _frame.AlphaMask : null;

        for (int y = 0; y < outputHeight; y++)
        {
            for (int x = 0; x < outputWidth; x++)
            {
                float sourceX = (x + 0.5f - _offset.X) / _zoom - 0.5f;
                float sourceY = (y + 0.5f - _offset.Y) / _zoom - 0.5f;
                if (sourceX < 0f || sourceY < 0f || sourceX > _working.GetWidth() - 1 || sourceY > _working.GetHeight() - 1)
                    continue;

                Color color = SampleBilinear(_working, sourceX, sourceY);
                if (mask is not null && !mask.IsEmpty())
                {
                    int maskX = Mathf.Clamp(x * mask.GetWidth() / outputWidth, 0, mask.GetWidth() - 1);
                    int maskY = Mathf.Clamp(y * mask.GetHeight() / outputHeight, 0, mask.GetHeight() - 1);
                    Color maskColor = mask.GetPixel(maskX, maskY);
                    color.A *= maskColor.A;
                }

                output.SetPixel(x, y, color);
            }
        }

        return output;
    }

    private void ApplyInitializedState()
    {
        _previewMaterial.SetShaderParameter("source_texture", _workingTexture);
        _previewMaterial.SetShaderParameter("source_size", new Vector2(_working.GetWidth(), _working.GetHeight()));
        _previewMaterial.SetShaderParameter("output_size", new Vector2(_frame.OutputSize.X, _frame.OutputSize.Y));
        _previewMaterial.SetShaderParameter("has_mask", _maskTexture is not null);
        if (_maskTexture is not null)
            _previewMaterial.SetShaderParameter("mask_texture", _maskTexture);
        _overlay.Texture = _frame.PreviewOverlay;
        _overlay.Visible = _frame.PreviewOverlay is not null;
        FitToFrame();
        LayoutPreview();
    }

    private void LayoutPreview()
    {
        if (!_initialized || _imageDisplay is null || _overlay is null)
            return;

        Rect2 frameRect = GetFrameRect();
        _imageDisplay.Position = frameRect.Position;
        _imageDisplay.Size = frameRect.Size;
        _overlay.Position = frameRect.Position;
        _overlay.Size = frameRect.Size;
        QueueRedraw();
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
        if (!GetFrameRect().HasPoint(localPosition))
            return;

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

        Vector2 sourceAnchor = (outputAnchor - _offset) / _zoom;
        _zoom = nextZoom;
        _offset = outputAnchor - sourceAnchor * _zoom;
        UpdatePreviewParameters();
        RelativeZoomChanged?.Invoke(relativeZoom);
    }

    private void UpdatePreviewParameters()
    {
        if (!_initialized)
            return;

        _previewMaterial.SetShaderParameter("image_scale", _zoom);
        _previewMaterial.SetShaderParameter("image_offset", _offset);
    }

    private void ApplyBrush(Vector2 fromLocal, Vector2 toLocal)
    {
        Rect2 frameRect = GetFrameRect();
        float displayScale = GetDisplayScale();
        Vector2 fromOutput = (fromLocal - frameRect.Position) / displayScale;
        Vector2 toOutput = (toLocal - frameRect.Position) / displayScale;
        Vector2 fromSource = (fromOutput - _offset) / _zoom;
        Vector2 toSource = (toOutput - _offset) / _zoom;
        float radius = Mathf.Max(1f, _brushSize / _zoom * 0.5f);
        float distance = fromSource.DistanceTo(toSource);
        int steps = Mathf.Max(1, Mathf.CeilToInt(distance / Mathf.Max(1f, radius * 0.45f)));
        for (int i = 0; i <= steps; i++)
            PaintCircle(fromSource.Lerp(toSource, (float)i / steps), radius);
        _workingTexture.Update(_working);
    }

    private void PaintCircle(Vector2 center, float radius)
    {
        int minX = Mathf.Max(0, Mathf.FloorToInt(center.X - radius));
        int maxX = Mathf.Min(_working.GetWidth() - 1, Mathf.CeilToInt(center.X + radius));
        int minY = Mathf.Max(0, Mathf.FloorToInt(center.Y - radius));
        int maxY = Mathf.Min(_working.GetHeight() - 1, Mathf.CeilToInt(center.Y + radius));
        float radiusSquared = radius * radius;

        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                Vector2 delta = new(x + 0.5f - center.X, y + 0.5f - center.Y);
                if (delta.LengthSquared() > radiusSquared)
                    continue;

                Color color = _working.GetPixel(x, y);
                color.A = _tool == ImageEditorTool.Erase ? 0f : _original.GetPixel(x, y).A;
                _working.SetPixel(x, y, color);
            }
        }
    }

    private void PushUndo()
    {
        Image snapshot = _working.Duplicate() as Image ?? throw new InvalidOperationException("Could not snapshot the edited image.");
        _undo.Add(new EditorSnapshot(snapshot, _zoom, _offset));
        if (_undo.Count > UndoLimit)
            _undo.RemoveAt(0);
        UndoAvailabilityChanged?.Invoke(true);
    }

    private static bool MatchesBackground(Color color, IReadOnlyList<Color> backgrounds, float tolerance)
    {
        if (color.A <= 0.001f)
            return true;

        float limit = tolerance * tolerance * 3f;
        foreach (Color background in backgrounds)
        {
            float red = color.R - background.R;
            float green = color.G - background.G;
            float blue = color.B - background.B;
            if (red * red + green * green + blue * blue <= limit)
                return true;
        }

        return false;
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

    private static void DownscaleWorkingImage(Image image)
    {
        int width = image.GetWidth();
        int height = image.GetHeight();
        int longest = Mathf.Max(width, height);
        if (longest <= MaxWorkingDimension)
            return;

        float scale = (float)MaxWorkingDimension / longest;
        image.Resize(
            Mathf.Max(1, Mathf.RoundToInt(width * scale)),
            Mathf.Max(1, Mathf.RoundToInt(height * scale)),
            Image.Interpolation.Lanczos);
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
                uniform float image_scale = 1.0;
                uniform vec2 image_offset = vec2(0.0);

                void fragment() {
                    vec2 output_pixel = UV * output_size;
                    vec2 source_pixel = (output_pixel - image_offset) / image_scale;
                    vec2 source_uv = source_pixel / source_size;
                    vec4 color = vec4(0.0);
                    if (source_uv.x >= 0.0 && source_uv.y >= 0.0 && source_uv.x <= 1.0 && source_uv.y <= 1.0) {
                        color = texture(source_texture, source_uv);
                    }
                    if (has_mask) {
                        vec4 mask = texture(mask_texture, UV);
                        color.a *= mask.a;
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

    private sealed record EditorSnapshot(Image Image, float Zoom, Vector2 Offset);
}
