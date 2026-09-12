using ReplayBenchmark;

return await ReplayBenchmarkProgram.RunAsync(args);

internal static class ReplayBenchmarkProgram
{
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args.Length == 0)
                return Usage();
            return args[0].ToLowerInvariant() switch
            {
                "freeze" => await FreezeAsync(args),
                "validate" => await ValidateAsync(args),
                "run" => await RunProfileAsync(args),
                "compare" => await CompareAsync(args),
                "refresh-environment" => await RefreshEnvironmentAsync(args),
                "diagnose" => await DiagnoseAsync(args),
                _ => Usage()
            };
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"ReplayBenchmark failed: {exception.Message}");
            return 2;
        }
    }

    private static int Usage()
    {
        Console.Error.WriteLine("Usage:");
        Console.Error.WriteLine("  ReplayBenchmark freeze <replay-dir> <manifest.json> [--before <UTC ISO-8601>]");
        Console.Error.WriteLine("  ReplayBenchmark validate <manifest.json> <replay-dir> [--allow-extra-replay-files]");
        Console.Error.WriteLine("  ReplayBenchmark run <normal|summary> <manifest.json> <replay-dir> <output.json> [concurrency] [--allow-extra-replay-files]");
        Console.Error.WriteLine("  ReplayBenchmark compare <normal.json> <summary.json> <deltas.json>");
        Console.Error.WriteLine("  ReplayBenchmark refresh-environment <run.json>");
        Console.Error.WriteLine("  ReplayBenchmark diagnose <replay-path> <diagnostic.json>");
        return 1;
    }

    private static async Task<int> FreezeAsync(string[] args)
    {
        if (args.Length is not 3 and not 5 || args.Length == 5 && !string.Equals(args[3], "--before", StringComparison.Ordinal))
            return Usage();
        DateTime? cutoff = args.Length == 5
            ? DateTime.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal)
            : null;
        var manifest = await ReplayManifestService.FreezeAsync(args[1], cutoff);
        await OracleJson.WriteAsync(args[2], manifest);
        Console.WriteLine($"Frozen {manifest.Entries.Count} replay files: {ReplayManifestService.Fingerprint(manifest)}");
        return 0;
    }

    private static async Task<int> ValidateAsync(string[] args)
    {
        if (args.Length is not 3 and not 4 || args.Length == 4 &&
            !string.Equals(args[3], "--allow-extra-replay-files", StringComparison.Ordinal))
            return Usage();
        var manifest = await OracleJson.ReadAsync<ReplayManifest>(args[1]);
        var problems = await ReplayManifestService.ValidateAsync(manifest, args[2], args.Length == 4);
        if (problems.Count == 0)
        {
            Console.WriteLine($"Valid: {manifest.Entries.Count} replay files ({ReplayManifestService.Fingerprint(manifest)})");
            return 0;
        }
        foreach (var problem in problems)
            Console.Error.WriteLine(problem);
        return 3;
    }

    private static async Task<int> RunProfileAsync(string[] args)
    {
        if (args.Length is < 5 or > 7) return Usage();
        var allowExtra = args.Contains("--allow-extra-replay-files", StringComparer.Ordinal);
        var remaining = args.Skip(5).Where(argument => argument != "--allow-extra-replay-files").ToArray();
        if (remaining.Length > 1 || args.Skip(5).Any(argument => argument != "--allow-extra-replay-files" &&
            !int.TryParse(argument, System.Globalization.CultureInfo.InvariantCulture, out _)))
            return Usage();
        var concurrency = remaining.Length == 1 ? int.Parse(remaining[0], System.Globalization.CultureInfo.InvariantCulture) : 2;
        var manifest = await OracleJson.ReadAsync<ReplayManifest>(args[2]);
        var run = await ReplayOracleService.RunAsync(manifest, args[3], args[1], concurrency, allowExtra);
        await OracleJson.WriteAsync(args[4], run);
        Console.WriteLine($"{run.Profile}: {run.Metrics.SuccessCount} loaded, {run.Metrics.FailureCount} failed, " +
            $"{run.Metrics.WallMilliseconds:F0} ms, {run.SummaryFingerprint}");
        return run.Failures.Count == 0 ? 0 : 4;
    }

    private static async Task<int> CompareAsync(string[] args)
    {
        if (args.Length != 4) return Usage();
        var expected = await OracleJson.ReadAsync<ReplayOracleRun>(args[1]);
        var actual = await OracleJson.ReadAsync<ReplayOracleRun>(args[2]);
        var comparison = ReplayComparisonService.Compare(expected, actual);
        await OracleJson.WriteAsync(args[3], comparison);
        Console.WriteLine(comparison.IsExactMatch
            ? "Exact summary match."
            : $"Summary mismatch: {comparison.Deltas.Count} delta(s).");
        return comparison.IsExactMatch ? 0 : 5;
    }

    private static async Task<int> RefreshEnvironmentAsync(string[] args)
    {
        if (args.Length != 2) return Usage();
        var run = await OracleJson.ReadAsync<ReplayOracleRun>(args[1]);
        await OracleJson.WriteAsync(args[1], ReplayOracleService.RefreshEnvironment(run));
        return 0;
    }

    private static async Task<int> DiagnoseAsync(string[] args)
    {
        if (args.Length != 3) return Usage();
        var diagnostic = ReplayDiagnostics.Diagnose(args[1]);
        await OracleJson.WriteAsync(args[2], diagnostic);
        Console.WriteLine($"Diagnostic {diagnostic.FileName}: constructor {diagnostic.Constructor.Milliseconds:F1} ms, " +
            $"read {diagnostic.ReadReplay.Milliseconds:F1} ms");
        return 0;
    }
}
