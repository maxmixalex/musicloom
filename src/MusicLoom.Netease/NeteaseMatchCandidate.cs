namespace MusicLoom.Netease;

public sealed record NeteaseMatchCandidate(
    long TrackId,
    string Name,
    string Artist,
    string Album,
    double? Score)
{
    public override string ToString()
    {
        string score =
            Score is double value
                ? $" · {value:0.###}"
                : string.Empty;

        return $"{Name} — {Artist} · {Album} · #{TrackId}{score}";
    }
}
