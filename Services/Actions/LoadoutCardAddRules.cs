#nullable enable

namespace Loadout.Services.Actions;

using System;
using System.Threading;

public static class LoadoutCardAddRules
{
    private static readonly AsyncLocal<int> IgnoreHandLimitDepth = new();
    private static readonly AsyncLocal<int?> HandLimitOverride = new();

    public static bool ShouldIgnoreHandLimit => IgnoreHandLimitDepth.Value > 0;
    public static int? CurrentHandLimitOverride => HandLimitOverride.Value;

    public static IDisposable IgnoreHandLimit()
    {
        IgnoreHandLimitDepth.Value++;
        return new IgnoreHandLimitScope();
    }

    public static IDisposable OverrideHandLimit(int maxCardsInHand)
    {
        int? previous = HandLimitOverride.Value;
        HandLimitOverride.Value = maxCardsInHand;
        return new HandLimitOverrideScope(previous);
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

    private sealed class HandLimitOverrideScope(int? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            HandLimitOverride.Value = previous;
        }
    }
}
