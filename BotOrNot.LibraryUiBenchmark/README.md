# Library UI benchmark

This executable hosts the real `LibraryView` and `LibraryViewModel` with Avalonia's Skia offscreen headless renderer. It records the library scan invocation through the final rendered frame, waits for the stream-disposal `ScanDrained` boundary, and separately reports the benchmark-process launch-to-frame measurement.

It is deliberately labelled **headless**, because it does not create a native desktop viewport. Use its output to compare parsing and Avalonia offscreen rendering changes. Native desktop viewport timing must be collected separately.

Run a production fixture in a fresh process:

```powershell
dotnet run -c Release --project BotOrNot.LibraryUiBenchmark -- `
  --fixture-dir C:\replays --manifest C:\benchmark\current79.json `
  --cache-dir C:\benchmark\cold-cache --cache-mode cold `
  --concurrency 2 --limit 79 --mode auto --output C:\benchmark\library-ui.json
```

`--mode auto` measures the persisted-directory `LibraryViewModel` constructor autoscan. `--mode manual` first renders the empty `LibraryView`, then invokes `ScanCommand`; its scan timer therefore begins after the simulated interactive frame. Neither mode runs production `Program` or `MainWindow` startup. The JSON calls the boundary `benchmarkManagedEntry` and states the hosted-view limitation.

`--manifest` is the strict `tools/ReplayBenchmark freeze` artifact. Before every run the harness verifies every relative path, length, UTC mtime and SHA-256 and rejects unexpected replay files. The result fingerprints displayed row and frequent-opponent order, records aggregate text/grid proof and the cache update mix, and can reject a summary result against a Normal reference with `--expected-result normal-ui.json`.

Specify cache state explicitly. `--cache-mode cold` rejects an existing cache file; `--cache-mode warm` rejects an absent one and requires every selected replay to emit a cached update. The harness never deletes or populates cache data. It records the cache file’s before/after hashes and marks the OS file-cache state as `uncontrolled`; an absent app cache is not an OS-cold claim.

The renderer remains **Skia offscreen headless**. Its final-frame timestamp and pixel/grid checks establish a deterministic headless boundary only. Native desktop compositor timing is collected by the separate native harness.

For a small renderer smoke check that does not parse replay files:

```powershell
dotnet run -c Release --project BotOrNot.LibraryUiBenchmark -- --self-test --output C:\benchmark\library-ui-smoke.json
```
