// PC 맞춤 추천: the command line of sb_framegen's bench modes and the lines it prints (docs/pc-check/CONTRACT.md,
// "Native"). Pure: no process is started here; PcCheckRunner starts the engine and hands each stdout line to Parse.
//   --bench            one {"type":"bench",...} line per quality, then {"type":"bench_done"}
//   --bench-load S     one {"type":"bench_load","sec":n,"framesPerSec":x,...} line a second, then {"type":"bench_done"}
//   failure            {"type":"error","message":"..."} (the engine's other modes say "msg"; both are read)
// Numbers are JSON numbers (invariant by definition); a number written as text, a negative time or a missing field
// makes the line Bad, and Bad lines are skipped, never guessed at.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace SystemBoost.App.Services.PcCheck;

public enum BenchLineType { Bench, BenchLoad, Done, Error, Other, Bad }

/// One stdout line of the engine. Bench: Result. BenchLoad: Sec and FramesPerSec. Error: Message. Gpu: the adapter.
public sealed record BenchLine(BenchLineType Type, FgBench? Result = null, int Sec = 0, double FramesPerSec = 0,
                               string Gpu = "", string Message = "");

public static class FgBenchProtocol
{
    /// Lowest first, as the engine's Quality enum.
    public static readonly IReadOnlyList<string> Qualities = new[] { "low", "performance", "balanced", "quality" };

    public const int MinLoadSeconds = 1, MaxLoadSeconds = 60, MaxSide = 16384;
    /// The whole --bench run is at most this long (contract); the runner waits a little more for the start-up.
    public const int BenchSeconds = 15;

    /// --bench at the display's resolution. genGpu: "same" or a value of --list-gpus (anything else is "same").
    /// parentPid 0 leaves --parent-pid out; shaderDir null or empty leaves --shader-dir out (the engine's default).
    public static IReadOnlyList<string> BenchArgs(int width, int height, string? genGpu, int parentPid, string? shaderDir = null)
    {
        var a = new List<string> { "--bench" };
        Common(a, width, height, genGpu, parentPid, shaderDir);
        return a;
    }

    /// --bench-load for `seconds` (kept within 1..60), same other arguments as BenchArgs.
    public static IReadOnlyList<string> LoadArgs(int seconds, int width, int height, string? genGpu, int parentPid, string? shaderDir = null)
    {
        var a = new List<string> { "--bench-load", Num(Math.Clamp(seconds, MinLoadSeconds, MaxLoadSeconds)) };
        Common(a, width, height, genGpu, parentPid, shaderDir);
        return a;
    }

    private static void Common(List<string> a, int width, int height, string? genGpu, int parentPid, string? shaderDir)
    {
        if (width <= 0 || height <= 0 || width > MaxSide || height > MaxSide)
            throw new ArgumentOutOfRangeException(nameof(width), $"{width}x{height}");
        a.AddRange(new[] { "--bench-width", Num(width), "--bench-height", Num(height), "--gen-gpu", GenGpu(genGpu) });
        if (parentPid > 0) a.AddRange(new[] { "--parent-pid", Num(parentPid) });
        if (!string.IsNullOrWhiteSpace(shaderDir)) a.AddRange(new[] { "--shader-dir", shaderDir });
    }

    /// "same", or the index --list-gpus printed (digits only). Anything else would be an unknown option to the engine.
    public static string GenGpu(string? value) =>
        value is { Length: > 0 and <= 3 } v && v.All(char.IsAsciiDigit) ? v : "same";

    private static string Num(int v) => v.ToString(CultureInfo.InvariantCulture);

