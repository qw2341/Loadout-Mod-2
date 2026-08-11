#nullable enable

namespace Loadout.Services.CustomRuns.Compilation;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

public static class CustomRunRngService
{
    public static IReadOnlyList<T> OrderDeterministically<T>(
        IEnumerable<T> values,
        string seed,
        string context,
        Func<T, string> stableKey)
    {
        return values
            .Select(value => (Value: value, Score: Score(seed, context, stableKey(value))))
            .OrderBy(item => item.Score, StringComparer.Ordinal)
            .ThenBy(item => stableKey(item.Value), StringComparer.Ordinal)
            .Select(item => item.Value)
            .ToList();
    }

    private static string Score(string seed, string context, string key)
    {
        byte[] bytes = Encoding.UTF8.GetBytes($"{seed}\n{context}\n{key}");
        return Convert.ToHexString(SHA256.HashData(bytes));
    }
}
