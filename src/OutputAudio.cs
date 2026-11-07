using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace BrokenPhone
{
    public sealed class OutputDevice
    {
        public uint Id;
        public string Key, Name;
        public override string ToString() { return Name; }
    }
    public static class OutputAudio
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct Caps
        {
            public ushort manufacturer, product; public uint version;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string name;
            public uint formats; public ushort channels, reserved; public uint support;
        }
        [DllImport("winmm.dll")] static extern uint waveOutGetNumDevs();
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)] static extern uint waveOutGetDevCapsW(UIntPtr device, out Caps caps, uint size);
        public static OutputDevice[] Devices()
        {
            var devices = new List<OutputDevice> { new OutputDevice { Id = UInt32.MaxValue, Key = "", Name = "Устройство Windows по умолчанию" } };
            for (uint i = 0; i < waveOutGetNumDevs(); i++)
            {
                Caps caps; if (waveOutGetDevCapsW(new UIntPtr(i), out caps, (uint)Marshal.SizeOf(typeof(Caps))) != 0) continue;
                string key = caps.manufacturer + ":" + caps.product + ":" + caps.name;
                int duplicate = devices.Count(device => device.Key.StartsWith(key, StringComparison.Ordinal));
                devices.Add(new OutputDevice { Id = i, Key = key + ":" + duplicate, Name = caps.name });
            }
            return devices.ToArray();
        }
        public static uint Resolve(string key)
        {
            var device = Devices().FirstOrDefault(value => value.Key == (key ?? ""));
            if (device == null) throw new IOException("Выбранное устройство вывода отключено. Выбери другое в настройках «Игрок и звук».");
            return device.Id;
        }
        public static byte[] ReadStereo(string file)
        {
            using (var stream = File.OpenRead(file)) using (var reader = new BinaryReader(stream))
            {
                if (new string(reader.ReadChars(4)) != "RIFF") throw new InvalidDataException("Не WAV-файл."); reader.ReadUInt32();
                if (new string(reader.ReadChars(4)) != "WAVE") throw new InvalidDataException("Не WAV-файл.");
                int channels = 0; byte[] pcm = null;
                while (stream.Position + 8 <= stream.Length)
                {
                    string chunk = new string(reader.ReadChars(4)); uint count = reader.ReadUInt32(); long end = stream.Position + count;
                    if (end > stream.Length) throw new InvalidDataException("Повреждён WAV-файл.");
                    if (chunk == "fmt ")
                    {
                        if (count < 16 || reader.ReadUInt16() != 1) throw new InvalidDataException("Нужен WAV PCM.");
                        channels = reader.ReadUInt16(); int rate = reader.ReadInt32(); reader.ReadInt32(); reader.ReadUInt16(); int bits = reader.ReadUInt16();
                        if ((channels != 1 && channels != 2) || rate != 44100 || bits != 16) throw new InvalidDataException("Нужен WAV 44100 Гц, mono/stereo, 16 бит.");
                    }
                    else if (chunk == "data") { if (count > 24 * 1024 * 1024) throw new InvalidDataException("Слишком длинная проба."); pcm = reader.ReadBytes((int)count); }
                    stream.Position = Math.Min(stream.Length, end + (count & 1));
                }
                if (pcm == null || channels == 0 || pcm.Length % (channels * 2) != 0) throw new InvalidDataException("Некорректный WAV.");
                if (channels == 2) return pcm;
                var stereo = new byte[pcm.Length * 2];
                for (int i = 0; i < pcm.Length; i += 2) { stereo[i * 2] = stereo[i * 2 + 2] = pcm[i]; stereo[i * 2 + 1] = stereo[i * 2 + 3] = pcm[i + 1]; }
                return stereo;
            }
        }
    }
    public sealed class OutputPlayer
    {
        readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        WaveMixer mixer;
        double volume = 1, duration;
        bool playing;
        int revision;
        public Func<uint> Device = () => UInt32.MaxValue;
        public event EventHandler MediaOpened, MediaEnded;
        public event EventHandler<OutputFailure> MediaFailed;
        public double Volume { get { return volume; } set { volume = Math.Max(0, Math.Min(1, value)); if (mixer != null) mixer.SetVolumes(volume, 0); } }
        public TimeSpan Position { get { return TimeSpan.FromSeconds(mixer == null ? 0 : mixer.Position); } set { if (mixer != null) mixer.Seek(value.TotalSeconds, playing); } }
        public uint DeviceId { get { return mixer == null ? UInt32.MaxValue : mixer.OutputDeviceId; } }
        public OutputPlayer()
        {
            timer.Tick += delegate
            {
                if (mixer == null || !playing) return;
                if (mixer.Failure != null) { Fail(mixer.Failure); return; }
                if (mixer.Position >= duration - .002) { playing = false; timer.Stop(); var ended = MediaEnded; if (ended != null) ended(this, EventArgs.Empty); }
            };
        }
        public void Open(Uri source)
        {
            Close(); int expected = revision;
            // Match MediaPlayer's asynchronous notification: callers attach their event first.
            timer.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (expected != revision) return;
                try
                {
                    byte[] pcm = OutputAudio.ReadStereo(source.LocalPath); duration = pcm.Length / 176400.0;
                    mixer = new WaveMixer(pcm, Device()); mixer.SetVolumes(volume, 0);
                    var opened = MediaOpened; if (opened != null) opened(this, EventArgs.Empty);
                }
                catch (Exception error) { Fail(error); }
            }));
        }
        void Fail(Exception error) { Close(); var failed = MediaFailed; if (failed != null) failed(this, new OutputFailure(error)); }
        public void Play() { if (mixer == null) return; playing = true; mixer.Resume(); timer.Start(); }
        public void Pause() { playing = false; timer.Stop(); if (mixer != null) mixer.Pause(); }
        public void Stop() { Pause(); if (mixer != null) mixer.Seek(0, false); }
        public void Close() { revision++; timer.Stop(); playing = false; if (mixer != null) { mixer.Dispose(); mixer = null; } }
    }
    public sealed class OutputFailure : EventArgs { public readonly Exception Error; public OutputFailure(Exception error) { Error = error; } }
}
