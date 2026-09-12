# Replay benchmark oracle

This tool freezes a private replay corpus, establishes a Normal-parser `ReplaySummary` oracle,
and compares a future library-summary profile without treating a timing improvement as evidence of
correctness. It stores only metadata, SHA-256 file identities, and summary projections; replay
files and generated evidence must remain outside source control.

Build from the repository root:

```powershell
dotnet build .\tools\ReplayBenchmark\ReplayBenchmark.csproj -c Release
```

For the current local corpus, keep evidence private under `work/issue60-evidence`:

```powershell
$tool = '.\tools\ReplayBenchmark\bin\Release\net10.0\ReplayBenchmark.dll'
$demos = "$env:LOCALAPPDATA\FortniteGame\Saved\Demos"
dotnet $tool freeze $demos .\work\issue60-evidence\current79-manifest.json
dotnet $tool validate .\work\issue60-evidence\current79-manifest.json $demos
dotnet $tool run normal .\work\issue60-evidence\current79-manifest.json $demos .\work\issue60-evidence\normal-baseline.json 2
```

If an earlier corpus is identifiable from a timestamp boundary, preserve it as a separate,
clearly labelled historical manifest. For example, this current machine's pre-September-12
inventory can be frozen without replacing the 79-file acceptance corpus:

```powershell
dotnet $tool freeze $demos .\work\issue60-evidence\historic76-20260911-manifest.json --before 2026-09-12T00:00:00Z
```

Once the parser work exposes `ReplayService.LoadSummaryAsync(string, CancellationToken)`, run and
compare the summary profile:

```powershell
dotnet $tool run summary .\work\issue60-evidence\current79-manifest.json $demos .\work\issue60-evidence\summary-candidate.json 2
dotnet $tool compare .\work\issue60-evidence\normal-baseline.json .\work\issue60-evidence\summary-candidate.json .\work\issue60-evidence\summary-deltas.json
```

`summary` intentionally fails before that public API exists. The reflection adapter keeps the
oracle project independent of an in-progress core API change, while still requiring the exact
return type and cancellation signature at runtime.

The manifest sort is the product selection order: descending write time, ordinal-ignore-case path,
then ordinal path. Every run validates length, timestamp, and SHA-256 before it decodes anything.
The canonical summary fingerprint includes identity, raw playlist and display name, all nullable
counts, analysis status, opponent completeness, and sorted stable-ID/name opponent pairs. A
comparison writes a per-file delta report so a different aggregate fingerprint cannot conceal a
single changed replay.
