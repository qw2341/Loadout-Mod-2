#nullable enable

namespace Loadout.Services.CustomRuns.Persistence;

using System;
using System.Buffers;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Loadout.Services.CustomRuns.Models;

public static class CustomRunSerializationService
{
    public const string SharePrefix = "L2CR1:";
    public const int MaximumCompressedBytes = 2 * 1024 * 1024;
    public const int MaximumDecompressedBytes = 8 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static JsonSerializerOptions SharedJsonOptions => JsonOptions;

    public static string Serialize(CustomRunDefinition definition)
    {
        CustomRunDefinition normalized = CustomRunNormalizationService.Normalize(
            CustomRunNormalizationService.Clone(definition));
        return JsonSerializer.Serialize(normalized, JsonOptions);
    }

    public static bool TryDeserialize(string? json, out CustomRunDefinition definition, out string error)
    {
        definition = new CustomRunDefinition();
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(json))
        {
            error = "Custom Run payload is empty.";
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("schemaVersion", out JsonElement schemaElement)
                || !schemaElement.TryGetInt32(out int schemaVersion))
            {
                error = "Custom Run schema version is missing.";
                return false;
            }

            if (!CustomRunMigrationService.TryMigrate(schemaVersion, json, out string migratedJson, out error))
                return false;

            CustomRunDefinition? decoded = JsonSerializer.Deserialize<CustomRunDefinition>(migratedJson, JsonOptions);
            if (decoded is null)
            {
                error = "Custom Run payload did not contain a definition.";
                return false;
            }

            definition = CustomRunNormalizationService.Normalize(decoded);
            return true;
        }
        catch (Exception exception)
        {
            error = $"Could not read Custom Run data. {exception.Message}";
            return false;
        }
    }

    public static string Encode(CustomRunDefinition definition)
    {
        byte[] payload = Encoding.UTF8.GetBytes(Serialize(definition));
        using MemoryStream output = new();
        using (BrotliStream brotli = new(output, CompressionLevel.SmallestSize, leaveOpen: true))
            brotli.Write(payload, 0, payload.Length);

        return SharePrefix + ToBase64Url(output.ToArray());
    }

    public static bool TryDecode(string? text, out CustomRunDefinition definition, out string error)
    {
        definition = new CustomRunDefinition();
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "Clipboard is empty.";
            return false;
        }

        string trimmed = text.Trim();
        if (!trimmed.StartsWith(SharePrefix, StringComparison.Ordinal))
        {
            error = "Clipboard does not contain an L2CR1 Custom Run.";
            return false;
        }

        try
        {
            string encoded = trimmed[SharePrefix.Length..];
            if (encoded.Length == 0 || encoded.Length > MaximumCompressedBytes * 2)
            {
                error = "Custom Run share payload is too large.";
                return false;
            }

            byte[] compressed = FromBase64Url(encoded);
            if (compressed.Length > MaximumCompressedBytes)
            {
                error = "Custom Run share payload is too large.";
                return false;
            }

            using MemoryStream input = new(compressed, writable: false);
            using BrotliStream brotli = new(input, CompressionMode.Decompress);
            using MemoryStream output = new();
            byte[] buffer = ArrayPool<byte>.Shared.Rent(81920);
            try
            {
                int total = 0;
                int read;
                while ((read = brotli.Read(buffer, 0, buffer.Length)) > 0)
                {
                    total += read;
                    if (total > MaximumDecompressedBytes)
                    {
                        error = "Custom Run decompressed payload is too large.";
                        return false;
                    }
                    output.Write(buffer, 0, read);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            return TryDeserialize(Encoding.UTF8.GetString(output.ToArray()), out definition, out error);
        }
        catch (Exception exception)
        {
            error = $"Could not decode Custom Run. {exception.Message}";
            return false;
        }
    }

    private static string ToBase64Url(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static byte[] FromBase64Url(string text)
    {
        string base64 = text.Replace('-', '+').Replace('_', '/');
        int padding = (4 - base64.Length % 4) % 4;
        if (padding > 0)
            base64 = base64.PadRight(base64.Length + padding, '=');
        return Convert.FromBase64String(base64);
    }
}
