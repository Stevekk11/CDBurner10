using System.Buffers.Binary;
using System.Text;

namespace CDBurner10.Tests;

internal sealed class TempDirectory : IDisposable
{
    public string Path { get; }

    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CDBurner10_Tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string CreateFile(string relativePath, byte[] content)
    {
        var full = System.IO.Path.Combine(Path, relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
        return full;
    }

    public string CreateTextFile(string relativePath, string content, Encoding? encoding = null)
    {
        var full = System.IO.Path.Combine(Path, relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, encoding ?? Encoding.UTF8);
        return full;
    }

    /// <summary>
    /// Creates a valid 44.1 kHz, 16-bit stereo PCM WAV file of the given duration in milliseconds.
    /// </summary>
    public string CreateSyntheticWav(string relativePath, int durationMs = 200)
    {
        const int sampleRate = 44100;
        const short channels = 2;
        const short bitsPerSample = 16;
        int blockAlign = channels * (bitsPerSample / 8);
        int byteRate = sampleRate * blockAlign;
        int numSamples = sampleRate * durationMs / 1000;
        int dataSize = numSamples * blockAlign;

        var wav = new byte[44 + dataSize];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(wav, 0);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(4, 4), 36 + dataSize);
        Encoding.ASCII.GetBytes("WAVE").CopyTo(wav, 8);
        Encoding.ASCII.GetBytes("fmt ").CopyTo(wav, 12);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(16, 4), 16); // PCM fmt chunk size
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(20, 2), 1);  // PCM format
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(22, 2), channels);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(24, 4), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(28, 4), byteRate);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(32, 2), (short)blockAlign);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(34, 2), bitsPerSample);
        Encoding.ASCII.GetBytes("data").CopyTo(wav, 36);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(40, 4), dataSize);

        // Write a quiet sine wave so it's valid non-zero audio
        for (int i = 0; i < numSamples; i++)
        {
            short sample = (short)(Math.Sin(2.0 * Math.PI * 440.0 * i / sampleRate) * 4000);
            int offset = 44 + i * blockAlign;
            BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(offset, 2), sample);
            BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(offset + 2, 2), sample);
        }

        return CreateFile(relativePath, wav);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
        catch
        {
            // Ignore cleanup errors
        }
    }
}
