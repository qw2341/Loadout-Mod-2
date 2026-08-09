#nullable enable

namespace Loadout.Services.CustomRuns.Compilation;

using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Loadout.Services.CustomRuns.Persistence;

public static class CustomRunHashService
{
    public static string Compute(ResolvedCustomRunSnapshot snapshot)
    {
        ResolvedCustomRunSnapshot canonical = new()
        {
            SchemaVersion = snapshot.SchemaVersion,
            SourceDefinitionId = snapshot.SourceDefinitionId,
            RunSeed = snapshot.RunSeed,
            Players = snapshot.Players.OrderBy(player => player.PlayerId).ToList(),
            Rules = snapshot.Rules.ToList(),
            Variables = snapshot.Variables.OrderBy(variable => variable.Id, StringComparer.Ordinal).ToList(),
            RequiredModIds = snapshot.RequiredModIds.OrderBy(id => id, StringComparer.Ordinal).ToList(),
            SnapshotHash = string.Empty
        };
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
            canonical,
            CustomRunSerializationService.SharedJsonOptions);
        return Convert.ToHexString(SHA256.HashData(payload));
    }
}
