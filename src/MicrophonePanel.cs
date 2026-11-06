using System;
using System.IO;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Media;

namespace BrokenPhone
{
    public sealed partial class GameWindow
    {
        const double MicCheckSeconds = 5;
        readonly MicrophoneRecorder checkMicrophone = new MicrophoneRecorder();
        readonly MediaPlayer checkPlayback = new MediaPlayer();
        float[] checkWave = new float[0];
        byte[] checkSample;
        string checkFile;
        double checkDuration;
        DateTime checkStart;
        bool checkPlaying, checkOpening, changingMicDevice;
        int checkPlaybackRevision, checkDevice = -1;
        void InitializeMicrophonePanel()
        {
            B("MicTestButton").Click += delegate { if (checkMicrophone.Recording) FinishMicCheck(); else StartMicCheck(); };
            B("MicReplayButton").Click += delegate { if (checkPlaying || checkOpening) StopMicPlayback(); else ListenMicCheck(); };
            B("MicClearButton").Click += delegate { ClearMicCheck(); };
            C<ComboBox>("MicrophoneCombo").SelectionChanged += delegate
            {
                int device = C<ComboBox>("MicrophoneCombo").SelectedIndex;
                if (changingMicDevice || device < 0 || device == checkDevice) return;
                ClearMicCheck(); checkDevice = device;
            };
            checkPlayback.MediaEnded += delegate { if (checkPlaying) { StopMicPlayback(); Text("MicStatus", "Прослушивание закончено. Можно повторить или записать ещё раз."); } };
            checkPlayback.MediaFailed += delegate { StopMicPlayback(); Text("MicStatus", "Запись не открылась. Запиши пробу ещё раз."); };
            UpdateMicControls();
        }
        void UpdateMicControls()
        {
            bool recording = checkMicrophone.Recording;
            B("MicTestButton").Content = recording ? "■ Остановить" : checkSample == null ? "● Записать пробу" : "● Записать ещё раз";
            B("MicTestButton").Background = Brush(recording ? "#F78781" : "#99D8B0");
            B("MicReplayButton").Content = checkPlaying || checkOpening ? "■ Стоп" : "▶ Прослушать";
            B("MicReplayButton").IsEnabled = checkSample != null && !recording;
            B("MicClearButton").IsEnabled = checkSample != null || recording;
            C<ComboBox>("MicrophoneCombo").IsEnabled = !recording;
            Text("MicSignalLabel", recording ? "● МИКРОФОН · ЗАПИСЬ" : checkPlaying || checkOpening ? "ПРОСЛУШИВАНИЕ" : checkSample != null ? "ТВОЯ ПРОБНАЯ ЗАПИСЬ" : "ПРОВЕРКА МИКРОФОНА");
        }
        void StartMicCheck()
        {
            if (microphone.Recording) return;
            ClearMicCheck();
            try
            {
                checkDevice = Math.Max(0, C<ComboBox>("MicrophoneCombo").SelectedIndex); checkMicrophone.Start(checkDevice); checkStart = DateTime.UtcNow;
                Text("MicStatus", "Говори в микрофон — волна показывает твой голос. Запись до 5 секунд."); UpdateMicControls();
            }
            catch (Exception e) { Text("MicStatus", e.Message); UpdateMicControls(); }
        }
        void FinishMicCheck()
        {
            if (!checkMicrophone.Recording) return;
            try { SetMicCheckSample(checkMicrophone.Stop()); }
            catch (Exception e) { Text("MicStatus", e.Message); UpdateMicControls(); }
        }
        void SetMicCheckSample(byte[] bytes)
        {
            StopMicPlayback(); var pcm = Audio.ReadPcm(bytes); int maximum = (int)(MicCheckSeconds * 88200);
            if (pcm.Length > maximum) Array.Resize(ref pcm, maximum);
            if (pcm.Length < 13230) throw new InvalidDataException("Запиши хотя бы 0,2 секунды голоса.");
            var sample = Audio.Wave(pcm); Audio.Validate(sample, MicCheckSeconds);
            string file = Path.Combine(Paths.Cache, "microphone-check-" + Guid.NewGuid().ToString("N") + ".wav"); File.WriteAllBytes(file, sample);
            DeleteMicFile(); checkSample = sample; checkFile = file; checkDuration = pcm.Length / 88200.0; checkWave = SignalAnalysis.Envelope(pcm, 400);
            float peak = checkWave.Length == 0 ? 0 : checkWave.Max();
            Text("MicStatus", peak < .001 ? "Звук не обнаружен. Проверь выбранный микрофон и его громкость." : peak > .98 ? "Запись готова, но звук перегружен. Убавь усиление микрофона и послушай пробу." : "Запись готова. Нажми «Прослушать», чтобы услышать свой микрофон.");
            Text("MicSignalTime", checkDuration.ToString("0.0") + " с"); C<SignalView>("MicSignal").Update(checkWave, 1, 0, false); UpdateMicControls();
        }
        void ListenMicCheck()
        {
            if (checkSample == null || checkMicrophone.Recording) return;
            StopMicPlayback(); int revision = checkPlaybackRevision; checkOpening = true;
            checkPlayback.Volume = C<Slider>("VolumeSlider").Value; UpdateMicControls();
            EventHandler opened = null;
            opened = delegate
            {
                checkPlayback.MediaOpened -= opened; if (revision != checkPlaybackRevision || currentPage != "Settings") return;
                checkOpening = false; checkPlaying = true; checkPlayback.Position = TimeSpan.Zero; checkPlayback.Play();
                Text("MicStatus", checkPlayback.Volume == 0 ? "Проба воспроизводится. Подними громкость ниже, чтобы её услышать." : "Слушай запись. Громкость воспроизведения можно изменить ниже."); UpdateMicControls();
            };
            checkPlayback.MediaOpened += opened; checkPlayback.Open(new Uri(checkFile));
        }
        void StopMicPlayback()
        {
            checkPlaybackRevision++; checkPlayback.Stop(); checkPlayback.Close(); checkPlaying = checkOpening = false; Text("MicSignalTime", checkDuration > 0 ? checkDuration.ToString("0.0") + " с" : "0.0 / 5.0 с"); UpdateMicControls();
        }
        void StopMicrophonePanel()
        {
            if (checkMicrophone.Recording) FinishMicCheck(); StopMicPlayback();
        }
        void DeleteMicFile()
        {
            if (checkFile == null) return;
            try { File.Delete(checkFile); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            checkFile = null;
        }
        void ClearMicCheck()
        {
            string failure = null; try { if (checkMicrophone.Recording) checkMicrophone.Stop(); } catch (Exception e) { failure = e.Message; }
            StopMicPlayback(); DeleteMicFile(); checkSample = null; checkDuration = 0; checkWave = new float[0];
            Text("MicStatus", failure ?? "Запиши короткую пробу, затем прослушай её. Во время записи будет двигаться волна."); Text("MicSignalTime", "0.0 / 5.0 с"); C<SignalView>("MicSignal").Update(checkWave, 1, 0, false); UpdateMicControls();
        }
        void RenderMicCapture(float[] wave, double elapsed, float level)
        {
            double position = Math.Min(1, elapsed / MicCheckSeconds);
            C<SignalView>("MicSignal").UpdateLive(wave, MicCheckSeconds, elapsed);
            Text("MicSignalTime", elapsed.ToString("0.0") + " / 5.0 с  ·  " + (level * 100).ToString("0") + "%");
        }
        void TickMicrophonePanel()
        {
            if (checkMicrophone.Recording)
            {
                double elapsed = (DateTime.UtcNow - checkStart).TotalSeconds; RenderMicCapture(checkMicrophone.Waveform, elapsed, checkMicrophone.Level);
                if (elapsed >= MicCheckSeconds) FinishMicCheck();
            }
            else if (checkPlaying)
            {
                double elapsed = checkPlayback.Position.TotalSeconds;
                C<SignalView>("MicSignal").Update(checkWave, 1, checkDuration > 0 ? elapsed / checkDuration : 0, false);
                Text("MicSignalTime", elapsed.ToString("0.0") + " / " + checkDuration.ToString("0.0") + " с");
            }
            else if (!checkOpening && currentPage == "Settings") C<SignalView>("MicSignal").Update(checkWave, 1, 0, false);
        }
    }
}
