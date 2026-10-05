# Architecture

MusicLoom is split so the synchronization engine does not depend on the Windows UI or any one metadata library.

```text
MusicLoom.App (WinUI 3) ─────┐
                             │
MusicLoom.Cli ───────────────┼── MusicLoom.Core
                             │        │
                             │        ├── MusicLoom.Metadata (ATL read adapter)
                             │        ├── MusicLoom.Tagging (TagLib# mutation)
                             │        └── MusicLoom.Netease
                             │                 │
                             └──────── MusicLoom.AudioFingerprint
```

## Core principles

### Incremental by construction

The Python prototype caches metadata for at most 1,000 entries with a one-day TTL and compares source files directly with targets that may later be mutated by lyric embedding. MusicLoom instead owns a persistent state document describing the source version, target path, recognition result, and processing recipe.

The current planner separates **source metadata cache validity** from **output recipe completion**:

1. stat the source file;
2. if size + mtime match, reuse the stored artist / album / artwork snapshot without reopening the audio file;
3. recompute the expected destination path from that cached metadata and the current destination root;
4. independently compare the completed per-track recipe with `SyncRecipe.For(settings)`;
5. mark the track `Unchanged` only when the source version, destination, and configured recipe are all satisfied.

This distinction is important. Enabling lyrics or changing `overrideExisting` makes an otherwise unchanged track `Modified` with `NeedsAudioCopy=false`: MusicLoom can run only the missing enrichment step. Changing the destination root also reuses cached metadata, but sets `NeedsAudioCopy=true` because the output path itself changed.

### Read-only planning before mutation

The first usable vertical slice is deliberately a dry-run:

```text
enumerate
  ↓
state/stat fast path
  ↓
metadata only for changed/new files
  ↓
legacy-compatible target path
  ↓
Unchanged / New / Modified / Error plan
```

Both CLI and WinUI consume this same plan. No tag or audio file is written by the scanner.

### Metadata is replaceable

`MusicLoom.Core` exposes `ITrackMetadataReader`; format-specific code lives in `MusicLoom.Metadata`.

The first adapter uses ATL.NET because it:

- is MIT licensed;
- supports all seven legacy formats, including ASF/WMA;
- reads through stream/seek oriented parsers rather than requiring a full-file byte array;
- exposes the artist / album artist / album / embedded-picture data needed by the current layout rules.

This boundary exists so the library can be replaced or partially specialized later without changing planning, state, CLI, or UI code.

### Providers are replaceable

Netease is an enrichment provider rather than an architectural dependency. Library scanning, layout, playlist mapping, state management, and local metadata operations remain usable if an online provider is unavailable.

### Native fingerprinting

The old runtime chain was Python → NumPy → PythonMonkey → JavaScript → Emscripten/WASM. MusicLoom contains a native C# implementation of the recovered `hyai_1.2.0_client_1.0.0` fingerprint pipeline:

- 8 kHz mono float32, exactly 3 seconds
- 2048-point Hamming-window FFT, 160-sample hop
- 100–4000 Hz feature band
- log-magnitude normalization and local peak filtering
- legacy `FPVER` / `Peak` binary container
- zlib container
- AES-128-ECB + PKCS#7 with the recovered client key
- Base64 payload for Netease `shazam_v2`

Golden vectors are taken directly from the original WASM used by MusicOrg.

## UI

`MusicLoom.App` is a WinUI 3 app built on Windows App SDK. It is intentionally thin. The current Overview page runs the shared dry-run scanner, displays totals, and lists pending entries. Configuration editing, detailed filtering, and actual sync execution will be layered on top of the same Core plan rather than reimplemented in XAML code-behind.


## Windows audio decoding

The legacy recognizer decodes audio with PyAV, resamples to 8 kHz mono float32, discards leading resampled frames whose RMS is below 0.01, then collects exactly 24,000 samples. Short non-silent input is padded with zeroes; entirely silent input produces no recognition window.

`MusicLoom.Audio.Windows` reproduces that pipeline with Windows Media Foundation through NAudio 3.1.0. The silence decision is isolated in `FingerprintWindowCollector` and regression-tested. Media Foundation does not expose the same decoder/resampler frame boundaries as PyAV, so the rewrite preserves the threshold-and-whole-block semantics but cannot promise identical start-sample selection for pathological fade-ins. The generated AFP pipeline itself remains independently golden-tested.


## Local materialization

Planning distinguishes a changed plan entry from an actual audio copy through `NeedsAudioCopy`. A recipe/state migration can therefore refresh cached metadata without rewriting a large unchanged audio file.

`LocalLibraryMaterializer` implements the non-lyrics filesystem portion of the legacy organizer:

- copy only plan entries that actually need an audio copy;
- preserve source modification/access times so legacy size+mtime identity checks remain meaningful;
- group tracks by target directory before choosing album art;
- if any track in a target album has embedded artwork, only explicit `Cover.<artist>.<album>` style source files are eligible;
- otherwise retain the legacy generic `Cover / Artwork / Front / Back / Folder` fallback;
- write top-level MusicBee `.m3u` playlists as UTF-8 `.txt` files rooted at `destinationPlaylistBase`.

