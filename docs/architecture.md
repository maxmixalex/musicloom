# Architecture

MusicLoom is split so the synchronization engine does not depend on the Windows UI or any one metadata library.

```text
MusicLoom.App (WinUI 3) ─────┐
                             │
MusicLoom.Cli ───────────────┼── MusicLoom.Core
                             │        │
                             │        ├── MusicLoom.Metadata (ATL adapter)
                             │        └── MusicLoom.Netease
                             │                 │
                             └──────── MusicLoom.AudioFingerprint
```

## Core principles

### Incremental by construction

The Python prototype caches metadata for at most 1,000 entries with a one-day TTL and compares source files directly with targets that may later be mutated by lyric embedding. MusicLoom instead owns a persistent state document describing the source version, target path, recognition result, and processing recipe.

The current planner already enforces the most important warm-path invariant:

1. stat the source file;
2. if size + mtime + recipe match the stored source state;
3. verify the stored destination still exists;
4. mark the track `Unchanged` **without calling the metadata reader**.

Only new or changed source files need metadata parsing. A missing destination is marked `Modified` from state without reopening the source metadata.

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

Lyrics mutation is behind `ITrackLyricsWriter`. The first enabled implementation is the legacy ID3 path for MP3 and WAV.

For one merged LRC document, `AtlLyricsWriter` deliberately writes the same three logical representations as the Python/Mutagen implementation:

- synchronized ID3v2 `SYLT`, language `eng`, timestamp format 2 (absolute milliseconds), content type 1, empty description;
- unsynchronized ID3v2 `USLT`, language `eng`, description `Lyrics`, containing the full LRC text;
- a user-defined text frame `TXXX:LYRICS`, also containing the full LRC text.

The regression corpus uses a generated WAV file, writes embedded ID3v2 through ATL, reopens it semantically, and also parses the raw RIFF/ID3 frame table to prove all three frame types exist. `overrideExisting=false` is verified to leave the file byte-for-byte unchanged.

Vorbis/FLAC/Opus and MP4 writing remain disabled until equivalent format-specific round-trip tests exist. WMA lyric mutation remains unsupported to match the old MusicOrg boundary.
