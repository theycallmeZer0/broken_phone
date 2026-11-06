using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Media;

namespace BrokenPhone
{
    public sealed partial class GameWindow
    {
        readonly MediaPlayer finalVoices = new MediaPlayer(), finalMovie = new MediaPlayer();
        bool finalVideoOpened, finalVoicesOpened, finalMovieOpened, changingFinalVolume, savingFinal;
        string finalOpeningRound, finalAckRound, finalTaskRound;
        RoomClient finalTaskClient;
        Task<string> finalVideoTask;
        CancellationTokenSource finalCancellation;

        void InitializeFinalPlayer()
        {
            finalVoices.MediaOpened += delegate { if (state != null && finalOpeningRound == state.round) { finalVoicesOpened = true; FinalPrepared(); } };
            finalMovie.MediaOpened += delegate { if (state != null && finalOpeningRound == state.round) { finalMovieOpened = true; FinalPrepared(); } };
            finalVoices.MediaFailed += delegate { Toast("Не удалось открыть дорожку голосов. Готовый MP4 можно сохранить."); };
            finalMovie.MediaFailed += delegate { Toast("Не удалось открыть дорожку ролика. Готовый MP4 можно сохранить."); };
            C<Slider>("ResultVoicesVolume").ValueChanged += delegate { if (!changingFinalVolume) { settings.ResultVoiceVolume = C<Slider>("ResultVoicesVolume").Value; Persist(); } UpdateFinalVolumes(); };
            C<Slider>("ResultMovieVolume").ValueChanged += delegate
            {
                if (!changingFinalVolume && state != null) { if (state.resultMovieKind == "original") settings.ResultOriginalVolume = C<Slider>("ResultMovieVolume").Value; else settings.ResultBackgroundVolume = C<Slider>("ResultMovieVolume").Value; Persist(); }
                UpdateFinalVolumes();
            };
        }
        void UpdateFinalVolumes()
        {
            double master = C<Slider>("VolumeSlider").Value;
            finalVoices.Volume = master * C<Slider>("ResultVoicesVolume").Value; finalMovie.Volume = master * C<Slider>("ResultMovieVolume").Value;
            Text("ResultVoicesLevel", (C<Slider>("ResultVoicesVolume").Value * 100).ToString("0") + "%"); Text("ResultMovieLevel", (C<Slider>("ResultMovieVolume").Value * 100).ToString("0") + "%");
            if (videoMode == "result") C<MediaElement>("Video").Volume = 0;
        }
        void ResetFinalPlayer()
        {
            if (finalCancellation != null) { finalCancellation.Cancel(); finalCancellation.Dispose(); finalCancellation = null; }
            finalVoices.Close(); finalMovie.Close(); finalVideoOpened = finalVoicesOpened = finalMovieOpened = finalMediaPrepared = false;
            finalOpeningRound = finalAckRound = null; finalVideoTask = null; finalTaskClient = null; finalTaskRound = null;
        }
        async Task<string> FinalVideo(RoomClient room, string round)
        {
            if (finalTaskClient != room || finalTaskRound != round || finalVideoTask == null)
            {
                finalTaskClient = room; finalTaskRound = round; if (finalCancellation == null) finalCancellation = new CancellationTokenSource();
                finalVideoTask = room.Asset("result", ".mp4", null, finalCancellation.Token);
            }
            string file = await finalVideoTask;
            if (client == room && state != null && state.round == round) finalFile = file;
            return file;
        }
        async Task PrepareFinalMedia(RoomClient room, string round)
        {
            Task<string> videoTask = FinalVideo(room, round);
            var token = finalCancellation.Token;
            Task<string> voicesTask = room.Asset("result_voices", ".m4a", null, token), movieTask = room.Asset("result_movie", ".m4a", null, token);
            await Task.WhenAll(videoTask, voicesTask, movieTask);
            if (disposed || room != client || state == null || state.round != round || state.phase != "result") return;
            finalOpeningRound = round; finalVideoOpened = finalVoicesOpened = finalMovieOpened = finalMediaPrepared = false;
            videoMode = "result"; videoRound = round; playbackAppliedVersion = 0; endedPlaybackVersion = 0;
            changingFinalVolume = true;
            C<Slider>("ResultVoicesVolume").Value = Math.Max(0, Math.Min(1, settings.ResultVoiceVolume));
            C<Slider>("ResultMovieVolume").Value = Math.Max(0, Math.Min(1, state.resultMovieKind == "original" ? settings.ResultOriginalVolume : settings.ResultBackgroundVolume));
            C<Slider>("ResultMovieVolume").IsEnabled = state.resultMovieKind != "none";
            Text("ResultMovieLabel", state.resultMovieKind == "background" ? "Фон ролика" : "Звук ролика"); changingFinalVolume = false;
            var video = C<MediaElement>("Video"); video.Source = new Uri(videoTask.Result); video.Volume = 0; video.Pause();
            finalVoices.Open(new Uri(voicesTask.Result)); finalMovie.Open(new Uri(movieTask.Result)); UpdateFinalVolumes(); RenderRound();
        }
        void FinalPrepared()
        {
            if (state == null || state.phase != "result" || finalOpeningRound != state.round) return;
            finalMediaPrepared = finalVideoOpened && finalVoicesOpened && finalMovieOpened;
            if (finalMediaPrepared && finalAckRound != state.round) { finalAckRound = state.round; Send(new Message { kind = "result_ready", round = state.round }); }
            RenderRound();
        }
        async Task SaveFinalVideo(string destination)
        {
            if (state == null || state.phase != "result") return;
            var room = client; string round = state.round; savingFinal = true; RenderRound();
            try
            {
                string file = await FinalVideo(room, round);
                await Task.Run(() => File.Copy(file, destination, true)); Toast("Общий дубль сохранён: " + destination);
            }
            finally { savingFinal = false; if (state != null && state.phase == "result") RenderRound(); }
        }
        void SyncFinalAudio(double position, bool playing, bool force)
        {
            foreach (var player in new[] { finalVoices, finalMovie })
            {
                if (force || Math.Abs(player.Position.TotalSeconds - position) > .25) player.Position = TimeSpan.FromSeconds(Math.Max(0, position));
                if (force) { if (playing) player.Play(); else player.Pause(); }
            }
        }
    }
}