The materializer intentionally does not persist final sync state yet. State must only be committed after future lyric/enrichment steps succeed, otherwise a partial run could be incorrectly treated as fully synchronized on the next warm scan.


## Lyrics writing compatibility

Lyrics mutation is behind `ITrackLyricsWriter` and deliberately separated from metadata scanning. `TagLibSharpLyricsWriter` in `MusicLoom.Tagging` covers the same writable formats as the Python implementation: MP3/WAV via ID3, FLAC/Ogg/Opus via Xiph/Vorbis comments, and M4A via the Apple `©lyr` atom.

Classic TagLib# 2.3.0 is used here because it performs seek/stream-based in-place media mutation. The NativeAOT path does **not** call the reflection-based `TagLib.File.Create` factory; it explicitly constructs `TagLib.Mpeg.AudioFile`, `TagLib.Riff.File`, `TagLib.Flac.File`, `TagLib.Ogg.File`, or `TagLib.Mpeg4.File`.

For one merged LRC document, MusicLoom writes the exact ID3 payloads produced by Mutagen 1.47.0 in the Python implementation:

- ID3v2.4 `SYLT`, UTF-8, language `eng`, timestamp format 2 (absolute milliseconds), content type 1, empty description;
- ID3v2.4 `USLT`, UTF-8, language `eng`, description `Lyrics`, containing the full LRC text;
- ID3v2.4 `TXXX:LYRICS`, UTF-8, containing the same full LRC text.

The writer uses a small write-only custom TagLib# frame type so the **payload bytes**, not merely the semantic fields, match the Mutagen 1.47.0 reference. Regression tests cover both generated WAV and MP3 files, preserve unrelated TXXX fields across overwrite, and verify `overrideExisting=false` leaves files byte-for-byte unchanged.

The full regression harness is published and executed under NativeAOT on both Linux and Windows, so the ID3 path remains continuously exercised rather than relying on a one-off probe.

A separate format corpus pins the TagLib# upstream test samples by commit and exercises real FLAC, Ogg Vorbis, Opus, and M4A containers in both managed and NativeAOT builds. The compatibility target is:

- FLAC / Ogg / Opus: `LYRICS` and `LYRICSENG`. The Python source assigned `lyrics`, `LYRICS`, and `LYRICSENG`, but Mutagen's Vorbis dictionary is case-insensitive, so the first two collapse to one serialized field.
- M4A: the standard Apple `©lyr` atom.
- WMA: no lyric mutation, matching the old MusicOrg unsupported branch.

The corpus also verifies that `overrideExisting=false` is byte-for-byte non-mutating and that unrelated metadata such as the source title survives lyric writes.


## Transactional sync execution

`SyncExecutor` composes the existing local materializer with provider-neutral recognition and lyrics interfaces. It deliberately distinguishes a completed negative enrichment result from a retryable failure.

For every new or modified track:

1. local files / covers / playlists are materialized first;
2. if lyrics are disabled, the current recipe completes immediately;
3. the lyric writer probes the destination **before any network call**;
4. an unsupported target, or existing lyrics with `overrideExisting=false`, completes non-fatally with zero recognition/lyrics requests;
5. recognition runs only when no reusable song ID exists;
6. a valid "no match" response is recorded as `RecognitionState.NoMatch` and completes non-fatally;
7. an empty lyric response is recorded as `LyricsState.NoLyrics` and completes non-fatally;
8. a successful write records a SHA-256 fingerprint of the normalized LF-only LRC payload.

Transport, decoding, and tag-write exceptions are different: useful partial state is persisted, but the current recipe is **not** marked complete. On the next scan the source metadata snapshot and destination path remain reusable, so the retry can run with `NeedsAudioCopy=false`. If recognition already succeeded before a lyric-fetch failure, the stored song ID is reused and fingerprint recognition is skipped on the retry.

This intentionally improves on the old one-day TTL cache: a successful match/no-match/no-lyrics outcome does not force repeated network work on every warm run, while genuine exceptions remain retryable.


## Live Windows sync safety

The WinUI shell keeps planning and mutation as separate user actions:

1. **Scan library** is read-only and builds the shared `LibraryPlan`.
2. **Apply sync** first performs another scan, so a stale plan cannot be applied after files/configuration changed.
3. Plans containing scan errors are not writable from the UI.
4. A confirmation dialog shows pending tracks, actual audio copies, enrichment-only work, destination root, state path, and whether Netease can be contacted.
5. Only after explicit confirmation does the UI compose `MediaFoundationFingerprintPcmDecoder + NeteaseTrackProvider + TagLibSharpLyricsWriter + SyncExecutor`.

`LibraryPathSafety` rejects an empty source/destination, an identical source and destination, and any destination nested inside the source tree. This prevents the organizer from recursively scanning and re-copying its own output.

Cancellation is cooperative. The executor stages per-track partial state before external enrichment work and writes the accumulated state with a non-cancelled token when cancellation is observed. A subsequent scan can therefore reuse source metadata and already materialized destinations instead of starting the track from scratch.