    /// One stdout line. Never throws.
    public static BenchLine Parse(string? line)
    {
        var text = line?.Trim().TrimStart('﻿');
        if (string.IsNullOrEmpty(text) || text[0] != '{') return Bad;
        try
        {
            using var doc = JsonDocument.Parse(text);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object || !Str(r, "type", out var type)) return Bad;
            Str(r, "gpu", out var gpu);
            switch (type)
            {
                case "bench":
                    if (!Str(r, "quality", out var q) || !Qualities.Contains(q)) return Bad;
                    if (!Int(r, "width", out var w) || !Int(r, "height", out var h) || w <= 0 || h <= 0) return Bad;
                    if (!Ms(r, "analyzeMs", out var analyze) || !Ms(r, "genMs", out var gen)) return Bad;
                    return new BenchLine(BenchLineType.Bench, new FgBench(q, w, h, analyze, gen, gpu), Gpu: gpu);
                case "bench_load":
                    if (!Int(r, "sec", out var sec) || sec < 0 || !Ms(r, "framesPerSec", out var fps)) return Bad;
                    return new BenchLine(BenchLineType.BenchLoad, Sec: sec, FramesPerSec: fps, Gpu: gpu);
                case "bench_done":
                    return new BenchLine(BenchLineType.Done, Gpu: gpu);
                case "error":
                    var message = Str(r, "message", out var m) ? m : Str(r, "msg", out var m2) ? m2 : "";
                    return new BenchLine(BenchLineType.Error, Gpu: gpu, Message: message);
                default:
                    return new BenchLine(BenchLineType.Other, Gpu: gpu);   // "alive" and anything newer: not ours to judge
            }
        }
        catch (JsonException) { return Bad; }
        catch (InvalidOperationException) { return Bad; }
        catch (FormatException) { return Bad; }
    }

    private static readonly BenchLine Bad = new(BenchLineType.Bad);

    private static bool Str(JsonElement r, string key, out string value)
    {
        value = "";
        if (!r.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.String) return false;
        value = v.GetString() ?? "";
        return true;
    }

    private static bool Int(JsonElement r, string key, out int value)
    {
        value = 0;
        return r.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out value);
    }

    // a time or a rate: a finite number, not below zero
    private static bool Ms(JsonElement r, string key, out double value)
    {
        value = 0;
        if (!r.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetDouble(out value)) return false;
        return double.IsFinite(value) && value >= 0;
    }

    /// The frames per second of one --bench-load run: the median of the seconds after the first `warmup`
    /// (clocks ramp up, shaders load). Null when nothing is left after the warm-up.
    public static double? LoadFps(IReadOnlyList<double> perSecond, int warmup)
    {
        var used = perSecond.Skip(Math.Max(0, warmup)).Where(v => double.IsFinite(v) && v > 0).OrderBy(v => v).ToList();
        if (used.Count == 0) return null;
        int mid = used.Count / 2;
        return used.Count % 2 == 1 ? used[mid] : (used[mid - 1] + used[mid]) / 2;
    }
}

/// Folds the lines of one engine run. Bench results keep the first line of each quality.
public sealed class BenchCollector
{
    private readonly List<FgBench> _results = new();
    private readonly List<double> _loads = new();

    public IReadOnlyList<FgBench> Results => _results;
    /// framesPerSec of each bench_load line, in order.
    public IReadOnlyList<double> LoadFps => _loads;
    public bool Done { get; private set; }
    /// The engine's own message of an error line (English), null when none came.
    public string? Error { get; private set; }
    public string Gpu { get; private set; } = "";
    public int BadLines { get; private set; }

    public void Add(BenchLine line)
    {
        if (line.Gpu.Length > 0) Gpu = line.Gpu;
        switch (line.Type)
        {
            case BenchLineType.Bench when line.Result is { } r:
                if (!_results.Any(x => x.Quality == r.Quality)) _results.Add(r);
                break;
            case BenchLineType.BenchLoad: _loads.Add(line.FramesPerSec); break;
            case BenchLineType.Done: Done = true; break;
            case BenchLineType.Error: Error ??= line.Message.Length > 0 ? line.Message : "error"; break;
            case BenchLineType.Bad: BadLines++; break;
        }
    }

    /// The bench results lowest quality first.
    public IReadOnlyList<FgBench> Ordered() =>
        _results.OrderBy(r => FgBenchProtocol.Qualities.ToList().IndexOf(r.Quality)).ToList();
}
