using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace BrokenPhone
{
    public sealed partial class GameWindow
    {
        readonly OutputPlayer headphonePlayback = new OutputPlayer();
        bool changingOutputDevice, headphonePlaying, headphoneOpening;
        void InitializeOutputSettings()
        {
            voice.Device = checkPlayback.Device = headphonePlayback.Device = () => OutputAudio.Resolve(settings.OutputDevice);
            C<ComboBox>("OutputDeviceCombo").SelectionChanged += delegate
            {
                var selected = C<ComboBox>("OutputDeviceCombo").SelectedItem as OutputDevice;
                if (changingOutputDevice || selected == null || selected.Key == settings.OutputDevice) return;
                StopHeadphoneTest(); StopMicPlayback(); voice.Stop(); voicePlaying = false;
                try
                {
                    if (finalMixer != null)
                    {
                        var replacement = new WaveMixer(finalPcmFiles[0], finalPcmFiles[1], state.resultDuration, selected.Id);
                        double position = finalMixer.Position; finalMixer.Dispose(); finalMixer = replacement; finalMixer.Seek(position, false); UpdateFinalVolumes();
                    }
                    settings.OutputDevice = selected.Key; Persist(); Text("HeadphoneStatus", "Устройство выбрано. Нажми «Проверить звук».");
                }
                catch (Exception error) { Text("HeadphoneStatus", error.Message); OpenOutputSettings(); }
            };
            B("HeadphoneTestButton").Click += delegate { if (headphonePlaying || headphoneOpening) StopHeadphoneTest(); else StartHeadphoneTest(); };
            headphonePlayback.MediaOpened += delegate
            {
                if (!headphoneOpening || currentPage != "Settings" || C<StackPanel>("PlayerSettingsSection").Visibility != Visibility.Visible) { StopHeadphoneTest(); return; }
                headphoneOpening = false; headphonePlaying = true; headphonePlayback.Play(); UpdateHeadphoneTest();
            };
            headphonePlayback.MediaEnded += delegate { StopHeadphoneTest(); Text("HeadphoneStatus", "Тест окончен: левый → правый → оба наушника. Можно повторить."); };
            headphonePlayback.MediaFailed += delegate(object sender, OutputFailure error) { StopHeadphoneTest(); Text("HeadphoneStatus", error.Error.Message); };
        }
        void OpenOutputSettings()
        {
            changingOutputDevice = true;
            var devices = OutputAudio.Devices(); var selected = devices.FirstOrDefault(device => device.Key == settings.OutputDevice);
            if (selected == null) { selected = devices[0]; settings.OutputDevice = ""; Text("HeadphoneStatus", "Прежнее устройство отключено. Выбрано устройство Windows по умолчанию."); }
            C<ComboBox>("OutputDeviceCombo").ItemsSource = devices; C<ComboBox>("OutputDeviceCombo").SelectedItem = selected; changingOutputDevice = false;
        }
        void StartHeadphoneTest()
        {
            StopHeadphoneTest(); StopMicPlayback();
            headphoneOpening = true; headphonePlayback.Volume = C<Slider>("VolumeSlider").Value; B("HeadphoneTestButton").Content = "■ Остановить";
            headphonePlayback.Open(new Uri(Path.Combine(Paths.Base, "Assets", "Audio", "headphones.wav")));
        }
        void StopHeadphoneTest()
        {
            headphonePlayback.Close(); headphonePlaying = headphoneOpening = false; B("HeadphoneTestButton").Content = "▶ Проверить звук";
            Text("HeadphoneStatus", "Левый → правый → оба наушника. Громкость регулируется ниже.");
        }
        void UpdateHeadphoneTest()
        {
            if (!headphonePlaying) return;
            double position = headphonePlayback.Position.TotalSeconds;
            Text("HeadphoneStatus", position < 1.5 ? "Сейчас звучит левый наушник" : position < 3 ? "Сейчас звучит правый наушник" : "Сейчас звучат оба наушника");
        }
    }
}
