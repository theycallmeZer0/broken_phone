using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BrokenPhone
{
    public static class Transfers
    {
        public static async Task<string> Post(string url, string form, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            var request = (HttpWebRequest)WebRequest.Create(url); request.Method = "POST"; request.ContentType = "application/x-www-form-urlencoded"; request.Timeout = 25000;
            byte[] body = Encoding.UTF8.GetBytes(form); request.ContentLength = body.Length;
            using (cancellation.Register(request.Abort))
                try
                {
                    using (var output = await request.GetRequestStreamAsync().ConfigureAwait(false)) await output.WriteAsync(body, 0, body.Length, cancellation).ConfigureAwait(false);
                    using (var response = await request.GetResponseAsync().ConfigureAwait(false))
                    using (var input = response.GetResponseStream())
                    using (var buffer = new MemoryStream())
                    {
                        var bytes = new byte[16384]; int size;
                        while ((size = await input.ReadAsync(bytes, 0, bytes.Length, cancellation).ConfigureAwait(false)) > 0)
                        { if (buffer.Length + size > 8 * 1024 * 1024) throw new InvalidDataException("Слишком большой ответ Steam."); buffer.Write(bytes, 0, size); }
                        return Encoding.UTF8.GetString(buffer.ToArray());
                    }
                }
                catch (Exception) { cancellation.ThrowIfCancellationRequested(); throw; }
        }
        public static async Task Download(string url, string destination, CancellationToken cancellation, Action<long, long> progress)
        {
            cancellation.ThrowIfCancellationRequested();
            var request = (HttpWebRequest)WebRequest.Create(url); request.Timeout = 25000; request.UserAgent = "BrokenPhone/0.10";
            string partial = destination + ".part-" + Guid.NewGuid().ToString("N");
            try
            {
                using (cancellation.Register(request.Abort))
                using (var response = await request.GetResponseAsync().ConfigureAwait(false))
                using (var input = response.GetResponseStream())
                using (var output = File.Create(partial))
                {
                    const long limit = 2L * 1024 * 1024 * 1024;
                    if (response.ContentLength > limit) throw new InvalidDataException("Файл больше 2 ГБ.");
                    byte[] bytes = new byte[65536]; long total = 0; int size;
                    while ((size = await input.ReadAsync(bytes, 0, bytes.Length, cancellation).ConfigureAwait(false)) > 0)
                    {
                        total += size; if (total > limit) throw new InvalidDataException("Файл больше 2 ГБ.");
                        await output.WriteAsync(bytes, 0, size, cancellation).ConfigureAwait(false); if (progress != null) progress(total, response.ContentLength);
                    }
                    if (response.ContentLength >= 0 && total != response.ContentLength) throw new IOException("Файл получен не полностью.");
                }
                cancellation.ThrowIfCancellationRequested();
                if (File.Exists(destination)) File.Replace(partial, destination, null); else File.Move(partial, destination);
            }
            catch (Exception) { cancellation.ThrowIfCancellationRequested(); throw; }
            finally { if (File.Exists(partial)) File.Delete(partial); }
        }
        public static string Process(string executable, string arguments, string directory, int timeout, CancellationToken cancellation, bool closeInput, Action<string> line = null)
        {
            cancellation.ThrowIfCancellationRequested();
            var info = new ProcessStartInfo(executable, arguments) { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = closeInput, RedirectStandardOutput = true, RedirectStandardError = true };
            using (var process = System.Diagnostics.Process.Start(info))
            using (var group = new ProcessGroup(process))
            {
                if (closeInput) process.StandardInput.Close();
                var stdout = ReadOutput(process.StandardOutput, line); var stderr = ReadOutput(process.StandardError, line); var watch = Stopwatch.StartNew();
                while (!process.HasExited || group.Active || !stdout.IsCompleted || !stderr.IsCompleted)
                {
                    if (cancellation.IsCancellationRequested || watch.ElapsedMilliseconds > timeout)
                    {
                        group.Stop(); try { if (!process.HasExited) process.Kill(); process.WaitForExit(5000); } catch (InvalidOperationException) { }
                        cancellation.ThrowIfCancellationRequested(); throw new TimeoutException("Загрузчик не ответил вовремя. Проверьте подключение и вход в Steam.");
                    }
                    if (process.HasExited) Thread.Sleep(50); else process.WaitForExit(100);
                }
                cancellation.ThrowIfCancellationRequested(); string log = stdout.Result + stderr.Result;
                File.WriteAllText(Path.Combine(Paths.Data, "steamcmd.log"), log);
                bool restarted = log.IndexOf("Update complete, launching", StringComparison.OrdinalIgnoreCase) >= 0;
                if ((process.ExitCode != 0 && !restarted) || log.IndexOf("ERROR!", StringComparison.OrdinalIgnoreCase) >= 0) throw new InvalidOperationException("Steam не разрешил загрузку. Войдите через «Настройки → Войти в Steam» в аккаунт с Dub Together.");
                return log;
            }
        }
        static async Task<string> ReadOutput(StreamReader reader, Action<string> report)
        {
            var log = new StringBuilder(); string line;
            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null) { log.AppendLine(line); if (report != null) report(line); }
            return log.ToString();
        }

    }

    // The official downloader can restart itself while updating. A Windows job
    // ensures Cancel stops those child processes as well as the original launcher.
    sealed class ProcessGroup : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)] struct BasicLimits { public long PerProcess, PerJob; public uint Flags; public UIntPtr MinWorkingSet, MaxWorkingSet; public uint ActiveProcessLimit; public UIntPtr Affinity; public uint Priority, Scheduling; }
        [StructLayout(LayoutKind.Sequential)] struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
        [StructLayout(LayoutKind.Sequential)] struct ExtendedLimits { public BasicLimits Basic; public IoCounters Io; public UIntPtr ProcessMemory, JobMemory, PeakProcess, PeakJob; }
        [StructLayout(LayoutKind.Sequential)] struct Accounting { public long UserTime, KernelTime, PeriodUserTime, PeriodKernelTime; public uint PageFaults, TotalProcesses, ActiveProcesses, TerminatedProcesses; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateJobObject(IntPtr attributes, string name);
        [DllImport("kernel32.dll")] static extern bool SetInformationJobObject(IntPtr job, int information, ref ExtendedLimits limits, uint size);
        [DllImport("kernel32.dll")] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll")] static extern bool QueryInformationJobObject(IntPtr job, int information, out Accounting accounting, uint size, IntPtr resultLength);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
        IntPtr handle;
        public ProcessGroup(Process process)
        {
            handle = CreateJobObject(IntPtr.Zero, null); if (handle == IntPtr.Zero) return;
            var limits = new ExtendedLimits { Basic = new BasicLimits { Flags = 0x2000 } };
            if (!SetInformationJobObject(handle, 9, ref limits, (uint)Marshal.SizeOf(typeof(ExtendedLimits))) || !AssignProcessToJobObject(handle, process.Handle)) { CloseHandle(handle); handle = IntPtr.Zero; }
        }
        public void Stop() { Dispose(); }
        public bool Active { get { Accounting accounting; return handle != IntPtr.Zero && QueryInformationJobObject(handle, 1, out accounting, (uint)Marshal.SizeOf(typeof(Accounting)), IntPtr.Zero) && accounting.ActiveProcesses != 0; } }
        public void Dispose() { if (handle != IntPtr.Zero) { CloseHandle(handle); handle = IntPtr.Zero; } }
    }
}
