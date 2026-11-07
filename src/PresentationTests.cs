using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace BrokenPhone
{
    public sealed partial class GameWindow
    {
        static bool SameHistory(byte[] first, byte[] second, int width, int height)
        {
            for (int y = 0; y < height; y++) for (int x = 4; x < width / 8; x++)
                for (int c = 0; c < 4; c++) if (first[(y * width + x) * 4 + c] != second[(y * width + x) * 4 + c]) return false;
            return true;
        }
        void StableWave(SignalView graph, double seconds)
        {
            var wave = Enumerable.Range(0, 180).Select(i => i % 17 < 7 ? .07f : .75f).ToArray();
            graph.UpdateLive(wave, seconds, seconds * .4); var initial = SignalPixels(graph);
            graph.UpdateLive(wave, seconds, seconds * .47); var delayed = SignalPixels(graph);
            if (!SameHistory(initial, delayed, (int)graph.ActualWidth, (int)graph.ActualHeight)) throw new Exception("История волны двигается между поступлениями звука.");
            graph.UpdateLive(wave.Concat(Enumerable.Repeat(.3f, 120)).ToArray(), seconds, seconds * .6); var appended = SignalPixels(graph);
            if (!SameHistory(initial, appended, (int)graph.ActualWidth, (int)graph.ActualHeight) || initial.SequenceEqual(appended)) throw new Exception("Живая волна смещает уже записанную часть или не показывает новые данные.");
        }
        public async Task PresentationSmoke()
        {
            var report = new List<string>(); RoomState original = state;
            try
            {
                await Until(() => Me != null, "Нет лобби."); original = state;
                OpenSettings(); C<Border>("MicrophoneCheckPanel").BringIntoView(); window.UpdateLayout(); StableWave(C<SignalView>("MicSignal"), 5);
                state = Json.Read<RoomState>(Json.Write(original)); state.phase = "recording"; state.round = "presentation";
                state.pack.lines = Enumerable.Range(0, 80).Select(i => new Line { start = i, end = i + 1, role = "Персонаж " + (i + 1), text = "Реплика" }).ToList();
                state.order = Enumerable.Repeat(client.Id, 80).ToArray(); state.turn = 30;
                Page("Round"); RenderRound(); window.UpdateLayout(); StableWave(C<SignalView>("Signal"), 5);
                report.Add("PASS: studio and microphone-check live waveforms keep historical pixels fixed while time advances and new samples are appended; live signal remains animated");
                state.pack.lines[state.turn].originalAvailable = true; RenderRound();
                if (!C<Slider>("OriginalVolume").IsEnabled || C<StackPanel>("OriginalVolumePanel").Visibility != Visibility.Visible) throw new Exception("Нет регулятора громкости оригинала на своём ходу.");
                string sample = System.IO.Path.Combine(Paths.Cache, "original-volume-" + Guid.NewGuid().ToString("N") + ".wav");
                File.WriteAllBytes(sample, Audio.Wave(new byte[88200]));
                try
                {
                    C<Slider>("VolumeSlider").Value = 0; C<Slider>("OriginalVolume").Value = .25;
                    PlayVoice(sample, 1, null, true); await Until(() => voicePlaying, "Оригинал не открылся в аудиоплеере.");
                    voice.Pause(); voicePlaying = false; playbackEnd = DateTime.MinValue;
                    C<Slider>("VolumeSlider").Value = .8;
                    if (Math.Abs(voice.Volume - .2) > .001) throw new Exception("Громкость оригинала не учитывает общий уровень.");
                    C<Slider>("OriginalVolume").Value = .75;
                    if (Math.Abs(voice.Volume - .6) > .001 || Math.Abs(Json.Read<Settings>(File.ReadAllText(System.IO.Path.Combine(Paths.Data, "settings.json"))).OriginalPreviewVolume - .75) > .001) throw new Exception("Изменение громкости оригинала не применилось или не сохранилось.");
                    C<Slider>("VolumeSlider").Value = 0; PlayVoice(sample, 1, null); await Until(() => voicePlaying, "Свой дубль не открылся.");
                    voice.Pause(); voicePlaying = false; playbackEnd = DateTime.MinValue; C<Slider>("VolumeSlider").Value = .8;
                    if (Math.Abs(voice.Volume - .8) > .001) throw new Exception("Громкость оригинала изменила прослушивание своего дубля.");
                    SaveScreen("original-volume-preview.png");
                    report.Add("PASS: original preview has a local volume slider; decoded audio gain changes immediately, multiplies the master volume, persists, and leaves own-take playback volume independent; all playback is muted or paused during testing");
                }
                finally { voice.Close(); C<Slider>("VolumeSlider").Value = 0; File.Delete(sample); }
                var rows = C<StackPanel>("TurnList");
                if (rows.Children.Count != 80 || C<StackPanel>("ChainPagination").Visibility != Visibility.Visible) throw new Exception("Цепочка голосов обрезана.");
                chainPage = 100; UpdateChainPage(); window.UpdateLayout(); int page = chainPage;
                if (((FrameworkElement)rows.Children[79]).Visibility != Visibility.Visible) throw new Exception("Последняя реплика недоступна через страницы.");
                RenderRound(); window.UpdateLayout();
                if (chainPage != page) throw new Exception("Обновление записи сбрасывает страницу цепочки.");
                SaveScreen("full-voice-chain-preview.png");
                report.Add("PASS: all 80 dialogue rows remain accessible through pages without scrolling; state refresh preserves the selected page");
            }
            catch (Exception error) { report.Add("FAIL: " + error); }
            finally { state = original; File.WriteAllLines(System.IO.Path.Combine(Paths.Base, "presentation-ui-test.txt"), report); window.Close(); }
        }
    }
}
