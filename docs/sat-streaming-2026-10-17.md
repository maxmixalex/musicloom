# MusicLoom – Streaming Provider SAT worksheet (2026-10-17)

Scope: **PR #12, unpublished Draft**, current development branch \`fix/sat-ux-sync-match-immersive-20261014\`. Use the exact **Windows app artifact** built from the PR's current HEAD, **not** an earlier MusicLoom.exe, nor the isolated CLI-only PoC artifact. Keep the PR unmerged while the user verifies visual quality and real accounts.

**Important status distinction:** GitHub CI uses deterministic mocked provider responses, and the Windows UI smoke uses offline media fixtures. CI can prove build, startup and mocked contract behavior; it CANNOT prove Spotify Premium access, Apple encrypted HLS decode, real CDN playback, or user-perceived visual smoothness. The tester decides PASS/FAIL; never treat an empty row below as PASS.

## Build to SAT handoff

1. Open [PR #12](https://github.com/maxmixalex/musicloom/pull/12) and its latest **Windows UI** Actions run. Confirm that **all four checks on the exact same head SHA** are green: Core NativeAOT, WinUI app build/publish/launch+matching smoke, Linux lyrics corpus, Windows lyrics corpus.
2. Download the Action artifact \`musicloom-win-x64\` from that Windows UI run. It contains the self-contained native Windows release, distinct from \`musicloom-provider-poc-win-x64\`. Do not mix files from different runs.
3. For Spotify or Apple, prepare an account you are authorized to use in the relevant region. Keep all cookies/tokens out of GitHub, screenshots, attached logs and bug reports. The optional PowerShell helper \`scripts/sat-streaming.ps1\` checks prerequisites and launches the chosen build without storing credentials.
4. Use a known local song, then switch to remote, then back to local. Inspect both main and immersive player. Record exact song/provider and application commit SHA for reproducibility.

## A. Spotify — real local decoded audio (not a remote device)

**Prerequisite:** independently install [go-librespot](https://github.com/devgianlu/go-librespot) on Windows using its documented release. It is an external, separately licensed project, **not bundled with MusicLoom**. Configure and launch its authorized user session, Windows WASAPI output, and loopback API:

~~~yaml
credentials:
  type: device_auth
server:
  enabled: true
  address: localhost
  port: 3678
audio_backend: wasapi
~~~

Authenticate using the real user's Spotify login/device pairing. Require the local API \`GET http://127.0.0.1:3678/\` to report \`playback_ready: true\`. MusicLoom will use a user-provided OAuth token if present; if not, it attempts to fetch an **ephemeral token from this same local session** via \`POST /token\` for catalog/lyric requests (never saved). If the returned token cannot access a particular Spotify endpoint, report the precise HTTP error and do not claim full search/lyric compatibility.

~~~powershell
.\scripts\sat-streaming.ps1 -Provider Spotify -MusicLoomExe 'C:\path\to\MusicLoom.exe'
~~~

SAT checklist:

- [ ] Spotify matching tab searches and displays song candidates using the authenticated local session, with artwork, artist, album and duration; no duplicate provider login is required when the session token has the needed permissions
- [ ] Clicking the selected Spotify candidate starts **audible complete-song audio on this PC** through the locally installed WASAPI decoder, not Spotify Desktop or another phone; local status reports the selected track URI and \`stopped=false\`
- [ ] Main player title, album, full-resolution immersive art and provider name become Spotify; previous/next are hidden for remote music
- [ ] Pause/resume, seek via slider or double-clicked lyric, karaoke follow and displayed duration use \`track.position\` and \`track.duration\` from the local playback session, not stale MediaPlayer state; timer remains steady during gaps between 450ms polls
- [ ] Volume slider changes the *actual Spotify audio*; stop/start switching between Spotify and a local MusicBee track without orphan background audio
- [ ] Exiting the matching dialog during an in-flight remote play command does not start unexpected audio later; changing song rapidly does not resurrect an older request
- [ ] Match/confirm/clear/update lyric operations persist to the **Spotify-specific** record and do not alter NetEase, QQ, KuGou, Migu or Apple records; source lyric text (including credit/band-member rows) stays unmodified

If the local service is absent, denied or returns no active track, MusicLoom must show a meaningful unavailable/authentication state and **never** claim the remote song is currently playing.

## B. Apple Music — preview and full HLS experiment are different SAT items

For song catalog search provide the user's own \`MUSICLOOM_APPLE_DEVELOPER_TOKEN\` or \`MUSICLOOM_APPLE_WEB_BEARER\`; for private TTML lyrics provide a web Bearer plus \`MUSICLOOM_APPLE_MEDIA_USER_TOKEN\`. Default preview mode:

~~~powershell
.\scripts\sat-streaming.ps1 -Provider ApplePreview -MusicLoomExe 'C:\path\to\MusicLoom.exe'
~~~

- [ ] Apple Music candidate search/details/cover and original TTML timestamps return where authorized
- [ ] The default mode uses the catalog-provided preview when present; it never falsely labels a preview a full-song stream
- [ ] Translation/romanization is shown only if the actual provider returned it, with original lines untouched and distinct provider match state durable

**Experimental full-song HLS** (a separate, non-default mode):

~~~powershell
.\scripts\sat-streaming.ps1 -Provider AppleFullHls -MusicLoomExe 'C:\path\to\MusicLoom.exe'
~~~

- [ ] The private web session returns \`enhancedAssetUrls.enhancedHls\` for a known authorized song, and the returned M3U8 host/format is accepted
- [ ] Native WinUI playback attempts the **full HLS resource**, not the short preview, only in this explicit mode
- [ ] If the native Windows decoder cannot handle the provider's protected media, the application reports a failure rather than falsely showing successful full playback; disabling the experimental flag restores the ordinary preview
- [ ] **Full Apple Music audio SAT** remains **NOT PASSED** until an actual protected full track plays audibly for its entire normal duration. Successful M3U8 discovery alone does NOT satisfy this item

The independent \`stream-info apple SONG_ID\` PoC prints codec/variant metadata from the actual playlist while withholding signed query parameters. Do not share user access tokens or protected media in the issue.

## C. Migu and existing providers

- [ ] Migu music search, full cover, original LRC, optional TRC and MRC work on a real song where supplied; only actual provider-granted HTTPS audio URLs are played
- [ ] Song-name, performer-name, credits and band-member lines remain unchanged, including timestamped \`[0,0]\` rows
- [ ] QQ/KuGou matching candidates have the actual native left selection indicator; the selected entry follows keyboard navigation and mouse selection
- [ ] All sync statuses including Syncing are under Filters, and Search / Filters / Sort / right-hand song counts remain aligned in the same toolbar row at typical window widths
- [ ] The sort button and order controls use Segoe Fluent glyphs (no Unicode arrow substitutions or redundant “排序：” prefix)
- [ ] Romanization-in-sync toggle defaults OFF; when toggled, only the sync export changes, not the canonical SQLite lyrics
- [ ] Immersive entry/exit uses approximately 460ms **cover-only** connected animation; both remote and local sources hide/show previous/next correctly

## Evidence to record

For each failure, record commit SHA, provider, account type (do NOT include identifying account details), song title/artist, timestamp, Windows build, whether URL lookup succeeded, whether the app emitted actual audio, the actual provider-side playback status, and relevant *redacted* diagnostics. Separate **FAILED** from **NOT TESTED / ACCOUNT UNAVAILABLE**. The automated test artifacts are attached to the exact matching Windows UI Actions run.

Blocking issues must be repaired in this PR. Do not merge it or report SAT complete based solely on offline fixtures.
