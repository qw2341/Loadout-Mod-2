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

        lock (Gate)
        {
            Pending.Enqueue(new QueuedMutation(mutation, description));
            if (_isDraining)
                return;

            _isDraining = true;
        }

        TaskHelper.RunSafely(DrainAsync());
    }

    public static void Reset()
    {
        lock (Gate)
        {
            // Do not mark the executor idle while an operation is still awaiting.
            // New-run work must remain behind that in-flight native command rather
            // than starting a second drain concurrently.
            Pending.Clear();
        }
    }

    private static async Task DrainAsync()
    {
        while (true)
        {
            QueuedMutation next;
            lock (Gate)
            {
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
