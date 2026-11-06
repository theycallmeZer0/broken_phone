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
        public async Task TurnGroupSmoke()
        {
            var report = new List<string>(); GameWindow other = null; Window peerWindow = null; string fixture = Path.Combine(Paths.Cache, "turn-group-" + Guid.NewGuid().ToString("N"));
            try
            {
                C<Slider>("VolumeSlider").Value = 0; await Until(() => Me != null, "Нет комнаты.");
                Directory.CreateDirectory(fixture); File.Copy(Path.Combine(Paths.Base, "Assets", "Demo", "video.mp4"), Path.Combine(fixture, "video.mp4"));
                File.WriteAllBytes(Path.Combine(fixture, "original.wav"), SelfTests.ToneWave(16, 220));
                File.WriteAllText(Path.Combine(fixture, "pack.json"), Json.Write(new { title = "Последовательные реплики " + Guid.NewGuid().ToString("N"), video = "video.mp4", original = "original.wav", lines = Enumerable.Range(0, 4).Select(i => new Line { start = i * 4, end = i * 4 + 4, role = i < 2 ? "Первый" : "Второй", text = "Фрагмент " + (i + 1) }).ToArray() }));
                var pack = PackLibrary.Load(fixture, ""); Send(new Message { kind = "pack", pack = pack }); await Until(() => state.pack.title == pack.title, "Пак не выбран.");
                using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("MainWindow.xaml")) peerWindow = (Window)XamlReader.Load(stream);
                peerWindow.ShowInTaskbar = false; peerWindow.Left = -10000; other = new GameWindow(peerWindow, false); peerWindow.Show(); other.C<Slider>("VolumeSlider").Value = 0;
                await other.Connect("127.0.0.1", server.Port, server.Code, ""); await Until(() => state.players.Count == 2 && other.Me != null, "Нет второго игрока.");
                B("ReadyButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); other.B("ReadyButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => state.players.All(p => p.ready), "Нет готовности.");
                B("StartButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await StartAutomaticRoles(other); await Until(() => IsTurn && sourcePrepared && other.sourcePrepared, "Нет своего блока.");
                await Until(() => C<SignalView>("Signal").HasGuide, "Исходная волна не загрузилась без прослушивания оригинала.");
                var guideOnly = SignalPixels(C<SignalView>("Signal"));
                C<SignalView>("Signal").UpdateLive(Enumerable.Repeat(.6f, 230).ToArray(), 4, 1.4);
                if (!C<SignalView>("Signal").HasGuide || guideOnly.SequenceEqual(SignalPixels(C<SignalView>("Signal")))) throw new Exception("Живая запись не рисуется поверх исходной волны.");
                SaveScreen("original-guide-preview.png");
                await Submit(); if (StudioTurn != 1 || take != null || state.turn != 0 || !B("RecordButton").IsEnabled) throw new Exception("Нельзя открыть ещё не записанную следующую реплику.");
                await Original(); await Until(() => voicePlaying, "Оригинал следующей реплики недоступен."); voice.Close(); voicePlaying = false;
                await SetTake(SelfTests.ToneWave(1, 660)); byte[] second = take.ToArray(); await Listen();
                if (StudioTurn != 0 || take != null) throw new Exception("Предыдущая не вернула незаписанную реплику.");
                await SetTake(SelfTests.ToneWave(1, 440)); await Submit();
                if (StudioTurn != 1 || !take.SequenceEqual(second)) throw new Exception("Запись следующей реплики потеряна при навигации.");
                await Listen(); await SetTake(SelfTests.ToneWave(1, 880)); byte[] revised = take.ToArray(); await Submit();
                if (!take.SequenceEqual(second)) throw new Exception("Перезапись предыдущей изменила следующую.");
                SaveScreen("turn-group-preview.png"); await Submit(); await Until(() => state.turn == 2 && other.IsTurn, "Блок не передан следующему игроку.");
                if (IsTurn || other.StudioTurn != 2) throw new Exception("Можно перейти за границу своего блока.");
                await other.Submit(); if (other.StudioTurn != 3) throw new Exception("Второй игрок не может пропустить незаписанную реплику для последующей записи.");
                await other.SetTake(SelfTests.ToneWave(1, 550)); await other.Submit();
                if (other.StudioTurn != 2 || state.phase != "recording") throw new Exception("Неполный блок был передан без записи всех реплик.");
                await other.SetTake(SelfTests.ToneWave(1, 330)); await other.Submit(); await other.Submit(); await Until(() => state.phase == "result" && other.state.phase == "result", "Нет общего монтажа.");
                string firstFile = await client.Asset("take:0", ".wav"), secondFile = await client.Asset("take:1", ".wav");
                if (!File.ReadAllBytes(firstFile).SequenceEqual(revised) || !File.ReadAllBytes(secondFile).SequenceEqual(second)) throw new Exception("В монтаж ушли неверные версии реплик.");
                report.Add("PASS: consecutive own turns support previous/next navigation, unrecorded clips, original preview, saved-take review and rerecording without losing neighbouring drafts");
                report.Add("PASS: the source waveform loads without mandatory listening and remains beneath the live recording wave on the same clip timeline");
                report.Add("PASS: only a fully recorded block is sent to the next player; ownership boundaries hold and final montage receives the latest separate take of every line");
            }
            catch (Exception error) { report.Add("FAIL: " + error); if (state != null) report.Add("Navigation state: " + Json.Write(new { phase = state.phase, state.turn, order = state.order, players = state.players, me = client == null ? "" : client.Id, sourcePrepared, roles = state.roles, peerPrepared = other != null && other.sourcePrepared })); }
            finally { File.WriteAllLines(Path.Combine(Paths.Base, "turn-navigation-ui-test.txt"), report); if (other != null) other.Dispose(); if (peerWindow != null) peerWindow.Close(); window.Close(); if (Directory.Exists(fixture)) Directory.Delete(fixture, true); }
        }
    }
}
