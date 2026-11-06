using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BrokenPhone
{
    public sealed partial class GameWindow
    {
        static string ClockText(double value) { return TimeSpan.FromSeconds(Math.Max(0, value)).ToString(@"mm\:ss"); }
        void UpdateEffects()
        {
            var buttons = C<StackPanel>("VoiceEffectButtons").Children.OfType<Button>().ToArray();
            for (int i = 0; i < buttons.Length; i++)
            {
                buttons[i].Background = Brush(i == selectedEffect ? "#EFC75E" : "#30364A"); buttons[i].Foreground = Brush(i == selectedEffect ? "#211B0F" : "#CDD2E5");
                buttons[i].IsEnabled = IsTurn && !microphone.Recording && !recordPending;
            }
            C<CheckBox>("NoiseGateCheck").IsEnabled = IsTurn && !microphone.Recording && !recordPending;
        }
        void RenderRound()
        {
            bool loading = state.phase == "loading", result = state.phase == "result", rendering = state.phase == "rendering";
            var main = C<Grid>("StudioMain"); main.RowDefinitions[1].Height = new GridLength(result || loading ? 515 : 370);
            main.RowDefinitions[2].Height = new GridLength(result || loading ? 0 : 54); main.RowDefinitions[3].Height = new GridLength(result || loading ? 0 : 86); main.RowDefinitions[4].Height = new GridLength(result || loading ? 0 : 42); main.RowDefinitions[6].Height = new GridLength(result ? 37 : 0);
            C<Grid>("SignalPanel").Visibility = result || loading ? Visibility.Collapsed : Visibility.Visible; C<Grid>("EffectsPanel").Visibility = result || loading ? Visibility.Collapsed : Visibility.Visible; C<Grid>("ResultTimeline").Visibility = result ? Visibility.Visible : Visibility.Collapsed;
            Text("RoundEyebrow", loading ? "ПОДГОТОВКА ПАКА / " + state.pack.title : result ? "ВАШ ОБЩИЙ ДУБЛЬ / " + state.pack.title : rendering ? "СОБИРАЕМ ОБЩИЙ ДУБЛЬ" : state.pack.title.ToUpperInvariant() + " / РЕПЛИКА " + (state.turn + 1) + " ИЗ " + state.order.Length);
            Text("RoundTitle", loading ? "Загружаем пак у всех игроков" : result ? "Смотрим вместе!" : rendering ? "Все голоса на месте." : IsTurn ? "Твой выход, " + state.Active.name + "!" : "У микрофона — " + (state.Active == null ? "…" : state.Active.name));
            Text("TurnPrompt", loading ? "Озвучка начнётся автоматически, когда все будут готовы." : result ? (IsHost ? "Пауза, перемотка и повтор у ведущего синхронизируются у всех." : "Показ начнётся автоматически. Просмотром управляет ведущий.") : rendering ? "Сводим реплики с видео…" : IsTurn ? "Предыдущую реплику и оригинал можно прослушать по желанию. R — запись, Enter — дальше." : "Ждём твоего хода.");
            Text("VideoLabel", result ? "ГОТОВЫЙ ДУБЛЬ · ВСЕ ГОЛОСА" : "ОРИГИНАЛЬНЫЕ ГОЛОСА ВЫКЛЮЧЕНЫ");
            Text("RoleCaption", IsTurn ? (state.pack.lines[state.turn].role ?? "ТВОЯ РЕПЛИКА").ToUpperInvariant() : "СЕТЕВАЯ СТУДИЯ");
            Text("LineCaption", IsTurn ? state.pack.lines[state.turn].text : "");
            B("ListenButton").Content = result ? "↻ Смотреть ещё раз" : "◀ Предыдущая";
            B("ListenButton").IsEnabled = result ? IsHost && (state.playbackVersion > 0 || !preparingFinal) : IsTurn && state.turn > 0 && sourcePrepared && !microphone.Recording && !processingTake;
            B("OriginalButton").Visibility = result || loading ? Visibility.Collapsed : Visibility.Visible;
            B("OriginalButton").IsEnabled = IsTurn && sourcePrepared && state.pack.lines[state.turn].originalAvailable && !microphone.Recording && !processingTake;
            B("OriginalButton").ToolTip = IsTurn && !state.pack.lines[state.turn].originalAvailable ? "В этом паке нет исходного звука реплики" : "Прослушать исходную реплику — необязательно";
            B("RecordButton").Visibility = result || loading ? Visibility.Collapsed : Visibility.Visible;
            B("RecordButton").IsEnabled = IsTurn && sourcePrepared && !recordPending && !processingTake;
            B("RecordButton").Content = microphone.Recording ? "■ Стоп" : rawTake != null ? "● Перезапись" : "● Запись";
            B("ReviewButton").Visibility = result || loading ? Visibility.Collapsed : Visibility.Visible; B("ReviewButton").IsEnabled = IsTurn && take != null && !microphone.Recording && !processingTake;
            B("SubmitButton").Content = result ? "↓ Сохранить MP4" : "Дальше ▶"; B("SubmitButton").IsEnabled = result ? finalFile != null : IsTurn && take != null && !microphone.Recording && !processingTake && !recordPending;
            B("PauseButton").Visibility = result ? Visibility.Visible : Visibility.Collapsed; B("PauseButton").IsEnabled = IsHost && state.playbackVersion > 0 && finalMediaPrepared; B("PauseButton").Content = state.playing ? "Ⅱ Пауза у всех" : "▶ Продолжить";
            C<StackPanel>("RoundActions").Visibility = !loading && (result || IsTurn) ? Visibility.Visible : Visibility.Collapsed;
            C<Slider>("PlaybackSlider").IsEnabled = IsHost && state.playbackVersion > 0 && finalMediaPrepared;
            B("CancelPreparationButton").Visibility = loading && IsHost ? Visibility.Visible : Visibility.Collapsed;
            C<ProgressBar>("PreparationProgress").Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
            C<ProgressBar>("PreparationProgress").Value = state.players.Count == 0 ? 0 : state.players.Average(p => p.downloadProgress);
            var waiting = C<Border>("WaitingOverlay"); waiting.Visibility = !IsTurn && !result || result && (!finalMediaPrepared || state.playbackVersion == 0) ? Visibility.Visible : Visibility.Collapsed;
            if (loading)
            {
                Text("WaitingTitle", "Готовим «" + state.pack.title + "»");
                Text("WaitingDetail", Me != null && Me.prepared ? "Твой пак готов. Ждём остальных игроков." : Me == null ? "Подключаемся…" : Me.loadStatus ?? "Загружаем файлы…");
                Text("WaitingPosition", "Пак готов у " + state.players.Count(p => p.prepared) + " из " + state.players.Count + " игроков");
            }
            else if (state.phase == "recording" && !IsTurn)
            {
                var notice = TurnNotice.For(state, client.Id); Text("WaitingTitle", notice.Title); Text("WaitingDetail", notice.Detail); Text("WaitingPosition", notice.Position); Text("TurnPrompt", notice.Position);
            }
            else if (rendering) { Text("WaitingTitle", "Собираем общий дубль"); Text("WaitingDetail", "Все реплики записаны. Ведущий монтирует видео со всеми голосами."); Text("WaitingPosition", "Просмотр откроется автоматически у всех."); }
            else if (result && (!finalMediaPrepared || state.playbackVersion == 0))
            {
                Text("WaitingTitle", finalMediaPrepared ? "Скоро общий просмотр" : "Загружаем готовое видео");
                Text("WaitingDetail", "Готово к показу: " + state.resultReady.Length + " из " + state.players.Count + ".");
                Text("WaitingPosition", "Ждём готовности ведущего и остальных игроков — старт будет общим.");
            }
            var dots = C<StackPanel>("LineDots"); dots.Children.Clear();
            for (int i = 0; i < Math.Min(32, state.order.Length); i++) dots.Children.Add(new System.Windows.Shapes.Ellipse { Width = 5, Height = 5, Margin = new Thickness(0, 0, 6, 0), Fill = Brush(i < state.turn ? "#99D8B0" : i == state.turn ? "#EFC75E" : "#40485F") });
            var turns = C<StackPanel>("TurnList"); turns.Children.Clear();
            if (loading)
                foreach (var player in state.players) turns.Children.Add(new TextBlock { Text = (player.prepared ? "✓ " : "⇩ ") + player.name + " · " + (player.downloadProgress * 100).ToString("0") + "%\n" + (player.loadStatus ?? "Ожидаем"), TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 8, 0, 8), Foreground = Brush(player.prepared ? "#99D8B0" : "#AAB4CC") });
            else if (result)
                foreach (var player in state.players) turns.Children.Add(new TextBlock { Text = "✓  " + player.name + " · " + state.order.Count(id => id == player.id) + " репл.", FontSize = 13, Margin = new Thickness(0, 8, 0, 8), Foreground = Brush("#99D8B0") });
            else
            {
                for (int i = 0; i < Math.Min(state.order.Length, 11); i++)
                {
                    var player = state.players.FirstOrDefault(p => p.id == state.order[i]); string label = (i + 1) + ". " + (player == null ? "Игрок" : player.name) + " · " + state.pack.lines[i].role;
                    turns.Children.Add(new TextBlock { Text = (i < state.turn ? "✓  " : i == state.turn ? "●  " : "○  ") + label, ToolTip = label, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12, Margin = new Thickness(0, 7, 0, 7), FontWeight = i == state.turn ? FontWeights.Bold : FontWeights.Normal, Foreground = Brush(i < state.turn ? "#99D8B0" : "#AAB4CC") });
                }
                if (state.order.Length > 11) turns.Children.Add(new TextBlock { Text = "… ещё " + (state.order.Length - 11) + " реплик", Foreground = Brush("#858478"), FontSize = 11 });
            }
            C<TextBlock>("RecordingTimer").Foreground = Brush(take != null ? "#99D8B0" : processingTake ? "#BDAAFF" : "#F78781");
            UpdateEffects(); C<TextBlock>("TurnPrompt").Visibility = String.IsNullOrEmpty(C<TextBlock>("RecordingTimer").Text) || result ? Visibility.Visible : Visibility.Collapsed;
        }
        async Task PrepareRound()
        {
            var room = client; string round = state.round;
            if (state.phase == "result")
            {
                if (preparingFinal && finalLoadingRound == round || finalMediaPrepared && videoRound == round) return;
                preparingFinal = true; finalLoadingRound = round;
                try
                {
                    string file = finalFile ?? await room.Asset("result", ".mp4"); if (room != client || state.round != round || state.phase != "result") return;
                    finalFile = file; finalMediaPrepared = false; videoMode = "result"; videoRound = round; playbackAppliedVersion = 0; endedPlaybackVersion = 0;
                    var video = C<MediaElement>("Video"); video.Source = new Uri(file); video.Volume = settings.Volume; video.Pause(); RenderRound();
                }
                finally { if (finalLoadingRound == round) preparingFinal = false; }
                return;
            }
            if (state.phase != "recording" && state.phase != "loading") return;
            if (sourceFile == null)
            {
                if (videoDownload == null || videoDownloadClient != room) { videoDownloadClient = room; videoDownload = LoadRoundPack(room, state.pack, round); }
                string file = await videoDownload; if (room != client || state.round != round || state.phase != "recording" && state.phase != "loading") return; sourceFile = file;
            }
            var player = C<MediaElement>("Video"); videoMode = "source"; videoRound = round;
            if (player.Source == null || player.Source.LocalPath != sourceFile) { sourcePrepared = false; player.Source = new Uri(sourceFile); player.Volume = 0; player.Pause(); }
            else { sourcePrepared = player.NaturalVideoWidth > 0; player.Position = TimeSpan.FromSeconds(state.pack.lines[state.turn].start); player.Pause(); }
            RenderRound(); AcknowledgePrepared();
        }
        async Task Listen()
        {
            if (state == null) return;
            if (state.phase == "result") { if (!finalMediaPrepared) await PrepareRound(); else ControlPlayback("restart", 0); return; }
            if (!IsTurn || state.turn == 0 || microphone.Recording || processingTake) return;
            string round = state.round; int turn = state.turn; var room = client;
            string file = previousFile ?? await room.Asset("take:" + (turn - 1), ".wav"); if (room != client || state.round != round || state.turn != turn) return; previousFile = file;
            Preview(file, state.pack.lines[turn - 1], "ПРЕДЫДУЩАЯ РЕПЛИКА", delegate { Send(new Message { kind = "heard", round = round, index = turn }); });
        }
        async Task Original()
        {
            if (!IsTurn || !state.pack.lines[state.turn].originalAvailable || microphone.Recording || processingTake) return;
            var room = client; string round = state.round; int turn = state.turn; B("OriginalButton").IsEnabled = false;
            try { string file = await room.Asset("original:" + turn, ".wav"); if (room == client && state.round == round && state.turn == turn) Preview(file, state.pack.lines[turn], "ОРИГИНАЛ", null); }
            finally { if (state != null && state.phase != "lobby") RenderRound(); }
        }
        void PreviewTake() { if (localTakeFile != null && IsTurn && !microphone.Recording && !processingTake) Preview(localTakeFile, state.pack.lines[state.turn], "МОЙ ДУБЛЬ · " + VoiceEffects.Names[selectedEffect].ToUpperInvariant(), null); }
        void Preview(string file, Line line, string label, Action after)
        {
            var video = C<MediaElement>("Video"); video.Volume = 0; video.Position = TimeSpan.FromSeconds(line.start); video.Play(); Text("SignalLabel", label); Text("VideoLabel", label);
            PlayVoice(file, line.end - line.start, delegate { video.Pause(); Text("VideoLabel", "ОРИГИНАЛЬНЫЕ ГОЛОСА ВЫКЛЮЧЕНЫ"); if (IsTurn) video.Position = TimeSpan.FromSeconds(state.pack.lines[state.turn].start); if (after != null) after(); });
        }
        void PlayVoice(string file, double duration, Action after)
        {
            voice.Stop(); voiceRevision++; int revision = voiceRevision; voicePlaying = false; voicePcm = Audio.ReadPcm(File.ReadAllBytes(file));
            traceWave = SignalAnalysis.Envelope(voicePcm, 400); traceDuration = voicePcm.Length / 88200.0;
            voice.Open(new Uri(file)); voice.Volume = settings.Volume; playbackEnd = DateTime.MaxValue; playbackFinished = after;
            EventHandler opened = null; opened = delegate { voice.MediaOpened -= opened; if (revision != voiceRevision) return; playbackEnd = DateTime.UtcNow.AddSeconds(Math.Min(duration, traceDuration)); voicePlaying = true; voice.Play(); }; voice.MediaOpened += opened;
        }
        void Record()
        {
            if (microphone.Recording) { FinishRecording(); return; }
            if (!IsTurn || !sourcePrepared || recordPending || processingTake) return;
            try
            {
                voice.Stop(); voiceRevision++; voicePlaying = false; playbackFinished = null; playbackEnd = DateTime.MinValue; take = null; rawTake = null;
                microphone.Start(settings.Microphone); recordStart = DateTime.UtcNow; recordDuration = state.pack.lines[state.turn].end - state.pack.lines[state.turn].start;
                expectedRound = state.round; expectedTurn = state.turn; Text("SignalLabel", "● МИКРОФОН · ЗАПИСЬ");
                Send(new Message { kind = "recording", index = expectedTurn, round = expectedRound, flag = true });
                var video = C<MediaElement>("Video"); video.Volume = 0; video.Position = TimeSpan.FromSeconds(state.pack.lines[state.turn].start); video.Play(); Text("VideoLabel", "● ЗАПИСЬ · ОРИГИНАЛЬНЫЕ ГОЛОСА ВЫКЛЮЧЕНЫ"); RenderRound();
            }
            catch (Exception e) { Toast(e.Message); }
        }
        async void FinishRecording()
        {
            try
            {
                byte[] bytes = microphone.Stop(); C<MediaElement>("Video").Pause();
                if (IsTurn) Send(new Message { kind = "recording", index = state.turn, round = state.round, flag = false });
                Audio.Validate(bytes, recordDuration); rawTake = bytes; await ApplyEffect();
            }
            catch (Exception error) { if (IsTurn) Send(new Message { kind = "recording", index = state.turn, round = state.round, flag = false }); take = null; Toast(error.Message); if (state != null && state.phase != "lobby") RenderRound(); }
        }
        async Task SetTake(byte[] bytes)
        {
            expectedRound = state.round; expectedTurn = state.turn; recordDuration = state.pack.lines[state.turn].end - state.pack.lines[state.turn].start; Audio.Validate(bytes, recordDuration); rawTake = bytes; await ApplyEffect();
        }
        async Task ApplyEffect()
        {
            if (rawTake == null || !IsTurn) return; if (effectCancellation != null) effectCancellation.Cancel(); var cancellation = new CancellationTokenSource(); effectCancellation = cancellation;
            int revision = ++takeRevision, effect = selectedEffect; bool noise = C<CheckBox>("NoiseGateCheck").IsChecked == true; byte[] raw = rawTake;
            voice.Stop(); voiceRevision++; voicePlaying = false; playbackFinished = null; playbackEnd = DateTime.MinValue;
            C<MediaElement>("Video").Pause(); Text("VideoLabel", "ОРИГИНАЛЬНЫЕ ГОЛОСА ВЫКЛЮЧЕНЫ");
            processingTake = true; take = null; Text("RecordingTimer", "Готовим дубль…"); RenderRound();
            try
            {
                byte[] processed = await Task.Run(() => VoiceEffects.Apply(raw, effect, noise, recordDuration, cancellation.Token), cancellation.Token);
                var pcm = Audio.ReadPcm(processed); var wave = await Task.Run(() => SignalAnalysis.Envelope(pcm, 400));
                if (revision != takeRevision || !IsTurn) return; take = processed; voicePcm = pcm; traceWave = wave; traceDuration = pcm.Length / 88200.0;
                localTakeFile = Path.Combine(Paths.Cache, "take-" + Guid.NewGuid().ToString("N") + ".wav"); File.WriteAllBytes(localTakeFile, take);
                Text("SignalLabel", "МОЙ ДУБЛЬ · " + VoiceEffects.Names[effect].ToUpperInvariant()); Text("RecordingTimer", "Дубль готов. Можно послушать, перезаписать или нажать «Дальше».");
            }
            catch (OperationCanceledException) { }
            finally { if (revision == takeRevision) { processingTake = false; if (state != null && state.phase != "lobby") RenderRound(); } if (effectCancellation == cancellation) effectCancellation = null; cancellation.Dispose(); }
        }
        async Task Submit()
        {
            if (state.phase == "result")
            {
                if (finalFile == null) return; var picker = new Microsoft.Win32.SaveFileDialog { Filter = "Видео MP4|*.mp4", FileName = "Broken Phone - " + DateTime.Now.ToString("yyyy-MM-dd HH-mm") + ".mp4", InitialDirectory = Paths.Exports };
                if (picker.ShowDialog(window) == true) { File.Copy(finalFile, picker.FileName, true); Toast("Общий дубль сохранён: " + picker.FileName); } return;
            }
            if (take == null || !IsTurn || processingTake || state.turn != expectedTurn || state.round != expectedRound) return;
            voice.Stop(); voiceRevision++; playbackFinished = null; playbackEnd = DateTime.MinValue; voicePlaying = false; recordPending = true; RenderRound(); var room = client;
            try { byte[] bytes = take; int turn = expectedTurn; string round = expectedRound; await Task.Run(() => room.Send(new Message { kind = "take", data = Convert.ToBase64String(bytes), index = turn, round = round })); }
            finally { recordPending = false; if (state != null && state.phase != "lobby") RenderRound(); }
        }
        void ControlPlayback(string action, double position) { if (IsHost && state.phase == "result" && state.playbackVersion > 0) Send(new Message { kind = "playback", text = action, position = position, round = state.round }); }
        void SyncPlayback()
        {
            if (state == null || state.phase != "result" || !finalMediaPrepared || state.playbackVersion == 0 || client == null) return;
            long now = client.ServerNow; if (now < state.playbackAt) return; var video = C<MediaElement>("Video"); double target = Playback.Position(state, now);
            if (playbackAppliedVersion != state.playbackVersion)
            {
                if (!state.playing) video.Pause();
                video.Position = TimeSpan.FromSeconds(Math.Min(target, Math.Max(0, state.resultDuration - .04)));
                if (state.playing) video.Play();
                playbackAppliedVersion = state.playbackVersion; lastPlaybackCheck = now;
            }
            else if (now - lastPlaybackCheck > 500 && state.playing && endedPlaybackVersion != state.playbackVersion)
            {
                if (Math.Abs(video.Position.TotalSeconds - target) > .25) video.Position = TimeSpan.FromSeconds(Math.Min(target, Math.Max(0, state.resultDuration - .04)));
                lastPlaybackCheck = now;
            }
            updatingPlaybackSlider = true; C<Slider>("PlaybackSlider").Maximum = Math.Max(.1, state.resultDuration); if (!draggingPlayback) C<Slider>("PlaybackSlider").Value = target; updatingPlaybackSlider = false;
            Text("PlaybackTime", ClockText(target) + " / " + ClockText(state.resultDuration));
        }
        void FullScreen()
        {
            if (!fullScreen) { savedWindowState = window.WindowState; savedWindowStyle = window.WindowStyle; window.WindowStyle = WindowStyle.None; window.WindowState = WindowState.Maximized; fullScreen = true; }
            else { window.WindowStyle = savedWindowStyle; window.WindowState = savedWindowState; fullScreen = false; }
        }
        void Tick()
        {
            SyncSteamName();
            TickMicrophonePanel();
            if (microphone.Recording)
            {
                double elapsed = (DateTime.UtcNow - recordStart).TotalSeconds;
                C<SignalView>("Signal").Update(microphone.Waveform, elapsed / recordDuration, elapsed / recordDuration, true);
                Text("SignalTime", elapsed.ToString("0.0") + " / " + recordDuration.ToString("0.0") + " с"); Text("RecordingTimer", "● Запись. Нажми R или «Стоп», чтобы закончить раньше."); C<TextBlock>("TurnPrompt").Visibility = Visibility.Collapsed;
                if (elapsed >= recordDuration) FinishRecording();
            }
            else
            {
                double cursor = voicePlaying && traceDuration > 0 ? voice.Position.TotalSeconds / traceDuration : 0;
                C<SignalView>("Signal").Update(traceWave, 1, cursor, false); Text("SignalTime", ClockText(voicePlaying ? voice.Position.TotalSeconds : traceDuration));
            }
            if (playbackEnd != DateTime.MinValue && playbackEnd != DateTime.MaxValue && DateTime.UtcNow >= playbackEnd)
            { playbackEnd = DateTime.MinValue; voice.Stop(); voicePlaying = false; var action = playbackFinished; playbackFinished = null; if (action != null) action(); }
            SyncPlayback();
        }
    }
}
