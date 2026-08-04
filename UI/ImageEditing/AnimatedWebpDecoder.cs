#nullable enable

namespace Loadout.UI.ImageEditing;

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Godot;

internal static class AnimatedWebpDecoder
{
    private const long MaxAnimationPixels = 32L * 1024L * 1024L;

    public static bool IsAnimated(byte[] data)
    {
        if (!IsWebp(data))
            return false;
        foreach (WebpChunk chunk in ReadChunks(data))
        {
            if (chunk.Type is "ANIM" or "ANMF")
                return true;
            if (chunk.Type == "VP8X" && chunk.Data.Length >= 1 && (chunk.Data[0] & 0x02) != 0)
                return true;
        }
        return false;
    }

    public static ImageMediaDocument Decode(byte[] data)
    {
        List<WebpChunk> chunks = ReadChunks(data);
        WebpChunk vp8x = chunks.Find(chunk => chunk.Type == "VP8X")
            ?? throw new InvalidDataException("The animated WebP canvas header is missing.");
        if (vp8x.Data.Length != 10)
            throw new InvalidDataException("The animated WebP canvas header is malformed.");
        int canvasWidth = ReadUInt24(vp8x.Data, 4) + 1;
        int canvasHeight = ReadUInt24(vp8x.Data, 7) + 1;
        if ((long)canvasWidth * canvasHeight > MaxAnimationPixels)
            throw new InvalidDataException("The animated WebP canvas is too large.");

        byte[] background = [0, 0, 0, 0];
        WebpChunk? animationHeader = chunks.Find(chunk => chunk.Type == "ANIM");
        if (animationHeader is { Data.Length: >= 4 })
        {
            background =
            [
                animationHeader.Data[2],
                animationHeader.Data[1],
                animationHeader.Data[0],
                animationHeader.Data[3]
            ];
        }

        byte[] canvas = new byte[checked(canvasWidth * canvasHeight * 4)];
        FillRect(canvas, canvasWidth, new Rect2I(0, 0, canvasWidth, canvasHeight), background);
        PreviousFrame? previous = null;
        List<ImageMediaFrame> frames = [];
        foreach (WebpChunk chunk in chunks)
        {
            if (chunk.Type != "ANMF")
                continue;
            if (chunk.Data.Length < 16)
                throw new InvalidDataException("An animated WebP frame header is malformed.");
            if (frames.Count >= ImageMediaDocument.MaxFrames
                || (long)(frames.Count + 1) * canvasWidth * canvasHeight > MaxAnimationPixels)
            {
                throw new InvalidDataException("The animated WebP contains too many pixels or frames.");
            }

            int x = ReadUInt24(chunk.Data, 0) * 2;
            int y = ReadUInt24(chunk.Data, 3) * 2;
            int width = ReadUInt24(chunk.Data, 6) + 1;
            int height = ReadUInt24(chunk.Data, 9) + 1;
            int durationMilliseconds = ReadUInt24(chunk.Data, 12);
            byte flags = chunk.Data[15];
            Rect2I rect = new(x, y, width, height);
            if ((long)x + width > canvasWidth || (long)y + height > canvasHeight)
                throw new InvalidDataException("An animated WebP frame lies outside its canvas.");

            ApplyPreviousDisposal(canvas, canvasWidth, background, previous);
            byte[] frameFile = BuildFrameFile(chunk.Data.AsSpan(16), width, height);
            Image frameImage = new();
            Error error = frameImage.LoadWebpFromBuffer(frameFile);
            if (error != Error.Ok || frameImage.IsEmpty())
                throw new InvalidDataException($"An animated WebP frame could not be decoded ({error}).");
            frameImage.Convert(Image.Format.Rgba8);
            if (frameImage.GetWidth() != width || frameImage.GetHeight() != height)
                throw new InvalidDataException("An animated WebP frame has inconsistent dimensions.");

            bool noBlend = (flags & 0x02) != 0;
            Composite(canvas, canvasWidth, rect, frameImage.GetData(), noBlend);
            Image frame = Image.CreateFromData(
                canvasWidth,
                canvasHeight,
                false,
                Image.Format.Rgba8,
                (byte[])canvas.Clone());
            double duration = durationMilliseconds <= 10
                ? 0.1
                : Math.Clamp(durationMilliseconds / 1000.0, 0.02, 10.0);
            frames.Add(new ImageMediaFrame(frame, duration));
            previous = new PreviousFrame((flags & 0x01) != 0, rect);
        }

        if (frames.Count == 0)
            throw new InvalidDataException("The animated WebP contains no frames.");
        return new ImageMediaDocument(frames);
    }

    private static void ApplyPreviousDisposal(
        byte[] canvas,
        int canvasWidth,
        byte[] background,
        PreviousFrame? previous)
    {
        if (previous?.DisposeToBackground == true)
            FillRect(canvas, canvasWidth, previous.Rect, background);
    }

