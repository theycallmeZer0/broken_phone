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
        bool originalPreview;
        void InitializeOriginalVolume()
        {
            var slider = C<Slider>("OriginalVolume");
            slider.ValueChanged += delegate { settings.OriginalPreviewVolume = slider.Value; Persist(); UpdatePreviewVolume(); };
            slider.Value = Math.Max(0, Math.Min(1, settings.OriginalPreviewVolume)); UpdatePreviewVolume();
        }
        void UpdatePreviewVolume()
        {
            voice.Volume = C<Slider>("VolumeSlider").Value * (originalPreview ? C<Slider>("OriginalVolume").Value : 1);
            Text("OriginalVolumeLevel", (C<Slider>("OriginalVolume").Value * 100).ToString("0") + "%");
        }
        static string ClockText(double value) { return TimeSpan.FromSeconds(Math.Max(0, value)).ToString(@"mm\:ss"); }
        void UpdateEffects()
        {
            var buttons = C<StackPanel>("VoiceEffectButtons").Children.OfType<Button>().ToArray();
            for (int i = 0; i < buttons.Length; i++)
            {
                buttons[i].Background = Brush(i == selectedEffect ? "#C8B8CC" : "#30364A"); buttons[i].Foreground = Brush(i == selectedEffect ? "#2E2130" : "#CDD2E5");
                buttons[i].IsEnabled = IsTurn && !microphone.Recording && !recordPending;
            }
            C<CheckBox>("NoiseGateCheck").IsEnabled = IsTurn && !microphone.Recording && !recordPending;
        }
        void RenderRound()
        {
            EnsureTurnGroup();
            bool loading = state.phase == "loading", result = state.phase == "result", rendering = state.phase == "rendering";
            var main = C<Grid>("StudioMain"); main.RowDefinitions[1].Height = new GridLength(1, GridUnitType.Star);
            main.RowDefinitions[2].Height = new GridLength(result || loading ? 0 : 54); main.RowDefinitions[3].Height = new GridLength(result || loading ? 0 : 86); main.RowDefinitions[4].Height = new GridLength(result || loading ? 0 : 42); main.RowDefinitions[6].Height = new GridLength(result ? 77 : IsTurn ? 34 : 0);
            C<StackPanel>("OriginalVolumePanel").Visibility = IsTurn ? Visibility.Visible : Visibility.Collapsed;
            C<Slider>("OriginalVolume").IsEnabled = IsTurn && state.pack.lines[StudioTurn].originalAvailable && !microphone.Recording;
            C<Grid>("SignalPanel").Visibility = result || loading ? Visibility.Collapsed : Visibility.Visible; C<Grid>("EffectsPanel").Visibility = result || loading ? Visibility.Collapsed : Visibility.Visible; C<Grid>("ResultTimeline").Visibility = result ? Visibility.Visible : Visibility.Collapsed;
            Text("RoundEyebrow", loading ? "ПОДГОТОВКА ПАКА / " + state.pack.title : result ? "ВАШ ОБЩИЙ ДУБЛЬ / " + state.pack.title : rendering ? "СОБИРАЕМ ОБЩИЙ ДУБЛЬ" : state.pack.title.ToUpperInvariant() + " / РЕПЛИКА " + (StudioTurn + 1) + " ИЗ " + state.order.Length);
            Text("RoundTitle", loading ? "Загружаем пак у всех игроков" : result ? "Смотрим вместе!" : rendering ? "Все голоса на месте." : IsTurn ? "Твой выход, " + state.Active.name + "!" : "У микрофона — " + (state.Active == null ? "…" : state.Active.name));
            Text("TurnPrompt", loading ? "Озвучка начнётся автоматически, когда все будут готовы." : result ? (IsHost ? "Пауза, перемотка и повтор у ведущего синхронизируются у всех." : "Ведущий запускает просмотр для всей комнаты.") : rendering ? "Сводим реплики с видео…" : IsTurn ? "Предыдущую реплику и оригинал можно прослушать по желанию. R — запись, Enter — дальше." : "Ждём твоего хода.");
            Text("VideoLabel", result ? "ГОТОВЫЙ ДУБЛЬ · ВСЕ ГОЛОСА" : "ОРИГИНАЛЬНЫЕ ГОЛОСА ВЫКЛЮЧЕНЫ");
            Text("RoleCaption", IsTurn ? (state.pack.lines[StudioTurn].role ?? "ТВОЯ РЕПЛИКА").ToUpperInvariant() : "СЕТЕВАЯ СТУДИЯ");
            Text("LineCaption", IsTurn ? state.pack.lines[StudioTurn].text : "");
            B("ListenButton").Content = result ? state.playbackVersion == 0 ? "▶ Запустить у всех" : "↻ Смотреть ещё раз" : "◀ Предыдущая";
            B("ListenButton").IsEnabled = result ? IsHost && finalMediaPrepared && Playback.EveryoneReady(state) : IsTurn && StudioTurn > 0 && sourcePrepared && !microphone.Recording && !processingTake && !recordPending;
            B("OriginalButton").Visibility = result || loading ? Visibility.Collapsed : Visibility.Visible;
            B("OriginalButton").IsEnabled = IsTurn && sourcePrepared && state.pack.lines[StudioTurn].originalAvailable && !microphone.Recording && !processingTake && !recordPending;
            B("OriginalButton").ToolTip = IsTurn && !state.pack.lines[StudioTurn].originalAvailable ? "В этом паке нет исходного звука реплики" : "Прослушать исходную реплику — необязательно";
            B("RecordButton").Visibility = result || loading ? Visibility.Collapsed : Visibility.Visible;
            B("RecordButton").IsEnabled = IsTurn && sourcePrepared && !recordPending && !processingTake;
            B("RecordButton").Content = microphone.Recording ? "■ Стоп" : rawTake != null ? "● Перезапись" : "● Запись";
            B("ReviewButton").Visibility = result || loading ? Visibility.Collapsed : Visibility.Visible; B("ReviewButton").IsEnabled = IsTurn && take != null && !microphone.Recording && !processingTake && !recordPending;
            B("SubmitButton").Content = result ? savingFinal ? "↓ Сохраняем…" : "↓ Скачать MP4" : "Дальше ▶"; B("SubmitButton").IsEnabled = result ? !savingFinal : IsTurn && (take != null || groupEnd > groupStart) && !microphone.Recording && !processingTake && !recordPending;
            B("ListenButton").ToolTip = result ? "Общий старт после подготовки видео и звука у всех игроков" : IsTurn && StudioTurn > groupStart ? "Открыть предыдущую свою реплику" : "Прослушать реплику предыдущего игрока";
            B("SubmitButton").ToolTip = result ? "Сохранить общий дубль на компьютер" : IsTurn && StudioTurn < groupEnd ? "Открыть следующую свою реплику" : "Передать весь записанный блок следующему игроку";
            B("PauseButton").Visibility = result ? Visibility.Visible : Visibility.Collapsed; B("PauseButton").IsEnabled = IsHost && state.playbackVersion > 0 && finalMediaPrepared; B("PauseButton").Content = state.playing ? "Ⅱ Пауза у всех" : "▶ Продолжить";
            C<StackPanel>("RoundActions").Visibility = !loading && (result || IsTurn) ? Visibility.Visible : Visibility.Collapsed;
            C<Slider>("PlaybackSlider").IsEnabled = IsHost && state.playbackVersion > 0 && finalMediaPrepared;
            B("CancelPreparationButton").Visibility = loading && IsHost ? Visibility.Visible : Visibility.Collapsed;
            C<ProgressBar>("PreparationProgress").Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
            C<ProgressBar>("PreparationProgress").Value = state.players.Count == 0 ? 0 : state.players.Average(p => p.downloadProgress);
            var waiting = C<Border>("WaitingOverlay"); waiting.Visibility = !IsTurn && !result || result && (!finalMediaPrepared || state.playbackVersion == 0 || state.playing && playbackAppliedVersion != state.playbackVersion) ? Visibility.Visible : Visibility.Collapsed;
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
            else if (result && (!finalMediaPrepared || state.playbackVersion == 0 || state.playing && playbackAppliedVersion != state.playbackVersion))
            {
                bool everyone = Playback.EveryoneReady(state);
                Text("WaitingTitle", state.playing ? "Начинаем вместе…" : everyone ? "Все готовы к просмотру" : "Готовим общий просмотр");
                Text("WaitingDetail", state.playing ? "Видео и голоса начнутся по общей команде." : !everyone ? "Ждём загрузки видео и звука у каждого игрока." : IsHost ? "Нажми «Запустить у всех», чтобы начать просмотр." : "Ведущий запускает просмотр для всех.");
                Text("WaitingPosition", "Готовы: " + state.players.Count(player => state.resultReady.Contains(player.id)) + " / " + state.players.Count + " · MP4 можно скачать отдельно.");
            }
            var dots = C<StackPanel>("LineDots"); dots.Children.Clear();
            for (int i = 0; i < Math.Min(32, state.order.Length); i++) dots.Children.Add(new System.Windows.Shapes.Ellipse { Width = 5, Height = 5, Margin = new Thickness(0, 0, 6, 0), Fill = Brush(DraftRecorded(i) ? "#C5B4D2" : i == StudioTurn ? "#C8B8CC" : "#40485F") });
            var turns = C<StackPanel>("TurnList"); turns.Children.Clear();
            if (loading)
                foreach (var player in state.players) turns.Children.Add(new TextBlock { Text = (player.prepared ? "✓ " : "⇩ ") + player.name + " · " + (player.downloadProgress * 100).ToString("0") + "%\n" + (player.loadStatus ?? "Ожидаем"), TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 8, 0, 8), Foreground = Brush(player.prepared ? "#C5B4D2" : "#BDB2CA") });
            else
            {
                for (int i = 0; i < state.order.Length; i++)
                {
                    var player = state.players.FirstOrDefault(p => p.id == state.order[i]); string label = (i + 1) + ". " + (player == null ? "Игрок" : player.name) + " · " + state.pack.lines[i].role;
                    turns.Children.Add(new TextBlock { Text = (DraftRecorded(i) ? "✓  " : i == StudioTurn ? "●  " : "○  ") + label, ToolTip = label, TextWrapping = TextWrapping.Wrap, FontSize = 14, Margin = new Thickness(0, 6, 0, 6), FontWeight = FontWeights.SemiBold, Foreground = Brush(DraftRecorded(i) ? "#C5B4D2" : "#BDB2CA") });
                }
            }
            UpdateChainPage();
            C<TextBlock>("RecordingTimer").Foreground = Brush(take != null ? "#C5B4D2" : processingTake ? "#CFB3D9" : "#DE8EAA");
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
                    await PrepareFinalMedia(room, round);
                }
                finally { if (finalLoadingRound == round) preparingFinal = false; }
                return;
            }
            if (state.phase != "recording" && state.phase != "loading") return;
            if (sourceFile == null)
            {
                if (videoDownload != null && (videoDownload.IsFaulted || videoDownload.IsCanceled)) videoDownload = null;
                EnsureSelectedPack();
                string file;
                try { file = await videoDownload; }
                catch (OperationCanceledException) { return; }
                catch (Exception error) { if (room == client && state.round == round && state.phase == "loading") Send(new Message { kind = "pack_failed", round = round, text = error.GetBaseException().Message }); throw; }
                if (room != client || state.round != round || state.phase != "recording" && state.phase != "loading") return; sourceFile = file;
            }
            var player = C<MediaElement>("Video"); videoMode = "source"; videoRound = round;
            if (player.Source == null || player.Source.LocalPath != sourceFile) { sourcePrepared = false; player.Source = new Uri(sourceFile); player.Volume = 0; player.Pause(); }
            else { sourcePrepared = player.NaturalVideoWidth > 0; player.Position = TimeSpan.FromSeconds(state.pack.lines[StudioTurn].start); player.Pause(); }
            RenderRound(); AcknowledgePrepared();
            if (IsTurn) { var guideTask = PrepareOriginalGuide(room, round, StudioTurn); }
            else C<SignalView>("Signal").SetGuide(null);
        }
        async Task Listen()
        {
            if (IsTurn && StudioTurn > groupStart) { await OpenDraft(StudioTurn - 1); return; }
            if (state == null) return;
            if (state.phase == "result") { if (!finalMediaPrepared) await PrepareRound(); else ControlPlayback("restart", 0); return; }
            if (!IsTurn || StudioTurn == 0 || microphone.Recording || processingTake) return;
            string round = state.round; int turn = StudioTurn; var room = client;
            string file = previousFile ?? await room.Asset("take:" + (turn - 1), ".wav"); if (room != client || state.round != round || StudioTurn != turn) return; previousFile = file;
            Preview(file, state.pack.lines[turn - 1], "ПРЕДЫДУЩАЯ РЕПЛИКА", delegate { Send(new Message { kind = "heard", round = round, index = turn }); });
        }
        async Task Original()
        {
            if (!IsTurn || !state.pack.lines[StudioTurn].originalAvailable || microphone.Recording || processingTake) return;
            var room = client; string round = state.round; int turn = StudioTurn; B("OriginalButton").IsEnabled = false;
            try { var clip = await OriginalClipFor(room, round, turn); if (room == client && state.round == round && StudioTurn == turn && !microphone.Recording && !processingTake && !recordPending) Preview(clip.File, state.pack.lines[turn], "ОРИГИНАЛ", null, true); }
            finally { if (state != null && state.phase != "lobby") RenderRound(); }
        }
        void PreviewTake() { if (localTakeFile != null && IsTurn && !microphone.Recording && !processingTake && !recordPending) Preview(localTakeFile, state.pack.lines[StudioTurn], "МОЙ ДУБЛЬ · " + VoiceEffects.Names[selectedEffect].ToUpperInvariant(), null); }
        void Preview(string file, Line line, string label, Action after, bool original = false)
        {
            var video = C<MediaElement>("Video"); video.Volume = 0; video.Position = TimeSpan.FromSeconds(line.start); video.Play(); Text("SignalLabel", label); Text("VideoLabel", label);
            PlayVoice(file, line.end - line.start, delegate { video.Pause(); Text("VideoLabel", "ОРИГИНАЛЬНЫЕ ГОЛОСА ВЫКЛЮЧЕНЫ"); if (IsTurn) video.Position = TimeSpan.FromSeconds(state.pack.lines[StudioTurn].start); if (after != null) after(); }, original);
        }
        void PlayVoice(string file, double duration, Action after, bool original = false)
        {
            voice.Close(); voiceRevision++; int revision = voiceRevision; voicePlaying = false; voicePcm = Audio.ReadPcm(File.ReadAllBytes(file));
            traceWave = SignalAnalysis.Envelope(voicePcm, 400); traceDuration = voicePcm.Length / 88200.0;
            originalPreview = original; UpdatePreviewVolume(); playbackEnd = DateTime.MaxValue; playbackFinished = after;
            EventHandler opened = null; opened = delegate { voice.MediaOpened -= opened; if (revision != voiceRevision) return; playbackEnd = DateTime.UtcNow.AddSeconds(Math.Min(duration, traceDuration)); voicePlaying = true; voice.Play(); }; voice.MediaOpened += opened;
            voice.Open(new Uri(file));
        }
        void Record()
        {
            if (microphone.Recording) { FinishRecording(); return; }
            if (!IsTurn || !sourcePrepared || recordPending || processingTake) return;
            try
            {
                voice.Stop(); voiceRevision++; voicePlaying = false; playbackFinished = null; playbackEnd = DateTime.MinValue; take = null; rawTake = null;
                microphone.Start(settings.Microphone); recordStart = DateTime.UtcNow; recordDuration = state.pack.lines[StudioTurn].end - state.pack.lines[StudioTurn].start;
                expectedRound = state.round; expectedTurn = StudioTurn; Text("SignalLabel", "● МИКРОФОН · ЗАПИСЬ");
                Send(new Message { kind = "recording", index = state.turn, round = expectedRound, flag = true });
                var video = C<MediaElement>("Video"); video.Volume = 0; video.Position = TimeSpan.FromSeconds(state.pack.lines[StudioTurn].start); video.Play(); Text("VideoLabel", "● ЗАПИСЬ · ОРИГИНАЛЬНЫЕ ГОЛОСА ВЫКЛЮЧЕНЫ"); RenderRound();
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
            expectedRound = state.round; expectedTurn = StudioTurn; recordDuration = state.pack.lines[StudioTurn].end - state.pack.lines[StudioTurn].start; Audio.Validate(bytes, recordDuration); rawTake = bytes; await ApplyEffect();
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
                var picker = new Microsoft.Win32.SaveFileDialog { Filter = "Видео MP4|*.mp4", FileName = "Broken Phone - " + DateTime.Now.ToString("yyyy-MM-dd HH-mm") + ".mp4", InitialDirectory = Paths.Exports };
                if (picker.ShowDialog(window) == true) await SaveFinalVideo(picker.FileName); return;
            }
            await SubmitTurnGroup();
        }
        void ControlPlayback(string action, double position) { if (IsHost && state.phase == "result" && finalMediaPrepared && Playback.EveryoneReady(state)) Send(new Message { kind = "playback", text = state.playbackVersion == 0 ? "start" : action, position = position, round = state.round }); }
        void SyncPlayback() { TickFinalPlayback(); }
        void FullScreen()
        {
            ApplyDisplayMode(fullScreen ? "windowed" : "fullscreen"); Persist();
        }
        void Tick()
        {
            SyncSteamName();
            RefreshSteamAvatars();
            TickMicrophonePanel();
            if (microphone.Recording)
            {
                double elapsed = (DateTime.UtcNow - recordStart).TotalSeconds;
                C<SignalView>("Signal").UpdateLive(microphone.Waveform, recordDuration, microphone.CapturedSeconds);
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
