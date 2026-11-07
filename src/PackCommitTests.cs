using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace BrokenPhone
{
    public static partial class SelfTests
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        static SafeFileHandle LockPackDirectory(string folder)
        {
            var handle = CreateFile(folder, 1, 3, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
            if (handle.IsInvalid) throw new IOException("Не удалось заблокировать тестовую папку: " + Marshal.GetLastWin32Error());
            return handle;
        }
        public static int PackCommitRegression()
        {
            var report = new List<string>(); string root = Path.Combine(Paths.Cache, "pack-commit-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string stage = Path.Combine(root, "stage"), target = Path.Combine(root, "installed"); Directory.CreateDirectory(stage); File.WriteAllText(Path.Combine(stage, "asset.txt"), "complete pack");
                using (var handle = LockPackDirectory(stage))
                {
                    bool blocked = false;
                    try { Directory.Move(stage, target); } catch (IOException error) { blocked = (error.HResult & 0xffff) == 5 || (error.HResult & 0xffff) == 32; }
                    Check(blocked && Directory.Exists(stage) && !Directory.Exists(target), "Не воспроизведён отказ перемещения заблокированной папки.");
                    var release = Task.Run(() => { Thread.Sleep(250); handle.Dispose(); }); var elapsed = Stopwatch.StartNew();
                    try { PackContent.MovePreparedDirectory(stage, target, CancellationToken.None); }
                    finally { release.Wait(); }
                    Check(elapsed.ElapsedMilliseconds >= 150 && File.ReadAllText(Path.Combine(target, "asset.txt")) == "complete pack" && !Directory.Exists(stage), "Готовый пак не установлен после снятия блокировки.");
                }
                report.Add("PASS: real Windows directory lock reproduces access denied; promotion waits for release and preserves every prepared file");
                stage = Path.Combine(root, "cancel-stage"); target = Path.Combine(root, "cancel-target"); Directory.CreateDirectory(stage);
                using (var handle = LockPackDirectory(stage)) using (var cancel = new CancellationTokenSource(100))
                {
                    bool cancelled = false; var elapsed = Stopwatch.StartNew();
                    try { PackContent.MovePreparedDirectory(stage, target, cancel.Token); } catch (OperationCanceledException) { cancelled = true; }
                    Check(cancelled && elapsed.ElapsedMilliseconds < 1000 && Directory.Exists(stage) && !Directory.Exists(target), "Отмена не прервала ожидание блокировки.");
                }
                report.Add("PASS: cancellation promptly stops waiting on a locked folder without publishing a partial pack"); return 0;
            }
            catch (Exception error) { report.Add("FAIL: " + error); return 1; }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); File.WriteAllLines(Path.Combine(Paths.Base, "pack-commit-test.txt"), report); }
        }
    }
}
