#nullable enable

namespace Loadout.Services.CustomRuns.Runtime;

using System;
using Loadout.Services.Events;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Random;

internal static class CustomRunEventRngScope
{
    public static IDisposable Begin(long occurrence) => EventRngScope.Begin(occurrence);

    public static bool TryCreate(EventModel eventModel, out Rng mixed)
    {
        return EventRngScope.TryCreate(eventModel, out mixed);
    }
}
