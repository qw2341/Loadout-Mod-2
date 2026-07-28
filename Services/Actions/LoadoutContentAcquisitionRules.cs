#nullable enable

namespace Loadout.Services.Actions;

using System;
using System.Threading;

public static class LoadoutContentAcquisitionRules
{
    private static readonly AsyncLocal<int> IgnoreModelRestrictionsDepth = new();

    public static bool ShouldIgnoreModelRestrictions => IgnoreModelRestrictionsDepth.Value > 0;

    public static IDisposable IgnoreModelRestrictions()
    {
        IgnoreModelRestrictionsDepth.Value++;
        return new IgnoreModelRestrictionsScope();
    }

    private sealed class IgnoreModelRestrictionsScope : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            IgnoreModelRestrictionsDepth.Value = Math.Max(0, IgnoreModelRestrictionsDepth.Value - 1);
        }
    }
}
