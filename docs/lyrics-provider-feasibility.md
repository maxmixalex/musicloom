# Additional lyric provider feasibility (2026-10-16)

**Research only, no provider activated by this document.** Keep MusicLoom's NetEase, QQ Music and KuGou independent match records, user-approved choices, ManualEmpty state, and canonical SQLite lyrics as the single source of truth. This revision corrects the earlier false implication that absence of a *public official* lyrics API means the service has no working community reverse-engineered API.

| Provider | What community code really demonstrates | Credentials / caveat | Engineering decision |
| --- | --- | --- | --- |
| Apple Music | The private Apple web-player AMP endpoint returns timed TTML, sometimes with word/syllable spans and localized translation/transliteration. Projects: MusanovaKit and applemusic-private. The official documented catalog API still does not expose this path. | Usually needs a web-player Bearer and a subscriber media-user-token. Requires private API access, may breach provider terms, and no independent live account test was performed. | **Technically viable as an opt-in advanced provider**, not default and not bundled with borrowed tokens. Parse TTML timecodes plus literal whitespace, translation and transliteration without damaging base lyrics. |
| Spotify | Internal spclient color-lyrics/v2 returns primarily LINE_SYNCED JSON, startTimeMs and words; community PHP, Rust and client implementations exist. Public Spotify Web API still does not provide lyrics text. | Authenticated session / web token; sp_dc-to-token and TOTP flows have changed and some projects report token-exchange denial. No assured word timing, translations or phonetic ruby in typical responses. | **Technically viable for line-synced lyrics**, but low incremental value beside existing providers; keep optional, do not request or cache passwords/cookies by default. |
| 咪咕音乐 | Two independently inspected community clients implement search, LRC, translated TRC, encrypted word-timed MRC, and artwork. See dedicated research document. | The tested source code describes anonymous primary endpoints; these are not documented or guaranteed. No successful real-time request from this environment. | **Strongest next experimental provider candidate**. Build an offline-fixture-tested .NET adapter without enabling a new UI tab or silent auto-match until live service checks pass. |
| Self-hosted LrcAPI | Publicly documented LRC query API, generally aggregating third-party sources. | LrcAPI is not an Apple, Spotify or Migu licensed source, and permission to re-distribute lyrics must be separately considered. | Potential opt-in generic fallback with explicit provenance, not a provider identity substitute. |

## Evidence details

### Apple Music

Unofficial web-player route (not the documented public Apple Music API):

~~~http
GET https://amp-api.music.apple.com/v1/catalog/{storefront}/songs/{songId}/syllable-lyrics?extend=ttmlLocalizations
~~~

Implementations:
- https://github.com/anselmoshim/applemusic-private/blob/main/applemusic_private/catalog.py
- https://github.com/anselmoshim/applemusic-private/blob/main/applemusic_private/lyrics.py
- https://github.com/rryam/MusanovaKit/blob/main/Sources/MusanovaKit/Lyrics/MusicLyricsRequest.swift
- https://github.com/zhaarey/apple-music-downloader
- TTML text, whitespace, transliteration and Ruby notes: https://github.com/amll-dev/applemusic-like-lyrics/blob/main/packages/docs/src/content/docs/en/guides/lyric/ttml.md

Distinguish full Apple TTML localization from Apple song catalog hasLyrics, a boolean. Neither Apple Music subscriptions nor API metadata provide the rights to publish downloaded lyrics. Keep account tokens out of logs, GitHub Actions, the SQLite song cache and application-wide settings.

### Spotify

Unofficial web-player route (not Spotify Web API):

~~~http
GET https://spclient.wg.spotify.com/color-lyrics/v2/track/{trackId}?format=json&market=from_token
Authorization: Bearer <user-session-token>
~~~

Expected sample shape: lyrics.syncType = LINE_SYNCED; lyrics.lines contain startTimeMs (often a *string*), words and generally empty syllables. Do not synthesize word timings from line timings. The 2026-era community projects disagree on how reliably a user cookie can be exchanged for a fresh token; success in an offline unit test does NOT establish live authorization.

Implementations and authentication limitations:
- https://github.com/akashrchandran/spotify-lyrics-api
- https://github.com/GlideClient/spotify-lyrics-api-rust
- https://github.com/Spotui/Spotui
- https://github.com/MelismaApp/melisma-server/blob/main/docs/TOKENS.md
- https://github.com/Yudaotor/lyrimuse/blob/main/lyrimuse-engine/spotifylyrics.go (Spotify *local cached* lyrics only; not a universal online search API).

### Migu

See [Migu provider protocol and offline verification](migu-provider-research.md) for detailed current code references, LRC/TRC/MRC response formats, the separate contentId/copyrightId/songId namespaces, error/caching policy, and acceptance gates.

## Shared integration gates

1. Provider capabilities must distinguish proven, experimental, missing and unknown (search, lyric lines, translation, romanization, word timing, remote audio).
2. Preserve independent provider matches in SQLite. A provider switch must not replace an existing matched card or overwrite canonical lyrics without the user's action, and explicit empty states must survive rescans.
3. Validate timing, Japanese lyric segmentation, quoted/HTML-like metadata and Unicode; never turn a line-level romanization into false character-level ruby.
4. Never embed third-party account tokens, credentials or borrowed web-player JWTs. Any optional authorized provider integration must use explicitly supplied user credentials protected by OS secret storage.
5. Require offline parser tests, live user-authorized tests, and license/TOS review before advertising providers as supported. A mocked 200 response is not a live verification.

## Prior official references

- Apple Music public catalog: https://developer.apple.com/documentation/AppleMusicAPI
- Apple Music authorized user requests: https://developer.apple.com/documentation/applemusicapi/user-authentication-for-musickit
- Spotify public API: https://developer.spotify.com/documentation/web-api/reference/
- Migu official cooperation portal: https://open.migu.cn/portal/
- LrcAPI: https://docs.lrc.cx/docs/APIv1/lyrics/
