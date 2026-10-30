using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace BrokenPhone
{
    public static class VoiceEffects
    {
        public static readonly string[] Names = { "Обычный", "Бурундук", "Монстр", "Эхо", "Робот", "Телефон" };
        public static byte[] Apply(byte[] raw, int effect, bool noiseGate, double limit, CancellationToken cancellation)
        {
            Audio.Validate(raw, limit); cancellation.ThrowIfCancellationRequested();
            if (effect == 0 && !noiseGate) return raw;
            string filter;
            switch (effect)
            {
                case 0: filter = "anull"; break;
                case 1: filter = "asetrate=63945,aresample=44100,atempo=0.689655"; break;
                case 2: filter = "asetrate=31752,aresample=44100,atempo=1.388889"; break;
                case 3: filter = "aecho=0.8:0.7:75|145:0.32|0.18"; break;
                case 4: filter = "aeval='val(0)*sin(2*PI*55*t)',acrusher=bits=8:mix=0.35"; break;
                case 5: filter = "highpass=f=350,lowpass=f=3200,acompressor=threshold=0.1:ratio=4:makeup=1.5"; break;
                default: throw new ArgumentException("Неизвестный эффект голоса.");
            }
            if (noiseGate) filter = "agate=threshold=0.004:ratio=3:attack=8:release=140," + filter;
            double seconds = Math.Min(limit, (raw.Length - 44) / 88200.0 + (effect == 3 ? .3 : 0));
            filter += ",alimiter=limit=0.95,apad,atrim=duration=" + Audio.N(seconds) + ",asetpts=PTS-STARTPTS";
            string key = Path.Combine(Paths.Cache, "effect-" + Guid.NewGuid().ToString("N")), input = key + "-raw.wav", output = key + "-voice.wav";
            try
            {
                File.WriteAllBytes(input, raw);
                Tools.Run("ffmpeg", "-y -v error -i " + Tools.Quote(input) + " -af " + Tools.Quote(filter) + " -ac 1 -ar 44100 -c:a pcm_s16le " + Tools.Quote(output), 30000, cancellation);
                var result = Audio.Wave(Audio.ReadPcm(File.ReadAllBytes(output))); Audio.Validate(result, limit); return result;
            }
            finally { if (File.Exists(input)) File.Delete(input); if (File.Exists(output)) File.Delete(output); }
        }
    }

    public static class SignalAnalysis
    {
        public static float[] Envelope(byte[] pcm, int points)
        {
            var result = new float[points]; int samples = pcm.Length / 2;
            for (int i = 0; i < points; i++)
            {
                int start = i * samples / points, end = Math.Max(start + 1, (i + 1) * samples / points); float peak = 0;
                for (int j = start; j < Math.Min(samples, end); j++) peak = Math.Max(peak, Math.Abs(BitConverter.ToInt16(pcm, j * 2) / 32768f)); result[i] = peak;
            }
            return result;
        }
    }
}
