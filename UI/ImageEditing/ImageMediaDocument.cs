#nullable enable

namespace Loadout.UI.ImageEditing;

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using Godot;

public sealed record ImageMediaFrame(Image Image, double DurationSeconds = 0.1);
public readonly record struct ImageMediaMetadata(int Width, int Height, int FrameCount);

public sealed class ImageMediaDocument
{
    public const int MaxFrames = 256;

    private readonly ImageMediaFrame[] _frames;

    public ImageMediaDocument(IEnumerable<ImageMediaFrame> frames)
    {
        ArgumentNullException.ThrowIfNull(frames);
        _frames = frames.ToArray();
        if (_frames.Length == 0 || _frames.Length > MaxFrames)
            throw new ArgumentException($"An image document must contain between 1 and {MaxFrames} frames.", nameof(frames));

        int width = _frames[0].Image?.GetWidth() ?? 0;
        int height = _frames[0].Image?.GetHeight() ?? 0;
        if (width <= 0 || height <= 0)
            throw new ArgumentException("The image document contains an empty frame.", nameof(frames));

        foreach (ImageMediaFrame frame in _frames)
        {
            if (frame.Image is null || frame.Image.IsEmpty())
                throw new ArgumentException("The image document contains an empty frame.", nameof(frames));
            if (frame.Image.GetWidth() != width || frame.Image.GetHeight() != height)
                throw new ArgumentException("All image document frames must have the same dimensions.", nameof(frames));
            if (!double.IsFinite(frame.DurationSeconds) || frame.DurationSeconds <= 0.0)
                throw new ArgumentException("Every image document frame must have a positive finite duration.", nameof(frames));
        }
    }

    public IReadOnlyList<ImageMediaFrame> Frames => _frames;
    public Image FirstImage => _frames[0].Image;
    public bool IsAnimated => _frames.Length > 1;
    public int Width => FirstImage.GetWidth();
    public int Height => FirstImage.GetHeight();

    public static ImageMediaDocument FromImage(Image image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return new ImageMediaDocument([new ImageMediaFrame(image)]);
    }
}

internal static class ImageAnimationSizing
{
    public const long MaxOutputPixelsAcrossFrames = 48L * 1024L * 1024L;
    public const long OptimizedMaxOutputPixelsAcrossFrames = 12L * 1024L * 1024L;
    private const int MaxOutputDimension = 500;

    public static Vector2I GetOutputSize(Vector2I requestedSize, int frameCount, bool optimize = false)
    {
        if (requestedSize.X <= 0 || requestedSize.Y <= 0 || frameCount <= 0 || (frameCount == 1 && !optimize))
            return requestedSize;

        long requestedPixels = (long)requestedSize.X * requestedSize.Y;
        double dimensionScale = Math.Min(
            1.0,
            (double)MaxOutputDimension / Math.Max(requestedSize.X, requestedSize.Y));
        double budgetScale = Math.Min(
            1.0,
            Math.Sqrt((double)(optimize ? OptimizedMaxOutputPixelsAcrossFrames : MaxOutputPixelsAcrossFrames)
                / (requestedPixels * frameCount)));
        double scale = Math.Min(dimensionScale, budgetScale);
        if (scale >= 1.0)
            return requestedSize;

        int width = Math.Max(1, (int)Math.Floor(requestedSize.X * scale));
        int height = Math.Max(1, (int)Math.Floor(requestedSize.Y * scale));
        return new Vector2I(width, height);
    }

    public static Vector2I GetTextureSize(Vector2I requestedSize, Vector2I sourceSize, int frameCount, bool preserveSourceSize)
    {
        if (preserveSourceSize)
            return GetOutputSize(sourceSize, frameCount);
        Vector2I size = GetOutputSize(requestedSize, frameCount);
        double scale = Math.Min(1.0, Math.Min((double)sourceSize.X / size.X, (double)sourceSize.Y / size.Y));
        return new Vector2I(Math.Max(1, (int)Math.Floor(size.X * scale)), Math.Max(1, (int)Math.Floor(size.Y * scale)));
    }
}

