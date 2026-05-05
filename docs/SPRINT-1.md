# Sprint 1 — Streaming resilience

Status: **DONE — shipped in v1.0.2**
Created: 2026-05-02 (right after v1.0.1 reconnect fix)
Completed: 2026-05-05
Goal: make the radio "tahan banting" against real-world network conditions
Target version: v1.0.2 ✓

## 1. Persistent reconnect mode

A toggle in Settings: *"Keep retrying on connection loss"*. When enabled, replace the current capped 3-attempt chain (2 s → 5 s → 10 s → give up) with capped exponential backoff that **never gives up**:

```
2s → 5s → 10s → 30s → 60s → 120s → 300s → 300s → 300s …
```

Use case: user leaves the radio in tray all day. Their Wi-Fi blips at lunchtime. They come back at 5pm and the music is still playing because the app kept retrying every 5 minutes after the first burst.

Implementation outline:
- New field `bool PersistentReconnect` in `AppState` (Station.cs), wire through `LoadState` / `SaveState`
- New `<CheckBox>` in `SettingsWindow.xaml` bound to it
- Modify `MaybeRetryOrFail()` in `MainWindow.xaml.cs`: when persistent mode on, never call `SetError()`; clamp delay index to last entry (300 s) once exhausted
- New delay table: `int[] RetryDelaysSecPersistent = { 2, 5, 10, 30, 60, 120, 300 };`

## 2. Network-aware retry

Subscribe to `System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged` and adjust retry behavior accordingly:

- `IsAvailable = false` → cancel pending retry timer; status text becomes `"⚠ Offline — waiting for connection"` in `ErrorBrush`. **Do not increment `_retryAttempt`** — we shouldn't burn attempts while there's no network at all.
- `IsAvailable = true` → if a station is selected, immediately call `PlayStation(st)` to retry. Reset retry counter.

Marshal both transitions to UI thread via `OnUI()`.

Use case: laptop suspended, Wi-Fi disconnects. Currently the 3-attempt chain might run while offline and then hit final error. With this feature, app waits patiently and resumes the moment the network comes back.

## 3. Mid-stream no-audio watchdog

LibVLC sometimes stays in `Playing` state even though the audio callback has stopped firing — typically after a long stall on a flaky stream that never produced an `EncounteredError` event. Visually this looks like "playing but the visualizer is flat and the speakers are silent".

Implementation outline:
- New field `private DateTime _lastAudioCallback = DateTime.MaxValue;` updated inside `AudioPlayCallback` (`UtcNow`)
- New `DispatcherTimer` (3 s interval) started in `SetupAudioPipeline`, stopped in `OnClosed`
- On tick: if `_player.State == VLCState.Playing && (DateTime.UtcNow - _lastAudioCallback).TotalSeconds > 5`, call `MaybeRetryOrFail()` to force reconnect

Use case: silent stalls recover automatically without user noticing.

## Done criteria

- [x] Three features implemented, no warnings on `dotnet build -c Release`
- [x] `installer.iss` AppVersion bumped to `1.0.2`
- [x] Republish folder + re-zip + recompile installer in `dist/`
- [x] `RELEASE_NOTES_1.0.2.md` written
- [x] Commit + push to `main`
- [x] `gh release create v1.0.2 …`
- [x] Smoke-tested locally with intentional Wi-Fi toggle

## Mid-sprint additions (also shipped in v1.0.2)

User added two more features mid-sprint:

### 4. Auto-play on dropdown selection
Picking a station from the dropdown now auto-plays it — no separate Play click needed. Implementation: a `_suppressAutoPlay` flag wraps every programmatic selection assignment (`BindStations`, `ApplyState`); user-initiated `SelectionChanged` events fall through to `PlayStation()`.

### 5. Favorites
- New `★ Favorit` source filter alongside Indonesia / Luar Negeri
- Star toggle button next to the dropdown (filled / outline glyph reflects state)
- Favorites stored as `List<string>` of station names in `AppState` — survives stations.json edits
- Source filter shows union of ID + International stations whose name is in Favorites
