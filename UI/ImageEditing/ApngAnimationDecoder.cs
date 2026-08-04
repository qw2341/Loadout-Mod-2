#nullable enable

namespace Loadout.UI.ImageEditing;

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Godot;

internal static class ApngAnimationDecoder
{
    private const long MaxAnimationPixels = 32L * 1024L * 1024L;
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];

    public static bool IsAnimated(byte[] data)
    {
        if (data.Length < PngSignature.Length || !data.AsSpan(0, PngSignature.Length).SequenceEqual(PngSignature))
            return false;
        foreach (PngChunk chunk in ReadChunks(data))
        {
            if (chunk.Type == "acTL")
                return true;
            if (chunk.Type == "IDAT")
                return false;
        }
        return false;
    }

    public static ImageMediaDocument Decode(byte[] data)
    {
        List<PngChunk> chunks = ReadChunks(data);
        PngChunk ihdr = chunks.Find(chunk => chunk.Type == "IHDR")
            ?? throw new InvalidDataException("The APNG header is missing.");
        if (ihdr.Data.Length != 13)
            throw new InvalidDataException("The APNG header is malformed.");
        int canvasWidth = ReadPositiveInt32(ihdr.Data, 0);
        int canvasHeight = ReadPositiveInt32(ihdr.Data, 4);
        if ((long)canvasWidth * canvasHeight > MaxAnimationPixels)
            throw new InvalidDataException("The APNG canvas is too large.");

        List<PngChunk> sharedChunks = [];
        List<ImageMediaFrame> frames = [];
        List<byte[]> frameData = [];
        FrameControl? control = null;
        byte[] canvas = new byte[checked(canvasWidth * canvasHeight * 4)];
        PreviousFrame? previous = null;
        bool sawImageData = false;

        foreach (PngChunk chunk in chunks)
        {
            switch (chunk.Type)
            {
                case "IHDR":
                case "acTL":
                    break;
                case "fcTL":
                    FinalizeFrame();
                    control = ParseFrameControl(chunk.Data, canvasWidth, canvasHeight);
                    break;
                case "IDAT":
                    if (control is not null)
                        frameData.Add(chunk.Data);
                    sawImageData = true;
                    break;
                case "fdAT":
                    if (control is null || chunk.Data.Length < 4)
                        throw new InvalidDataException("The APNG frame data is malformed.");
                    frameData.Add(chunk.Data[4..]);
                    sawImageData = true;
                    break;
                case "IEND":
                    FinalizeFrame();
                    break;
                default:
                    if (!sawImageData && chunk.Type is not ("fcTL" or "fdAT"))
                        sharedChunks.Add(chunk);
                    break;
            }
        }

        if (frames.Count == 0)
            throw new InvalidDataException("The APNG contains no animation frames.");
        return new ImageMediaDocument(frames);

        void FinalizeFrame()
        {
            if (control is null || frameData.Count == 0)
                return;
            if (frames.Count >= ImageMediaDocument.MaxFrames
                || (long)(frames.Count + 1) * canvasWidth * canvasHeight > MaxAnimationPixels)
            {
                throw new InvalidDataException("The APNG contains too many pixels or animation frames.");
            }

            ApplyPreviousDisposal(canvas, canvasWidth, previous);
            byte[]? restoreCanvas = control.DisposeOperation == 2 ? (byte[])canvas.Clone() : null;
            Image subframe = DecodeSubframe(control, ihdr.Data, sharedChunks, frameData);
            Composite(canvas, canvasWidth, control, subframe.GetData());
            Image frame = Image.CreateFromData(
                canvasWidth,
                canvasHeight,
                false,
                Image.Format.Rgba8,
                (byte[])canvas.Clone());
            double denominator = control.DelayDenominator == 0 ? 100.0 : control.DelayDenominator;
            double duration = Math.Clamp(control.DelayNumerator / denominator, 0.02, 10.0);
            frames.Add(new ImageMediaFrame(frame, duration));
            previous = new PreviousFrame(control.DisposeOperation, control.Rect, restoreCanvas);
            frameData.Clear();
            control = null;
        }
    }

    private static Image DecodeSubframe(
        FrameControl control,
        byte[] originalHeader,
        IReadOnlyList<PngChunk> sharedChunks,
        IReadOnlyList<byte[]> frameData)
    {
        byte[] header = (byte[])originalHeader.Clone();
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), (uint)control.Rect.Size.X);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4, 4), (uint)control.Rect.Size.Y);
        using MemoryStream png = new();
        png.Write(PngSignature);
        WriteChunk(png, "IHDR", header);
        foreach (PngChunk chunk in sharedChunks)
            WriteChunk(png, chunk.Type, chunk.Data);
        foreach (byte[] compressed in frameData)
            WriteChunk(png, "IDAT", compressed);
        WriteChunk(png, "IEND", []);

        Image image = new();
        Error error = image.LoadPngFromBuffer(png.ToArray());
        if (error != Error.Ok || image.IsEmpty())
            throw new InvalidDataException($"An APNG frame could not be decoded ({error}).");
        image.Convert(Image.Format.Rgba8);
        return image;
    }

    private static void Composite(byte[] canvas, int canvasWidth, FrameControl control, byte[] source)
    {
        int frameWidth = control.Rect.Size.X;
        int frameHeight = control.Rect.Size.Y;
        for (int y = 0; y < frameHeight; y++)
        {
            for (int x = 0; x < frameWidth; x++)
            {
                int sourceOffset = (y * frameWidth + x) * 4;
                int targetOffset = ((control.Rect.Position.Y + y) * canvasWidth + control.Rect.Position.X + x) * 4;
                if (control.BlendOperation == 0)
                {
                    Buffer.BlockCopy(source, sourceOffset, canvas, targetOffset, 4);
                    continue;
                }

                float sourceAlpha = source[sourceOffset + 3] / 255f;
                float targetAlpha = canvas[targetOffset + 3] / 255f;
                float outputAlpha = sourceAlpha + targetAlpha * (1f - sourceAlpha);
                if (outputAlpha <= 0.0001f)
                {
                    canvas[targetOffset] = 0;
                    canvas[targetOffset + 1] = 0;
                    canvas[targetOffset + 2] = 0;
                    canvas[targetOffset + 3] = 0;
                    continue;
                }

                for (int channel = 0; channel < 3; channel++)
                {
                    float sourceColor = source[sourceOffset + channel] / 255f;
                    float targetColor = canvas[targetOffset + channel] / 255f;
                    float outputColor = (sourceColor * sourceAlpha
                        + targetColor * targetAlpha * (1f - sourceAlpha)) / outputAlpha;
                    canvas[targetOffset + channel] = (byte)Math.Clamp(Math.Round(outputColor * 255f), 0, 255);
                }
                canvas[targetOffset + 3] = (byte)Math.Clamp(Math.Round(outputAlpha * 255f), 0, 255);
            }
        }
    }

    private static void ApplyPreviousDisposal(byte[] canvas, int canvasWidth, PreviousFrame? previous)
    {
        if (previous is null || previous.DisposeOperation == 0)
            return;
        if (previous.DisposeOperation == 2 && previous.RestoreCanvas is not null)
        {
            Buffer.BlockCopy(previous.RestoreCanvas, 0, canvas, 0, canvas.Length);
            return;
        }
        if (previous.DisposeOperation != 1)
            return;

        for (int y = previous.Rect.Position.Y; y < previous.Rect.End.Y; y++)
        {
            int offset = (y * canvasWidth + previous.Rect.Position.X) * 4;
            Array.Clear(canvas, offset, previous.Rect.Size.X * 4);
        }
    }

    private static FrameControl ParseFrameControl(byte[] data, int canvasWidth, int canvasHeight)
    {
        if (data.Length != 26)
            throw new InvalidDataException("An APNG frame-control chunk is malformed.");
        int width = ReadPositiveInt32(data, 4);
        int height = ReadPositiveInt32(data, 8);
        int x = ReadNonNegativeInt32(data, 12);
        int y = ReadNonNegativeInt32(data, 16);
        if ((long)x + width > canvasWidth || (long)y + height > canvasHeight)
            throw new InvalidDataException("An APNG frame lies outside its canvas.");
        ushort delayNumerator = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(20, 2));
        ushort delayDenominator = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(22, 2));
        byte dispose = data[24];
        byte blend = data[25];
        if (dispose > 2 || blend > 1)
            throw new InvalidDataException("An APNG frame uses an invalid operation.");
        return new FrameControl(
            new Rect2I(x, y, width, height),
            delayNumerator,
            delayDenominator,
            dispose,
            blend);
    }

    private static List<PngChunk> ReadChunks(byte[] data)
    {
        if (data.Length < PngSignature.Length || !data.AsSpan(0, PngSignature.Length).SequenceEqual(PngSignature))
            throw new InvalidDataException("The selected file is not a PNG image.");
        List<PngChunk> chunks = [];
        int offset = PngSignature.Length;
        while (offset < data.Length)
        {
            if (data.Length - offset < 12)
                throw new InvalidDataException("The PNG chunk table is truncated.");
            uint rawLength = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));
            if (rawLength > int.MaxValue)
                throw new InvalidDataException("A PNG chunk is too large.");
            int length = (int)rawLength;
            if (length > data.Length - offset - 12)
                throw new InvalidDataException("A PNG chunk is truncated.");
            string type = Encoding.ASCII.GetString(data, offset + 4, 4);
            byte[] chunkData = data.AsSpan(offset + 8, length).ToArray();
            chunks.Add(new PngChunk(type, chunkData));
            offset += length + 12;
            if (type == "IEND")
                break;
        }
        return chunks;
    }

    private static int ReadPositiveInt32(byte[] data, int offset)
    {
        int value = ReadNonNegativeInt32(data, offset);
        return value > 0 ? value : throw new InvalidDataException("An APNG dimension is invalid.");
    }

    private static int ReadNonNegativeInt32(byte[] data, int offset)
    {
        uint value = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));
        return value <= int.MaxValue
            ? (int)value
            : throw new InvalidDataException("An APNG coordinate is too large.");
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        stream.Write(length);
        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);
        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, CalculateCrc(typeBytes, data));
        stream.Write(crc);
    }

    private static uint CalculateCrc(byte[] type, byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte value in type)
            crc = UpdateCrc(crc, value);
        foreach (byte value in data)
            crc = UpdateCrc(crc, value);
        return crc ^ 0xFFFFFFFF;
    }

    private static uint UpdateCrc(uint crc, byte value)
    {
        crc ^= value;
        for (int i = 0; i < 8; i++)
            crc = (crc & 1) != 0 ? 0xEDB88320 ^ crc >> 1 : crc >> 1;
        return crc;
    }

    private sealed record PngChunk(string Type, byte[] Data);

    private sealed record FrameControl(
        Rect2I Rect,
        ushort DelayNumerator,
        ushort DelayDenominator,
        byte DisposeOperation,
        byte BlendOperation);

    private sealed record PreviousFrame(byte DisposeOperation, Rect2I Rect, byte[]? RestoreCanvas);
}
