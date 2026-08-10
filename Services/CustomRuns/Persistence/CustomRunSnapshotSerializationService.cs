#nullable enable

namespace Loadout.Services.CustomRuns.Persistence;

using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using Loadout.Services.CustomRuns.Compilation;

public static class CustomRunSnapshotSerializationService
{
    public const int MaximumPayloadBytes = 1024 * 1024;

    public static string Serialize(ResolvedCustomRunSnapshot snapshot)
    {
        return JsonSerializer.Serialize(snapshot, CustomRunSerializationService.SharedJsonOptions);
    }

    public static bool TryDeserialize(
        string? payload,
        out ResolvedCustomRunSnapshot snapshot,
        out string error)
    {
        snapshot = new ResolvedCustomRunSnapshot();
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(payload))
        {
            error = "Custom Run snapshot is empty.";
            return false;
        }

        if (Encoding.UTF8.GetByteCount(payload) > MaximumPayloadBytes)
        {
            error = "Custom Run snapshot is too large.";
            return false;
        }

        try
        {
            ResolvedCustomRunSnapshot? decoded = JsonSerializer.Deserialize<ResolvedCustomRunSnapshot>(
                payload,
                CustomRunSerializationService.SharedJsonOptions);
            if (decoded is null || decoded.SchemaVersion != 1)
            {
                error = "Custom Run snapshot has an unsupported schema version.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(decoded.RunSeed)
                || string.IsNullOrWhiteSpace(decoded.SnapshotHash)
                || decoded.Players.Count == 0)
            {
                error = "Custom Run snapshot is incomplete.";
                return false;
            }

            if (decoded.RunSeed.Length > 64
                || decoded.SnapshotHash.Length != 64
                || decoded.Players.Count > 8
                || decoded.RequiredModIds.Count > 128
                || decoded.Rules.Count > 0
                || decoded.Variables.Count > 0)
            {
                error = "Custom Run snapshot exceeds supported bounds.";
                return false;
            }

            if (decoded.Players.Select(player => player.PlayerId).Distinct().Count() != decoded.Players.Count
                || decoded.Players.Any(player =>
                    string.IsNullOrWhiteSpace(player.CharacterModelId)
                    || player.CharacterModelId.Length > 256
                    || !InRange(player.PotionSlots, 0, 20)
                    || !InRange(player.StartingGold, 0, 999999)
                    || !InRange(player.StartingCurrentHp, 1, 99999)
                    || !InRange(player.StartingMaxHp, 1, 99999)
                    || !InRange(player.BaseEnergyPerTurn, 0, 99)
                    || !InRange(player.CardsDrawnPerTurn, 0, 99)))
            {
                error = "Custom Run snapshot contains an invalid player setup.";
                return false;
            }

            string computed = CustomRunHashService.Compute(decoded);
            if (!string.Equals(computed, decoded.SnapshotHash, StringComparison.Ordinal))
            {
                error = "Custom Run snapshot hash did not match.";
                return false;
            }

            snapshot = decoded;
            return true;
        }
        catch (Exception exception)
        {
            error = $"Could not read Custom Run snapshot. {exception.Message}";
            return false;
        }
    }

    private static bool InRange(int? value, int minimum, int maximum)
    {
        return !value.HasValue || value.Value >= minimum && value.Value <= maximum;
    }
}
