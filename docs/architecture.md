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

### Ancillary resources are first-class plan entries

Album artwork and MusicBee playlists participate in dry-run planning independently from audio tracks. A cover or `.m3u` change can therefore produce actionable work even when every audio file is otherwise `Unchanged`.

The scanner computes:

- `CoverPlanEntry` values using the same legacy album grouping and cover-name precedence as the materializer;
- `PlaylistPlanEntry` values containing the mapped destination text and whether the target `.txt` actually differs.

`LibraryPlan.PendingCount` includes pending tracks plus changed covers and playlists, while `PendingTrackCount` remains track-only. The WinUI Apply button uses the combined count, so playlist-only or cover-only changes can be synchronized without forcing an unrelated audio change.

### Destination drift detection

Tracks completed by the transactional executor record the final destination file size and last-write timestamp after all configured enrichment has finished. Warm scans compare that inexpensive output signature in addition to source/state/recipe checks.

If an external tool, sync service, or manual edit changes a known destination file, MusicLoom emits a `Modified` track with `NeedsAudioCopy=true`, rematerializes it from the untouched source, and reapplies the configured enrichment recipe. The source metadata snapshot remains reusable, so detecting destination drift still performs zero metadata reads.

Destination signatures are backward-compatible and lazily adopted. Older state entries without a destination signature keep their previous warm-scan behavior; they gain drift protection the next time the track actually completes a sync recipe. This avoids a one-time full-library rewrite after upgrading.

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

`MusicLoom.App` is a WinUI 3 shell over the same scanner/planner/executor used by the regression harness.

The SAT UI has three real surfaces:

- **Overview** — configuration/state selection, read-only scan, source/destination summary, pending counts, guarded Apply sync, cancellation, and destination access;
- **Plan** — searchable/filterable Copy / Enrich / Cover / Playlist / Error rows generated from the shared `LibraryPlan`;
- **Activity** — scan/sync summaries, retryable errors, state-recovery warnings, and access to the rotating diagnostic log.

Every Apply sync performs a fresh scan before confirmation. User-facing messages avoid raw stack traces; full exceptions go to the diagnostic log under the user's local application-data directory. The default state file also lives under local application data rather than beside the executable.

## Windows audio decoding

The legacy recognizer decodes audio with PyAV, resamples to 8 kHz mono float32, discards leading resampled frames whose RMS is below 0.01, then collects exactly 24,000 samples. Short non-silent input is padded with zeroes; entirely silent input produces no recognition window.

A Windows Media Foundation-only implementation was tested against the seven legacy formats and failed for Ogg Vorbis and Opus on a stock GitHub Windows runner. MusicLoom therefore uses `CompositeFingerprintPcmDecoder` rather than assuming optional system codecs are installed:

- MP3 → fully managed NLayer, downmixed during decode;
- WAV → NAudio `WaveFileReader`;
- Ogg Vorbis → NAudio.Vorbis / NVorbis;
- Opus → Concentus.Oggfile / Concentus, decoded directly to 8 kHz;
- FLAC, M4A and WMA → Windows Media Foundation.

PCM/WAV/Vorbis paths use NAudio's managed WDL resampler where the source rate is not 8 kHz. Every backend feeds the same `FingerprintWindowCollector`, so silence thresholding and the exact 24,000-sample AFP input contract remain centralized.

The Windows format corpus validates MP3, FLAC, M4A, Ogg Vorbis, Opus, WAV and WMA with meaningful decoded audio and a generated AFP. All 7/7 formats pass both managed execution and the published NativeAOT binary. Media Foundation still has decoder/resampler block boundaries that differ from PyAV, so pathological fade-in start-sample selection is not promised to be bit-identical; the AFP algorithm itself remains independently golden-tested.

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

Long runs checkpoint the atomic JSON state every 25 successfully completed tracks. Retryable errors checkpoint immediately, cancellation checkpoints before propagating, and a final snapshot is always written on normal completion. This avoids a full state rewrite for every song while bounding hard-crash rework to a small batch.

Negative provider results intentionally follow the old metadata-cache cadence instead of becoming permanent. `RecognitionState.NoMatch` and `LyricsState.NoLyrics` satisfy the current recipe for 24 hours. After that TTL expires, the scanner emits a metadata-I/O-free `Modified` entry with `NeedsAudioCopy=false`, so only the relevant enrichment call is retried. Written lyrics, pre-existing lyrics, and unsupported formats remain stable for the current recipe.

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


## Path safety

Live sync requires source and destination library roots to be disjoint. The roots may not be identical, the destination may not be nested inside the source, and the source may not be nested inside the destination. This ensures generated output cannot be rescanned as source input or accidentally land back inside the source library.

Metadata-derived directory names are sanitized component-by-component, not only after a full path is assembled. Dot traversal components, leading/trailing-dot names, and Windows device names such as `CON` / `LPT1` are rewritten to safe directory names. `LibraryLayout` then performs a second containment check on the fully resolved destination path and refuses any result outside the configured destination root.

The live WinUI path also validates the incremental state path. A state file may not be placed inside the source library, so the UI promise that sync never mutates the source tree includes MusicLoom's own state file as well as audio, cover, and playlist outputs.


Negative enrichment timestamps are accepted as fresh only when their age is between zero and the 24-hour TTL. A clock rollback or future timestamp therefore causes a safe retry instead of extending a negative cache indefinitely.


## Failure isolation and state recovery

Destination mutation is designed so a single filesystem failure does not poison the whole batch.

- audio and cover copies are written to a uniquely named sibling temporary file and atomically moved into place only after the copy and timestamp preservation succeed;
- playlist text uses the same sibling-temp/replace pattern;
- per-track, cover, and playlist I/O failures are returned as structured materialization errors while unrelated resources continue;
- a track whose audio materialization failed is never sent to recognition/lyrics and is never marked complete in state;
- state writes use a uniquely named temporary file and retain the last successfully loaded/saved primary as `.bak`;
- if the primary state JSON is unreadable but the backup is valid, `StateStore` recovers the backup and exposes a warning so the UI can tell the user what happened;
- after backup recovery, the next save deliberately does not overwrite the good backup with the damaged primary.

State-store failures themselves remain fatal: if MusicLoom cannot safely persist resumable state, the live run stops rather than claiming progress it cannot prove.
