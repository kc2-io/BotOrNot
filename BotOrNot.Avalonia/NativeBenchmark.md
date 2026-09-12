# Native library benchmark route

This Windows-only route runs the production `App`, `MainWindow`, and `LibraryView` wiring. It is not enabled during a normal application launch.

Create a private JSON config with fresh, isolated settings and cache paths. The route refuses to delete or reuse a cache file, so an app-cold run is explicit.

```json
{
  "replayDirectory": "C:\\private\\Demos",
  "settingsPath": "C:\\private\\native-c2-settings.json",
  "cachePath": "C:\\private\\native-c2-cache.json",
  "outputPath": "C:\\private\\native-c2-result.json",
  "viewportPngPath": "C:\\private\\native-c2-viewport.png",
  "scanLimit": 79,
  "maxConcurrency": 2,
  "scanProfile": "summary",
  "requireColdCache": true,
  "completionTimeoutSeconds": 180
}
```

After an approved native-window run, launch the release binary in a fresh process:

```powershell
BotOrNot.Avalonia\bin\Release\net10.0\BotOrNot.exe --benchmark-config C:\private\native-c2.json
```

The result records process-to-window-open, scan-to-model, scan-to-drain, scan-to-`DwmFlush`, and scan-to-viewport-PNG timings. The PNG is an Avalonia `RenderTargetBitmap` captured after a real native window opens, lays out, receives render-priority work, and returns from `DwmFlush`. `DwmFlush` establishes compositor handoff; it does not prove physical display scanout.
