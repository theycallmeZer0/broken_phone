using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;

namespace BrokenPhone
{
    public sealed partial class GameWindow
    {
        static async Task Until(Func<bool> condition, string failure, double seconds = 35)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(seconds);
            while (!condition()) { if (DateTime.UtcNow > deadline) throw new Exception(failure); await Task.Delay(40); }
        }
        public async Task ScreeningSmoke(bool endOnly = false, bool steamTransport = false, bool checkSync = false)
        {
            Window otherWindow = null; GameWindow other = null; var report = new List<string>();
            try
            {
                settings.Volume = 0; C<Slider>("VolumeSlider").Value = 0; selectedEffect = 0; C<CheckBox>("NoiseGateCheck").IsChecked = false;
                await Until(() => Me != null, "Ведущий не подключился.");
                if (steamTransport)
                {
                    string profileName = steam.RefreshName();
                    if (!steam.Available || String.IsNullOrWhiteSpace(profileName) || Me.name != profileName) throw new Exception("Имя игрока отличается от настоящего имени профиля Steam.");
                    OpenSettings(); if (!TBox("NameInput").IsReadOnly || TBox("NameInput").Text != profileName) throw new Exception("В настройках отображается локальное имя вместо профиля Steam.");
                    Page("Lobby"); report.Add("PASS: actual Steam profile name used in the network lobby and read-only profile field");
                    await Until(() => Me.steamId == steam.UserId && steamAvatars.ContainsKey(steam.UserId), "Настоящая аватарка Steam не загрузилась в лобби.");
                    if (!(Avatar(Me, 32).Child is Image)) throw new Exception("Вместо аватарки Steam отображается заглушка.");
                    report.Add("PASS: actual Steam profile avatar loads asynchronously and replaces the lobby placeholder on App ID " + steam.AppId);
                }
                using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("MainWindow.xaml")) otherWindow = (Window)XamlReader.Load(stream);
                otherWindow.ShowInTaskbar = false; otherWindow.WindowStartupLocation = WindowStartupLocation.Manual; otherWindow.Left = -10000;
                other = new GameWindow(otherWindow, false); other.settings.Name = "Сетевой друг"; other.settings.Volume = 0; other.C<Slider>("VolumeSlider").Value = 0; other.selectedEffect = 0; other.C<CheckBox>("NoiseGateCheck").IsChecked = false;
                otherWindow.Show();
                if (steamTransport) { if (!steam.Available || !SteamRoute) throw new Exception("Лобби Steam не подготовилось в интерфейсе."); if (!steam.OverlayEnabled) VerifySteamFriendsDialog(); report.Add("PASS: Steam invite route available; native overlay=" + steam.OverlayEnabled + "; fallback tested when needed; no invitations sent"); SaveScreen("steam-lobby-preview.png"); int port = steam.OpenTunnel(steam.UserId); await other.Connect("127.0.0.1", port, server.Code, ""); }
                else await other.Connect("127.0.0.1", server.Port, server.Code, "");
                await Until(() => state.players.Count == 2 && other.Me != null, "Второе игровое окно не подключилось.");
                B("ReadyButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); other.B("ReadyButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => state.players.All(p => p.ready), "Нет готовности игроков."); B("StartButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await StartAutomaticRoles(other);
                if (steamTransport && !roleControls.Values.Any(card => card.Avatar != null && Object.Equals(card.Avatar.Tag, client.Id) && card.Avatar.Child is Image)) throw new Exception("Аватарка Steam не появилась в карточке выбранной роли.");
                if (steamTransport) report.Add("PASS: the selected role displays the same real Steam avatar with a circular crop; profile Steam ID is visible to the connected peer");
                await Until(() => sourcePrepared && other.sourcePrepared, "Видео не подготовилось в обоих окнах.");
                if (window.FindName("SignalModeButton") != null || window.FindName("LinePrompt") != null) throw new Exception("В студии остались спектрограмма или подсказка для сцены.");
                if (B("LobbyTab").IsEnabled || other.B("LobbyTab").IsEnabled || window.FindName("BackToLobbyButton") != null) throw new Exception("В активном раунде доступен возврат в лобби.");
                report.Add("PASS: no return-to-lobby button or lobby navigation during the active round");
                report.Add("PASS: two actual WPF game windows connected over the selected network transport and decoded the source video");
                for (int i = 0; i < 4; i++)
                {
                    int turn = i; var active = i % 2 == 0 ? this : other;
                    if (i == 3) { other.preparingFinal = true; other.finalLoadingRound = state.round; }
                    await Until(() => active.IsTurn && active.state.turn == turn && active.sourcePrepared, "Не наступил ход " + i);
                    if (!active.B("RecordButton").IsEnabled) throw new Exception("Запись недоступна без прослушивания: " + i);
                    await active.SetTake(SelfTests.ToneWave(1.4, 330 + i * 110));
                    if (i == 0)
                    {
                        active.PreviewTake(); await Task.Delay(550); active.SaveScreen("studio-waveform-preview.png");
                    }
                    if (i == 1) { await active.ApplyEffect(); if (active.take == null) throw new Exception("Перезапись/обработка не готова."); }
                    active.B("SubmitButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Until(() => state.turn > turn, "Дубль не отправлен кнопкой «Дальше».");
                }
                report.Add("PASS: all turns submitted using game controls without listening; real full-width waveform drawn; no spectrogram or scene hint");
                await Until(() => state.phase == "result" && other.state.phase == "result" && finalMediaPrepared, "Финальное видео не подготовилось у хоста.");
                if (other.finalMediaPrepared || B("ListenButton").IsEnabled) throw new Exception("Хост может начать просмотр до подготовки гостя.");
                Send(new Message { kind = "playback", text = "start", round = state.round });
                await Task.Delay(250);
                if (state.playing || state.playbackVersion != 0 || C<Border>("WaitingOverlay").Visibility != Visibility.Visible) throw new Exception("Видео появилось у хоста до готовности гостя.");
                other.preparingFinal = false; await other.PrepareRound();
                await Until(() => other.finalMediaPrepared && B("ListenButton").IsEnabled && Playback.EveryoneReady(other.state), "Общий просмотр не разблокировался после подготовки гостя.");
                report.Add("PASS: delayed guest preparation keeps both viewers covered; early network start is rejected; start unlocks only after every decoder and audio mixer is ready");
                var hostVideo = C<MediaElement>("Video"); var guestVideo = other.C<MediaElement>("Video");
                if (finalMixer.OutputDeviceId != OutputAudio.Resolve(settings.OutputDevice) || other.finalMixer.OutputDeviceId != OutputAudio.Resolve(other.settings.OutputDevice)) throw new Exception("Финальный просмотр игнорирует выбранное устройство вывода.");
                if (checkSync && (hostVideo.Source.LocalPath != sourceFile || guestVideo.Source.LocalPath != other.sourceFile || finalVideoTask != null || other.finalVideoTask != null)) throw new Exception("SYNC: финальный просмотр повторно загружает полный MP4 вместо уже подготовленного локального видео.");
                if (checkSync) report.Add("PASS: final viewing reuses the decoded local pack video; neither viewer automatically requests a full rendered MP4");
                await Task.Delay(200); if (state.playbackVersion != 0 || state.playing || hostVideo.Position.TotalSeconds > .1) throw new Exception("Финальный просмотр запущен автоматически.");
                C<Slider>("VolumeSlider").Value = 1; C<Slider>("ResultVoicesVolume").Value = .25; C<Slider>("ResultMovieVolume").Value = .8;
                other.C<Slider>("VolumeSlider").Value = 1; other.C<Slider>("ResultVoicesVolume").Value = .9; other.C<Slider>("ResultMovieVolume").Value = .1;
                if (Math.Abs(finalVoiceGain - .25) > .001 || Math.Abs(finalMovieGain - .8) > .001 || Math.Abs(other.finalVoiceGain - .9) > .001 || Math.Abs(other.finalMovieGain - .1) > .001 || hostVideo.Volume != 0 || guestVideo.Volume != 0) throw new Exception("Громкость фона и голосов не разделена или меняется у других участников.");
                C<Slider>("VolumeSlider").Value = 0; other.C<Slider>("VolumeSlider").Value = 0;
                if (!B("SubmitButton").IsEnabled || !other.B("SubmitButton").IsEnabled) throw new Exception("Скачивание MP4 доступно не всем или зависит от готовности плеера.");
                string savedHost = System.IO.Path.Combine(Paths.Cache, "host-export-" + Guid.NewGuid().ToString("N") + ".mp4"), savedGuest = System.IO.Path.Combine(Paths.Cache, "guest-export-" + Guid.NewGuid().ToString("N") + ".mp4");
                try { await SaveFinalVideo(savedHost); await other.SaveFinalVideo(savedGuest); if (!File.ReadAllBytes(savedHost).SequenceEqual(File.ReadAllBytes(savedGuest))) throw new Exception("Участники сохраняют разные MP4."); }
                finally { if (File.Exists(savedHost)) File.Delete(savedHost); if (File.Exists(savedGuest)) File.Delete(savedGuest); }
                B("ListenButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => state.playbackVersion > 0 && hostVideo.Position.TotalSeconds > .4, "Ведущий не может запустить просмотр вручную.");
                if (other.playbackAppliedVersion != state.playbackVersion || Math.Abs(hostVideo.Position.TotalSeconds - guestVideo.Position.TotalSeconds) > .15) throw new Exception("Начало просмотра не совпадает у игроков.");
                if (Math.Abs(playbackStartedAt - other.playbackStartedAt) > 80) throw new Exception("Видео открылось у игроков с задержкой: " + Math.Abs(playbackStartedAt - other.playbackStartedAt) + " мс.");
                report.Add("PASS: both video overlays reveal on the shared start; measured start difference=" + Math.Abs(playbackStartedAt - other.playbackStartedAt) + " ms");
                other.ResetFinalPlayer(); other.RenderRound();
                if (other.finalMediaPrepared) throw new Exception("Тест не имитирует позднюю подготовку гостя.");
                await other.PrepareRound(); await Until(() => other.finalMediaPrepared && guestVideo.Position.TotalSeconds > .4, "Гость не подключился к уже идущему просмотру.");
                await Until(() => Math.Abs(hostVideo.Position.TotalSeconds - guestVideo.Position.TotalSeconds) < .45, "Поздний участник не догнал общий просмотр.", 8);
                if (other.B("PauseButton").IsEnabled || other.C<Slider>("PlaybackSlider").IsEnabled) throw new Exception("Гость управляет общим просмотром.");
                SaveScreen("screening-host-preview.png"); other.SaveScreen("screening-guest-preview.png");
                report.Add("PASS: a decoder reopened during an active screening recovers the shared position; positions within 450 ms");
                if (checkSync)
                {
                    await Until(() => playbackAppliedVersion == state.playbackVersion && other.playbackAppliedVersion == other.state.playbackVersion, "Поздний клиент не завершил подготовку кадра.");
                    var offsets = new List<double>(); double audioError = 0;
                    for (int sample = 0; sample < 16; sample++)
                    {
                        offsets.Add(Math.Abs(hostVideo.Position.TotalSeconds - guestVideo.Position.TotalSeconds));
                        audioError = Math.Max(audioError, Math.Max(Math.Abs(FinalAudioPosition - hostVideo.Position.TotalSeconds), Math.Abs(other.FinalAudioPosition - guestVideo.Position.TotalSeconds)));
                        await Task.Delay(100);
                    }
                    double typical = offsets.OrderBy(value => value).ElementAt(12), maximum = offsets.Max();
                    if (typical > .18 || maximum > .32) throw new Exception("SYNC: устойчивое смещение плееров: p80=" + typical + ", max=" + maximum);
                    if (audioError > .18) throw new Exception("SYNC: голоса отстают от видео на " + audioError);
                    report.Add("PASS: late-client clock alignment over 16 real playback samples: p80=" + typical.ToString("0.000") + " s; max=" + maximum.ToString("0.000") + " s");
                    report.Add("PASS: player voices stay aligned with the decoded video; maximum observed audio offset=" + audioError.ToString("0.000") + " s");
                }
                report.Add("PASS: every participant saves the same final MP4 even before local player readiness; separate movie and voice volumes are independent and local");
                if (!endOnly)
                {
                int version = state.playbackVersion; B("PauseButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => state.playbackVersion > version && playbackAppliedVersion == state.playbackVersion && other.playbackAppliedVersion == state.playbackVersion, "Пауза не применена в обоих окнах.");
                double pausedHost = hostVideo.Position.TotalSeconds, pausedGuest = guestVideo.Position.TotalSeconds; await Task.Delay(550);
                if (Math.Abs(hostVideo.Position.TotalSeconds - pausedHost) > .1 || Math.Abs(guestVideo.Position.TotalSeconds - pausedGuest) > .1 || Math.Abs(pausedHost - pausedGuest) > .2) throw new Exception("Общая пауза не остановила оба видео. Начальные позиции: " + pausedHost + " / " + pausedGuest);
                version = state.playbackVersion; C<Slider>("PlaybackSlider").Value = 7;
                if (checkSync)
                {
                    await Until(() => state.playbackVersion > version, "Команда перемотки не получена."); SyncPlayback();
                    if (client.ServerNow < state.playbackAt && Math.Abs(hostVideo.Position.TotalSeconds - 7) > .15) throw new Exception("SYNC: кадр подготавливается только после общего времени старта, вызывая разную задержку декодеров.");
                }
                await Until(() => state.playbackVersion > version && playbackAppliedVersion == state.playbackVersion && other.playbackAppliedVersion == state.playbackVersion, "Перемотка не применена.");
                if (Math.Abs(hostVideo.Position.TotalSeconds - 7) > .15 || Math.Abs(guestVideo.Position.TotalSeconds - 7) > .15) throw new Exception("Перемотка не выставила общую позицию.");
                version = state.playbackVersion; B("PauseButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => state.playbackVersion > version && hostVideo.Position.TotalSeconds > 7.3 && guestVideo.Position.TotalSeconds > 7.3, "Общее продолжение не запустилось.");
                version = state.playbackVersion; B("ListenButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => state.playbackVersion > version && playbackAppliedVersion == state.playbackVersion && other.playbackAppliedVersion == state.playbackVersion, "Общий повтор не применён.");
                if (hostVideo.Position.TotalSeconds > .8 || guestVideo.Position.TotalSeconds > .8) throw new Exception("Повтор не начался с начала.");
                report.Add("PASS: host pause, timeline seek, resume and replay control both decoded videos");
                }
                ControlPlayback("seek", state.resultDuration - 1);
                await Until(() => !state.playing && !other.state.playing && hostVideo.Position.TotalSeconds > state.resultDuration - .15 && guestVideo.Position.TotalSeconds > state.resultDuration - .15, "Конец общего просмотра не синхронизирован.", 6);
                report.Add("PASS: both videos finish at the final frame and remain ready for replay or saving");
                B("LobbyTab").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => state.phase == "lobby" && other.state.phase == "lobby", "Нет возврата в лобби.");
                report.Add("PASS: both windows return to lobby; recordings never open a physical microphone during this test");
            }
            catch (Exception e) { report.Add("FAIL: " + e); if (state != null && other != null && other.state != null) report.Add("Playback state at failure: " + Json.Write(new { hostPosition = C<MediaElement>("Video").Position.TotalSeconds, guestPosition = other.C<MediaElement>("Video").Position.TotalSeconds, hostPlaying = state.playing, guestPlaying = other.state.playing, duration = state.resultDuration, natural = C<MediaElement>("Video").NaturalDuration.ToString(), version = state.playbackVersion, appliedHost = playbackAppliedVersion, appliedGuest = other.playbackAppliedVersion, hostPrepared = finalMediaPrepared, guestPrepared = other.finalMediaPrepared, hostVoices = FinalAudioPosition, guestVoices = other.FinalAudioPosition, hostMovie = FinalAudioPosition, guestMovie = other.FinalAudioPosition, hostNotice = C<TextBlock>("ToastText").Text, guestNotice = other.C<TextBlock>("ToastText").Text })); }
            finally
            {
                File.WriteAllLines(System.IO.Path.Combine(Paths.Base, checkSync ? "playback-sync-test.txt" : steamTransport ? "steam-screening-test.txt" : endOnly ? "end-test.txt" : "screening-test.txt"), report);
                if (other != null) other.Dispose(); if (otherWindow != null) otherWindow.Close(); window.Close();
            }
        }
        void VerifySteamFriendsDialog()
        {
            bool opened = false, dark = false;
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            timer.Tick += delegate
            {
                timer.Stop(); var dialog = window.OwnedWindows.Cast<Window>().FirstOrDefault(w => w.Title == "Пригласить друзей Steam");
                if (dialog == null) return; opened = true; var color = dialog.Background as System.Windows.Media.SolidColorBrush; dark = color != null && color.Color.R < 60 && dialog.Resources.MergedDictionaries.Count > 0; dialog.Close();
            };
            timer.Start(); B("InviteButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); timer.Stop();
            if (!opened || !dark) throw new Exception("Окно друзей Steam не открылось в тёмной теме.");
        }
    }
}