    private static void Composite(byte[] canvas, int canvasWidth, Rect2I rect, byte[] source, bool noBlend)
    {
        for (int y = 0; y < rect.Size.Y; y++)
        {
            for (int x = 0; x < rect.Size.X; x++)
            {
                int sourceOffset = (y * rect.Size.X + x) * 4;
                int targetOffset = ((rect.Position.Y + y) * canvasWidth + rect.Position.X + x) * 4;
                if (noBlend)
                {
                    Buffer.BlockCopy(source, sourceOffset, canvas, targetOffset, 4);
                    continue;
                }

                float sourceAlpha = source[sourceOffset + 3] / 255f;
                float targetAlpha = canvas[targetOffset + 3] / 255f;
                float outputAlpha = sourceAlpha + targetAlpha * (1f - sourceAlpha);
                if (outputAlpha <= 0.0001f)
                {
                    Array.Clear(canvas, targetOffset, 4);
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

    private static void FillRect(byte[] canvas, int canvasWidth, Rect2I rect, byte[] color)
    {
        for (int y = rect.Position.Y; y < rect.End.Y; y++)
        {
            for (int x = rect.Position.X; x < rect.End.X; x++)
            {
                int offset = (y * canvasWidth + x) * 4;
                Buffer.BlockCopy(color, 0, canvas, offset, 4);
            }
        }
    }

    private static byte[] BuildFrameFile(ReadOnlySpan<byte> framePayload, int width, int height)
    {
        using MemoryStream stream = new();
        stream.Write(Encoding.ASCII.GetBytes("RIFF"));
        stream.Write([0, 0, 0, 0]);
        stream.Write(Encoding.ASCII.GetBytes("WEBP"));
        if (ContainsChunk(framePayload, "ALPH"))
        {
            byte[] extendedHeader = new byte[10];
            extendedHeader[0] = 0x10;
            WriteUInt24(extendedHeader, 4, width - 1);
            WriteUInt24(extendedHeader, 7, height - 1);
            WriteChunk(stream, "VP8X", extendedHeader);
        }
        stream.Write(framePayload);
        byte[] result = stream.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4, 4), (uint)(result.Length - 8));
        return result;
    }

    private static bool ContainsChunk(ReadOnlySpan<byte> data, string expectedType)
    {
        int offset = 0;
        while (data.Length - offset >= 8)
        {
            string type = Encoding.ASCII.GetString(data.Slice(offset, 4));
            uint rawLength = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset + 4, 4));
            if (rawLength > int.MaxValue || rawLength > data.Length - offset - 8)
                return false;
            if (type == expectedType)
                return true;
            int length = (int)rawLength;
            offset += 8 + length + (length & 1);
        }
        return false;
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        stream.Write(Encoding.ASCII.GetBytes(type));
        Span<byte> size = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)data.Length);
        stream.Write(size);
        stream.Write(data);
        if ((data.Length & 1) != 0)
            stream.WriteByte(0);
    }

    private static void WriteUInt24(byte[] data, int offset, int value)
    {
        data[offset] = (byte)value;
        data[offset + 1] = (byte)(value >> 8);
        data[offset + 2] = (byte)(value >> 16);
    }

    private static List<WebpChunk> ReadChunks(byte[] data)
    {
        if (!IsWebp(data))
            throw new InvalidDataException("The selected file is not a WebP image.");
        List<WebpChunk> chunks = [];
        int offset = 12;
        while (offset < data.Length)
        {
            if (data.Length - offset < 8)
                throw new InvalidDataException("The WebP chunk table is truncated.");
            string type = Encoding.ASCII.GetString(data, offset, 4);
            uint rawLength = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 4, 4));
            if (rawLength > int.MaxValue)
                throw new InvalidDataException("A WebP chunk is too large.");
            int length = (int)rawLength;
            if (length > data.Length - offset - 8)
                throw new InvalidDataException("A WebP chunk is truncated.");
            chunks.Add(new WebpChunk(type, data.AsSpan(offset + 8, length).ToArray()));
            offset += 8 + length + (length & 1);
        }
        return chunks;
    }

    private static bool IsWebp(byte[] data)
    {
        return data.Length >= 12
            && data[0] == 'R' && data[1] == 'I' && data[2] == 'F' && data[3] == 'F'
            && data[8] == 'W' && data[9] == 'E' && data[10] == 'B' && data[11] == 'P';
    }

    private static int ReadUInt24(byte[] data, int offset)
    {
        return data[offset] | data[offset + 1] << 8 | data[offset + 2] << 16;
    }

    private sealed record WebpChunk(string Type, byte[] Data);
    private sealed record PreviousFrame(bool DisposeToBackground, Rect2I Rect);
}
