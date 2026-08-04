#nullable enable

namespace Loadout.UI.ImageEditing;

using System;
using System.Collections.Generic;
using System.IO;
using Godot;

public static class ImageMediaLoader
{
    private const long MaxInputBytes = 128L * 1024L * 1024L;

    public const string FileDialogPatterns =
        "*.png,*.jpg,*.jpeg,*.jpe,*.jfif,*.gif,*.webp,*.bmp,*.dib,*.tga,*.svg";

    public const string FileDialogMimeTypes =
        "image/png,image/jpeg,image/gif,image/webp,image/bmp,image/x-tga,image/svg+xml";

    public static Image LoadFromFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        FileInfo file = new(path);
        if (!file.Exists)
            throw new FileNotFoundException("The selected image file does not exist.", path);
        if (file.Length <= 0 || file.Length > MaxInputBytes)
            throw new InvalidDataException("The selected image file is empty or too large.");

        byte[] data = File.ReadAllBytes(file.FullName);
        string extension = file.Extension.ToLowerInvariant();
        if (extension == ".gif" || HasPrefix(data, "GIF87a") || HasPrefix(data, "GIF89a"))
            return GifFirstFrameDecoder.Decode(data);

        Image image = new();
        Error error = extension switch
        {
            ".png" => image.LoadPngFromBuffer(data),
            ".jpg" or ".jpeg" or ".jpe" or ".jfif" => image.LoadJpgFromBuffer(data),
            ".webp" => image.LoadWebpFromBuffer(data),
            ".bmp" or ".dib" => image.LoadBmpFromBuffer(data),
            ".tga" => image.LoadTgaFromBuffer(data),
            ".svg" => image.LoadSvgFromBuffer(data),
            _ => LoadBySignature(image, data)
        };

        if (error != Error.Ok)
        {
            Image fallback = Image.LoadFromFile(file.FullName);
            if (fallback is not null && !fallback.IsEmpty())
                return fallback;
            throw new InvalidDataException($"The selected image format could not be decoded ({error}).");
        }

