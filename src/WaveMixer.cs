using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace BrokenPhone
{
    // Both audio stems use one device queue and one sample clock. Gains apply to
    // the next small PCM block, independently of compressed-file seeking.
    public sealed class WaveMixer : IDisposable
    {
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        struct Format { public ushort tag, channels; public uint rate, average; public ushort align, bits, extra; }
        [StructLayout(LayoutKind.Sequential)]
        struct Header { public IntPtr data; public uint size, recorded; public UIntPtr user; public uint flags, loops; public IntPtr next; public UIntPtr reserved; }
        [StructLayout(LayoutKind.Explicit, Size = 12)]
        struct Time { [FieldOffset(0)] public uint type; [FieldOffset(4)] public uint value; }
        [DllImport("winmm.dll")] static extern uint waveOutOpen(out IntPtr handle, uint device, ref Format format, UIntPtr callback, UIntPtr instance, uint flags);
        [DllImport("winmm.dll")] static extern uint waveOutPrepareHeader(IntPtr handle, IntPtr header, uint size);
        [DllImport("winmm.dll")] static extern uint waveOutUnprepareHeader(IntPtr handle, IntPtr header, uint size);
        [DllImport("winmm.dll")] static extern uint waveOutWrite(IntPtr handle, IntPtr header, uint size);
        [DllImport("winmm.dll")] static extern uint waveOutGetPosition(IntPtr handle, ref Time time, uint size);
        [DllImport("winmm.dll")] static extern uint waveOutPause(IntPtr handle);
        [DllImport("winmm.dll")] static extern uint waveOutRestart(IntPtr handle);
        [DllImport("winmm.dll")] static extern uint waveOutReset(IntPtr handle);
        [DllImport("winmm.dll")] static extern uint waveOutClose(IntPtr handle);
        const int FramesPerBlock = 882, BytesPerFrame = 4;
        readonly object gate = new object();
        readonly List<IntPtr> headers = new List<IntPtr>(), buffers = new List<IntPtr>();
        readonly byte[] voice = new byte[FramesPerBlock * BytesPerFrame], movie = new byte[FramesPerBlock * BytesPerFrame], output = new byte[FramesPerBlock * BytesPerFrame];
        readonly uint headerSize = (uint)Marshal.SizeOf(typeof(Header));
        Stream voices, background;
        IntPtr device;
        Task worker;
        bool disposed, playing;
        long origin, queued, totalFrames;
        double voiceGain = 1, movieGain = 1;
        public Exception Failure { get; private set; }
        public uint OutputDeviceId { get; private set; }

        public WaveMixer(string voiceFile, string movieFile, double duration, uint outputDevice = UInt32.MaxValue)
        {
            try
            {
                voices = File.OpenRead(voiceFile); background = File.OpenRead(movieFile); Initialize(duration, outputDevice);
            }
            catch { Dispose(); throw; }
        }
        public WaveMixer(byte[] stereoPcm, uint outputDevice)
        {
            try { voices = new MemoryStream(stereoPcm, false); Initialize(stereoPcm.Length / 176400.0, outputDevice); }
            catch { Dispose(); throw; }
        }
        void Initialize(double duration, uint outputDevice)
        {
                totalFrames = (long)Math.Ceiling(duration * 44100); OutputDeviceId = outputDevice;
                var format = new Format { tag = 1, channels = 2, rate = 44100, average = 176400, align = 4, bits = 16 };
                Check(waveOutOpen(out device, outputDevice, ref format, UIntPtr.Zero, UIntPtr.Zero, 0)); Check(waveOutPause(device));
                for (int i = 0; i < 3; i++)
                {
                    IntPtr data = Marshal.AllocHGlobal(output.Length), pointer = Marshal.AllocHGlobal((int)headerSize); buffers.Add(data); headers.Add(pointer);
                    Marshal.StructureToPtr(new Header { data = data, size = (uint)output.Length }, pointer, false); Check(waveOutPrepareHeader(device, pointer, headerSize));
                }
                SeekLocked(0, false);
                worker = Task.Factory.StartNew(() =>
                {
                    try { while (true) { lock (gate) { if (disposed) return; if (playing) Fill(); } Thread.Sleep(4); } }
                    catch (Exception error) { Failure = error; }
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        static void Check(uint result) { if (result != 0) throw new IOException("Не удалось открыть воспроизведение звука Windows (код " + result + ")."); }
        double PositionLocked()
        {
            if (device == IntPtr.Zero) return 0;
            var time = new Time { type = 2 }; Check(waveOutGetPosition(device, ref time, 12));
            double frames = time.type == 2 ? time.value : time.type == 4 ? time.value / 4.0 : time.value * 44.1;
            return Math.Min(totalFrames, origin + frames) / 44100.0;
        }
        public double Position { get { lock (gate) return PositionLocked(); } }
        public void SetVolumes(double voicesVolume, double movieVolume)
        {
            lock (gate)
            {
                voiceGain = Math.Max(0, Math.Min(1, voicesVolume)); movieGain = Math.Max(0, Math.Min(1, movieVolume));
                if (!disposed && !playing) SeekLocked(PositionLocked(), false);
            }
        }
        public void Seek(double seconds, bool play) { lock (gate) { if (!disposed) SeekLocked(seconds, play); } }
        void SeekLocked(double seconds, bool play)
        {
            Check(waveOutReset(device)); Check(waveOutPause(device)); origin = queued = Math.Min(totalFrames, Math.Max(0, (long)Math.Round(seconds * 44100)));
            voices.Position = Math.Min(voices.Length, queued * BytesPerFrame); if (background != null) background.Position = Math.Min(background.Length, queued * BytesPerFrame); Fill(); playing = play;
            if (play) Check(waveOutRestart(device));
        }
        public void Pause() { lock (gate) { if (!disposed) { Check(waveOutPause(device)); playing = false; } } }
        public void Resume() { lock (gate) { if (!disposed) { playing = true; Check(waveOutRestart(device)); } } }
        static void ReadBlock(Stream stream, byte[] bytes, int count)
        {
            Array.Clear(bytes, 0, bytes.Length); int offset = 0, read;
            if (stream == null) return;
            while (offset < count && (read = stream.Read(bytes, offset, count - offset)) > 0) offset += read;
        }
        void Fill()
        {
            foreach (IntPtr pointer in headers)
            {
                var header = (Header)Marshal.PtrToStructure(pointer, typeof(Header)); if ((header.flags & 16) != 0 || queued >= totalFrames) continue;
                int frames = (int)Math.Min(FramesPerBlock, totalFrames - queued), bytes = frames * BytesPerFrame;
                ReadBlock(voices, voice, bytes); ReadBlock(background, movie, bytes);
                for (int i = 0; i < bytes; i += 2)
                {
                    int value = (int)(BitConverter.ToInt16(voice, i) * voiceGain + BitConverter.ToInt16(movie, i) * movieGain);
                    short sample = (short)Math.Max(-32767, Math.Min(32767, value)); output[i] = (byte)(sample & 255); output[i + 1] = (byte)((sample >> 8) & 255);
                }
                Marshal.Copy(output, 0, header.data, bytes); header.size = (uint)bytes; header.flags &= ~1u; Marshal.StructureToPtr(header, pointer, false); Check(waveOutWrite(device, pointer, headerSize)); queued += frames;
            }
        }
        public void Dispose()
        {
            lock (gate) { if (disposed) return; disposed = true; if (device != IntPtr.Zero) waveOutReset(device); }
            if (worker != null) worker.Wait(1500);
            lock (gate)
            {
                foreach (IntPtr pointer in headers) { if (device != IntPtr.Zero) waveOutUnprepareHeader(device, pointer, headerSize); Marshal.FreeHGlobal(pointer); }
                foreach (IntPtr pointer in buffers) Marshal.FreeHGlobal(pointer);
                if (device != IntPtr.Zero) { waveOutClose(device); device = IntPtr.Zero; }
                if (voices != null) voices.Dispose(); if (background != null) background.Dispose();
            }
        }
    }
}
