# Migu Music lyrics provider: protocol and feasibility (2026-10-16)

## Scope, provenance and verification status

Research for a possible fourth MusicLoom lyrics provider. **No Migu integration is enabled yet.** The following is grounded in actual open-source request/parser implementations, not merely claimed API lists:

- https://github.com/Domdkw/miguMusic-api-enhanced (MIT; last checked source includes updated LRC, TRC and MRC implementations)
- https://github.com/Yudaotor/lyrimuse/blob/main/lyrimuse-engine/migu.go (GPL-3.0; mature matching, fallback, candidate validation, metadata, cover lookup)
- https://github.com/Yudaotor/lyrimuse/blob/main/lyrimuse-engine/migumrc.go (GPL-3.0; MRC format reference)
- https://github.com/Yudaotor/lyrimuse/blob/main/lyrimuse-engine/migumrc_test.go (encrypted synthetic MRC test vector)
- https://github.com/jsososo/MiguMusicApi (older library, useful for historical copyrightId requests)

**Validation:** the encrypted MRC fixture in the Lyrimuse tests was independently decrypted using a separate minimal algorithm implementation; it exactly reproduced the expected UTF-16LE plaintext and two timestamped karaoke lines. This is an OFFLINE format check, not a successful Migu network call. This execution environment could not fetch real Migu responses. No live service availability, rate limit or title coverage is claimed.

## Candidate retrieval, two generations of endpoints

A. Newer direct lyrics route used by Lyrimuse:

~~~http
GET https://{supported-migu-host}/MIGUM2.0/v1.0/content/search_all.do?text={keyword}&pageNo=1&pageSize=10&searchSwitch=%7B%22song%22%3A1%7D&isCorrect=1
Referer: https://m.music.migu.cn/
User-Agent: Mozilla/5.0
~~~

The Lyrimuse code reports anonymous queries working when it was tested. Success envelope: code == "000000"; songs at songResultData.result[]. Relevant fields:

| JSON field | Meaning |
| --- | --- |
| name | Candidate song title, *not* guaranteed equal to local title |
| singers[].name | Candidate performers |
| albums[].id / name | Candidate album; sometimes absent |
| copyrightId | Rights/catalog identifier; preserve separately from contentId |
| lyricUrl | Direct line-timed / plain LRC text URL, possibly empty |
| trcUrl | Optional translated LRC, often absent |
| mrcurl | Optional encrypted per-word MRC, often absent |
| imgItems[].img / imgSizeType | Candidate cover options, 03 generally largest |
| songAliasName / translateName | Alternative titles (often separated with Chinese list delimiter) |
| rateFormats / newRateFormats | Codec + file size; possible coarse duration estimate, not an authoritative duration |

B. The enhanced SDK's H5 search and detail route:

~~~http
GET https://app.c.nf.migu.cn/bmw/search/song/v1.0?pageNo=1&text={keyword}
GET https://app.u.nf.migu.cn/MIGUM2.0/v1.0/content/resourceinfo.do?resourceId={contentId}&resourceType=2
~~~

The detail response uses code == "000000" and resource[0].lrcUrl, .trcUrl and .mrcUrl (camel case here). The enhanced SDK's /lyric handler downloads selected LRC/TRC URLs and /mrc fetches the encrypted MRC. Do **not** conflate songId, contentId and copyrightId, and do not route a copyrightId into the contentId detail call unverified.

An older route in jsososo/MiguMusicApi uses copyrightId:

~~~http
GET http://music.migu.cn/v3/api/music/audioPlayer/getLyric?copyrightId={copyrightId}
~~~

This historical HTTP endpoint is neither the preferred modern route nor proof of 2026 service availability.

The Lyrimuse fallback uses a separate signed Jadeite search endpoint, with a different response shape (songResultData.resultList[][]). Treat this as **optional future experimental fallback**: no need to ship a brittle mobile-client signature before the unsigned/direct search and detail variants are tested end to end.

## The actual three lyric channels

| Channel | Source field | Body | Fidelity | Caveat |
| --- | --- | --- | --- | --- |
| Original | lyricUrl / lrcUrl | LRC text; sometimes GBK/GB18030 | Line timestamps or plain text | May include timed "歌曲名/歌手名" metadata; remove only known metadata labels |
| Translation | trcUrl | Translated LRC, same basic clock | Line-level translation | Not guaranteed for every foreign-language song; merge by bounded timestamp, not row index |
| Karaoke | mrcurl / mrcUrl | Hex-encoded cipher text | Line and word/character start/duration | Decode MRC first; rows often have absolute-millisecond word timestamps. Coverage varies. |

**Romanization:** no independently established Migu-specific phonetic/romaji channel was found in these implementations. TRC is translated lyrics, not automatically Japanese romanization. Do not claim romanization support based on TRC or MRC alone.

### MRC decoding format (research)

The enhanced TypeScript client and independently maintained Lyrimuse Go client agree on the wire data shape:

1. Read bounded hex text in 16-hex-digit blocks as signed 64-bit words.
2. Use the provider's 64-bit XXTEA-like decryption variant with Java-style signed overflow and arithmetic shifts (it is NOT standard .NET 32-bit XXTEA).
3. Interpret decrypted bytes in little-endian UTF-16; remove padding.
4. Parse lines such as:

