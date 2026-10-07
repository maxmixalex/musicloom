using MusicLoom.AudioFingerprint;
using MusicLoom.Core.Enrichment;
using MusicLoom.Core.Lyrics;
using MusicLoom.Metadata;

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
        IReadOnlyList<NeteaseMatchCandidate> candidates =
            await _client.MatchFileCandidatesAsync(
                _decoder,
                audioFilePath,
                cancellationToken);

        if (candidates.Count == 0)
        {
            return TrackRecognitionResult.NoMatch();
        }

        (double? durationMs,
            TrackMetadata? metadata) =
                await TryReadLocalTrackInfoAsync(
                    audioFilePath,
                    cancellationToken);

        NeteaseMatchCandidate first =
            NeteaseCandidateRanker
                .Rank(
                    candidates,
                    durationMs,
                    metadata?.Title,
                    metadata?.AlbumArtist
                        ?? metadata?.Artist,
                    metadata?.Album)
                [0];
        return TrackRecognitionResult.Matched(
            first.TrackId,
            first.Name,
            first.Artist,
            first.Album);
    }

    public async Task<IReadOnlyList<NeteaseMatchCandidate>>
        FindCandidatesAsync(
            string audioFilePath,
            CancellationToken cancellationToken = default)
    {
        IReadOnlyList<NeteaseMatchCandidate> candidates =
            await _client.MatchFileCandidatesAsync(
                _decoder,
                audioFilePath,
                cancellationToken);

        (double? durationMs,
            TrackMetadata? metadata) =
                await TryReadLocalTrackInfoAsync(
                    audioFilePath,
                    cancellationToken);

        return NeteaseCandidateRanker.Rank(
            candidates,
            durationMs,
            metadata?.Title,
            metadata?.AlbumArtist
                ?? metadata?.Artist,
            metadata?.Album);
    }

    private static async Task<(
        double? DurationMs,
        TrackMetadata? Metadata)>
        TryReadLocalTrackInfoAsync(
            string audioFilePath,
            CancellationToken cancellationToken)
    {
        double? durationMs = null;
        TrackMetadata? metadata = null;

        try
        {
            double duration =
                AtlTrackInspector
                    .ReadDurationMs(
                        audioFilePath);

            if (double.IsFinite(
                    duration)
                && duration > 0)
            {
                durationMs =
                    duration;
            }
        }
        catch
        {
        }

        try
        {
            metadata =
                await new AtlMetadataReader()
                    .ReadAsync(
                        audioFilePath,
                        cancellationToken);
        }
        catch
        {
        }

        return (
            durationMs,
            metadata);
    }

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
