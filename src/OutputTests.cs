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
        static double ChannelEnergy(byte[] pcm, int channel, double begin, double end)
        {
            long sum = 0; int count = 0;
            for (int frame = (int)(begin * 44100); frame < (int)(end * 44100); frame++) { int value = BitConverter.ToInt16(pcm, frame * 4 + channel * 2); sum += (long)value * value; count++; }
            return Math.Sqrt(sum / (double)Math.Max(1, count));
        }
        public async Task OutputSmoke()
        {
            var report = new List<string>(); string savedKey = settings.OutputDevice; double savedVolume = settings.Volume;
            try
            {
                await Until(() => Me != null, "Нет лобби."); OpenSettings(); C<Slider>("VolumeSlider").Value = 0;
                byte[] pcm = OutputAudio.ReadStereo(Path.Combine(Paths.Base, "Assets", "Audio", "headphones.wav"));
                if (pcm.Length != 44100 * 4 * 4.5 || ChannelEnergy(pcm, 0, .1, 1.2) < 500 || ChannelEnergy(pcm, 1, .1, 1.2) > 0 || ChannelEnergy(pcm, 1, 1.6, 2.7) < 500 || ChannelEnergy(pcm, 0, 1.6, 2.7) > 0 || ChannelEnergy(pcm, 0, 3.1, 4.2) < 500 || ChannelEnergy(pcm, 1, 3.1, 4.2) < 500) throw new Exception("Тестовый WAV не разделяет левый, правый и общий каналы.");
                report.Add("PASS: real 4.5-second stereo WAV contains left-only, right-only and both-channel test tones with smooth fades");
                var devices = C<ComboBox>("OutputDeviceCombo").Items.Cast<OutputDevice>().ToArray();
                OutputDevice selected = devices[0];
                foreach (var candidate in devices.Skip(1))
                {
                    try { using (var probe = new WaveMixer(new byte[1764], candidate.Id)) { } selected = candidate; break; }
                    catch (IOException) { }
                }
                C<ComboBox>("OutputDeviceCombo").SelectedItem = selected;
                B("HeadphoneTestButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => headphonePlaying && headphonePlayback.Position.TotalSeconds > .1, "Тест наушников не начался.");
                if (headphonePlayback.DeviceId != selected.Id || headphonePlayback.Volume != 0) throw new Exception("Тест играет не на выбранном выходе или не учитывает общую громкость.");
                SaveScreen("audio-output-preview.png");
                B("HeadphoneTestButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (headphonePlaying || headphoneOpening) throw new Exception("Кнопка не остановила звук.");
                SetMicCheckSample(SelfTests.ToneWave(.8, 440)); ListenMicCheck();
                await Until(() => checkPlaying && checkPlayback.Position.TotalSeconds > .1, "Проба микрофона не начала воспроизведение.");
                if (checkPlayback.DeviceId != selected.Id) throw new Exception("Проба микрофона игнорирует выбранный выход.");
                StopMicPlayback(); PlayVoice(checkFile, .8, null);
                await Until(() => voicePlaying && voice.Position.TotalSeconds > .1, "Студийный дубль не начал воспроизведение.");
                if (voice.DeviceId != selected.Id) throw new Exception("Студия игнорирует выбранный выход.");
                voice.Close(); voicePlaying = false; playbackEnd = DateTime.MinValue;
                report.Add("PASS: headphone test, microphone replay and studio playback open the actual selected Windows output device; all test playback is muted");
                if (Json.Read<Settings>(File.ReadAllText(SettingsFile)).OutputDevice != selected.Key) throw new Exception("Выбор устройства не сохранён.");
                B("HeadphoneTestButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => headphonePlaying, "Повтор теста не начался."); SettingsSection("Display");
                if (headphonePlaying || headphoneOpening || checkPlaying) throw new Exception("Звук не остановился при смене вкладки.");
                SettingsSection("Player"); B("HeadphoneTestButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => !headphonePlaying && !headphoneOpening && C<TextBlock>("HeadphoneStatus").Text.Contains("окончен"), "Тест не завершился автоматически.", 8);
                report.Add("PASS: selected device persists; stop, replay, natural ending and leaving the audio tab clean up playback");
            }
            catch (Exception error) { report.Add("FAIL: " + error); }
            finally { StopHeadphoneTest(); voice.Close(); settings.OutputDevice = savedKey; settings.Volume = savedVolume; Persist(); File.WriteAllLines(Path.Combine(Paths.Base, "audio-output-ui-test.txt"), report); window.Close(); }
        }
    }
}