~~~text
[1000,900]你(1000,300)好(1300,300)世(1600,150)界(1750,150)
[2500,600]Hel(2500,300)lo(2800,300)
~~~

5. The pair in square brackets is (lineStartMilliseconds, lineDurationMilliseconds); a word's following parentheses are (absoluteWordStartMilliseconds, wordDurationMilliseconds). Do not add the line start again. Ignore synthetic [0,0] title/credit lines, and preserve original syllable segments and their end times in canonical LrcWord entries.
6. A malformed or missing MRC must not erase good existing LRC or translations.

Offline synthetic fixture test and original code:
- https://github.com/Domdkw/miguMusic-api-enhanced/blob/main/src/utils/decryptMrc.ts
- https://github.com/Yudaotor/lyrimuse/blob/main/lyrimuse-engine/migumrc_test.go

The test vector was independently confirmed to produce 117 UTF-16 code units including metadata, title and two timed lyric rows. This does not establish any real-song decoder success until user-authorized network fixtures are available.

## Matching and artwork: important correctness constraints

- First search performer + title; if empty/unusable, search title alone. Inspect candidates using normalized title, aliases, performer names, version (Live, Remix, remaster), album and available duration before accepting.
- Avoid treating search rank alone as exact identity. Reject candidates with missing lyrics for lyric matching; a candidate may still be a legitimate *provider match* even when its lyrics are absent, just as NetEase works today.
- Preserve provider-specific IDs and artwork. Lyrimuse reports search imgItems sometimes refer to the original release even when albums[0] is a compilation. Prefer verified albumId-specific artwork from resourceinfo?resourceType=2003; fall back to song cover if unavailable.
- Retrieve originals for at most a few validated candidates, with bounded concurrency, and choose by deterministic rank/identity and actual lyrics availability rather than first network response.
- A translation may fail independently; preserve available base lyrics. Do not turn romanized text into translation or infer per-character Ruby where no alignment exists.
- MusicLoom's canonical SQLite cache remains the only lyric document consumed by player and sync; each remote result retains its origin, timestamps and source key for reproducible manual choice.
- Keep any future Migu matching view parallel to, not mixed into, NetEase/QQ/KuGou match records. Reuse the original NetEase-derived shared components instead of creating a fourth unrelated UI.

## Transport and failure handling

The existing third-party projects sometimes downgrade HTTPS to HTTP when the provider CDN fails. **MusicLoom should not silently do so.**

- Accept returned lyric/image URLs only from specifically audited Migu HTTPS hostnames; block redirects to unknown hosts, localhost, IP literals and non-HTTPS. Reassess the actual observed CDN host list in live tests.
- Bound lyric files to roughly 512 KiB, encrypted MRC to 1 MiB, JSON to 2 MiB; use cancellation + short timeouts + low parallelism + per-song cache and 429 backoff.
- Decode UTF-8 strictly, then GB18030 with a known encoding provider where necessary; reject binary/HTML error bodies as valid lyrics.
- Distinguish not found, blocked, missing synchronization, decryption failure, translation absent and network error. Do not negative-cache temporary 403/429/5xx.
- Avoid logging raw provider cookies (if auth is ever added), signed secrets, complete lyrics content or personal local paths.

## Acceptance requirements before enabling the provider

1. Capture at least several real authorized successful search/detail responses; verify song title, artist, album, contentId/copyrightId mapping and source URL hosts.
2. Confirm LRC, TRC and MRC on at least one multilingual song with both translation and word timing; test absent TRC/MRC, instrumental and normal plain lyrics.
3. Compare decrypted MRC word/line timing with the provider's own display. Verify Japanese base line does not wrap merely because an added reading is wider.
4. Add isolated .NET client tests with mock HttpMessageHandler and synthetic encrypted MRC vectors. Keep the decoder separate from UI; avoid copying GPL-3.0 Lyrimuse code into MIT MusicLoom. The MIT-enhanced repository and public file formats are useful references, with proper attribution if code is adapted.
5. Only then expose search/confirm/clear/update in an independent Migu tab and add opt-in metadata auto-match. Never auto-overwrite NetEase/QQ/KuGou records or sync destinations.

## Source links

- Enhanced Migu search: https://github.com/Domdkw/miguMusic-api-enhanced/blob/main/src/modules/search.ts
- Enhanced Migu lyric fetch: https://github.com/Domdkw/miguMusic-api-enhanced/blob/main/src/modules/lyric.ts
- Enhanced Migu MRC fetch: https://github.com/Domdkw/miguMusic-api-enhanced/blob/main/src/modules/mrc.ts
- Enhanced Migu SDK docs: https://github.com/Domdkw/miguMusic-api-enhanced/blob/main/docs/api/resource.md
- Lyrimuse Migu matching: https://github.com/Yudaotor/lyrimuse/blob/main/lyrimuse-engine/migu.go
- Lyrimuse MRC decoder/tests: https://github.com/Yudaotor/lyrimuse/blob/main/lyrimuse-engine/migumrc.go
- Original older API: https://github.com/jsososo/MiguMusicApi/blob/master/routes/lyric.ts
- Official Migu partnership portal: https://open.migu.cn/portal/
