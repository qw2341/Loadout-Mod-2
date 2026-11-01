#nullable enable

namespace Loadout.UI.ImageEditing;

using System;
using System.Buffers.Binary;
using System.IO;
using Godot;

public static class ImageMediaEncoding
{
    public static (byte[] Data, string Extension) Encode(Image image, bool optimize)
    {
        byte[] png = image.SavePngToBuffer();
        if (png.Length == 0)
            throw new IOException("The image could not be encoded as PNG.");
        if (optimize)
        {
            byte[] webp = image.SaveWebpToBuffer(lossy: false);
            if (webp.Length > 0 && webp.Length < png.Length)
                return (webp, ".webp");
        }
        return (png, ".png");
    }

    public static ImageMediaMetadata ReadMetadata(Stream stream)
    {
        Span<byte> header = stackalloc byte[12];
        stream.ReadExactly(header);
        int width;
        int height;
        if (header[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
        {
            stream.ReadExactly(header);
            if (!header[..4].SequenceEqual("IHDR"u8))
                throw new InvalidDataException("The saved image has an invalid PNG header.");
            width = BinaryPrimitives.ReadInt32BigEndian(header.Slice(4, 4));
            height = BinaryPrimitives.ReadInt32BigEndian(header.Slice(8, 4));
        }
        else if (header[..4].SequenceEqual("RIFF"u8) && header[8..12].SequenceEqual("WEBP"u8))
        {
            Span<byte> chunk = stackalloc byte[8];
            stream.ReadExactly(chunk);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);
            if (chunk[..4].SequenceEqual("VP8L"u8) && size >= 5)
            {
                stream.ReadExactly(header[..5]);
                if (header[0] != 0x2f)
                    throw new InvalidDataException("The saved image has an invalid lossless WebP header.");
                uint bits = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(1, 4));
                width = (int)(bits & 0x3fff) + 1;
                height = (int)((bits >> 14) & 0x3fff) + 1;
            }
            else if (chunk[..4].SequenceEqual("VP8X"u8) && size == 10)
            {
                stream.ReadExactly(header[..10]);
                if ((header[0] & 0x02) != 0)
                    throw new InvalidDataException("Saved animated portraits must use an animation package.");
                width = (header[4] | header[5] << 8 | header[6] << 16) + 1;
                height = (header[7] | header[8] << 8 | header[9] << 16) + 1;
            }
            else if (chunk[..4].SequenceEqual("VP8 "u8) && size >= 10)
            {
                stream.ReadExactly(header[..10]);
                if (!header.Slice(3, 3).SequenceEqual(new byte[] { 0x9d, 0x01, 0x2a }))
                    throw new InvalidDataException("The saved image has an invalid WebP header.");
                width = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(6, 2)) & 0x3fff;
                height = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(8, 2)) & 0x3fff;
            }
            else
                throw new InvalidDataException("The saved image has an unsupported WebP header.");
        }
        else
            throw new InvalidDataException("The saved image is not PNG or WebP.");

        if (width <= 0 || height <= 0)
            throw new InvalidDataException("The saved image dimensions are invalid.");
        return new ImageMediaMetadata(width, height, 1);
    }
}
