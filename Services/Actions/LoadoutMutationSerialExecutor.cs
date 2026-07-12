#nullable enable

namespace Loadout.Services.Actions;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Helpers;

/// <summary>
/// Executes every authoritative Loadout mutation in strict FIFO order.
/// Native game commands are asynchronous and must never overlap when they
/// mutate the same run state on different peers.
/// </summary>
public static class LoadoutMutationSerialExecutor
{
    private static readonly object Gate = new();
    private static readonly Queue<QueuedMutation> Pending = new();
    private static bool _isDraining;
    private static int _generation;

    public static int PendingCount
    {
        get
        {
            lock (Gate)
                return Pending.Count + (_isDraining ? 1 : 0);
        }
    }

    public static void Enqueue(Func<Task> mutation, string description)
    {
        ArgumentNullException.ThrowIfNull(mutation);

        int generation;
        lock (Gate)
        {
            generation = _generation;
            Pending.Enqueue(new QueuedMutation(mutation, description));
            if (_isDraining)
                return;

            _isDraining = true;
        }

        _ = TaskHelper.RunSafely(DrainAsync(generation));
    }

    public static void Reset()
    {
        lock (Gate)
        {
            // Invalidate an in-flight drain as well as its pending work. A native
            // choice/reward screen cannot be forcibly cancelled here, but it must
            // never keep a later run behind the old run's executor.
            _generation++;
            Pending.Clear();
            _isDraining = false;
        }
    }

    private static async Task DrainAsync(int generation)
    {
        while (true)
        {
            QueuedMutation next;
            lock (Gate)
            {
                if (generation != _generation)
                    return;

                if (Pending.Count == 0)
                {
                    _isDraining = false;
                    return;
                }

                next = Pending.Dequeue();
            }

            try
            {
                await next.Action();
            }
            catch (Exception exception)
            {
                GD.PushWarning($"Loadout mutation '{next.Description}' failed. {exception}");
            }
        }
    }

    private readonly record struct QueuedMutation(Func<Task> Action, string Description);
}
