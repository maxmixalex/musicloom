namespace MusicLoom.Core.State;

public static class QuickFileHash
{
    private const int BlockSize = 32 * 1024;
    private const ulong OffsetBasis = 14695981039346656037UL;
    private const ulong Prime = 1099511628211UL;

    public static ulong Compute(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, BlockSize, FileOptions.SequentialScan);
        long length = stream.Length;
        Span<byte> buffer = stackalloc byte[BlockSize];
        ulong hash = OffsetBasis;

        int firstRead = stream.Read(buffer);
        hash = Append(hash, buffer[..firstRead]);

        if (length > BlockSize)
        {
            stream.Seek(Math.Max(0, length - BlockSize), SeekOrigin.Begin);
            int lastRead = stream.Read(buffer);
            hash = Append(hash, buffer[..lastRead]);
        }

        hash ^= (ulong)length;
        hash *= Prime;
        return hash;
    }

    private static ulong Append(ulong hash, ReadOnlySpan<byte> data)
    {
        foreach (byte value in data)
        {
            hash ^= value;
            hash *= Prime;
        }

        return hash;
    }
}
