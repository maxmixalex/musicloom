using System.Buffers.Binary;

internal static class TestWaveFactory
{
    public static void WritePcm16(
        string path,
        int sampleRate = 8_000,
        int channels = 1,
        int seconds = 4)
    {
        const int bitsPerSample = 16;
        int frames = sampleRate * seconds;
        int bytesPerSample = bitsPerSample / 8;
        int blockAlign = channels * bytesPerSample;
        int dataSize = frames * blockAlign;
        byte[] data = new byte[44 + dataSize];

        "RIFF"u8.CopyTo(data);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4, 4), 36 + dataSize);
        "WAVE"u8.CopyTo(data.AsSpan(8));
        "fmt "u8.CopyTo(data.AsSpan(12));
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(16, 4), 16);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(20, 2), 1);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(22, 2), checked((short)channels));
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(24, 4), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(28, 4), sampleRate * blockAlign);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(32, 2), checked((short)blockAlign));
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(34, 2), (short)bitsPerSample);
        "data"u8.CopyTo(data.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(40, 4), dataSize);

        int offset = 44;
        for (int frame = 0; frame < frames; frame++)
        {
            short sample = (short)Math.Round(
                Math.Sin(2.0 * Math.PI * 440.0 * frame / sampleRate)
                * 0.25
                * short.MaxValue);

            for (int channel = 0; channel < channels; channel++)
            {
                BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(offset, 2), sample);
                offset += 2;
            }
        }

        File.WriteAllBytes(path, data);
    }
}
