using System.Buffers;
using System.IO.Hashing;

namespace MusicLoom.Core.State;

public static class QuickFileHash
{
    private const int BlockSize = 32 * 1024;
    private const int MaximumProbeSize = BlockSize * 2;

    public static ulong Compute(string path)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(MaximumProbeSize);
        try
        {
            using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                BlockSize,
                FileOptions.RandomAccess);

            long length = stream.Length;
            int firstRead = ReadBlock(stream, buffer.AsSpan(0, BlockSize));

            int total = firstRead;
            if (length > BlockSize)
            {
                stream.Seek(
                    Math.Max(0, length - BlockSize),
                    SeekOrigin.Begin);
                total += ReadBlock(
                    stream,
                    buffer.AsSpan(BlockSize, BlockSize));
            }

            return XxHash3.HashToUInt64(buffer.AsSpan(0, total));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static int ReadBlock(
        Stream stream,
        Span<byte> destination)
    {
        int total = 0;
        while (total < destination.Length)
        {
            int read = stream.Read(destination[total..]);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }
}
