using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BrokenPhone
{
    public sealed partial class GameWindow
    {
        static byte[] SignalPixels(SignalView signal)
        {
            signal.UpdateLayout(); int width = (int)signal.ActualWidth, height = (int)signal.ActualHeight;
            var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); image.Render(signal);
            var pixels = new byte[width * height * 4]; image.CopyPixels(pixels, width * 4, 0); return pixels;
        }
        public async Task MicrophonePanelSmoke()
        {
            var report = new List<string>();
            try
            {
                settings.Volume = 0; OpenSettings(); window.UpdateLayout();
                var graph = C<SignalView>("MicSignal");
                if (graph.ActualHeight > 45 || graph.ActualHeight < 35 || graph.ActualWidth < 360 || B("MicReplayButton").IsEnabled) throw new Exception("Мини-волна или начальные кнопки проверки микрофона неверны.");
                if (window.FindName("SignalModeButton") != null || window.FindName("LinePrompt") != null || typeof(SignalView).GetProperty("Spectrogram") != null) throw new Exception("Остались спектрограмма или подсказка для сцены.");
                var initial = SignalPixels(graph); byte[] sample = SelfTests.ToneWave(1.2, 440, 20000); var wave = SignalAnalysis.Envelope(Audio.ReadPcm(sample), 400);
                var captured = Enumerable.Repeat(.5f, 172).ToArray(); RenderMicCapture(captured, 1.10, .5f);
                if (graph.LiveCursorSeconds - captured.Length * SignalAnalysis.SamplesPerPoint / 44100.0 > .01) throw new Exception("Полоска записи опережает полученные данные микрофона.");
                report.Add("PASS: recording cursor stays at the captured sample boundary even when the wall clock leads by 100 ms");
                RenderMicCapture(wave, 2, .6f); var live = SignalPixels(graph);
                if (initial.SequenceEqual(live) || !C<TextBlock>("MicSignalTime").Text.Contains("60%")) throw new Exception("Визуализация не отражает поступающий звук.");
                SaveScreen("microphone-capture-preview.png"); RenderMicCapture(wave, 3.5, .4f);
                if (live.SequenceEqual(SignalPixels(graph))) throw new Exception("Волна не изменяется по мере записи.");
                report.Add("PASS: compact waveform uses the same renderer as studio; captured PCM changes the shape and time cursor; no spectrogram or scene hint");
                var studioTake = take; var studioWave = traceWave; SetMicCheckSample(sample);
                if (!ReferenceEquals(studioTake, take) || !ReferenceEquals(studioWave, traceWave) || !File.ReadAllBytes(checkFile).SequenceEqual(sample) || !B("MicReplayButton").IsEnabled || !B("MicClearButton").IsEnabled) throw new Exception("Проба изменила игровой дубль или не подготовилась для прослушивания.");
                SaveScreen("microphone-check-preview.png");
                B("MicReplayButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => checkPlaying && checkPlayback.Position.TotalSeconds > .15, "Пробная запись не воспроизводится.", 8);
                if (!B("MicReplayButton").Content.ToString().Contains("Стоп")) throw new Exception("Нет остановки прослушивания.");
                SaveScreen("microphone-playback-preview.png");
                B("MicReplayButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (checkPlaying || checkOpening || checkSample == null) throw new Exception("Стоп не остановил прослушивание или удалил запись.");
                B("MicReplayButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => !checkPlaying && !checkOpening && C<TextBlock>("MicStatus").Text.Contains("закончено"), "Нет окончания повторного прослушивания.", 8);
                report.Add("PASS: real WAV decoder plays the exact test sample; stop, replay and natural playback completion update the controls");
                B("MicReplayButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => checkPlaying, "Не началось третье прослушивание.", 8);
                B("LobbyTab").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (checkPlaying || checkOpening || currentPage != "Lobby") throw new Exception("Прослушивание продолжилось после выхода из настроек.");
                OpenSettings(); string samplePath = checkFile; B("MicClearButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (checkSample != null || checkWave.Length != 0 || B("MicReplayButton").IsEnabled || File.Exists(samplePath)) throw new Exception("Сброс не очистил запись и волну.");
                SetMicCheckSample(Audio.Wave(new byte[88200]));
                if (!C<TextBlock>("MicStatus").Text.Contains("не обнаружен") || checkWave.Any(x => x != 0)) throw new Exception("Тишина показана как работающий голос.");
                ClearMicCheck(); SaveScreen("microphone-empty-preview.png");
                report.Add("PASS: leaving settings stops playback; reset deletes the sample; silence shows a flat wave and clear no-signal status");
                report.Add("PASS: automated test uses known PCM and muted playback; no physical microphone capture was started");
            }
            catch (Exception e) { report.Add("FAIL: " + e); }
            finally { File.WriteAllLines(System.IO.Path.Combine(Paths.Base, "microphone-ui-test.txt"), report); window.Close(); }
        }
    }
}
