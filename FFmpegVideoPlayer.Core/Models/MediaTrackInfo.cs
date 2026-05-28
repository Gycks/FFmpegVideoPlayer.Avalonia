namespace FFmpegVideoPlayer.Core.Models;

/// <summary>
/// Describes one audio or subtitle stream found in a media file.
/// </summary>
public sealed class MediaTrackInfo
{
    public int StreamIndex { get; init; }
    public string Label { get; init; } = string.Empty;
    public string Language { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public bool IsDefault { get; init; }
    public bool IsAudio { get; init; }

    public override string ToString() => Label;
}