using System;

namespace BrokenPhone
{
    public static class Playback
    {
        public const int Protocol = 11;
        public static long Now { get { return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); } }
        public static double Position(RoomState state, long serverNow)
        {
            double value = state.playbackPosition;
            if (state.playing) value += Math.Max(0, serverNow - state.playbackAt) / 1000.0;
            return Math.Max(0, Math.Min(state.resultDuration, value));
        }
    }
}
