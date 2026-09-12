# Library UI benchmark

This executable hosts the real `LibraryView` and `LibraryViewModel` with Avalonia's Skia offscreen headless renderer. It records the library scan invocation through the final rendered frame, waits for the stream-disposal `ScanDrained` boundary, and separately reports the benchmark-process launch-to-frame measurement.

It is deliberately labelled **headless**, because it does not create a native desktop viewport. Use its output to compare parsing and Avalonia offscreen rendering changes. Native desktop viewport timing must be collected separately.

Run a production fixture in a fresh process:

```powershell
dotnet run -c Release --project BotOrNot.LibraryUiBenchmark -- `
  --fixture-dir C:\replays --manifest C:\benchmark\current79.json `
  --cache-dir C:\benchmark\cold-cache `
  --concurrency 2 --limit 79 --mode auto --output C:\benchmark\library-ui.json
```

`--mode auto` measures the persisted-directory `LibraryViewModel` constructor autoscan. `--mode manual` creates the same model without a saved directory and invokes `ScanCommand`. Neither mode runs production `Program` or `MainWindow` startup; the JSON calls that timing `benchmarkProcessLaunch` and states the hosted-view boundary. The output records which mode ran, the exact options observed by `IReplayCacheService.ScanAsync`, the final scan labels/counts, and a Skia `RenderTargetBitmap` PNG-content check.

`--manifest` is a non-empty JSON array, or an object with a `files`/`entries` array. Each entry needs `relativePath`, `file`, `name`, or `Name`; optional `length`, `Length`, or `size` values are verified. The fixture directory is checked against the frozen manifest before scanning. Use an absent file in `--cache-dir` (or an explicit `--cache-path`) for an app-cold cache run; the harness never deletes existing cache data.

For a small renderer smoke check that does not parse replay files:

```powershell
dotnet run -c Release --project BotOrNot.LibraryUiBenchmark -- --self-test --output C:\benchmark\library-ui-smoke.json
```