        if (image.IsEmpty())
            throw new InvalidDataException("The selected image contains no pixel data.");
        return image;
    }

    private static Error LoadBySignature(Image image, byte[] data)
    {
        if (data.Length >= 8
            && data[0] == 0x89
            && data[1] == 0x50
            && data[2] == 0x4E
            && data[3] == 0x47)
        {
            return image.LoadPngFromBuffer(data);
        }

        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xD8)
            return image.LoadJpgFromBuffer(data);
        if (data.Length >= 12 && HasPrefix(data, "RIFF") && HasPrefix(data, "WEBP", 8))
            return image.LoadWebpFromBuffer(data);
        if (data.Length >= 2 && data[0] == (byte)'B' && data[1] == (byte)'M')
            return image.LoadBmpFromBuffer(data);

        return Error.FileUnrecognized;
    }

    private static bool HasPrefix(byte[] data, string value, int offset = 0)
    {
        if (offset < 0 || data.Length - offset < value.Length)
            return false;

        for (int i = 0; i < value.Length; i++)
        {
            if (data[offset + i] != (byte)value[i])
                return false;
        }

        return true;
    }

    private static class GifFirstFrameDecoder
    {
        private const int MaxGifPixels = 64 * 1024 * 1024;

        public static Image Decode(byte[] data)
        {
            GifReader reader = new(data);
            string signature = reader.ReadAscii(6);
            if (signature is not ("GIF87a" or "GIF89a"))
                throw new InvalidDataException("The selected file is not a valid GIF image.");

            int canvasWidth = reader.ReadUInt16();
            int canvasHeight = reader.ReadUInt16();
            ValidateDimensions(canvasWidth, canvasHeight);
            byte logicalPacked = reader.ReadByte();
            int backgroundIndex = reader.ReadByte();
            reader.ReadByte();

            Color32[]? globalColors = (logicalPacked & 0x80) != 0
                ? ReadColorTable(reader, 1 << ((logicalPacked & 0x07) + 1))
                : null;
            int? transparentIndex = null;

            while (!reader.EndOfData)
            {
                byte introducer = reader.ReadByte();
                if (introducer == 0x3B)
                    break;
                if (introducer == 0x00)
                    continue;
                if (introducer == 0x21)
                {
                    byte label = reader.ReadByte();
                    if (label == 0xF9)
                    {
                        int blockSize = reader.ReadByte();
                        if (blockSize != 4)
                            throw new InvalidDataException("The GIF graphic-control block is malformed.");
                        byte packed = reader.ReadByte();
                        reader.Skip(2);
                        int candidateTransparency = reader.ReadByte();
                        transparentIndex = (packed & 0x01) != 0 ? candidateTransparency : null;
                        if (reader.ReadByte() != 0)
                            throw new InvalidDataException("The GIF graphic-control block is unterminated.");
                    }
                    else
                    {
                        reader.SkipSubBlocks();
                    }
                    continue;
                }

                if (introducer != 0x2C)
                    throw new InvalidDataException("The GIF contains an unsupported block.");

                int frameLeft = reader.ReadUInt16();
                int frameTop = reader.ReadUInt16();
                int frameWidth = reader.ReadUInt16();
                int frameHeight = reader.ReadUInt16();
                ValidateDimensions(frameWidth, frameHeight);
                if (frameLeft + frameWidth > canvasWidth || frameTop + frameHeight > canvasHeight)
                    throw new InvalidDataException("The GIF frame lies outside its logical canvas.");

                byte imagePacked = reader.ReadByte();
                Color32[]? colors = (imagePacked & 0x80) != 0
                    ? ReadColorTable(reader, 1 << ((imagePacked & 0x07) + 1))
                    : globalColors;
                if (colors is null)
                    throw new InvalidDataException("The GIF does not contain a color table.");

                int minimumCodeSize = reader.ReadByte();
                byte[] compressed = reader.ReadSubBlocks();
                byte[] indices = DecodeLzw(compressed, minimumCodeSize, checked(frameWidth * frameHeight));
                if ((imagePacked & 0x40) != 0)
                    indices = Deinterlace(indices, frameWidth, frameHeight);

                return CreateImage(
                    canvasWidth,
                    canvasHeight,
                    frameLeft,
                    frameTop,
                    frameWidth,
                    frameHeight,
                    backgroundIndex,
                    transparentIndex,
                    globalColors,
                    colors,
                    indices);
            }

            throw new InvalidDataException("The GIF does not contain an image frame.");
        }

        private static Image CreateImage(
            int canvasWidth,
            int canvasHeight,
            int frameLeft,
            int frameTop,
            int frameWidth,
            int frameHeight,
            int backgroundIndex,
            int? transparentIndex,
            Color32[]? globalColors,
            Color32[] frameColors,
            byte[] indices)
        {
            byte[] pixels = new byte[checked(canvasWidth * canvasHeight * 4)];
            if (transparentIndex is null
                && globalColors is not null
                && backgroundIndex >= 0
                && backgroundIndex < globalColors.Length)
            {
                Color32 background = globalColors[backgroundIndex];
                for (int i = 0; i < canvasWidth * canvasHeight; i++)
                    WritePixel(pixels, i, background, 255);
            }

            for (int y = 0; y < frameHeight; y++)
            {
                for (int x = 0; x < frameWidth; x++)
                {
                    int colorIndex = indices[y * frameWidth + x];
                    if (transparentIndex == colorIndex)
                        continue;
                    if (colorIndex >= frameColors.Length)
                        throw new InvalidDataException("The GIF frame references a missing color.");

                    int canvasIndex = (frameTop + y) * canvasWidth + frameLeft + x;
                    WritePixel(pixels, canvasIndex, frameColors[colorIndex], 255);
                }
            }

            return Image.CreateFromData(canvasWidth, canvasHeight, false, Image.Format.Rgba8, pixels);
        }

        private static void WritePixel(byte[] pixels, int pixelIndex, Color32 color, byte alpha)
        {
            int offset = pixelIndex * 4;
            pixels[offset] = color.Red;
            pixels[offset + 1] = color.Green;
            pixels[offset + 2] = color.Blue;
            pixels[offset + 3] = alpha;
        }

        private static Color32[] ReadColorTable(GifReader reader, int count)
        {
            Color32[] colors = new Color32[count];
            for (int i = 0; i < count; i++)
                colors[i] = new Color32(reader.ReadByte(), reader.ReadByte(), reader.ReadByte());
            return colors;
        }

        private static byte[] DecodeLzw(byte[] data, int minimumCodeSize, int expectedPixels)
        {
            if (minimumCodeSize is < 2 or > 8)
                throw new InvalidDataException("The GIF LZW code size is invalid.");

            int clearCode = 1 << minimumCodeSize;
            int endCode = clearCode + 1;
            int nextCode = endCode + 1;
            int codeSize = minimumCodeSize + 1;
            int[] prefixes = new int[4096];
            byte[] suffixes = new byte[4096];
            byte[] stack = new byte[4097];
            for (int i = 0; i < clearCode; i++)
                suffixes[i] = (byte)i;

            GifBitReader bits = new(data);
            byte[] output = new byte[expectedPixels];
            int outputCount = 0;
            int previousCode = -1;
            int firstCharacter = 0;

            while (outputCount < expectedPixels)
            {
                int code = bits.ReadCode(codeSize);
                if (code < 0)
                    break;
                if (code == clearCode)
                {
                    codeSize = minimumCodeSize + 1;
                    nextCode = endCode + 1;
                    previousCode = -1;
                    continue;
                }
                if (code == endCode)
                    break;

                int inputCode = code;
                int stackCount = 0;
                if (previousCode < 0)
                {
                    if (code >= clearCode)
                        throw new InvalidDataException("The GIF LZW stream starts with an invalid code.");
                    output[outputCount++] = (byte)code;
                    firstCharacter = code;
                    previousCode = code;
                    continue;
                }

                if (code == nextCode)
                {
                    stack[stackCount++] = (byte)firstCharacter;
                    code = previousCode;
                }
                else if (code > nextCode)
                {
                    throw new InvalidDataException("The GIF LZW stream contains an invalid dictionary code.");
                }

                while (code >= clearCode)
                {
                    if (code >= nextCode || stackCount >= stack.Length)
                        throw new InvalidDataException("The GIF LZW dictionary is malformed.");
                    stack[stackCount++] = suffixes[code];
                    code = prefixes[code];
                }

                firstCharacter = suffixes[code];
                stack[stackCount++] = (byte)firstCharacter;
                while (stackCount > 0 && outputCount < expectedPixels)
                    output[outputCount++] = stack[--stackCount];

                if (nextCode < 4096)
                {
                    prefixes[nextCode] = previousCode;
                    suffixes[nextCode] = (byte)firstCharacter;
                    nextCode++;
                    if (nextCode == 1 << codeSize && codeSize < 12)
                        codeSize++;
                }

                previousCode = inputCode;
            }

            if (outputCount != expectedPixels)
                throw new InvalidDataException("The GIF image data ended before the first frame was complete.");
            return output;
        }

        private static byte[] Deinterlace(byte[] source, int width, int height)
        {
            byte[] result = new byte[source.Length];
            int sourceRow = 0;
            int[] starts = [0, 4, 2, 1];
            int[] steps = [8, 8, 4, 2];
            for (int pass = 0; pass < starts.Length; pass++)
            {
                for (int y = starts[pass]; y < height; y += steps[pass])
                {
                    Buffer.BlockCopy(source, sourceRow * width, result, y * width, width);
                    sourceRow++;
                }
            }
            return result;
        }

        private static void ValidateDimensions(int width, int height)
        {
            if (width <= 0 || height <= 0 || (long)width * height > MaxGifPixels)
                throw new InvalidDataException("The GIF dimensions are invalid or too large.");
        }

        private readonly record struct Color32(byte Red, byte Green, byte Blue);

        private sealed class GifReader(byte[] data)
        {
            private int _offset;

            public bool EndOfData => _offset >= data.Length;

            public byte ReadByte()
            {
                if (_offset >= data.Length)
                    throw new EndOfStreamException("The GIF ended unexpectedly.");
                return data[_offset++];
            }

            public int ReadUInt16()
            {
                int low = ReadByte();
                return low | ReadByte() << 8;
            }

            public string ReadAscii(int count)
            {
                if (count < 0 || data.Length - _offset < count)
                    throw new EndOfStreamException("The GIF ended unexpectedly.");
                string value = System.Text.Encoding.ASCII.GetString(data, _offset, count);
                _offset += count;
                return value;
            }

            public void Skip(int count)
            {
                if (count < 0 || data.Length - _offset < count)
                    throw new EndOfStreamException("The GIF ended unexpectedly.");
                _offset += count;
            }

            public byte[] ReadSubBlocks()
            {
                List<byte> bytes = [];
                while (true)
                {
                    int count = ReadByte();
                    if (count == 0)
                        return bytes.ToArray();
                    if (data.Length - _offset < count)
                        throw new EndOfStreamException("The GIF data block ended unexpectedly.");
                    for (int i = 0; i < count; i++)
                        bytes.Add(data[_offset + i]);
                    _offset += count;
                }
            }

            public void SkipSubBlocks()
            {
                while (true)
                {
                    int count = ReadByte();
                    if (count == 0)
                        return;
                    Skip(count);
                }
            }
        }

        private sealed class GifBitReader(byte[] data)
        {
            private int _bitOffset;

            public int ReadCode(int bitCount)
            {
                if (_bitOffset + bitCount > data.Length * 8)
                    return -1;

                int value = 0;
                for (int bit = 0; bit < bitCount; bit++)
                {
                    int absoluteBit = _bitOffset + bit;
                    value |= ((data[absoluteBit >> 3] >> (absoluteBit & 7)) & 1) << bit;
                }
                _bitOffset += bitCount;
                return value;
            }
        }
    }
}
