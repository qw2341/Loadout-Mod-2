#nullable enable

namespace Loadout.UI.ImageEditing;

using System;
using System.Buffers.Binary;
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

public static class ImageAnimationPackage
{
    public const string Extension = ".loadoutanim";
    private const int CurrentVersion = 1;
    private const long MaxEntryBytes = 64L * 1024L * 1024L;
    private const long MaxPackageBytes = 512L * 1024L * 1024L;
    private const string ManifestEntryName = "animation.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static void Save(string path, ImageMediaDocument document)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(document);

        using FileStream stream = new(path, FileMode.Create, System.IO.FileAccess.Write, FileShare.None);
        using ZipArchive archive = new(stream, ZipArchiveMode.Create, leaveOpen: false);
        List<AnimationFrameRecord> records = new(document.Frames.Count);
        for (int i = 0; i < document.Frames.Count; i++)
        {
            string frameFile = $"frames/{i:D4}.png";
            byte[] png = document.Frames[i].Image.SavePngToBuffer();
            if (png.Length == 0)
                throw new IOException($"Animation frame {i} could not be encoded as PNG.");
            ZipArchiveEntry entry = archive.CreateEntry(frameFile, CompressionLevel.Fastest);
            using Stream entryStream = entry.Open();
            entryStream.Write(png);
            records.Add(new AnimationFrameRecord(frameFile, NormalizeDuration(document.Frames[i].DurationSeconds)));
        }

        AnimationManifest manifest = new()
        {
            Version = CurrentVersion,
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
        using ZipArchive archive = new(stream, ZipArchiveMode.Read, leaveOpen: false);
        ZipArchiveEntry manifestEntry = archive.GetEntry(ManifestEntryName)
            ?? throw new InvalidDataException("The animation package manifest is missing.");
        if (manifestEntry.Length <= 0 || manifestEntry.Length > MaxEntryBytes)
            throw new InvalidDataException("The animation package manifest is invalid.");

        AnimationManifest? manifest;
        using (Stream manifestStream = manifestEntry.Open())
            manifest = JsonSerializer.Deserialize<AnimationManifest>(manifestStream, JsonOptions);
        if (manifest is null || manifest.Version != CurrentVersion)
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

            byte[] png = new byte[checked((int)frameEntry.Length)];
            using (Stream frameStream = frameEntry.Open())
                frameStream.ReadExactly(png);
            Image image = new();
            Error error = image.LoadPngFromBuffer(png);
            if (error != Error.Ok || image.IsEmpty())
                throw new InvalidDataException($"Animation frame '{frameRecord.File}' could not be decoded.");
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
        if (manifest is null || manifest.Version != CurrentVersion)
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
        byte[] png = new byte[checked((int)frameEntry.Length)];
        using (Stream frameStream = frameEntry.Open())
            frameStream.ReadExactly(png);
        Image image = new();
        Error error = image.LoadPngFromBuffer(png);
        if (error != Error.Ok || image.IsEmpty())
            throw new InvalidDataException($"Animation frame '{firstFrame.File}' could not be decoded.");
        return image;
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
        if (manifest is null || manifest.Version != CurrentVersion)
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
            (width, height) = ReadPngDimensions(frameStream);
        }
        if (width <= 0 || height <= 0)
            throw new InvalidDataException("The animation package dimensions are invalid.");
        return new ImageMediaMetadata(width, height, manifest.Frames.Count);
    }

    private static double NormalizeDuration(double duration)
    {
        return double.IsFinite(duration) ? Math.Clamp(duration, 0.02, 10.0) : 0.1;
    }

    private static (int Width, int Height) ReadPngDimensions(Stream stream)
    {
        Span<byte> header = stackalloc byte[24];
        stream.ReadExactly(header);
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (!header[..8].SequenceEqual(signature)
            || !header.Slice(12, 4).SequenceEqual("IHDR"u8))
        {
            throw new InvalidDataException("The animation package first frame is not a valid PNG.");
        }

        int width = BinaryPrimitives.ReadInt32BigEndian(header.Slice(16, 4));
        int height = BinaryPrimitives.ReadInt32BigEndian(header.Slice(20, 4));
        return (width, height);
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
