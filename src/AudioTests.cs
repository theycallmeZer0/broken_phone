using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace BrokenPhone
{
    public static partial class SelfTests
    {
        internal static byte[] ToneWave(double seconds, double frequency, int amplitude = 7000)
        {
            var pcm = new byte[(int)(seconds * 44100) * 2];
            for (int i = 0; i < pcm.Length / 2; i++)
            {
                double envelope = .65 + .35 * Math.Sin(i * 2 * Math.PI * 3 / 44100);
                short sample = (short)(Math.Sin(i * 2 * Math.PI * frequency / 44100) * amplitude * envelope);
                pcm[i * 2] = (byte)(sample & 255); pcm[i * 2 + 1] = (byte)(sample >> 8);
            }
            return Audio.Wave(pcm);
        }
        static double Energy(byte[] pcm, double start, double end)
        {
            int begin = Math.Max(0, (int)(start * 44100)), last = Math.Min(pcm.Length / 2, (int)(end * 44100)); double sum = 0;
            for (int i = begin; i < last; i++) { double value = BitConverter.ToInt16(pcm, i * 2); sum += value * value; }
            return Math.Sqrt(sum / Math.Max(1, last - begin));
        }
        static double Pitch(byte[] pcm)
        {
            int first = 8820, last = Math.Min(pcm.Length / 2 - 1, 35280), crossings = 0;
            for (int i = first; i < last; i++) if (BitConverter.ToInt16(pcm, i * 2) <= 0 && BitConverter.ToInt16(pcm, (i + 1) * 2) > 0) crossings++;
            return crossings * 44100.0 / (last - first);
        }
        static void AudioFeatureTests(List<string> results)
        {
            byte[] raw = ToneWave(1.1, 440), pcm = Audio.ReadPcm(raw);
            Check(SignalAnalysis.Envelope(new byte[2048], 100).All(x => x == 0), "Звуковая волна показывает сигнал в тишине.");
            Check(SignalAnalysis.Envelope(pcm, 400).Max() > .2, "Звуковая волна не отражает PCM.");
            for (int effect = 0; effect < VoiceEffects.Names.Length; effect++)
            {
                byte[] processed = VoiceEffects.Apply(raw, effect, false, 2, CancellationToken.None); Audio.Validate(processed, 2);
                double duration = Audio.ReadPcm(processed).Length / 88200.0;
                Check(Math.Abs(duration - (effect == 3 ? 1.4 : 1.1)) < .025, "Эффект изменил длину реплики: " + effect);
                if (effect == 0) Check(processed.SequenceEqual(raw), "Обычный голос изменяет запись.");
                else Check(!Audio.ReadPcm(processed).SequenceEqual(pcm), "Эффект не обработал звук: " + effect);
                if (effect == 1 || effect == 2)
                {
                    double pitch = Pitch(Audio.ReadPcm(processed));
                    Check(effect == 1 ? pitch > 600 && pitch < 680 : pitch > 290 && pitch < 350, "Неверное изменение высоты голоса: " + pitch);
                }
            }
            byte[] quiet = ToneWave(1.1, 440, 35); byte[] gated = VoiceEffects.Apply(quiet, 0, true, 2, CancellationToken.None);
            Check(Energy(Audio.ReadPcm(gated), .2, .9) < Energy(Audio.ReadPcm(quiet), .2, .9) * .3, "Подавление тихого фонового шума не работает.");
            results.Add("PASS: all six real voice effects, preserved timing, pitch shift, noise gate, PCM waveform and silence detection");
            string folder = Path.Combine(Paths.Cache, "original-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
            try
            {
                string source = Path.Combine(folder, "source.wav"), output = Path.Combine(folder, "original.wav"); File.WriteAllBytes(source, raw);
                var pack = new Pack { original = source }; pack.lines.Add(new Line { start = .2, end = .9 }); Audio.Original(pack, 0, output);
                Check(Math.Abs(Audio.ReadPcm(File.ReadAllBytes(output)).Length / 88200.0 - .7) < .01, "Оригинальная реплика нарезана неверно.");
                pack.original = ""; pack.lines[0].start = 0; pack.lines[0].end = 3;
                pack.sourceCues.Add(new SourceCue { file = source, start = .4, end = 1.5 }); pack.sourceCues.Add(new SourceCue { file = source, start = 1.7, end = 2.8 }); Audio.Original(pack, 0, output);
                var mixed = Audio.ReadPcm(File.ReadAllBytes(output));
                Check(Energy(mixed, 0, .3) == 0 && Energy(mixed, .5, 1.4) > 1000 && Energy(mixed, 1.5, 1.7) == 0 && Energy(mixed, 1.8, 2.7) > 1000, "Оригинальные звуковые фрагменты потеряли таймкоды.");
                results.Add("PASS: optional original audio from source track and timed per-line source files");
            }
            finally { Directory.Delete(folder, true); }
        }
        static void CheckMixedVoices(string video, Pack pack)
        {
            string wav = video + ".decoded.wav";
            try
            {
                Tools.Run("ffmpeg", "-y -v error -i " + Tools.Quote(video) + " -vn -ac 1 -ar 44100 -c:a pcm_s16le " + Tools.Quote(wav), 30000);
                var pcm = Audio.ReadPcm(File.ReadAllBytes(wav));
                foreach (var line in pack.lines) Check(Energy(pcm, line.start + .08, line.start + .4) > 500, "В финальном видео отсутствует озвучка: " + line.start);
            }
            finally { if (File.Exists(wav)) File.Delete(wav); }
        }
    }
}
