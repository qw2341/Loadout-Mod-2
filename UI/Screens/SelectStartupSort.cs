#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Loadout.UI.Screens;

public sealed class SelectStartupSort
{
    private readonly int[] _originalIndices;
    private readonly string[] _modIds;
    private readonly Comparison<int>[] _comparisons;
    private Exception? _error;

    public SelectStartupSort(int[] originalIndices, string[] modIds, Comparison<int>[] comparisons)
    {
        if (originalIndices.Length != modIds.Length)
            throw new ArgumentException("Every sort entry must have a mod ID.");

        _originalIndices = originalIndices;
        _modIds = modIds;
        _comparisons = comparisons;
        Order = Enumerable.Range(0, originalIndices.Length).ToArray();
    }

    public int[] Order { get; private set; }
    public Exception? Error => _error;
    public static int WorkerLimit => Math.Clamp(Environment.ProcessorCount - 1, 1, 4);

    // Comparisons must close over captured values, never live models or nodes.
    public static Task PrepareAsync(IReadOnlyList<SelectStartupSort> sorts, CancellationToken token)
    {
        return Task.Run(() =>
        {
            Dictionary<string, List<(SelectStartupSort Sort, int[] Indices)>> jobs = new(StringComparer.Ordinal);
            Dictionary<SelectStartupSort, List<int[]>> partitions = new();
            foreach (SelectStartupSort sort in sorts)
            {
                token.ThrowIfCancellationRequested();
                List<int[]> runs = new();
                partitions.Add(sort, runs);
                foreach (IGrouping<string, int> group in sort.Order.GroupBy(index => sort._modIds[index], StringComparer.Ordinal))
                {
                    int[] indices = group.ToArray();
                    runs.Add(indices);
                    if (!jobs.TryGetValue(group.Key, out var work))
                        jobs.Add(group.Key, work = new());
                    work.Add((sort, indices));
                }
            }

            Parallel.ForEach(jobs.Values, new ParallelOptions
            {
                CancellationToken = token,
                MaxDegreeOfParallelism = WorkerLimit
            }, work =>
            {
                foreach (var (sort, indices) in work)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        Array.Sort(indices, sort.Compare);
                    }
                    catch (Exception exception)
                    {
                        Interlocked.CompareExchange(ref sort._error, exception, null);
                    }
                }
            });

            foreach (var (sort, runs) in partitions)
            {
                token.ThrowIfCancellationRequested();
                if (sort.Error is not null)
                    continue;
                try
                {
                    PriorityQueue<(int Run, int Offset), int> queue = new(Comparer<int>.Create(sort.Compare));
                    for (int i = 0; i < runs.Count; i++)
                        queue.Enqueue((i, 0), runs[i][0]);

                    int[] merged = new int[sort.Order.Length];
                    int output = 0;
                    while (queue.TryDequeue(out var cursor, out int index))
                    {
                        token.ThrowIfCancellationRequested();
                        merged[output++] = index;
                        int next = cursor.Offset + 1;
                        if (next < runs[cursor.Run].Length)
                            queue.Enqueue((cursor.Run, next), runs[cursor.Run][next]);
                    }
                    sort.Order = merged;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    sort._error = exception;
                }
            }
        }, token);
    }

    private int Compare(int left, int right)
    {
        foreach (Comparison<int> comparison in _comparisons)
        {
            int result = comparison(left, right);
            if (result != 0)
                return result;
        }
        return _originalIndices[left].CompareTo(_originalIndices[right]);
    }
}
