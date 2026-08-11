#nullable enable

namespace Loadout.Services.CustomRuns.Compilation;

using System;
using System.Security.Cryptography;
using System.Text.Json;
using Loadout.Services.CustomRuns.Models;
using Loadout.Services.CustomRuns.Persistence;

public static class RuleBehaviorHashService
{
    public static string Compute(RuleDefinition rule)
    {
        var behavior = new
        {
            rule.Trigger,
            rule.Conditions,
            rule.Actions,
            rule.Limit
        };
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
            behavior,
            CustomRunSerializationService.SharedJsonOptions);
        return Convert.ToHexString(SHA256.HashData(payload));
    }
}
