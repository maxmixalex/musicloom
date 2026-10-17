# Independent music provider PoC — use and verification

This repository includes **three experimental providers in standalone .NET 10 projects**. MusicLoom.App does not reference these PoCs and its existing three matching panels are intentionally unchanged while the user performs SAT. PoC code only demonstrates a method and its protocol behavior; it is not a promise of online availability, lyric licensing or seamless WinUI playback.

PoC executables will be uploaded as separate GitHub Actions artifacts named musicloom-provider-poc-win-x64 and musicloom-provider-poc-linux-x64. These standalone apps do not overwrite MusicLoom.exe or change the existing SAT binary.

## Nonofficial full-song streaming research and execution

The new work no longer assumes that Spotify Connect or MusicKit are the only possible listening backends. The already reverse-engineered *complete audio flow* is separate from the publicly documented catalog API, and a stream locator is not the same thing as decoded playback.

### Apple Music enhanced HLS

A community project sends a web-player AMP catalog song request with the query argument extend=extendedAssetUrls, then reads data[0].attributes.extendedAssetUrls.enhancedHls. This is a full-song HLS playlist URI, not the catalog's short preview URL. The separate AppleEnhancedHlsPoc class now makes this private request using the account-owned web Bearer token, validates Apple CDN hostnames, reads a bounded HLS playlist, and reports the master playlist's codec variants. It distinguishes encrypted media playlists from master playlists where protection is not yet known. No segments or keys are fetched and no signed URL query strings are printed.

Run after setting MUSICLOOM_APPLE_WEB_BEARER from the user's own web session:

    dotnet run --project src/MusicLoom.ProviderPoc.Cli -- stream-info apple 123456789

The result may list AAC, ALAC, Atmos or other codec groups. A successful M3U8 request proves there is a complete-song streaming resource, but it does NOT prove WinUI can decode protected streams; the media pipeline and user session remain independent tasks.

Source evidence:
- https://github.com/zhaarey/apple-music-downloader/blob/main/internal/amp-api/song.go
- https://github.com/WorldObservationLog/AppleMusicDecrypt/blob/v3/src/hls.py

### Spotify locally decoded audio, without Spotify Web API playback

The actively maintained MIT librespot client resolves Spotify file IDs through private spclient storage-resolve, reads CDN byte ranges, obtains track audio keys and decodes to PCM. The separate go-librespot client provides Windows WASAPI output, plus an HTTP loopback control API. This is actual Spotify audio playing *on the Windows PC*, rather than merely instructing an already running Spotify Desktop application to play.

An independently written SpotifyLocalStreamBridge is now available. It talks only to a locally running go-librespot service (never to a remote host), validates 22-character Spotify track IDs, tests session readiness before attempting playback, and implements play/pause/resume/seek/status. The Spotify client remains separately installed and separately authorized; Musicloom does not copy or redistribute its GPL code.

Prepare a user-owned go-librespot session with the documented device_auth or interactive login flow, and configure the client with:

    server:
      enabled: true
      address: localhost
      port: 3678
    audio_backend: wasapi

Then use the isolated Musicloom PoC executable:

    dotnet run --project src/MusicLoom.ProviderPoc.Cli -- listen-local spotify 4iV5W9uYEdYUVa79Axb7Rh --execute
    dotnet run --project src/MusicLoom.ProviderPoc.Cli -- status-local spotify 4iV5W9uYEdYUVa79Axb7Rh
    dotnet run --project src/MusicLoom.ProviderPoc.Cli -- seek-local spotify 4iV5W9uYEdYUVa79Axb7Rh --position-ms=42000

Only the first command actually starts audio. The daemon must already be running, connected to an entitled user account and bound to the local loopback network interface. No account password, refresh token, decrypted media or raw audio link is written by the Musicloom bridge. Its success means the daemon accepted the command; use the status endpoint and audible playback to verify real audio.

Current boundary: the go-librespot process outputs through its own WASAPI backend. WinUI has not yet taken over its decoded PCM or synchronized its own player transport with this process. Keeping this pathway opt-in and isolated protects existing Musicloom SAT from unintended audio-device switching.

Source evidence:
- https://github.com/librespot-org/librespot
- https://github.com/devgianlu/go-librespot/blob/master/API.md
- https://github.com/devgianlu/go-librespot/blob/master/api-spec.yml

## Build, tests and offline evidence

Run the independent eight-fixture suite:

~~~sh
dotnet run --project tests/MusicLoom.ProviderPoc.Tests/MusicLoom.ProviderPoc.Tests.csproj -c Release
~~~

