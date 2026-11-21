namespace CDBurner10.Core.Audio;

public enum AudioPlaybackState
{
    Stopped,
    Playing,
    Paused
}

public sealed class AudioPlayerService : IDisposable
{
    private int _streamHandle;
    private float _volume = 1.0f;
    private AudioPlaybackState _state = AudioPlaybackState.Stopped;

    public string? CurrentFile { get; private set; }
    public AudioPlaybackState State => _state;

    public TimeSpan Duration
    {
        get
        {
            if (_streamHandle == 0) return TimeSpan.Zero;
            long len = NativeBass.BASS_ChannelGetLength(_streamHandle, NativeBass.BASS_POS_BYTE);
            if (len < 0) return TimeSpan.Zero;
            return TimeSpan.FromSeconds(Math.Max(0, NativeBass.BASS_ChannelBytes2Seconds(_streamHandle, len)));
        }
    }

    public TimeSpan Position
    {
        get
        {
            if (_streamHandle == 0) return TimeSpan.Zero;
            long pos = NativeBass.BASS_ChannelGetPosition(_streamHandle, NativeBass.BASS_POS_BYTE);
            if (pos < 0) return TimeSpan.Zero;
            return TimeSpan.FromSeconds(Math.Max(0, NativeBass.BASS_ChannelBytes2Seconds(_streamHandle, pos)));
        }
    }

    public float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0f, 1f);
            if (_streamHandle != 0)
            {
                NativeBass.BASS_ChannelSetAttribute(_streamHandle, NativeBass.BASS_ATTRIB_VOL, _volume);
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

        int handle = NativeBass.BASS_StreamCreateFile(false, filePath, 0, 0, NativeBass.BASS_UNICODE);
        if (handle == 0)
            return false;

        _streamHandle = handle;
        CurrentFile = filePath;
        NativeBass.BASS_ChannelSetAttribute(_streamHandle, NativeBass.BASS_ATTRIB_VOL, _volume);

        if (!NativeBass.BASS_ChannelPlay(_streamHandle, restart: true))
        {
            Stop();
            return false;
        }

        SetState(AudioPlaybackState.Playing);
        return true;
    }

    public void Pause()
    {
        if (_streamHandle == 0 || _state != AudioPlaybackState.Playing) return;
        if (NativeBass.BASS_ChannelPause(_streamHandle))
        {
            SetState(AudioPlaybackState.Paused);
        }
    }

    public void Resume()
    {
        if (_streamHandle == 0 || _state != AudioPlaybackState.Paused) return;
        if (NativeBass.BASS_ChannelPlay(_streamHandle, restart: false))
        {
            SetState(AudioPlaybackState.Playing);
        }
    }

    public void TogglePlayPause()
    {
        if (_state == AudioPlaybackState.Playing) Pause();
        else if (_state == AudioPlaybackState.Paused) Resume();
        else if (!string.IsNullOrEmpty(CurrentFile)) Play(CurrentFile);
    }

    public void Stop()
    {
        if (_streamHandle != 0)
        {
            NativeBass.BASS_ChannelStop(_streamHandle);
            NativeBass.BASS_StreamFree(_streamHandle);
            _streamHandle = 0;
        }
        SetState(AudioPlaybackState.Stopped);
    }

    public void Seek(TimeSpan position)
    {
        if (_streamHandle == 0) return;
        double seconds = Math.Clamp(position.TotalSeconds, 0, Duration.TotalSeconds);
        long bytes = NativeBass.BASS_ChannelSeconds2Bytes(_streamHandle, seconds);
        if (bytes >= 0)
        {
            NativeBass.BASS_ChannelSetPosition(_streamHandle, bytes, NativeBass.BASS_POS_BYTE);
        }
    }

    public void PollPlaybackState()
    {
        if (_streamHandle != 0 && _state == AudioPlaybackState.Playing)
        {
            int active = NativeBass.BASS_ChannelIsActive(_streamHandle);
            if (active == NativeBass.BASS_ACTIVE_STOPPED)
            {
                SetState(AudioPlaybackState.Stopped);
            }
        }
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
