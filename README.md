# 📷 Sentry Replay

[![Release](https://img.shields.io/github/release/danielchalmers/SentryReplay?include_prereleases&style=flat-square)](https://github.com/danielchalmers/SentryReplay/releases)
[![License](https://img.shields.io/github/license/danielchalmers/SentryReplay?style=flat-square)](LICENSE)

Review Tesla dashcam and Sentry Mode footage on Windows — all four cameras on one timeline, with a marker that jumps you straight to the moment each event happened.

![Sentry Replay](https://github.com/user-attachments/assets/5b2efe25-bac3-4902-8e0e-e006c92c5ddf)

## Features

- **One timeline** — clips grouped by day with thumbnails, event reason, and location. Search across clips, places, and events.
- **Event markers** — a marker on the seek bar at the exact moment each Sentry/Honk/braking event fired. Jump to it with the button or the `E` key.
- **Every angle** — watch a 2×2 grid or any single camera, with live previews of all four. Click a preview to switch.
- **Finds your footage** — auto-detects the `TeslaCam` folder on a connected USB drive, or point it at any folder.
- **Smooth playback** — variable speed (0.25×–4×), scrubbing, and previous/next clip navigation.

## Install

1. Download the latest `SentryReplay-x64.zip` (or `-arm64`) from the [Releases](https://github.com/danielchalmers/SentryReplay/releases) page. An `.msi` installer is also available.
2. Extract and run `SentryReplay.exe`.
3. On first launch it offers to download FFmpeg (~80 MB) if it isn't already present.

Requires Windows 10 or 11.

## Shortcuts

| Key | Action |
| --- | --- |
| `Space` | Play / pause |
| `←` / `→` | Seek 5 seconds |
| `Ctrl` + `←` / `→` | Previous / next clip |
| `1`–`5` | Grid / Front / Rear / Left / Right |
| `E` | Jump to event |
| `Ctrl` + `F` | Search |

## Build from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```sh
dotnet run --project SentryReplay
```

## License

[AGPL-3.0](LICENSE) © Daniel Chalmers
