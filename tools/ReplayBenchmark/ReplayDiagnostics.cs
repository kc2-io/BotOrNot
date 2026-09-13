using System.Diagnostics;

namespace ReplayBenchmark;

public static class ReplayDiagnostics
{
    public static ReplayDiagnosticRun Diagnose(string replayPath)
    {
        var fullPath = Path.GetFullPath(replayPath);
        var file = new FileInfo(fullPath);
        if (!file.Exists)
            throw new FileNotFoundException("Replay file not found.", fullPath);

        var constructor = Measure(() => new DiagnosticReplayReader());
        var read = Measure(() => constructor.Value.ReadReplay(fullPath));
        return new ReplayDiagnosticRun(
            ReplayDiagnosticRun.CurrentSchemaVersion,
            DateTime.UtcNow,
            file.Name,
            file.Length,
            ReplayOracleService.CaptureEnvironment(),
            constructor.Metric,
            read.Metric,
            constructor.Value.Stages);
    }

    private static (T Value, DiagnosticMetric Metric) Measure<T>(Func<T> action)
    {
        var timestamp = Stopwatch.GetTimestamp();
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var value = action();
        return (value, new DiagnosticMetric(
            (Stopwatch.GetTimestamp() - timestamp) * 1000d / Stopwatch.Frequency,
            GC.GetAllocatedBytesForCurrentThread() - allocated));
    }
}
