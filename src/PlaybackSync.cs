using System;
using System.Windows.Controls;

namespace BrokenPhone
{
    public sealed partial class GameWindow
    {
        int playbackPreparedVersion;
        long localPlaybackStart, lastVideoSeek;
        bool appliedPlaybackPlaying;
        static double PlaybackRate(double error) { return Math.Abs(error) < .04 ? 1 : Math.Max(.96, Math.Min(1.04, 1 + error * .5)); }
        void PauseFinalPlayback()
        {
            C<MediaElement>("Video").Pause(); if (finalMixer != null) finalMixer.Pause();
        }
        void PreparePlaybackSeek(long start)
        {
            localPlaybackStart = start; double target = Math.Min(Playback.Position(state, start), Math.Max(0, state.resultDuration - .04));
            var video = C<MediaElement>("Video"); video.Pause(); video.SpeedRatio = 1; video.Position = TimeSpan.FromSeconds(target); finalMixer.Seek(target, false); RenderRound();
        }
        void TickFinalPlayback()
        {
            if (state == null || state.phase != "result" || currentPage != "Round" || !finalMediaPrepared || finalMixer == null || state.playbackVersion == 0 || client == null) return;
            long now = client.ServerNow; var video = C<MediaElement>("Video");
            if (playbackPreparedVersion != state.playbackVersion)
            {
                playbackPreparedVersion = state.playbackVersion; localPlaybackStart = state.playbackAt;
                if (state.playing || !appliedPlaybackPlaying) PreparePlaybackSeek(state.playing && now >= state.playbackAt ? now + 300 : Math.Max(now, state.playbackAt));
            }
            if (now < localPlaybackStart) return;
            double target = Playback.Position(state, now);
            if (IsHost && state.playing && target >= state.resultDuration && endedPlaybackVersion != state.playbackVersion)
            { endedPlaybackVersion = state.playbackVersion; Send(new Message { kind = "playback", text = "end", round = state.round, index = state.playbackVersion }); }
            if (playbackAppliedVersion != state.playbackVersion)
            {
                if (state.playing && now - localPlaybackStart > 180) { PreparePlaybackSeek(now + 300); return; }
                if (state.playing) { video.Play(); video.Position = TimeSpan.FromSeconds(Math.Min(target, state.resultDuration - .04)); finalMixer.Seek(target, true); }
                else { video.Pause(); video.SpeedRatio = 1; video.Position = TimeSpan.FromSeconds(Math.Min(target, Math.Max(0, state.resultDuration - .04))); finalMixer.Seek(target, false); }
                appliedPlaybackPlaying = state.playing; playbackAppliedVersion = state.playbackVersion; lastPlaybackCheck = lastVideoSeek = now; RenderRound();
            }
            else if (state.playing && endedPlaybackVersion != state.playbackVersion)
            {
                double audio = finalMixer.Position;
                if (now - lastPlaybackCheck > 180)
                {
                    if (Math.Abs(target - audio) > .18) { finalMixer.Seek(target, true); audio = finalMixer.Position; }
                    lastPlaybackCheck = now;
                }
                double error = audio - video.Position.TotalSeconds;
                if (Math.Abs(error) > .10 && now - lastVideoSeek > 100) { video.Position = TimeSpan.FromSeconds(Math.Min(audio, state.resultDuration - .04)); lastVideoSeek = now; }
                else video.SpeedRatio = PlaybackRate(error);
            }
            updatingPlaybackSlider = true; C<Slider>("PlaybackSlider").Maximum = Math.Max(.1, state.resultDuration); if (!draggingPlayback) C<Slider>("PlaybackSlider").Value = target; updatingPlaybackSlider = false;
            Text("PlaybackTime", ClockText(target) + " / " + ClockText(state.resultDuration));
        }
    }
}
