using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests;

public class MediaExtensionsTests
{
    [Fact]
    public void StrmExtension_IsStrm()
    {
        Assert.Equal(".strm", MediaExtensions.StrmExtension);
    }

    [Theory]
    [InlineData(".mkv")]
    [InlineData(".mp4")]
    [InlineData(".avi")]
    [InlineData(".MKV")]
    [InlineData(".ts")]
    [InlineData(".webm")]
    public void VideoExtensions_ContainsKnownFormats(string ext)
    {
        Assert.Contains(ext, MediaExtensions.VideoExtensions);
    }

    [Fact]
    public void VideoExtensions_DoesNotContainAudio()
    {
        Assert.DoesNotContain(".mp3", MediaExtensions.VideoExtensions);
    }

    [Theory]
    [InlineData(".srt")]
    [InlineData(".ass")]
    [InlineData(".vtt")]
    [InlineData(".SRT")]
    public void SubtitleExtensions_ContainsKnownFormats(string ext)
    {
        Assert.Contains(ext, MediaExtensions.SubtitleExtensions);
    }

    [Theory]
    [InlineData(".jpg")]
    [InlineData(".png")]
    [InlineData(".webp")]
    public void ImageExtensions_ContainsKnownFormats(string ext)
    {
        Assert.Contains(ext, MediaExtensions.ImageExtensions);
    }

    [Theory]
    [InlineData(".nfo")]
    [InlineData(".xml")]
    public void NfoExtensions_ContainsKnownFormats(string ext)
    {
        Assert.Contains(ext, MediaExtensions.NfoExtensions);
    }

    [Theory]
    [InlineData(".mp3")]
    [InlineData(".flac")]
    [InlineData(".opus")]
    [InlineData(".m4a")]
    public void AudioExtensions_ContainsKnownFormats(string ext)
    {
        Assert.Contains(ext, MediaExtensions.AudioExtensions);
    }

    [Theory]
    [InlineData(".mp3", "MP3")]
    [InlineData(".flac", "FLAC")]
    [InlineData(".opus", "Opus")]
    [InlineData(".dsf", "DSD")]
    public void AudioExtensionToCodec_MapsCorrectly(string ext, string expectedCodec)
    {
        Assert.Equal(expectedCodec, MediaExtensions.AudioExtensionToCodec[ext]);
    }

    [Theory]
    [InlineData("forced")]
    [InlineData("sdh")]
    [InlineData("hi")]
    [InlineData("cc")]
    public void SubtitleFlags_ContainsKnownFlags(string flag)
    {
        Assert.Contains(flag, MediaExtensions.SubtitleFlags);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData("eng")]
    [InlineData("deu")]
    [InlineData("jpn")]
    public void KnownLanguageCodes_ContainsCommonCodes(string code)
    {
        Assert.Contains(code, MediaExtensions.KnownLanguageCodes);
    }

    [Fact]
    public void KnownLanguageCodes_IsCaseInsensitive()
    {
        Assert.Contains("EN", MediaExtensions.KnownLanguageCodes);
        Assert.Contains("ENG", MediaExtensions.KnownLanguageCodes);
    }
}