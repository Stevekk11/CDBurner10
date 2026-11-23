using System.Diagnostics;
using CDBurner10.Core.Dvd;
using NAudio.Wave;

namespace CDBurner10.Core.Audio;

public enum AudioPlaybackState
{
    Stopped,
    Playing,
    Paused
}

/// <summary>
/// Audio preview player powered by NAudio (WaveOutEvent + AudioFileReader) with
/// transparent FFmpeg decode fallback for formats outside Windows Media Foundation
/// (e.g. Opus, APE, WavPack, MPC, OGG).
/// </summary>
public sealed class AudioPlayerService : IDisposable
{
    private readonly object _sync = new();
    private WaveOutEvent? _outputDevice;
    private AudioFileReader? _reader;
    private string? _tempDecodedWav;
    private float _volume = 1.0f;
    private AudioPlaybackState _state = AudioPlaybackState.Stopped;

    public string? CurrentFile { get; private set; }
    public AudioPlaybackState State => _state;

    public TimeSpan Duration
    {
        get
        {
            lock (_sync)
            {
                return _reader?.TotalTime ?? TimeSpan.Zero;
            }
        }
    }

    public TimeSpan Position
    {
        get
        {
            lock (_sync)
            {
                return _reader?.CurrentTime ?? TimeSpan.Zero;
            }
        }
    }

    public float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0f, 1f);
            lock (_sync)
            {
                if (_reader is not null)
                    _reader.Volume = _volume;
            }
        }
    }

    public event EventHandler<AudioPlaybackState>? StateChanged;

    public bool Play(string filePath)
    {
        BassAudioEngine.Initialize();
        Stop();

        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return false;

        AudioFileReader? reader = null;
        string? tempWav = null;

        try
        {
            reader = new AudioFileReader(filePath);
        }
        catch
        {
            // Fallback: decode exotic formats (Opus, APE, WV, MPC, OGG, etc.) to a temp WAV via FFmpeg
            tempWav = TryDecodeToTempWav(filePath);
            if (tempWav is not null)
            {
                try
                {
                    reader = new AudioFileReader(tempWav);
                }
                catch
                {
                    TryDeleteFile(tempWav);
                    return false;
                }
            }
        }

        if (reader is null)
            return false;

        try
        {
            reader.Volume = _volume;
            var device = new WaveOutEvent();
            device.PlaybackStopped += (_, _) =>
            {
                if (_state == AudioPlaybackState.Playing && _reader is not null && _reader.Position >= _reader.Length)
                {
                    SetState(AudioPlaybackState.Stopped);
                }
            };
            device.Init(reader);
            device.Play();

            lock (_sync)
            {
                _reader = reader;
                _outputDevice = device;
                _tempDecodedWav = tempWav;
                CurrentFile = filePath;
            }

            SetState(AudioPlaybackState.Playing);
            return true;
        }
        catch
        {
            reader.Dispose();
            if (tempWav is not null) TryDeleteFile(tempWav);
            Stop();
            return false;
        }
    }

    public void Pause()
    {
        lock (_sync)
        {
            if (_outputDevice is null || _state != AudioPlaybackState.Playing) return;
            _outputDevice.Pause();
        }
        SetState(AudioPlaybackState.Paused);
    }

    public void Resume()
    {
        lock (_sync)
        {
            if (_outputDevice is null || _state != AudioPlaybackState.Paused) return;
            _outputDevice.Play();
        }
        SetState(AudioPlaybackState.Playing);
    }

    public void TogglePlayPause()
    {
        if (_state == AudioPlaybackState.Playing) Pause();
        else if (_state == AudioPlaybackState.Paused) Resume();
        else if (!string.IsNullOrEmpty(CurrentFile)) Play(CurrentFile);
    }

    public void Stop()
    {
        string? tempToDelete;
        lock (_sync)
        {
            try { _outputDevice?.Stop(); } catch { }
            _outputDevice?.Dispose();
            _outputDevice = null;

            _reader?.Dispose();
            _reader = null;

            tempToDelete = _tempDecodedWav;
            _tempDecodedWav = null;
        }

        if (tempToDelete is not null)
            TryDeleteFile(tempToDelete);

        SetState(AudioPlaybackState.Stopped);
    }

    public void Seek(TimeSpan position)
    {
        lock (_sync)
        {
            if (_reader is null) return;
            double clamped = Math.Clamp(position.TotalSeconds, 0, _reader.TotalTime.TotalSeconds);
            _reader.CurrentTime = TimeSpan.FromSeconds(clamped);
        }
    }

    public void PollPlaybackState()
    {
        bool stopped = false;
        lock (_sync)
        {
            if (_outputDevice is not null && _state == AudioPlaybackState.Playing &&
                _outputDevice.PlaybackState == PlaybackState.Stopped)
            {
                stopped = true;
            }
        }
        if (stopped)
            SetState(AudioPlaybackState.Stopped);
    }

    private static string? TryDecodeToTempWav(string sourceFile)
    {
        string? ffmpeg = DvdVideoConverter.FindFfmpeg();
        if (string.IsNullOrWhiteSpace(ffmpeg)) return null;

        string tempWav = Path.Combine(Path.GetTempPath(), $"cdburner10_play_{Guid.NewGuid():N}.wav");
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpeg,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add("-y");
            psi.ArgumentList.Add("-hide_banner");
            psi.ArgumentList.Add("-loglevel");
            psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(sourceFile);
            psi.ArgumentList.Add("-vn");
            psi.ArgumentList.Add("-ar");
            psi.ArgumentList.Add("44100");
            psi.ArgumentList.Add("-ac");
            psi.ArgumentList.Add("2");
            psi.ArgumentList.Add("-c:a");
            psi.ArgumentList.Add("pcm_s16le");
            psi.ArgumentList.Add(tempWav);

            using var process = Process.Start(psi);
            if (process is null) return null;
            process.WaitForExit(30_000);
            if (process.ExitCode == 0 && File.Exists(tempWav))
                return tempWav;
        }
        catch
        {
            // Ignore decode errors
        }

        TryDeleteFile(tempWav);
        return null;
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); } catch { }
    }

    private void SetState(AudioPlaybackState newState)
    {
        if (_state == newState) return;
        _state = newState;
        StateChanged?.Invoke(this, _state);
    }

    public void Dispose()
    {
        Stop();
    }
}
