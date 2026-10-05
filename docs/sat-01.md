# SAT 0.1 — First user acceptance pass

This is the first hands-on SAT for MusicLoom. The goal is not to validate a full production library yet; it is to validate the complete Windows workflow on a small, disposable destination.

## Safety boundary

For this pass:

- use a **small source subset** (roughly 10–30 tracks);
- use a **new empty destination directory**;
- keep the source and destination completely separate;
- use a state file outside the source tree;
- do not use your final phone/library destination yet.

MusicLoom validates that source, destination, and state locations cannot recursively overlap. It does not intentionally modify source audio files.

## Suggested test corpus

Include a mix if available:

- MP3
- FLAC
- M4A
- Ogg Vorbis
- Opus
- WAV
- WMA

Also include:

- at least one album with embedded artwork;
- at least one album using a sibling `Cover.*` / `Folder.*` image;
- one or two MusicBee `.m3u` playlists;
- a few tracks that already contain lyrics;
- a few tracks without lyrics.

WMA lyric writing is intentionally unsupported because the Python tool behaved the same way.

## Setup

1. Download the Windows app artifact from the latest successful **Windows UI** workflow.
2. Extract it to a normal writable directory.
3. Copy `config.example.json` to `config.json`.
4. Edit the test paths in `config.json`.
5. Launch `MusicLoom.App.exe`.

The UI defaults the state file to:

```text
%LOCALAPPDATA%\MusicLoom\state.json
```

You can choose another state folder in the UI.

## Pass A — read-only scan

1. Choose the test `config.json`.
2. Click **Scan library**.
3. Confirm:
   - source and destination shown on Overview are correct;
   - track count is plausible;
   - no files appear in the destination yet;
   - Errors is zero, or every error is understandable;
   - **Review plan** opens the real Plan page.
4. On Plan:
   - filter by Audio copy / Enrichment / Cover / Playlist / Errors;
   - try the search box;
   - inspect several source → destination mappings.

Stop here and report before applying if a generated destination path looks wrong.

## Pass B — first live sync

1. Return to Overview.
2. Click **Apply sync**.
3. Read the confirmation dialog carefully.
4. Confirm only if the destination and counts are correct.
5. During the run:
   - progress should remain responsive;
   - Cancel should remain available;
   - one failed track should not stop unrelated tracks.
6. After completion, inspect **Activity**.

Verify the destination manually:

- folder layout;
- filenames;
- copied covers;
- generated playlist text files;
- embedded lyrics on a few representative tracks;
- source files remain unchanged.

## Pass C — warm scan

Immediately click **Scan library** again.

Expected result:

```text
Pending = 0
Audio copies = 0
Cover writes = 0
Playlist writes = 0
```

This is the most important performance/incrementality check.

## Pass D — small incremental changes

Make only one change at a time and scan after each:

1. add one new source track;
2. modify one source track;
3. change an album cover;
4. edit one MusicBee playlist;
5. delete one destination audio file manually.

Expected behavior:

- only the relevant item(s) become pending;
- unchanged source metadata should not be reread unnecessarily;
- a manually changed/missing destination should be rematerialized;
- cover-only and playlist-only changes should be actionable without audio copies.

## Pass E — cancellation / retry

Use a slightly larger test subset if needed.

1. Start Apply sync.
2. Click **Cancel** while enrichment is running.
3. Scan again.
4. Apply again.

Expected behavior:

- already completed/checkpointed work is reused;
- the next run does not blindly recopy finished audio;
- remaining work stays pending.

## Error recovery checks

You do **not** need to intentionally damage your library.

If a normal test produces an error:

1. open **Activity**;
2. click **Open log folder**;
3. keep `musicloom.log`;
4. keep the state JSON and its `.bak` if present.

If the primary state JSON becomes unreadable but a valid backup exists, MusicLoom should recover from the backup and show a visible warning instead of silently continuing.

## What to send back

For the first SAT report, the most useful bundle is:

- screenshot of Overview after the first scan;
- screenshot of Plan with a few representative rows;
- screenshot of Activity after sync;
- `musicloom.log`;
- state JSON (and `.bak` if present);
- the test `config.json`;
- a short note for any mismatch:
  - source file;
  - expected destination/result;
  - actual destination/result.

Do not send your full music library unless a specific small fixture is needed to reproduce a bug.

## Known non-goals for SAT 0.1

- MusicLoom does not currently prune/delete orphaned files from the destination when a source file is removed.
- The GUI is focused on scan/review/apply rather than being a music player or full tag editor.
- Online recognition/lyrics availability still depends on the Netease endpoints.
- This pass is Windows-first; the WinUI app is the SAT target.
