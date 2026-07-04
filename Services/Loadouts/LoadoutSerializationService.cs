#nullable enable

namespace Loadout.Services.Loadouts;

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;
using Loadout.Services.CardModification;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

public static class LoadoutSerializationService
{
    public const string ClipboardPrefix = "STS2_LOADOUT_V1:";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static JsonSerializerOptions SharedJsonOptions => JsonOptions;

    public static SavedLoadout Capture(Player player, LoadoutKind kind)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        SavedLoadout loadout = new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = BuildDefaultName(kind),
            Kind = kind,
            CreatedAtUnixSeconds = now,
            UpdatedAtUnixSeconds = now
        };

        if (kind is LoadoutKind.Cards or LoadoutKind.CardsAndRelics)
            loadout.Cards = CaptureCards(player);

        if (kind is LoadoutKind.Relics or LoadoutKind.CardsAndRelics)
            loadout.Relics = CaptureRelics(player);

        return Normalize(loadout);
    }

    public static string Encode(SavedLoadout loadout)
    {
        SavedLoadout normalized = Normalize(loadout.Clone());
        string json = JsonSerializer.Serialize(normalized, JsonOptions);
        byte[] payload = Encoding.UTF8.GetBytes(json);

        using MemoryStream output = new();
        using (GZipStream gzip = new(output, CompressionLevel.SmallestSize, leaveOpen: true))
            gzip.Write(payload, 0, payload.Length);

        return ClipboardPrefix + ToBase64Url(output.ToArray());
    }

    public static bool TryDecode(string? text, out SavedLoadout loadout, out string error)
    {
        loadout = new SavedLoadout();
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "Clipboard is empty.";
            return false;
        }

        string trimmed = text.Trim();
        if (!trimmed.StartsWith(ClipboardPrefix, StringComparison.Ordinal))
        {
            error = "Clipboard does not contain an STS2 loadout.";
            return false;
        }

        try
        {
            byte[] compressed = FromBase64Url(trimmed[ClipboardPrefix.Length..]);
            using MemoryStream input = new(compressed);
            using GZipStream gzip = new(input, CompressionMode.Decompress);
            using MemoryStream output = new();
            gzip.CopyTo(output);
            string json = Encoding.UTF8.GetString(output.ToArray());
            SavedLoadout? decoded = JsonSerializer.Deserialize<SavedLoadout>(json, JsonOptions);
            if (decoded is null)
            {
                error = "Loadout payload is empty.";
                return false;
            }

            loadout = Normalize(decoded);
            return true;
        }
        catch (Exception exception)
        {
            error = $"Could not decode loadout. {exception.Message}";
            return false;
        }
    }

    public static SavedLoadout Normalize(SavedLoadout loadout)
    {
        loadout.Id = string.IsNullOrWhiteSpace(loadout.Id)
            ? Guid.NewGuid().ToString("N")
            : loadout.Id.Trim();
        loadout.Name = string.IsNullOrWhiteSpace(loadout.Name)
            ? BuildDefaultName(loadout.Kind)
            : loadout.Name.Trim();

        if (!Enum.IsDefined(loadout.Kind))
            loadout.Kind = LoadoutKind.Cards;

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (loadout.CreatedAtUnixSeconds <= 0)
            loadout.CreatedAtUnixSeconds = now;

        if (loadout.UpdatedAtUnixSeconds <= 0)
            loadout.UpdatedAtUnixSeconds = loadout.CreatedAtUnixSeconds;

        loadout.Cards = loadout.Cards
            .Where(card => !string.IsNullOrWhiteSpace(card.ModelId))
            .Select(NormalizeCard)
            .ToList();
        loadout.Relics = loadout.Relics
            .Where(relic => !string.IsNullOrWhiteSpace(relic.ModelId))
            .Select(NormalizeRelic)
            .ToList();

        if (loadout.Kind == LoadoutKind.Cards)
            loadout.Relics.Clear();
        else if (loadout.Kind == LoadoutKind.Relics)
            loadout.Cards.Clear();

        return loadout;
    }

    public static bool ModelIdMatches(AbstractModel model, string modelId)
    {
        return string.Equals(model.Id.ToString(), modelId, StringComparison.Ordinal)
               || string.Equals(model.Id.Entry, modelId, StringComparison.OrdinalIgnoreCase);
    }

    public static CardModel? ResolveCard(string modelId)
    {
        return ModelDb.AllCards.FirstOrDefault(card => ModelIdMatches(card, modelId));
    }

    public static RelicModel? ResolveRelic(string modelId)
    {
        return ModelDb.AllRelics.FirstOrDefault(relic => ModelIdMatches(relic, modelId));
    }

    private static List<SavedCardLoadoutEntry> CaptureCards(Player player)
    {
        List<SavedCardLoadoutEntry> cards = [];
        Dictionary<string, SavedCardLoadoutEntry> compactEntries = new(StringComparer.Ordinal);

        foreach (CardModel card in player.Deck.Cards)
        {
            CardModificationState state = CardModificationStateService.GetEffectiveStateForCard(card);
            if (!state.IsEmpty)
            {
                cards.Add(new SavedCardLoadoutEntry
                {
                    ModelId = card.Id.ToString(),
                    UpgradeLevel = Math.Max(0, card.CurrentUpgradeLevel),
                    Count = 1,
                    ModificationState = state.Clone()
                });
                continue;
            }

            string key = $"{card.Id}|{Math.Max(0, card.CurrentUpgradeLevel)}";
            if (compactEntries.TryGetValue(key, out SavedCardLoadoutEntry? existing))
            {
                existing.Count++;
                continue;
            }

            SavedCardLoadoutEntry entry = new()
            {
                ModelId = card.Id.ToString(),
                UpgradeLevel = Math.Max(0, card.CurrentUpgradeLevel),
                Count = 1
            };
            compactEntries[key] = entry;
            cards.Add(entry);
        }

        return cards;
    }

    private static List<SavedRelicLoadoutEntry> CaptureRelics(Player player)
    {
        Dictionary<string, SavedRelicLoadoutEntry> entries = new(StringComparer.Ordinal);
        foreach (RelicModel relic in player.Relics)
        {
            string key = relic.Id.ToString();
            if (entries.TryGetValue(key, out SavedRelicLoadoutEntry? existing))
            {
                existing.Count++;
                continue;
            }

            entries[key] = new SavedRelicLoadoutEntry
            {
                ModelId = key,
                Count = 1
            };
        }

        return entries.Values.ToList();
    }

    private static SavedCardLoadoutEntry NormalizeCard(SavedCardLoadoutEntry card)
    {
        card.ModelId = card.ModelId.Trim();
        card.UpgradeLevel = Math.Max(0, card.UpgradeLevel);
        card.Count = Math.Max(1, card.Count);
        card.ModificationState = card.ModificationState?.Clone();
        card.ModificationState?.Normalize();
        if (card.ModificationState is not null && card.ModificationState.IsEmpty)
            card.ModificationState = null;

        if (card.ModificationState is not null)
            card.Count = 1;

        return card;
    }

    private static SavedRelicLoadoutEntry NormalizeRelic(SavedRelicLoadoutEntry relic)
    {
        relic.ModelId = relic.ModelId.Trim();
        relic.Count = Math.Max(1, relic.Count);
        return relic;
    }

    private static string BuildDefaultName(LoadoutKind kind)
    {
        string label = kind switch
        {
            LoadoutKind.Relics => "Relic Loadout",
            LoadoutKind.CardsAndRelics => "Deck + Relics",
            _ => "Deck Loadout"
        };
        return $"{label} {DateTime.Now:HHmm}";
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
