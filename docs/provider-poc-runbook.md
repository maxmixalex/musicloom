# Independent music provider PoC — use and verification

This repository includes **three experimental providers in standalone .NET 10 projects**. MusicLoom.App does not reference these PoCs and its existing three matching panels are intentionally unchanged while the user performs SAT. PoC code only demonstrates a method and its protocol behavior; it is not a promise of online availability, lyric licensing or seamless WinUI playback.

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

- Migu: only an actual server-granted HTTPS URI on an accepted Migu domain can be offered for a playable track. When no authorized stream exists, the client returns Unavailable. One successful URL resolution does not establish that an unentitled account may play it.
- Apple Music: a catalog-provided preview is a preview, not a full song. Full subscriber playback needs an authorized MusicKit SDK session on a supported Apple/web runtime, which the standalone Windows .NET HTTP client does not supply.
- Spotify: the Web API has no freely usable full-song MP3 URL. An explicit Premium Spotify Connect PUT command can select the song on a user's existing player; a 204 acknowledges the command, not audible playback in MusicLoom. A separate Web Playback SDK integration is possible with authorization and platform/policy limits. Deprecated preview_url is not used as full playback.

This separation is mandatory. Do not fabricate an audio URL from a catalog ID, assume all songs have anonymous previews, or silently substitute a local track for remote content.

## Lyrics: never delete provider-supplied text

The experiment does not special-case or strip song titles, singers, songwriter credits, composers or band members. If a timed line says "歌曲名", "歌手名", "乐队成员", "作词", or "作曲", that remains in the original and parsed data; Migu [0,0] MRC title/credit lines are also retained. Even if a source includes unusual non-lyric metadata, leave its text untouched instead of guessing what the author intended. Missing TRC or malformed MRC cannot erase LRC.

The lyric bundle carries raw provider text beside its parsed time structures. Apple localizations retain their real locale labels instead of being guessed as translations or romanization. Spotify color-lyrics rows retain time values where they are genuinely supplied; no invented syllable timings.

## CI and practical acceptance

Linux and Windows workflows compile the standalone tests; Linux also runs NativeAOT tests. All HTTP fixture responses are local mocks to avoid shipping or leaking real login tokens. Before integration, obtain provider-authorized real tests for 1) metadata and artwork, 2) LRC/TRC/MRC or TTML/JSON lyric completeness, 3) match validity for covers, multiple artists and live/remaster variants, 4) actual entitlement-based playback.

The new PoC must not be wired to the old three provider matches or their canonical SQLite lyrics yet. Future UI use must follow the approved NetEase-derived common tab/search components and independent match/confirm/clear states; no unrequested UI fork.

Sources and background are recorded in migu-provider-research.md and lyrics-provider-feasibility.md. Implementations are newly authored C# based on observable protocols; GPL-3.0 Lyrimuse implementation code was not copied. Its synthetic encrypted MRC fixture is used as clearly attributed public test data, cross-checked independently.
