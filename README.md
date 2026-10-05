# MusicLoom

MusicLoom is a local music-library organizer and synchronization tool for keeping a desktop library and a mobile-player library consistent.

It is a compiled rewrite of a personal Python `musicorg` workflow originally built around **MusicBee → Salt Player (Android)**. The rewrite focuses first on preserving the existing organization, lyrics, cover, and playlist behavior while removing the Python/NumPy/PythonMonkey/WASM/PyNCM runtime stack and making unchanged libraries genuinely incremental.

> **Status:** early rewrite. The native fingerprint core, Netease WEAPI client, LRC model, path planner, persistent state foundation, development CLI, and WinUI 3 shell are in place. Full media-tag I/O and end-to-end library synchronization are the next milestones.

## Why a rewrite?

The old tool works, but its hottest recognition path crosses several runtimes:

```text
Audio → PyAV → NumPy → Python list → PythonMonkey → JS → WASM → PyNCM → Netease
```

MusicLoom's target is:

```text
Audio → native PCM → native AFP → small WEAPI client
```

A second goal is more important than microbenchmarks: after an initial successful sync, an unchanged library should require **zero Netease requests and zero audio-file writes**.

## Projects

- `MusicLoom.Core` — configuration, incremental state, LRC, library layout, playlist mapping
- `MusicLoom.AudioFingerprint` — native implementation of the recovered Netease fingerprint algorithm
- `MusicLoom.Netease` — minimal WEAPI client for fingerprint matching and lyrics
- `MusicLoom.Cli` — diagnostics / automation frontend, NativeAOT-ready
- `MusicLoom.App` — Windows UI using WinUI 3
- `MusicLoom.Tests` — dependency-free golden/regression harness

## Native AFP progress

The fingerprint implementation no longer depends on the old Emscripten module. Current golden checks include:

- the complete encrypted/Base64 output for a zero PCM vector, directly captured from the original WASM;
- exact peak coordinates for a deterministic 24,000-sample reference vector;
- amplitude tolerance checks against the recovered implementation.

The remaining fidelity work is focused on FFT floating-point ordering for pathological boundary inputs. Normal recognition is designed around the same peak representation and wire format as the original client.

## Development CLI

```text
musicloom fingerprint-pcm <3s-8khz-mono-f32le.pcm>
musicloom config-check [config.json]
musicloom state-info [.musicloom/state.json]
```

## Configuration

`config.example.json` mirrors the current Python configuration shape so migration does not require inventing new user-facing policy yet.

## Build

Core projects target .NET 10. CI runs core build/tests and NativeAOT CLI verification on the `home-docker` runner. The WinUI 3 project is validated separately on Windows.

See [`docs/architecture.md`](docs/architecture.md) and [`docs/legacy-behavior.md`](docs/legacy-behavior.md).
