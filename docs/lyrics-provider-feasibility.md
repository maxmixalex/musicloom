# Additional lyric provider feasibility (2026-10-16)

This is a **research decision**, not an implementation or a promise of unauthenticated lyric access. MusicLoom's existing NetEase / QQ Music / KuGou match state and canonical SQLite lyric cache must not be altered by exploratory providers.

| Provider | Publicly documented evidence | Decision |
| --- | --- | --- |
| Apple Music | The Apple Music API song catalog exposes `hasLyrics` as a **boolean**, but the inspected public song/search API documentation does not provide a lyrics-text or synchronized word-timing response. | **Metadata matching only** until a licensed, documented way to retrieve the actual lyric payload is found. Do not claim that `hasLyrics` is the lyric text. |
| Spotify | The Spotify Web API track data exposes `has_lyrics` (presence/unknown), track metadata and external IDs such as ISRC. The inspected public Web API reference does not publish an endpoint returning timed lyrics. The API policies also restrict content downloading. | **Metadata / ISRC lookup only**, not a lyrics provider. Do not depend on private mobile or desktop endpoints. |
| 咪咕音乐 | Publicly accessible music catalog exists. Community reverse-engineered endpoints and unofficial SDKs are discoverable, but no official, versioned public lyrics contract or redistribution permission was verified. | **Research / experimental adapter only**. Require stable response fixtures, explicit error handling, permission review and repeatable online tests before enabling by default. |
| Self-hosted LrcAPI | A documented `/api/v1/lyrics/single` and `/api/v1/lyrics/advance` API can return LRC text and search candidates. This is **not** Apple/Spotify/Migu licensed content; its underlying sources/terms require separate review. | **Candidate for an opt-in, user-configured generic LRC provider**, with provenance preserved. Never use it to silently overwrite an explicitly selected provider match. |

## Proposed integration gates

1. Introduce a provider capability matrix: search, metadata, lyrics, translation, romanization, word timing, audio preview, with **unknown** distinct from **unsupported**.
2. Parse each external response into canonical `LrcDocument` while retaining provider/source identity and manual-empty precedence. A provider without lyrics must not be advertised as providing lyrics.
3. Keep any new provider in an independent match record and the established shared UI (find / confirm / clear / update). Do not regress NetEase's match card behavior.
4. Use offline fixtures and actual service checks separately; avoid writing credentials, cookies, or reverse-engineered session tokens to the repo. Obtain appropriate access rights before exposing a cloud lyric retrieval feature.

## References checked

- Apple Music song attributes: https://developer.apple.com/documentation/applemusicapi/songs/attributes-data.dictionary
- Apple Music catalog search: https://developer.apple.com/documentation/applemusicapi/search-for-catalog-resources-%28by-type%29
- Spotify track endpoint: https://developer.spotify.com/documentation/web-api/reference/get-track
- Spotify API reference: https://developer.spotify.com/documentation/web-api/reference/
- 咪咕音乐 public app: https://m.music.migu.cn/v5/
- Community Migu endpoints (unofficial and not a stability/permission guarantee): https://jsososo.github.io/MiguMusicApi/
- LrcAPI document: https://docs.lrc.cx/docs/APIv1/lyrics/