public static class ImageAnimationPackage
{
    public const string Extension = ".loadoutanim";
    private const int CurrentVersion = 2;
    private const long MaxEntryBytes = 64L * 1024L * 1024L;
    private const long MaxPackageBytes = 512L * 1024L * 1024L;
    private const string ManifestEntryName = "animation.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static void Save(string path, ImageMediaDocument document, bool optimize = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(document);

        using FileStream stream = new(path, FileMode.Create, System.IO.FileAccess.Write, FileShare.None);
        using ZipArchive archive = new(stream, ZipArchiveMode.Create, leaveOpen: false);
        List<AnimationFrameRecord> records = new(document.Frames.Count);
        for (int i = 0; i < document.Frames.Count; i++)
        {
            (byte[] data, string extension) = ImageMediaEncoding.Encode(document.Frames[i].Image, optimize);
            string frameFile = $"frames/{i:D4}{extension}";
            ZipArchiveEntry entry = archive.CreateEntry(frameFile,
                extension == ".webp" ? CompressionLevel.NoCompression : CompressionLevel.Fastest);
            using Stream entryStream = entry.Open();
            entryStream.Write(data);
            records.Add(new AnimationFrameRecord(frameFile, NormalizeDuration(document.Frames[i].DurationSeconds)));
        }

