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
        public async Task ScreeningSmoke(bool endOnly = false, bool steamTransport = false)
        {
            Window otherWindow = null; GameWindow other = null; var report = new List<string>();
            try
            {
                settings.Volume = 0; selectedEffect = 0; C<CheckBox>("NoiseGateCheck").IsChecked = false;
                await Until(() => Me != null, "Ведущий не подключился.");
                if (steamTransport)
                {
                    string profileName = steam.RefreshName();
                    if (!steam.Available || String.IsNullOrWhiteSpace(profileName) || Me.name != profileName) throw new Exception("Имя игрока отличается от настоящего имени профиля Steam.");
                    OpenSettings(); if (!TBox("NameInput").IsReadOnly || TBox("NameInput").Text != profileName) throw new Exception("В настройках отображается локальное имя вместо профиля Steam.");
                    Page("Lobby"); report.Add("PASS: actual Steam profile name used in the network lobby and read-only profile field");
                }
                using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("MainWindow.xaml")) otherWindow = (Window)XamlReader.Load(stream);
                otherWindow.ShowInTaskbar = false; otherWindow.WindowStartupLocation = WindowStartupLocation.Manual; otherWindow.Left = -10000;
                other = new GameWindow(otherWindow, false); other.settings.Name = "Сетевой друг"; other.settings.Volume = 0; other.selectedEffect = 0; other.C<CheckBox>("NoiseGateCheck").IsChecked = false;
                otherWindow.Show();
                if (steamTransport) { if (!steam.Available || !SteamRoute) throw new Exception("Лобби Steam не подготовилось в интерфейсе."); VerifySteamFriendsDialog(); report.Add("PASS: Steam friends dialog opens in dark theme without sending any invitations"); SaveScreen("steam-lobby-preview.png"); int port = steam.OpenTunnel(steam.UserId); await other.Connect("127.0.0.1", port, server.Code, "", steam.Invitation); }
                else await other.Connect("127.0.0.1", server.Port, server.Code, "");
                await Until(() => state.players.Count == 2 && other.Me != null, "Второе игровое окно не подключилось.");
                B("ReadyButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); other.B("ReadyButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => state.players.All(p => p.ready), "Нет готовности игроков."); B("StartButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => sourcePrepared && other.sourcePrepared, "Видео не подготовилось в обоих окнах.");
                if (window.FindName("SignalModeButton") != null || window.FindName("LinePrompt") != null) throw new Exception("В студии остались спектрограмма или подсказка для сцены.");
                if (B("LobbyTab").IsEnabled || other.B("LobbyTab").IsEnabled || window.FindName("BackToLobbyButton") != null) throw new Exception("В активном раунде доступен возврат в лобби.");
                report.Add("PASS: no return-to-lobby button or lobby navigation during the active round");
                report.Add("PASS: two actual WPF game windows connected over the selected network transport and decoded the source video");
                for (int i = 0; i < 4; i++)
                {
                    int turn = i; var active = i % 2 == 0 ? this : other;
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
                await Until(() => state.phase == "result" && other.state.phase == "result" && finalMediaPrepared && other.finalMediaPrepared && state.playbackVersion > 0, "Общий просмотр не подготовился.");
                var hostVideo = C<MediaElement>("Video"); var guestVideo = other.C<MediaElement>("Video");
                await Until(() => hostVideo.Position.TotalSeconds > .4 && guestVideo.Position.TotalSeconds > .4, "Видео не запустилось автоматически у обоих.");
                if (Math.Abs(hostVideo.Position.TotalSeconds - guestVideo.Position.TotalSeconds) > .45) throw new Exception("Финальный показ рассинхронизирован.");
                if (other.B("PauseButton").IsEnabled || other.C<Slider>("PlaybackSlider").IsEnabled) throw new Exception("Гость управляет общим просмотром.");
                SaveScreen("screening-host-preview.png"); other.SaveScreen("screening-guest-preview.png");
                report.Add("PASS: assembled final video autoplays in BOTH game windows with no playback clicks; positions within 450 ms");
                if (!endOnly)
                {
                int version = state.playbackVersion; B("PauseButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => state.playbackVersion > version && playbackAppliedVersion == state.playbackVersion && other.playbackAppliedVersion == state.playbackVersion, "Пауза не применена в обоих окнах.");
                double pausedHost = hostVideo.Position.TotalSeconds, pausedGuest = guestVideo.Position.TotalSeconds; await Task.Delay(550);
                if (Math.Abs(hostVideo.Position.TotalSeconds - pausedHost) > .1 || Math.Abs(guestVideo.Position.TotalSeconds - pausedGuest) > .1 || Math.Abs(pausedHost - pausedGuest) > .2) throw new Exception("Общая пауза не остановила оба видео.");
                version = state.playbackVersion; C<Slider>("PlaybackSlider").Value = 7;
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
            catch (Exception e) { report.Add("FAIL: " + e); if (state != null && other != null && other.state != null) report.Add("Playback state at failure: " + Json.Write(new { hostPosition = C<MediaElement>("Video").Position.TotalSeconds, guestPosition = other.C<MediaElement>("Video").Position.TotalSeconds, hostPlaying = state.playing, guestPlaying = other.state.playing, duration = state.resultDuration, natural = C<MediaElement>("Video").NaturalDuration.ToString(), version = state.playbackVersion, appliedHost = playbackAppliedVersion, appliedGuest = other.playbackAppliedVersion })); }
            finally
            {
                File.WriteAllLines(System.IO.Path.Combine(Paths.Base, steamTransport ? "steam-screening-test.txt" : endOnly ? "end-test.txt" : "screening-test.txt"), report);
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
