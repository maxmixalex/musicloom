# MusicLoom

MusicLoom is a local music-library organizer and synchronization tool for keeping a desktop library and a mobile-player library consistent.

It is a compiled rewrite of a personal Python `musicorg` workflow originally built around **MusicBee → Salt Player (Android)**. The rewrite focuses first on preserving the existing organization, lyrics, cover, and playlist behavior while removing the Python/NumPy/PythonMonkey/WASM/PyNCM runtime stack and making unchanged libraries genuinely incremental.

> **Status:** first-SAT candidate. Native AFP, Netease WEAPI, recipe-aware incremental state, atomic/resumable sync, legacy-compatible lyric writing for ID3/Vorbis/MP4, and a guarded WinUI 3 scan/review/apply workflow are in place.

## Why a rewrite?

The old tool works, but its hottest recognition path crosses several runtimes:

```text
Audio → PyAV → NumPy → Python list → PythonMonkey → JS → WASM → PyNCM → Netease
```

MusicLoom's target is:

```text
Audio → native PCM → native AFP → small WEAPI client
```

A second goal is more important than microbenchmarks: after an initial successful sync, an unchanged library should require **zero metadata parsing, zero Netease requests, and zero audio-file writes**.

## Projects

- `MusicLoom.Core` — configuration, incremental state, LRC, library layout, playlist mapping, dry-run planning
- `MusicLoom.Metadata` — streaming/seek-based read adapter; currently ATL.NET
- `MusicLoom.Tagging` — mutation adapter; TagLib# with exact legacy ID3 payloads
- `MusicLoom.AudioFingerprint` — native implementation of the recovered Netease fingerprint algorithm
- `MusicLoom.Netease` — minimal WEAPI client for fingerprint matching and lyrics
- `MusicLoom.Cli` — diagnostics / automation frontend, NativeAOT
- `MusicLoom.App` — Windows UI using WinUI 3
- `MusicLoom.Tests` — golden/regression harness
- `MusicLoom.FormatCorpus` — pinned real-container lyric mutation corpus (FLAC/Ogg/Opus/M4A)

## Dry-run scanning

MusicLoom currently scans the same seven formats as the Python tool:

`.mp3`, `.flac`, `.m4a`, `.ogg`, `.wav`, `.opus`, `.wma`.

A scan produces `Unchanged`, `New`, `Modified`, or `Error` entries and does **not** modify music files. If a source file matches its persistent state entry and its destination still exists, MusicLoom skips metadata parsing entirely.

ATL.NET is isolated behind `ITrackMetadataReader` and is used for scanning because it reads through stream/seek-oriented parsers. TagLibSharp2 0.6.0 currently uses `ReadAllBytes` in its file readers, so it is not used on the scan hot path. Mutation is separate: `MusicLoom.Tagging` uses classic TagLib# 2.3.0 with explicit format classes, avoiding its reflection-based factory in NativeAOT code.

## Native AFP progress

The fingerprint implementation no longer depends on the old Emscripten module. Current golden checks include:

- the decoded wire payload for a zero PCM vector captured from the original WASM;
- exact peak coordinates for a deterministic 24,000-sample reference vector;
- amplitude tolerance checks against the recovered implementation.

The remaining fidelity work is focused on FFT floating-point ordering for pathological boundary inputs. Normal recognition is designed around the same peak representation and wire format as the original client.

## Development CLI

```text
musicloom fingerprint-pcm <3s-8khz-mono-f32le.pcm>
musicloom config-check [config.json]
musicloom state-info [.musicloom/state.json]
musicloom scan [config.json] [.musicloom/state.json]
```

`scan` is read-only and prints pending changes.

## Windows UI

The WinUI 3 app exposes the same planner as the CLI. **Scan library** is read-only. **Apply sync** is enabled only for an error-free pending plan, forces a fresh scan, shows a confirmation summary (pending tracks, actual audio copies, destination, state path, and network behavior), and then executes the shared transactional sync engine. Sync can be cancelled; completed/partial state is persisted so the next scan resumes safely.

## Configuration

`config.example.json` mirrors the current Python configuration shape so migration does not require inventing new user-facing policy yet.

## Build

Core projects target .NET 10. CI runs regression tests and NativeAOT CLI publishing on the `home-docker` runner. WinUI 3 and Windows NativeAOT are validated separately on GitHub's Windows runner.

See [`docs/architecture.md`](docs/architecture.md), [`docs/legacy-behavior.md`](docs/legacy-behavior.md), and the first [`SAT checklist`](docs/sat-01.md).


## Licensing note

MusicLoom source remains MIT. The mutation layer depends on TagLib# 2.3.0, which is LGPL-2.1-only. NativeAOT can statically link managed dependencies into the native executable, so binary distributors must also satisfy the LGPL requirements applicable to that distribution model. See [`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md).
