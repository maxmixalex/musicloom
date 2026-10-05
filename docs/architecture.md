# Architecture

MusicLoom is split so the synchronization engine does not depend on the Windows UI.

```text
MusicLoom.App (WinUI 3)
       │
MusicLoom.Core ───── MusicLoom.Netease
       │                    │
       └──────────── MusicLoom.AudioFingerprint

MusicLoom.Cli uses the same libraries for diagnostics and automation.
```

## Core principles

### Incremental by construction

The Python prototype caches metadata for at most 1,000 entries with a one-day TTL and compares source files directly with targets that may later be mutated by lyric embedding. MusicLoom instead owns a persistent state document describing the source version, target path, recognition result, and processing recipe. A warm run should eventually avoid opening, decoding, recognizing, copying, or rewriting unchanged audio.

### Providers are replaceable

Netease is an enrichment provider rather than an architectural dependency. Library scanning, layout, playlist mapping, state management, and local metadata operations remain usable if an online provider is unavailable.

### Native fingerprinting

The old runtime chain was Python → NumPy → PythonMonkey → JavaScript → Emscripten/WASM. MusicLoom contains a native C# implementation of the recovered `hyai_1.2.0_client_1.0.0` fingerprint pipeline:

- 8 kHz mono float32, exactly 3 seconds
- 2048-point Hamming-window FFT, 160-sample hop
- 100–4000 Hz feature band
- log-magnitude normalization and local peak filtering
- legacy `FPVER` / `Peak` binary container
- RFC 1950 zlib stream using stored blocks
- AES-128-ECB + PKCS#7 with the recovered client key
- Base64 payload for Netease `shazam_v2`

Golden vectors are taken directly from the original WASM used by MusicOrg.

## UI

`MusicLoom.App` is a WinUI 3 app built on Windows App SDK. It is intentionally thin: configuration, planning, sync, and persistence belong in Core. The UI can therefore evolve independently without making the command-line engine or tests Windows-only.
