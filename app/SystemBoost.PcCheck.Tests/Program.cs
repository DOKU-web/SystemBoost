// PC 맞춤 추천 tests. dotnet run --project app/SystemBoost.PcCheck.Tests -c Release  ->  exit code 1 when a check fails.
// AdvisorTests.cs: the rules and the CPU benchmark (advisor part). RunnerTests.cs: the bench protocol and the report
// file (runner part). Each part adds its checks in its own file only.
using System;
using System.Collections.Generic;

namespace SystemBoost.PcCheck.Tests;

internal sealed class CheckFailed : Exception
{
    public CheckFailed(string message) : base(message) { }
}

internal static class T
{
    public static int Passed, Failed;

    public static void Test(string name, Action body)
    {
        try { body(); Passed++; Console.WriteLine("ok    " + name); }
        catch (Exception e) { Failed++; Console.WriteLine("FAIL  " + name + ": " + e.Message); }
    }

    public static void Check(bool ok, string what) { if (!ok) throw new CheckFailed(what); }

    public static void Equal<TV>(TV expected, TV actual, string what)
    {
        if (!EqualityComparer<TV>.Default.Equals(expected, actual)) throw new CheckFailed($"{what}: expected {expected}, got {actual}");
    }
}

internal static class Program
{
    private static int Main()
    {
        AdvisorTests.Run();
        RunnerTests.Run();
        Console.WriteLine($"{T.Passed} passed, {T.Failed} failed");
        return T.Failed == 0 ? 0 : 1;
    }
}
