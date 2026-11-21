using System.Text;

namespace CDBurner10.Core.Audio;

internal sealed class WavWriter : IDisposable
{
    private readonly FileStream _stream;
    private readonly BinaryWriter _writer;
    private long _dataBytesWritten;
    private bool _closed;

    public string FileName { get; }

    public WavWriter(string fileName, int sampleRate = 44100, short channels = 2, short bitsPerSample = 16)
    {
        FileName = fileName;
        var dir = Path.GetDirectoryName(fileName);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _stream = new FileStream(fileName, FileMode.Create, FileAccess.Write, FileShare.None);
        _writer = new BinaryWriter(_stream, Encoding.ASCII, leaveOpen: false);

        int byteRate = sampleRate * channels * (bitsPerSample / 8);
        short blockAlign = (short)(channels * (bitsPerSample / 8));

        // 44-byte RIFF/WAVE PCM header (sizes patched on Close)
        _writer.Write("RIFF"u8);
        _writer.Write(0); // ChunkSize placeholder
        _writer.Write("WAVE"u8);
        _writer.Write("fmt "u8);
        _writer.Write(16); // Subchunk1Size (PCM)
        _writer.Write((short)1); // AudioFormat = PCM
        _writer.Write(channels);
        _writer.Write(sampleRate);
        _writer.Write(byteRate);
        _writer.Write(blockAlign);
        _writer.Write(bitsPerSample);
        _writer.Write("data"u8);
        _writer.Write(0); // Subchunk2Size placeholder
    }

    public void WriteSamples(short[] samples, int byteCount)
    {
        if (byteCount <= 0) return;
        int sampleCount = Math.Min(samples.Length, byteCount / 2);
        var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(samples.AsSpan(0, sampleCount));
        _stream.Write(bytes);
        _dataBytesWritten += bytes.Length;
    }

    public void WriteSilence(int byteCount)
    {
        if (byteCount <= 0) return;
        // Align to 4-byte stereo 16-bit frame boundary
        byteCount = (byteCount / 4) * 4;
        var zeros = new byte[Math.Min(byteCount, 16384)];
        int remaining = byteCount;
        while (remaining > 0)
        {
            int chunk = Math.Min(remaining, zeros.Length);
            _stream.Write(zeros, 0, chunk);
            _dataBytesWritten += chunk;
            remaining -= chunk;
        }
    }

    public void Close()
    {
        if (_closed) return;
        _closed = true;

        _writer.Flush();
        _stream.Seek(4, SeekOrigin.Begin);
        _writer.Write((int)(36 + _dataBytesWritten));
        _stream.Seek(40, SeekOrigin.Begin);
        _writer.Write((int)_dataBytesWritten);
        _writer.Dispose();
    }

    public void Dispose() => Close();
}
