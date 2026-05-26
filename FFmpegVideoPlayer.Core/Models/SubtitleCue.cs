namespace FFmpegVideoPlayer.Core.Models;

public class SubtitleCue
{
    public long StartMs { get; init; }
    public long EndMs { get; init; }
    public string Text { get; init; } = string.Empty;
}