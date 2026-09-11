using System;
using System.Windows.Media.Imaging;

namespace WinNotch.Models
{
    public record LyricLine(TimeSpan Time, string Text);

    public class MediaMetadata
    {
        public string Title { get; set; } = string.Empty;
        public string Artist { get; set; } = string.Empty;
        public BitmapImage? Thumbnail { get; set; }
        public bool HasThumbnail => Thumbnail != null;
        public bool IsPlaying { get; set; }
        public TimeSpan Position { get; set; }
        public TimeSpan Duration { get; set; }
        public DateTimeOffset LastUpdatedTime { get; set; }

        public TimeSpan CurrentEstimatedPosition
        {
            get
            {
                if (!IsPlaying) return Position;
                var elapsed = DateTimeOffset.UtcNow - LastUpdatedTime;
                if (elapsed <= TimeSpan.Zero) return Position;

                var estimated = Position + elapsed;
                return (Duration > TimeSpan.Zero && estimated > Duration) ? Duration : estimated;
            }
        }
    }
}