Run the CLI (no credentials needed for Migu's attempted anonymous search):

~~~sh
dotnet run --project src/MusicLoom.ProviderPoc.Cli -- search migu "歌曲名"
dotnet run --project src/MusicLoom.ProviderPoc.Cli -- detail migu 600929000001520013
dotnet run --project src/MusicLoom.ProviderPoc.Cli -- lyrics migu 600929000001520013
dotnet run --project src/MusicLoom.ProviderPoc.Cli -- playback migu 600929000001520013
~~~

Apple Music uses the **official catalog API** for search/details when given a user-supplied developer token; when absent, a web-player Bearer can be tried against the privately observed AMP catalog. The private syllable-lyrics endpoint additionally requires a subscriber media-user-token. Both must come from the actual user: the PoC does not solicit passwords or silently reuse another account.

~~~sh
# Only set these from your own authenticated account outside repo/history:
# MUSICLOOM_APPLE_DEVELOPER_TOKEN
# MUSICLOOM_APPLE_WEB_BEARER
# MUSICLOOM_APPLE_MEDIA_USER_TOKEN
dotnet run --project src/MusicLoom.ProviderPoc.Cli -- search apple "test"
dotnet run --project src/MusicLoom.ProviderPoc.Cli -- lyrics apple 123456
~~~

Spotify uses a user OAuth token for documented catalog endpoints, a *separate* user-supplied web-player bearer for the private color-lyrics endpoint, and a Premium device for full playback via the official Connect API:

~~~sh
# MUSICLOOM_SPOTIFY_OAUTH_TOKEN
# MUSICLOOM_SPOTIFY_LYRICS_BEARER
dotnet run --project src/MusicLoom.ProviderPoc.Cli -- search spotify "test"
dotnet run --project src/MusicLoom.ProviderPoc.Cli -- lyrics spotify 4iV5W9uYEdYUVa79Axb7Rh
dotnet run --project src/MusicLoom.ProviderPoc.Cli -- playback spotify 4iV5W9uYEdYUVa79Axb7Rh

# This last command is deliberately explicit and will attempt to change your playback:
dotnet run --project src/MusicLoom.ProviderPoc.Cli -- play spotify 4iV5W9uYEdYUVa79Axb7Rh --execute --device-id=YOUR_DEVICE
~~~

Environment tokens are read only in the running process and never written to the repository, a local credentials file or the existing SQLite store. The CLI suppresses the query string of potentially signed audio links.


## Independent Apple MusicKit playback experiment (browser)

The C# provider PoC is a catalog/lyrics/preview requester, not a full DRM playback engine. An **additional standalone** MusicKit JS v3 HTML experiment implements the official user-authorized playback path with a real song queue:

1. Serve the checked-in file (not via file://) from a local HTTP origin: run Python's simple HTTP server from the repository root, then visit the HTML file.
2. Register the local origin or use a developer-token configuration accepted by Apple MusicKit, with your own developer token and subscription.
3. In a normal supported browser, click Configure, Sign in, Play. Inspect *audible* playback rather than only whether the play promise resolves.
4. The HTML never sends a raw audio URL to the C# client and never records developer tokens itself. MusicKit may retain an authorized session. A WinUI WebView2 or Electron runtime may behave differently because protected-media / EME support can differ.

File: poc/apple-musickit-playback.html. This path is based on documented MusicKit JS v3, not Apple private lyric endpoint scraping.


## Spotify SDK full-playback browser experiment

The additional static page poc/spotify-web-playback.html uses Spotify's **official Web Playback SDK**, a user's Premium OAuth token and a deliberate Connect playback command. It tests real protected audio in a supported browser without trying to extract audio URLs. Run it from a local HTTP server (not file://) and ensure the OAuth client has the required redirect/origin policy and streaming/user-modify-playback-state scope.

For both browser playback experiments, success means confirmed audible output on a real authorized account. A successful promise or Spotify HTTP 204 alone is not SAT, and WinUI WebView2 protected-media support must be tested independently.

## Playback means different things on different platforms

- Migu: if details do not contain a playable URL, the PoC can attempt the independent PC listen/v1.0 endpoint *only when both contentId and copyrightId were supplied*. Only a successful, validated HTTPS URL is accepted; missing or denied grants remain unavailable.
- Migu: only an actual server-granted HTTPS URI on an accepted Migu domain can be offered for a playable track. When no authorized stream exists, the client returns Unavailable. One successful URL resolution does not establish that an unentitled account may play it.
- Apple Music: a catalog-provided preview is a preview, not a full song. Full subscriber playback needs an authorized MusicKit SDK session on a supported Apple/web runtime, which the standalone Windows .NET HTTP client does not supply.
- Spotify: the Web API has no freely usable full-song MP3 URL. An explicit Premium Spotify Connect PUT command can select the song on a user's existing player; a 204 acknowledges the command, not audible playback in MusicLoom. A separate Web Playback SDK integration is possible with authorization and platform/policy limits. Deprecated preview_url is not used as full playback.

This separation is mandatory. Do not fabricate an audio URL from a catalog ID, assume all songs have anonymous previews, or silently substitute a local track for remote content.

Migu lyric downloads decode valid UTF-8 first, then strict GB18030 when UTF-8 is invalid, preserving the original text rather than replacing unmatched bytes with question marks.

## Lyrics: never delete provider-supplied text

The experiment does not special-case or strip song titles, singers, songwriter credits, composers or band members. If a timed line says "歌曲名", "歌手名", "乐队成员", "作词", or "作曲", that remains in the original and parsed data; Migu [0,0] MRC title/credit lines are also retained. Even if a source includes unusual non-lyric metadata, leave its text untouched instead of guessing what the author intended. Missing TRC or malformed MRC cannot erase LRC.

The lyric bundle carries raw provider text beside its parsed time structures. Apple localizations retain their real locale labels instead of being guessed as translations or romanization. Spotify color-lyrics rows retain time values where they are genuinely supplied; no invented syllable timings.

## CI and practical acceptance

Linux and Windows workflows compile the standalone tests; Linux also runs NativeAOT tests. All HTTP fixture responses are local mocks to avoid shipping or leaking real login tokens. Before integration, obtain provider-authorized real tests for 1) metadata and artwork, 2) LRC/TRC/MRC or TTML/JSON lyric completeness, 3) match validity for covers, multiple artists and live/remaster variants, 4) actual entitlement-based playback.

The new PoC must not be wired to the old three provider matches or their canonical SQLite lyrics yet. Future UI use must follow the approved NetEase-derived common tab/search components and independent match/confirm/clear states; no unrequested UI fork.

Sources and background are recorded in migu-provider-research.md and lyrics-provider-feasibility.md. Implementations are newly authored C# based on observable protocols; GPL-3.0 Lyrimuse implementation code was not copied. Its synthetic encrypted MRC fixture is used as clearly attributed public test data, cross-checked independently.
