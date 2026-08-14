#nullable enable

namespace Loadout.Services.CustomRuns.Runtime;

using System;
using System.Linq;
using System.Threading;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Random;

internal static class CustomRunEventRngScope
{
    private static readonly AsyncLocal<ulong?> CurrentMixin = new();

    public static IDisposable Begin(long occurrence)
    {
        ulong? previous = CurrentMixin.Value;
        CurrentMixin.Value = unchecked((ulong)occurrence);
        return new Scope(previous);
    }

    public static bool TryCreate(EventModel eventModel, out Rng mixed)
    {
        if (CurrentMixin.Value is not { } mixin || eventModel.Owner is not { } owner)
        {
            mixed = null!;
            return false;
        }

        Player seedPlayer = owner;
        if (eventModel.IsShared && owner.RunState.Players.FirstOrDefault() is { } firstPlayer)
            seedPlayer = firstPlayer;
        mixed = new Rng(seedPlayer, eventModel.Id, mixin);
        return true;
    }

    private sealed class Scope(ulong? previous) : IDisposable
    {
        public void Dispose()
        {
            CurrentMixin.Value = previous;
        }
    }
}
