using CDBurner10.Core.Audio;

namespace CDBurner10.Tests;

public sealed class BassAudioEngineTests
{
    [Fact]
    public void Initialize_ExtensionsCheck_AndShutdown_SucceedsLive()
    {
        try
        {
            BassAudioEngine.Initialize();
            Assert.True(BassAudioEngine.IsInitialized);

            var audioExts = BassAudioEngine.GetSupportedExtensions(includePlaylists: false);
            Assert.Contains(".mp3", audioExts);
            Assert.Contains(".flac", audioExts);
            Assert.Contains(".wav", audioExts);
            Assert.Contains(".ogg", audioExts);
            Assert.Contains(".opus", audioExts);
            Assert.Contains(".wma", audioExts);
            Assert.DoesNotContain(".m3u", audioExts);

            var withPlaylists = BassAudioEngine.GetSupportedExtensions(includePlaylists: true);
            Assert.Contains(".m3u", withPlaylists);
            Assert.Contains(".m3u8", withPlaylists);
            Assert.Contains(".wpl", withPlaylists);
            Assert.Contains(".nra", withPlaylists);
            Assert.Contains(".cue", withPlaylists);

            var filters = BassAudioEngine.GetSupportedExtensions(includePlaylists: false, formatAsFilter: true);
            Assert.Contains("*.flac", filters);
            Assert.Contains("*.mp3", filters);

            Assert.True(BassAudioEngine.IsAudioFile(@"C:\Music\track01.FLAC"));
            Assert.True(BassAudioEngine.IsAudioFile(@"C:\Music\track02.mp3"));
            Assert.False(BassAudioEngine.IsAudioFile(@"C:\Docs\readme.txt"));
            Assert.False(BassAudioEngine.IsAudioFile(""));
        }
        finally
        {
            BassAudioEngine.Shutdown();
            Assert.False(BassAudioEngine.IsInitialized);
        }
    }
}
