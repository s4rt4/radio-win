# Classic Radio (Native Windows)

Native WPF rebuild of the web-based `radio` project. Same brown/gold look
(gold-bordered card, gradient slider, spectrum visualizer), but no browser
runtime — single `.exe`, fast cold-start, no Electron, no WebView.

## Stack

- **C# 12 / .NET 8 / WPF** — UI rendered via DirectX, no Chromium
- **LibVLCSharp** — streaming playback (HLS `.m3u8`, Icecast / Shoutcast, raw
  MP3 / AAC) routed through `SetAudioCallbacks` so we own the PCM
- **NAudio** — `WaveOutEvent` for output, `FastFourierTransform` for visualizer
- **H.NotifyIcon.Wpf** — tray icon + popup + context menu

## Features

| Area | What it does |
|---|---|
| Playback | 86 Indonesian + 110 international stations, prev/next/play/pause/mute |
| Visualizer | Real-time 64-band spectrum, **isolated to LibVLC's PCM** (other apps' audio doesn't leak in), spring-damper bar physics, 60 fps |
| Tray | Close (X) hides to tray instead of exiting; left-click tray → popup with logo / status / volume / playback buttons; right-click → Show Radio / Exit |
| Settings | Gear button in title bar → master/detail dialog to add / edit / delete stations; saves to `%APPDATA%\ClassicRadio\stations.json` (bundled JSON stays as fallback) |
| Persistence | Last source, last station, volume, and mute state saved across launches in `%APPDATA%\ClassicRadio\state.json` |
| Connection | Auto-retry with exponential backoff (2 s → 5 s → 10 s) on stream error |
| First-run hint | Play button pulses on first launch to draw attention |
| Keyboard shortcuts | See below |

## Keyboard shortcuts

| Key | Action |
|---|---|
| `Space` | Toggle play / pause |
| `Ctrl + ←` / `Ctrl + →` | Previous / next station |
| `Ctrl + ↑` / `Ctrl + ↓` | Volume +5 / -5 |
| `Ctrl + M` | Mute toggle |
| `Esc` | Hide window to tray |

## Prerequisites

Install **.NET 8 SDK** (one-time):

```powershell
winget install Microsoft.DotNet.SDK.8
```

Then close and reopen the terminal so `dotnet` is on `PATH`.

## Run from source

```powershell
cd C:\laragon\www\radio-win
dotnet run -c Release
```

First run downloads NuGet packages including LibVLC native binaries
(~80 MB, one-time).

## Build a polished, distributable single-file exe

```powershell
dotnet publish -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:PublishReadyToRun=true `
  -p:DebugType=embedded
```

| Flag | Why |
|---|---|
| `-r win-x64 --self-contained` | Bundles the .NET 8 runtime so the user doesn't need it pre-installed |
| `PublishSingleFile=true` | One `.exe` instead of a folder of DLLs |
| `IncludeNativeLibrariesForSelfExtract` | Pulls LibVLC `.dll`s into the self-extract bundle so the player can launch from any folder |
| `PublishReadyToRun=true` | AOT-precompiles managed assemblies → ~30 % faster cold start |
| `DebugType=embedded` | Embeds .pdb so we don't litter the publish folder with loose symbols (still trims size if you don't need debug info; switch to `none` for the smallest exe) |

Output: `bin\Release\net8.0-windows\win-x64\publish\ClassicRadio.exe`
(~80 MB single file). Double-click to run, no install needed.

> **Note**: WPF can't be assembly-trimmed safely (heavy reflection in XAML
> binding), so `-p:PublishTrimmed=true` is intentionally omitted. Code-signing
> the exe with a real certificate eliminates SmartScreen "Unknown publisher"
> warnings — orthogonal to this build.

## Project layout

| File | Role |
|---|---|
| `radio-win.csproj` | Project + dependencies |
| `App.xaml` / `App.xaml.cs` | App entry point |
| `MainWindow.xaml` / `.cs` | Main player UI + playback / visualizer / tray / shortcuts |
| `SettingsWindow.xaml` / `.cs` | Edit-stations dialog |
| `Station.cs` | `Station`, `StationData`, `AppState` JSON shapes |
| `stations.json` | 86 ID + 110 international stations (bundled fallback) |
| `radio-win-logo.{ico,png,svg}` | App icon assets |
| `app.manifest` | Per-monitor DPI awareness |

## On-disk state

| Path | What |
|---|---|
| `<exe folder>\stations.json` | Bundled stations (read-only fallback) |
| `%APPDATA%\ClassicRadio\stations.json` | User-edited stations (overrides bundled if present) |
| `%APPDATA%\ClassicRadio\state.json` | Last source / station / volume / mute |
| `%TEMP%\ClassicRadio.ico` | Extracted icon used to set the Win32 taskbar HICON |

To reset to factory: delete the `%APPDATA%\ClassicRadio` folder.

## Visualizer details

LibVLC's `SetAudioFormat("S16N", 44100, 2)` + `SetAudioCallbacks` deliver raw
PCM through a managed callback. The samples are dual-fed:

1. NAudio `BufferedWaveProvider` → `WaveOutEvent` (so the user can hear audio).
2. A 512-sample FFT (pre-computed Hamming window) → 64 log-spaced bars
   rendered to a WPF `Canvas`.

Bar motion uses a per-bar spring-damper (stiffness 0.55, damping 0.35) on the
attack and exponential decay on the fall — feels snappy on transients without
juddering when the signal is flat.

## Known gotchas

- A handful of URLs in the bundled `stations.json` (e.g. `kbsradio1.stream`,
  `radiko.jp/live/...`) were placeholders in the original web project and may
  not actually resolve. Use the Settings dialog to fix them.
- LibVLC startup is async; the first `Play` after launch may take 1-2 s while
  VLC initializes. Subsequent plays are instant.
- Streams protected by **Wowza Player License** (e.g. `wz.mari.co.id`) check
  for a runtime token from their JS player and cannot be bypassed by spoofing
  HTTP headers alone.
