using MusicLoom.AudioFingerprint;
using MusicLoom.Core.Enrichment;
using MusicLoom.Core.Lyrics;

namespace MusicLoom.Netease;

public sealed class NeteaseTrackProvider :
    ITrackRecognizer,
    ITrackLyricsProvider
{
    private readonly NeteaseClient _client;
    private readonly IFingerprintPcmDecoder _decoder;

    public NeteaseTrackProvider(
        NeteaseClient client,
        IFingerprintPcmDecoder decoder)
    {
        _client = client;
        _decoder = decoder;
    }

    public async Task<TrackRecognitionResult> RecognizeAsync(
        string audioFilePath,
        CancellationToken cancellationToken = default)
    {
        long? songId = await _client.MatchFileAsync(
            _decoder,
            audioFilePath,
            cancellationToken);

        return songId is long id
            ? TrackRecognitionResult.Matched(id)
            : TrackRecognitionResult.NoMatch();
    }

    public Task<IReadOnlyList<NeteaseMatchCandidate>>
        FindCandidatesAsync(
            string audioFilePath,
            CancellationToken cancellationToken = default) =>
        _client.MatchFileCandidatesAsync(
            _decoder,
            audioFilePath,
            cancellationToken);

    public async Task<LyricsLookupResult> GetLyricsAsync(
        long trackId,
        CancellationToken cancellationToken = default)
    {
        LrcDocument? lyrics = await _client.GetLyricsAsync(
            trackId,
            cancellationToken);

        return lyrics is null || lyrics.Lines.Count == 0
            ? LyricsLookupResult.NoLyrics()
            : LyricsLookupResult.Found(lyrics);
    }
}
