# Native library benchmark route

This Windows-only route runs the production `App`, `MainWindow`, and `LibraryView` wiring. It is not enabled during a normal application launch.

Create a private JSON config with a `ReplayBenchmark freeze` manifest, fresh isolated settings/cache paths, and an approved visible-window placement. The route validates every manifest file's relative path, ordering, length, UTC mtime, and SHA-256 before it opens the app. It refuses to delete or reuse a cache file, so an app-cold run is explicit.

```json
{
  "replayDirectory": "C:\\private\\Demos",
  "manifestPath": "C:\\private\\current79-manifest.json",
  "settingsPath": "C:\\private\\native-c2-settings.json",
  "cachePath": "C:\\private\\native-c2-cache.json",
  "outputPath": "C:\\private\\native-c2-result.json",
  "viewportPngPath": "C:\\private\\native-c2-viewport.png",
  "nativeClientCapturePath": "C:\\private\\native-c2-client.bmp",
  "scanLimit": 79,
  "maxConcurrency": 2,
  "scanProfile": "summary",
  "requireColdCache": true,
  "nativeVisible": true,
  "windowX": 120,
  "windowY": 120,
  "windowWidth": 1100,
  "windowHeight": 800,
  "completionTimeoutSeconds": 180
}
```

After an approved native-window run, launch the release binary in a fresh process:

```powershell
BotOrNot.Avalonia\bin\Release\net10.0\BotOrNot.exe --benchmark-config C:\private\native-c2.json
```

The route opens a visible native window with `ShowActivated=false` at the supplied placement. Run it only after native-window approval. A watchdog writes a failed result and shuts down if the scan never drains.

The result verifies selected manifest identities, all rows loaded with no errors, real `DataGrid` binding/realized rows, rendered scan/aggregate labels, and a deterministic summary-projection fingerprint. It records configured worker policy and reports observed worker concurrency as unavailable until the streaming service exposes a worker-count diagnostic.

It waits for an animation frame, a composition update, and a rendered composition batch before calling `DwmFlush`. It writes two captures: a Win32 `PrintWindow(PW_CLIENTONLY)` client BMP and a separately labelled Avalonia `RenderTargetBitmap` diagnostic PNG. `DwmFlush` establishes compositor handoff; neither it nor either capture proves physical display scanout.
