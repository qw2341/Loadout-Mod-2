#nullable enable

namespace Loadout.Services.Actions;

using System;
using System.Threading;

public static class LoadoutCardAddRules
{
    private static readonly AsyncLocal<int> IgnoreHandLimitDepth = new();

    public static bool ShouldIgnoreHandLimit => IgnoreHandLimitDepth.Value > 0;

    public static IDisposable IgnoreHandLimit()
    {
        IgnoreHandLimitDepth.Value++;
        return new IgnoreHandLimitScope();
    }

    private sealed class IgnoreHandLimitScope : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            IgnoreHandLimitDepth.Value = Math.Max(0, IgnoreHandLimitDepth.Value - 1);
        }
    }

}
