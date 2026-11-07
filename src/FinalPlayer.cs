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
        WaveMixer finalMixer;
        string[] finalPcmFiles = new string[0];
        double finalVoiceGain, finalMovieGain;
        double FinalAudioPosition { get { return finalMixer == null ? 0 : finalMixer.Position; } }
        bool finalVideoOpened, changingFinalVolume, savingFinal;
        string finalOpeningRound, finalAckRound, finalTaskRound, finalWarmingRound;
        RoomClient finalTaskClient;
        Task<string> finalVideoTask;
        CancellationTokenSource finalCancellation;

        void InitializeFinalPlayer()
        {
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
            finalVoiceGain = master * C<Slider>("ResultVoicesVolume").Value; finalMovieGain = master * C<Slider>("ResultMovieVolume").Value;
            if (finalMixer != null) finalMixer.SetVolumes(finalVoiceGain, finalMovieGain);
            Text("ResultVoicesLevel", (C<Slider>("ResultVoicesVolume").Value * 100).ToString("0") + "%"); Text("ResultMovieLevel", (C<Slider>("ResultMovieVolume").Value * 100).ToString("0") + "%");
            if (videoMode == "result") C<MediaElement>("Video").Volume = 0;
        }
        void ResetFinalPlayer()
        {
            if (finalCancellation != null) { finalCancellation.Cancel(); finalCancellation.Dispose(); finalCancellation = null; }
            if (finalMixer != null) { finalMixer.Dispose(); finalMixer = null; }
            foreach (string pcm in finalPcmFiles) try { File.Delete(pcm); } catch (IOException) { } finalPcmFiles = new string[0];
            finalVideoOpened = finalMediaPrepared = false;
            finalOpeningRound = finalAckRound = finalWarmingRound = null; finalVideoTask = null; finalTaskClient = null; finalTaskRound = null;
            playbackPreparedVersion = 0; playbackAppliedVersion = 0; appliedPlaybackPlaying = false;
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
            if (finalCancellation == null) finalCancellation = new CancellationTokenSource();
            Task<string> videoTask = !String.IsNullOrEmpty(sourceFile) && File.Exists(sourceFile) ? Task.FromResult(sourceFile) : FinalVideo(room, round);
            var token = finalCancellation.Token;
            Task<string> voicesTask = room.Asset("result_voices", ".m4a", null, token), movieTask = room.Asset("result_movie", ".m4a", null, token);
            await Task.WhenAll(videoTask, voicesTask, movieTask);
            if (disposed || room != client || state == null || state.round != round || state.phase != "result") return;
            finalOpeningRound = round; finalVideoOpened = finalMediaPrepared = false;
            videoMode = "result"; videoRound = round; playbackAppliedVersion = 0; endedPlaybackVersion = 0;
            changingFinalVolume = true;
            C<Slider>("ResultVoicesVolume").Value = Math.Max(0, Math.Min(1, settings.ResultVoiceVolume));
            C<Slider>("ResultMovieVolume").Value = Math.Max(0, Math.Min(1, state.resultMovieKind == "original" ? settings.ResultOriginalVolume : settings.ResultBackgroundVolume));
            C<Slider>("ResultMovieVolume").IsEnabled = state.resultMovieKind != "none";
            Text("ResultMovieLabel", state.resultMovieKind == "background" ? "Фон ролика" : "Звук ролика"); changingFinalVolume = false;
            finalPcmFiles = new[] { Path.Combine(Paths.Cache, "screen-voices-" + Guid.NewGuid().ToString("N") + ".pcm"), Path.Combine(Paths.Cache, "screen-movie-" + Guid.NewGuid().ToString("N") + ".pcm") };
            string[] pcmFiles = finalPcmFiles; double duration = state.resultDuration;
            await Task.WhenAll(Task.Run(() => DecodeFinalAudio(voicesTask.Result, pcmFiles[0], duration, token), token), Task.Run(() => DecodeFinalAudio(movieTask.Result, pcmFiles[1], duration, token), token));
            if (disposed || client != room || state == null || state.round != round || state.phase != "result") return;
            finalMixer = new WaveMixer(pcmFiles[0], pcmFiles[1], duration, OutputAudio.Resolve(settings.OutputDevice));
            var video = C<MediaElement>("Video"); video.Close(); video.Source = null; video.Source = new Uri(videoTask.Result); video.Volume = 0; video.Pause();
            UpdateFinalVolumes(); RenderRound();
        }
        async void FinalPrepared()
        {
            if (state == null || state.phase != "result" || finalOpeningRound != state.round || !finalVideoOpened || finalMixer == null || finalWarmingRound == state.round || finalAckRound == state.round) return;
            string round = state.round; var cancellation = finalCancellation; finalWarmingRound = round;
            try
            {
                // MediaOpened reports metadata, before the first frame has been decoded.
                // Warm the decoder silently behind the overlay before announcing readiness.
                var video = C<MediaElement>("Video"); video.Volume = 0; video.Play();
                await Task.Delay(180, cancellation.Token);
                if (disposed || cancellation != finalCancellation || state == null || state.round != round || state.phase != "result") return;
                video.Pause(); video.Position = TimeSpan.Zero;
                await Task.Delay(180, cancellation.Token);
                if (disposed || cancellation != finalCancellation || state == null || state.round != round || state.phase != "result") return;
                finalMediaPrepared = true; finalAckRound = round; Send(new Message { kind = "result_ready", round = round }); RenderRound();
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { if (!disposed) Toast("Не удалось подготовить общий просмотр: " + error.Message); }
            finally { if (finalWarmingRound == round) finalWarmingRound = null; }
        }
        static void DecodeFinalAudio(string input, string output, double duration, CancellationToken token)
        {
            Tools.Run("ffmpeg", "-y -v error -i " + Tools.Quote(input) + " -vn -ar 44100 -ac 2 -t " + Audio.N(duration) + " -f s16le " + Tools.Quote(output), 600000, token);
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
    }
}