        AnimationManifest manifest = new()
        {
            Version = optimize ? CurrentVersion : 1,
            Width = document.Width,
            Height = document.Height,
            Frames = records
        };
        ZipArchiveEntry manifestEntry = archive.CreateEntry(ManifestEntryName, CompressionLevel.Fastest);
        using Stream manifestStream = manifestEntry.Open();
        JsonSerializer.Serialize(manifestStream, manifest, JsonOptions);
    }

    public static ImageMediaDocument Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        FileInfo file = new(path);
        if (!file.Exists)
            throw new FileNotFoundException("The animation package does not exist.", path);
        if (file.Length <= 0 || file.Length > MaxPackageBytes)
            throw new InvalidDataException("The animation package is empty or too large.");

        using FileStream stream = new(file.FullName, FileMode.Open, System.IO.FileAccess.Read, FileShare.Read);
        return Load(stream);
    }

    public static ImageMediaDocument Load(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length == 0 || data.LongLength > MaxPackageBytes)
            throw new InvalidDataException("The animation package is empty or too large.");
        using MemoryStream stream = new(data, writable: false);
        return Load(stream);
    }

    private static ImageMediaDocument Load(Stream stream)
    {
        using ZipArchive archive = new(stream, ZipArchiveMode.Read, leaveOpen: true);
        ZipArchiveEntry manifestEntry = archive.GetEntry(ManifestEntryName)
            ?? throw new InvalidDataException("The animation package manifest is missing.");
        if (manifestEntry.Length <= 0 || manifestEntry.Length > MaxEntryBytes)
            throw new InvalidDataException("The animation package manifest is invalid.");

        AnimationManifest? manifest;
        using (Stream manifestStream = manifestEntry.Open())
            manifest = JsonSerializer.Deserialize<AnimationManifest>(manifestStream, JsonOptions);
        if (manifest is null || manifest.Version is < 1 or > CurrentVersion)
            throw new InvalidDataException("The animation package version is unsupported.");
        if (manifest.Frames is not { Count: > 0 } || manifest.Frames.Count > ImageMediaDocument.MaxFrames)
            throw new InvalidDataException("The animation package frame count is invalid.");

        List<ImageMediaFrame> frames = new(manifest.Frames.Count);
        long totalBytes = 0;
        foreach (AnimationFrameRecord frameRecord in manifest.Frames)
        {
            if (string.IsNullOrWhiteSpace(frameRecord.File)
                || frameRecord.File.Contains('\\')
                || frameRecord.File.StartsWith('/')
                || frameRecord.File.Contains("..", StringComparison.Ordinal))
            {
                throw new InvalidDataException("The animation package contains an invalid frame path.");
            }

            ZipArchiveEntry frameEntry = archive.GetEntry(frameRecord.File)
                ?? throw new InvalidDataException($"Animation frame '{frameRecord.File}' is missing.");
            if (frameEntry.Length <= 0 || frameEntry.Length > MaxEntryBytes)
                throw new InvalidDataException($"Animation frame '{frameRecord.File}' is invalid.");
            totalBytes += frameEntry.Length;
            if (totalBytes > MaxPackageBytes)
                throw new InvalidDataException("The animation package expands beyond the supported size.");

            byte[] data = new byte[checked((int)frameEntry.Length)];
            using (Stream frameStream = frameEntry.Open())
                frameStream.ReadExactly(data);
            Image image = DecodeFrame(data, frameRecord.File, manifest.Frames.Count);
            frames.Add(new ImageMediaFrame(image, NormalizeDuration(frameRecord.DurationSeconds)));
        }

        return new ImageMediaDocument(frames);
    }

    public static Image LoadFirstFrame(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        FileInfo file = new(path);
        if (!file.Exists)
            throw new FileNotFoundException("The animation package does not exist.", path);
        if (file.Length <= 0 || file.Length > MaxPackageBytes)
            throw new InvalidDataException("The animation package is empty or too large.");

        using FileStream stream = new(file.FullName, FileMode.Open, System.IO.FileAccess.Read, FileShare.Read);
        using ZipArchive archive = new(stream, ZipArchiveMode.Read, leaveOpen: false);
        ZipArchiveEntry manifestEntry = archive.GetEntry(ManifestEntryName)
            ?? throw new InvalidDataException("The animation package manifest is missing.");
        if (manifestEntry.Length <= 0 || manifestEntry.Length > MaxEntryBytes)
            throw new InvalidDataException("The animation package manifest is invalid.");

        AnimationManifest? manifest;
        using (Stream manifestStream = manifestEntry.Open())
            manifest = JsonSerializer.Deserialize<AnimationManifest>(manifestStream, JsonOptions);
        if (manifest is null || manifest.Version is < 1 or > CurrentVersion)
            throw new InvalidDataException("The animation package version is unsupported.");
        if (manifest.Frames is not { Count: > 0 } || manifest.Frames.Count > ImageMediaDocument.MaxFrames)
            throw new InvalidDataException("The animation package frame count is invalid.");

        AnimationFrameRecord firstFrame = manifest.Frames[0];
        if (string.IsNullOrWhiteSpace(firstFrame.File)
            || firstFrame.File.Contains('\\')
            || firstFrame.File.StartsWith('/')
            || firstFrame.File.Contains("..", StringComparison.Ordinal))
        {
            throw new InvalidDataException("The animation package contains an invalid frame path.");
        }

        ZipArchiveEntry frameEntry = archive.GetEntry(firstFrame.File)
            ?? throw new InvalidDataException($"Animation frame '{firstFrame.File}' is missing.");
        if (frameEntry.Length <= 0 || frameEntry.Length > MaxEntryBytes)
            throw new InvalidDataException($"Animation frame '{firstFrame.File}' is invalid.");
        byte[] data = new byte[checked((int)frameEntry.Length)];
        using (Stream frameStream = frameEntry.Open())
            frameStream.ReadExactly(data);
        return DecodeFrame(data, firstFrame.File, manifest.Frames.Count);
    }

    public static ImageMediaMetadata ReadMetadata(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        FileInfo file = new(path);
        if (!file.Exists)
            throw new FileNotFoundException("The animation package does not exist.", path);
        if (file.Length <= 0 || file.Length > MaxPackageBytes)
            throw new InvalidDataException("The animation package is empty or too large.");

        using FileStream stream = new(file.FullName, FileMode.Open, System.IO.FileAccess.Read, FileShare.Read);
        using ZipArchive archive = new(stream, ZipArchiveMode.Read, leaveOpen: false);
        ZipArchiveEntry manifestEntry = archive.GetEntry(ManifestEntryName)
            ?? throw new InvalidDataException("The animation package manifest is missing.");
        if (manifestEntry.Length <= 0 || manifestEntry.Length > MaxEntryBytes)
            throw new InvalidDataException("The animation package manifest is invalid.");

        AnimationManifest? manifest;
        using (Stream manifestStream = manifestEntry.Open())
            manifest = JsonSerializer.Deserialize<AnimationManifest>(manifestStream, JsonOptions);
        if (manifest is null || manifest.Version is < 1 or > CurrentVersion)
            throw new InvalidDataException("The animation package version is unsupported.");
        if (manifest.Frames is not { Count: > 0 } || manifest.Frames.Count > ImageMediaDocument.MaxFrames)
            throw new InvalidDataException("The animation package frame count is invalid.");

        long totalBytes = 0;
        ZipArchiveEntry? firstFrameEntry = null;
        foreach (AnimationFrameRecord frameRecord in manifest.Frames)
        {
            if (string.IsNullOrWhiteSpace(frameRecord.File)
                || frameRecord.File.Contains('\\')
                || frameRecord.File.StartsWith('/')
                || frameRecord.File.Contains("..", StringComparison.Ordinal))
            {
                throw new InvalidDataException("The animation package contains an invalid frame path.");
            }

            ZipArchiveEntry frameEntry = archive.GetEntry(frameRecord.File)
                ?? throw new InvalidDataException($"Animation frame '{frameRecord.File}' is missing.");
            if (frameEntry.Length <= 0 || frameEntry.Length > MaxEntryBytes)
                throw new InvalidDataException($"Animation frame '{frameRecord.File}' is invalid.");
            totalBytes += frameEntry.Length;
            if (totalBytes > MaxPackageBytes)
                throw new InvalidDataException("The animation package expands beyond the supported size.");
            firstFrameEntry ??= frameEntry;
        }

        int width = manifest.Width;
        int height = manifest.Height;
        if (width <= 0 || height <= 0)
        {
            using Stream frameStream = firstFrameEntry!.Open();
            ImageMediaMetadata metadata = ImageMediaEncoding.ReadMetadata(frameStream);
            width = metadata.Width;
            height = metadata.Height;
        }
        if (width <= 0 || height <= 0)
            throw new InvalidDataException("The animation package dimensions are invalid.");
        return new ImageMediaMetadata(width, height, manifest.Frames.Count);
    }

    private static double NormalizeDuration(double duration)
    {
        return double.IsFinite(duration) ? Math.Clamp(duration, 0.02, 10.0) : 0.1;
    }

    private static Image DecodeFrame(byte[] data, string fileName, int frameCount)
    {
        using MemoryStream stream = new(data, writable: false);
        ImageMediaMetadata metadata = ImageMediaEncoding.ReadMetadata(stream);
        if ((long)metadata.Width * metadata.Height * frameCount > ImageAnimationSizing.MaxOutputPixelsAcrossFrames)
            throw new InvalidDataException("The animation package exceeds the decoded frame budget.");

        Image image = new();
        Error error = fileName.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)
            ? image.LoadWebpFromBuffer(data)
            : image.LoadPngFromBuffer(data);
        if (error != Error.Ok || image.IsEmpty())
            throw new InvalidDataException($"Animation frame '{fileName}' could not be decoded.");
        return image;
    }

    private sealed class AnimationManifest
    {
        public int Version { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public List<AnimationFrameRecord>? Frames { get; set; }
    }

    private sealed record AnimationFrameRecord(string File, double DurationSeconds);
}
