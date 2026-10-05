# Legacy MusicOrg compatibility baseline

MusicLoom starts as a compiled rewrite of the existing Python `musicorg` workflow. The first milestone deliberately preserves observable behavior before introducing new policy choices.

## Library input

The legacy scanner accepts:

- `.mp3`
- `.flac`
- `.m4a`
- `.ogg`
- `.wav`
- `.opus`
- `.wma`

The organizer uses `album artist` when available, otherwise `artist`.

Target layout:

```text
<destination>/<album artist or artist>/<album>/<original filename>
```

If only artist is known, the album directory is omitted. If neither artist nor album metadata is useful, the file stays at the destination root.

Characters invalid in Windows filenames are replaced with spaces and the result is trimmed. Directory components beginning or ending with `.` receive the same compatibility fix as the Python implementation.

## Album art

The existing behavior is intentionally retained as the compatibility target:

1. Prefer explicit album-specific files named `Cover.<artist>.<album>` / `Cover.<album artist>.<album>` in the source directory.
2. If at least one track already has embedded artwork, only explicit album-specific external covers are considered.
3. Otherwise accept the legacy generic names `Cover`, `Artwork`, `Front`, `Back`, and `Folder`.
4. Copy the selected file into the target album directory as `Cover.<original extension>`.

No new embedded-vs-sidecar policy is introduced in the rewrite phase.

## Lyrics

The legacy implementation recognizes a track with Netease's `shazam_v2` audio matcher, fetches original and translated LRC, and aligns translations to original lines within 200 ms.

The tag-writing compatibility target remains:

- ID3: `SYLT`, `USLT`, and `TXXX:LYRICS`
- Vorbis comments: the Python code assigns `lyrics`, `LYRICS`, and `LYRICSENG`, but Mutagen treats Vorbis keys case-insensitively; the serialized compatibility target is therefore `LYRICS` + `LYRICSENG`
- MP4: `©lyr`

MusicLoom does not currently reinterpret these choices based on Salt Player for Windows. Android Salt Player and Salt Player for Windows are treated as distinct products with potentially different behavior.

## Playlists

Legacy MusicBee `.m3u` paths are mapped from source files to their organized target paths, then rooted at `destinationPlaylistBase`, written using `/`, and emitted as `<playlist-name>.txt` in the destination library root.

MusicLoom's parser additionally ignores standard `#EXTM3U` / `#EXTINF` comment lines rather than treating them as file paths.
