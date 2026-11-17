// PC 맞춤 추천: a short CPU benchmark in plain .NET (no game, nothing written). Owner: the advisor part (CONTRACT.md).
// The work is fixed and deterministic: a xorshift sequence, lookups in a 32 KB table of the thread's own (memory, not
// files), integer mixing and a little floating point. A score is work units per second, summed over the threads;
// each side starts with a short warm-up that is not counted. Single thread: one thread. All threads: one thread per
// logical processor. A side's result is the median of its runs, its noise the spread (max - min) / median.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace SystemBoost.App.Services.PcCheck;

public static class CpuBenchmark
{
    internal const int TableSize = 4096, UnitSteps = 4096;
    private static long _sink;   // keeps the work from being optimised away

    /// Single-thread and all-thread scores: `repeats` runs of `each` per side, the median, and the spread as NoisePercent
    /// (the larger spread of the two sides). HighPlan* stay null (the runner measures the high-performance plan with
    /// MultiAsync). Cancels with ct (OperationCanceledException).
    public static Task<CpuBench> RunAsync(TimeSpan each, int repeats, CancellationToken ct) =>
        Task.Factory.StartNew(() =>
        {
            var single = Side(1, each, repeats, ct);
            var multi = Side(Environment.ProcessorCount, each, repeats, ct);
            return new CpuBench(single.Score, multi.Score, Math.Max(single.NoisePercent, multi.NoisePercent));
        }, ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    /// The all-thread score alone (median of `repeats` runs of `each`) and its spread in percent.
    public static Task<(double Score, double NoisePercent)> MultiAsync(TimeSpan each, int repeats, CancellationToken ct) =>
        Task.Factory.StartNew(() => Side(Environment.ProcessorCount, each, repeats, ct),
                              ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private static (double Score, double NoisePercent) Side(int threads, TimeSpan each, int repeats, CancellationToken ct)
    {
        repeats = Math.Max(1, repeats);
        if (each <= TimeSpan.Zero) each = TimeSpan.FromMilliseconds(1);
        Measure(threads, TimeSpan.FromTicks(Math.Min(each.Ticks / 4 + 1, TimeSpan.FromMilliseconds(250).Ticks)), ct);   // warm-up
        var scores = new List<double>();
        for (int i = 0; i < repeats; i++) scores.Add(Measure(threads, each, ct));
        return (Median(scores), SpreadPercent(scores));
    }

    /// One run: work units per second, summed over the threads.
    private static double Measure(int threads, TimeSpan each, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        threads = Math.Max(1, threads);
        long length = Math.Max(1, (long)(each.TotalSeconds * Stopwatch.Frequency));
        var units = new long[threads];
        var seconds = new double[threads];
        using var go = new ManualResetEventSlim(false);
        var workers = new Thread[threads];
        for (int t = 0; t < threads; t++)
        {
            int id = t;
            workers[t] = new Thread(() => Work(id, length, go, units, seconds, ct)) { IsBackground = true, Name = "SystemBoost CPU check" };
            workers[t].Start();
        }
        go.Set();
        foreach (var w in workers) w.Join();
        ct.ThrowIfCancellationRequested();
        double score = 0;
        for (int t = 0; t < threads; t++) if (seconds[t] > 0) score += units[t] / seconds[t];
        return score;
    }

    private static void Work(int id, long length, ManualResetEventSlim go, long[] units, double[] seconds, CancellationToken ct)
    {
        try
        {
            var table = Table((ulong)id + 1);
            ulong seed = 0x9E3779B97F4A7C15UL * ((ulong)id + 1);
            long n = 0, sink = 0;
            go.Wait();
            long start = Stopwatch.GetTimestamp(), now = start, end = start + length;
            while (!ct.IsCancellationRequested)
            {
                sink ^= (long)Unit(seed + (ulong)n, table);
                n++;
                now = Stopwatch.GetTimestamp();
                if (now >= end) break;
            }
            units[id] = n;
            seconds[id] = (now - start) / (double)Stopwatch.Frequency;
            Interlocked.Add(ref _sink, sink);
        }
        catch (Exception) { units[id] = 0; seconds[id] = 0; }   // a thread of its own: never let it end the app
    }

    /// The table a thread reads: filled from its own seed, the same every time.
    internal static ulong[] Table(ulong seed)
    {
        var t = new ulong[TableSize];
        ulong x = seed * 0xD1B54A32D192ED03UL | 1;
        for (int i = 0; i < t.Length; i++) { x ^= x << 13; x ^= x >> 7; x ^= x << 17; t[i] = x; }
        return t;
    }

    /// One work unit. The same seed and table always give the same number.
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    internal static ulong Unit(ulong seed, ulong[] table)
    {
        ulong x = seed + 0x9E3779B97F4A7C15UL, acc = 0;   // splitmix: every seed its own start, never 0
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
        x ^= x >> 31;
        if (x == 0) x = 1;
        double f = 1.0;
        for (int i = 0; i < UnitSteps; i++)
        {
            x ^= x << 13; x ^= x >> 7; x ^= x << 17;
            ulong v = table[(int)(x & (TableSize - 1))];
            acc = (acc ^ v) * 0x9E3779B97F4A7C15UL + (acc >> 29);
            if ((i & 15) == 0) f = f * 0.999 + Math.Sqrt((x >> 11) * 1e-9);
        }
        return acc ^ (ulong)(long)f;
    }

    internal static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return 0;
        var s = values.OrderBy(v => v).ToArray();
        return s.Length % 2 == 1 ? s[s.Length / 2] : (s[s.Length / 2 - 1] + s[s.Length / 2]) / 2;
    }

    /// (max - min) / median in percent; 0 for a single run.
    internal static double SpreadPercent(IReadOnlyList<double> values)
    {
        double median = Median(values);
        return values.Count < 2 || median <= 0 ? 0 : (values.Max() - values.Min()) / median * 100;
    }
}
