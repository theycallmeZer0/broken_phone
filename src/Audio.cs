using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BrokenPhone
{
    public sealed class MicrophoneRecorder : IDisposable
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct Caps { public ushort mid, pid; public uint version; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string name; public uint formats; public ushort channels, reserved; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        struct Format { public ushort tag, channels; public uint rate, average; public ushort align, bits, extra; }
        [StructLayout(LayoutKind.Sequential)]
        struct Header { public IntPtr data; public uint size, recorded; public UIntPtr user; public uint flags, loops; public IntPtr next; public UIntPtr reserved; }
        [DllImport("winmm.dll")] static extern uint waveInGetNumDevs();
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)] static extern uint waveInGetDevCapsW(UIntPtr id, out Caps caps, uint size);
        [DllImport("winmm.dll")] static extern uint waveInOpen(out IntPtr handle, uint id, ref Format format, UIntPtr callback, UIntPtr instance, uint flags);
        [DllImport("winmm.dll")] static extern uint waveInPrepareHeader(IntPtr handle, IntPtr header, uint size);
        [DllImport("winmm.dll")] static extern uint waveInUnprepareHeader(IntPtr handle, IntPtr header, uint size);
        [DllImport("winmm.dll")] static extern uint waveInAddBuffer(IntPtr handle, IntPtr header, uint size);
        [DllImport("winmm.dll")] static extern uint waveInStart(IntPtr handle);
        [DllImport("winmm.dll")] static extern uint waveInStop(IntPtr handle);
        [DllImport("winmm.dll")] static extern uint waveInReset(IntPtr handle);
        [DllImport("winmm.dll")] static extern uint waveInClose(IntPtr handle);
        readonly ManualResetEvent stop = new ManualResetEvent(false);
        Task worker;
        byte[] result;
        Exception failure;
        public volatile float Level;
        public volatile float[] Waveform = new float[0];
        public bool Recording { get; private set; }
        public static string[] Devices()
        {
            var result = new List<string> { "Микрофон по умолчанию" };
            for (uint i = 0; i < waveInGetNumDevs(); i++) { Caps caps; if (waveInGetDevCapsW(new UIntPtr(i), out caps, (uint)Marshal.SizeOf(typeof(Caps))) == 0) result.Add(caps.name); }
            return result.ToArray();
        }
        public void Start(int deviceIndex)
        {
            if (Recording) throw new InvalidOperationException("Запись уже идёт.");
            result = null; failure = null; Waveform = new float[0]; stop.Reset();
            var format = new Format { tag = 1, channels = 1, rate = 44100, average = 88200, align = 2, bits = 16 };
            IntPtr handle;
            uint error = waveInOpen(out handle, deviceIndex <= 0 ? UInt32.MaxValue : (uint)(deviceIndex - 1), ref format, UIntPtr.Zero, UIntPtr.Zero, 0);
            if (error != 0) throw new InvalidOperationException("Микрофон недоступен (код " + error + "). Проверьте устройство и разрешение Windows для классических приложений.");
            var headers = new List<IntPtr>(); var buffers = new List<IntPtr>();
            uint headerSize = (uint)Marshal.SizeOf(typeof(Header));
            try
            {
                for (int i = 0; i < 8; i++)
                {
                    IntPtr data = Marshal.AllocHGlobal(8820), header = Marshal.AllocHGlobal((int)headerSize); buffers.Add(data); headers.Add(header);
                    Marshal.StructureToPtr(new Header { data = data, size = 8820 }, header, false);
                    if (waveInPrepareHeader(handle, header, headerSize) != 0 || waveInAddBuffer(handle, header, headerSize) != 0) throw new InvalidOperationException("Не удалось подготовить микрофон.");
                }
                if (waveInStart(handle) != 0) throw new InvalidOperationException("Не удалось начать запись.");
            }
            catch
            {
                waveInReset(handle); foreach (IntPtr header in headers) { waveInUnprepareHeader(handle, header, headerSize); Marshal.FreeHGlobal(header); }
                foreach (IntPtr buffer in buffers) Marshal.FreeHGlobal(buffer); waveInClose(handle); throw;
            }
            Recording = true;
            worker = Task.Factory.StartNew(() =>
            {
                using (var pcm = new MemoryStream())
                {
                    int next = 0;
                    var envelope = new List<float>();
                    int waveSamples = 0; float wavePeak = 0;
                    Action<Header> consume = h =>
                    {
                        if (h.recorded == 0) return;
                        byte[] data = new byte[h.recorded]; Marshal.Copy(h.data, data, 0, data.Length); pcm.Write(data, 0, data.Length);
                        int peak = 0;
                        for (int s = 0; s + 1 < data.Length; s += 2)
                        {
                            int sample = Math.Abs((int)BitConverter.ToInt16(data, s)); peak = Math.Max(peak, sample); wavePeak = Math.Max(wavePeak, sample / 32768f);
                            if (++waveSamples == SignalAnalysis.SamplesPerPoint) { envelope.Add(wavePeak); wavePeak = 0; waveSamples = 0; }
                        }
                        Level = peak / 32768f; Waveform = envelope.ToArray();
                    };
                    try
                    {
                        var watch = Stopwatch.StartNew();
                        while (!stop.WaitOne(8) && watch.Elapsed.TotalSeconds < 65)
                        {
                            for (int b = 0; b < headers.Count; b++)
                            {
                                var header = (Header)Marshal.PtrToStructure(headers[next], typeof(Header)); if ((header.flags & 1) == 0) break;
                                consume(header); header.recorded = 0; Marshal.StructureToPtr(header, headers[next], false); waveInAddBuffer(handle, headers[next], headerSize); next = (next + 1) % headers.Count;
                            }
                        }
                        waveInStop(handle); waveInReset(handle);
                        for (int i = 0; i < headers.Count; i++) { var h = (Header)Marshal.PtrToStructure(headers[(next + i) % headers.Count], typeof(Header)); if ((h.flags & 1) != 0) consume(h); }
                        result = Audio.Wave(pcm.ToArray());
                    }
                    catch (Exception e) { failure = e; }
                    finally
                    {
                        waveInReset(handle); foreach (var header in headers) { waveInUnprepareHeader(handle, header, headerSize); Marshal.FreeHGlobal(header); }
                        foreach (var buffer in buffers) Marshal.FreeHGlobal(buffer); waveInClose(handle); Level = 0;
                    }
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        public byte[] Stop()
        {
            if (!Recording) return result;
            stop.Set(); if (!worker.Wait(5000)) throw new TimeoutException("Микрофон не завершил запись."); Recording = false;
            if (failure != null) throw failure; return result;
        }
        public void Dispose() { if (Recording) Stop(); stop.Dispose(); }
    }

    public static class Audio
    {
        public static byte[] ReadPcm(byte[] wav)
        {
            using (var stream = new MemoryStream(wav)) using (var reader = new BinaryReader(stream))
            {
                if (wav.Length < 44 || Encoding.ASCII.GetString(reader.ReadBytes(4)) != "RIFF") throw new InvalidDataException("Не WAV-файл."); reader.ReadInt32(); if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "WAVE") throw new InvalidDataException("Не WAV-файл.");
                bool format = false; byte[] pcm = null;
                while (stream.Position + 8 <= stream.Length)
                {
                    string id = Encoding.ASCII.GetString(reader.ReadBytes(4)); uint size = reader.ReadUInt32(); long end = stream.Position + size;
                    if (end > stream.Length) throw new InvalidDataException("Повреждён WAV-файл.");
                    if (id == "fmt ") { if (size < 16) throw new InvalidDataException("Повреждён формат WAV."); format = reader.ReadUInt16() == 1 && reader.ReadUInt16() == 1 && reader.ReadInt32() == 44100; reader.ReadInt32(); format = format && reader.ReadUInt16() == 2 && reader.ReadUInt16() == 16; }
                    if (id == "data") { if (size > 6 * 1024 * 1024 || (size & 1) != 0) throw new InvalidDataException("Некорректный размер PCM."); pcm = reader.ReadBytes((int)size); }
                    stream.Position = Math.Min(stream.Length, end + (size & 1));
                }
                if (!format || pcm == null) throw new InvalidDataException("Нужен WAV PCM 44100 Гц, mono, 16 бит."); return pcm;
            }
        }
        public static void Original(Pack pack, int index, string output)
        {
            var line = pack.lines[index]; double length = line.end - line.start;
            var cues = pack.sourceCues.Where(c => c.end > line.start && c.start < line.end && File.Exists(c.file) && (String.IsNullOrEmpty(line.sourceKey) || c.key == line.sourceKey)).ToArray();
            if (cues.Length == 0 && !String.IsNullOrEmpty(pack.original) && File.Exists(pack.original))
            {
                Tools.Run("ffmpeg", "-y -v error -ss " + N(line.start) + " -i " + Tools.Quote(pack.original) + " -t " + N(length) + " -vn -ac 1 -ar 44100 -c:a pcm_s16le " + Tools.Quote(output), 30000);
                File.WriteAllBytes(output, Wave(ReadPcm(File.ReadAllBytes(output)))); return;
            }
            if (cues.Length == 0) throw new InvalidOperationException("В этом паке нет оригинальной записи этой реплики.");
            var mix = new int[(int)Math.Ceiling(length * 44100)];
            foreach (var cue in cues)
            {
                string temporary = output + ".source.wav";
                try
                {
                    double begin = Math.Max(cue.start, line.start), end = Math.Min(cue.end, line.end);
                    Tools.Run("ffmpeg", "-y -v error -ss " + N(begin - cue.start) + " -i " + Tools.Quote(cue.file) + " -t " + N(end - begin) + " -ac 1 -ar 44100 -c:a pcm_s16le " + Tools.Quote(temporary), 30000);
                    byte[] pcm = ReadPcm(File.ReadAllBytes(temporary)); int offset = (int)Math.Round((begin - line.start) * 44100);
                    for (int i = 0; i < pcm.Length / 2 && offset + i < mix.Length; i++) mix[offset + i] += BitConverter.ToInt16(pcm, i * 2);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            var result = new byte[mix.Length * 2]; for (int i = 0; i < mix.Length; i++) { short sample = (short)Math.Max(Int16.MinValue, Math.Min(Int16.MaxValue, mix[i])); result[i * 2] = (byte)(sample & 255); result[i * 2 + 1] = (byte)(sample >> 8); } File.WriteAllBytes(output, Wave(result));
        }
        public static byte[] Wave(byte[] pcm)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + pcm.Length); writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
                writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(44100); writer.Write(88200); writer.Write((short)2); writer.Write((short)16);
                writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(pcm.Length); writer.Write(pcm); return stream.ToArray();
            }
        }
        public static void Validate(byte[] bytes, double limit)
        {
            if (bytes.Length < 44 || bytes.Length > 6 * 1024 * 1024) throw new InvalidDataException("Некорректный размер записи.");
            if (Encoding.ASCII.GetString(bytes, 0, 4) != "RIFF" || Encoding.ASCII.GetString(bytes, 8, 8) != "WAVEfmt " || BitConverter.ToInt32(bytes, 16) != 16 || BitConverter.ToInt16(bytes, 20) != 1 || BitConverter.ToInt16(bytes, 22) != 1 || BitConverter.ToInt32(bytes, 24) != 44100 || BitConverter.ToInt16(bytes, 34) != 16 || Encoding.ASCII.GetString(bytes, 36, 4) != "data" || BitConverter.ToInt32(bytes, 40) != bytes.Length - 44)
                throw new InvalidDataException("Нужна запись WAV PCM 44100 Гц, mono, 16 бит.");
            double seconds = (bytes.Length - 44) / 88200.0;
            if (seconds < .15 || seconds > limit + 1) throw new InvalidDataException("Длина записи не соответствует реплике.");
        }
        public static string N(double value) { return value.ToString("0.###", CultureInfo.InvariantCulture); }
        public static string MovieAudio(Pack pack) { return !String.IsNullOrEmpty(pack.background) ? pack.background : !String.IsNullOrEmpty(pack.original) && File.Exists(pack.original) ? pack.original : ""; }
        public static string MovieKind(Pack pack) { return !String.IsNullOrEmpty(pack.background) ? "background" : String.IsNullOrEmpty(MovieAudio(pack)) ? "none" : "original"; }
        public static void Render(Pack pack, string[] takes, string output)
        {
            double duration = PackLibrary.Duration(pack.video);
            string track = Path.Combine(Path.GetDirectoryName(output), "voices.wav");
            // A float timeline adds overlapping takes without clipping either voice.
            // Bounded blocks keep long videos out of memory; FFmpeg limits the final mix once.
            int frames = (int)Math.Ceiling(duration * 44100), totalBytes = checked(frames * 4);
            using (var stream = File.Create(track))
            using (var writer = new BinaryWriter(stream))
            {
                byte[] header = Wave(new byte[0]); Array.Copy(BitConverter.GetBytes(totalBytes + 36), 0, header, 4, 4); Array.Copy(BitConverter.GetBytes(totalBytes), 0, header, 40, 4); writer.Write(header);
                stream.Position = 20; writer.Write((short)3); stream.Position = 28; writer.Write(44100 * 4); writer.Write((short)4); writer.Write((short)32);
                // SetLength creates a zero-filled timeline, including gaps between cues.
                stream.SetLength(44L + totalBytes);
                var encoded = new byte[16384 * 4]; var mixed = new float[16384];
                for (int i = 0; i < takes.Length; i++)
                {
                    var line = pack.lines[i]; int start = (int)Math.Round(line.start * 44100);
                    byte[] wav = File.ReadAllBytes(takes[i]); Validate(wav, line.end - line.start);
                    int length = Math.Min(Math.Min((int)Math.Round((line.end - line.start) * 44100), (wav.Length - 44) / 2), frames - start);
                    for (int offset = 0; offset < length; offset += mixed.Length)
                    {
                        int count = Math.Min(mixed.Length, length - offset), bytes = count * 4;
                        long position = 44L + (start + offset) * 4L; stream.Position = position;
                        int read = 0; while (read < bytes) { int chunk = stream.Read(encoded, read, bytes - read); if (chunk == 0) throw new EndOfStreamException(); read += chunk; }
                        Buffer.BlockCopy(encoded, 0, mixed, 0, bytes);
                        for (int sample = 0; sample < count; sample++) mixed[sample] += BitConverter.ToInt16(wav, 44 + (offset + sample) * 2) / 32768f;
                        Buffer.BlockCopy(mixed, 0, encoded, 0, bytes); stream.Position = position; stream.Write(encoded, 0, bytes);
                    }
                }
            }
            string args = "-y -v error -i " + Tools.Quote(pack.video) + " -i " + Tools.Quote(track);
            bool background = !String.IsNullOrEmpty(pack.background);
            if (background) args += " -i " + Tools.Quote(pack.background);
            string filter = background ? "[1:a][2:a]amix=inputs=2:normalize=0,alimiter=limit=0.95,apad[mix]" : "[1:a]alimiter=limit=0.95,apad[mix]";
            args += " -filter_complex " + Tools.Quote(filter) + " -map 0:v:0 -map \"[mix]\" -c:v libx264 -preset fast -crf 21 -c:a aac -t " + N(duration) + " -movflags +faststart " + Tools.Quote(output);
            Tools.Run("ffmpeg", args, 600000);
            string directory = Path.GetDirectoryName(output);
            Tools.Run("ffmpeg", "-y -v error -i " + Tools.Quote(track) + " -af alimiter=limit=0.95,apad -t " + N(duration) + " -c:a aac -b:a 160k " + Tools.Quote(Path.Combine(directory, "result-voices.m4a")), 600000);
            string movie = MovieAudio(pack), movieInput = String.IsNullOrEmpty(movie) ? "-f lavfi -i anullsrc=r=44100:cl=mono" : "-i " + Tools.Quote(movie);
            Tools.Run("ffmpeg", "-y -v error " + movieInput + " -vn -af apad -t " + N(duration) + " -c:a aac -b:a 160k " + Tools.Quote(Path.Combine(directory, "result-movie.m4a")), 600000);
        }
    }
}
