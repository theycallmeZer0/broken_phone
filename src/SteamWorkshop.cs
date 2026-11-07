using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BrokenPhone
{
    public static class SteamWorkshopWorker
    {
        public static int Run(string id, string report)
        {
            Action<Message> write = message => File.AppendAllText(report, Json.Write(message) + Environment.NewLine, Encoding.UTF8);
            try
            {
                using (var steam = new SteamBridge(true))
                {
                    if (!steam.Available || steam.AppId != UInt32.Parse(WorkshopService.AppId)) throw new IOException("Steam не открыл сессию Workshop Dub Together. Проверь доступ к игре в текущем профиле Steam.");
                    string folder = steam.DownloadWorkshopItem(id, progress => write(new Message { kind = "workshop_progress", text = progress.Stage, position = progress.Fraction, bytes = progress.Received, totalBytes = progress.Total }), CancellationToken.None).GetAwaiter().GetResult();
                    write(new Message { kind = "workshop_result", text = folder }); return 0;
                }
            }
            catch (Exception error) { write(new Message { kind = "workshop_error", text = error.GetBaseException().Message }); return 1; }
        }
    }
    public sealed partial class SteamBridge
    {
        sealed class WorkshopAccessException : IOException { public WorkshopAccessException(string message) : base(message) { } }
        static readonly SemaphoreSlim workshopHelper = new SemaphoreSlim(1, 1);
        readonly Dictionary<ulong, TaskCompletionSource<int>> workshopRequests = new Dictionary<ulong, TaskCompletionSource<int>>();
        public bool OverlayEnabled { get { lock (Gate) return Available && !disposed && SteamNative.SteamAPI_ISteamUtils_IsOverlayEnabled(SteamNative.SteamAPI_SteamUtils_v010()); } }
        public bool OverlayNeedsPresent { get { lock (Gate) return Available && !disposed && SteamNative.SteamAPI_ISteamUtils_BOverlayNeedsPresent(SteamNative.SteamAPI_SteamUtils_v010()); } }
        public bool OpenOverlay(bool invite)
        {
            lock (Gate)
            {
                if (!OverlayEnabled) return false;
                if (invite && LobbyId != 0) SteamNative.SteamAPI_ISteamFriends_ActivateGameOverlayInviteDialog(friends, LobbyId);
                else SteamNative.SteamAPI_ISteamFriends_ActivateGameOverlay(friends, "Friends");
                return true;
            }
        }
        void WorkshopDownloaded(IntPtr data)
        {
            ulong item = unchecked((ulong)Marshal.ReadInt64(data, 8)); TaskCompletionSource<int> request;
            if (!workshopRequests.TryGetValue(item, out request)) return;
            if (unchecked((uint)Marshal.ReadInt32(data)) != UInt32.Parse(WorkshopService.AppId)) { request.TrySetException(new WorkshopAccessException("Steam отказал в доступе к Workshop Dub Together из текущей игры (App ID " + AppId + ", код " + Marshal.ReadInt32(data, 16) + ").")); return; }
            request.TrySetResult(Marshal.ReadInt32(data, 16));
        }
        static string InstalledWorkshopItem(IntPtr ugc, ulong item)
        {
            ulong size; uint timestamp; var folder = new byte[4096];
            if (!SteamNative.SteamAPI_ISteamUGC_GetItemInstallInfo(ugc, item, out size, folder, (uint)folder.Length, out timestamp)) return null;
            int length = Array.IndexOf(folder, (byte)0); return Encoding.UTF8.GetString(folder, 0, length < 0 ? folder.Length : length);
        }
        public async Task<string> DownloadWorkshopItem(string id, Action<PackProgress> progress, CancellationToken cancellation)
        {
            try { return await DownloadWorkshopNative(id, progress, cancellation).ConfigureAwait(false); }
            catch (WorkshopAccessException)
            {
                if (AppId == UInt32.Parse(WorkshopService.AppId)) throw;
            }
            lock (Gate)
            {
                if (!Available || disposed || !SteamNative.SteamAPI_ISteamApps_BIsSubscribedApp(SteamNative.SteamAPI_SteamApps_v008(), UInt32.Parse(WorkshopService.AppId)))
                    throw new IOException("Для загрузки этих паков текущему профилю Steam нужен доступ к Dub Together. Повторный вход в Steam не требуется.");
            }
            progress(PackProgress.Step("Подключаем Workshop к текущему профилю Steam…"));
            await workshopHelper.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                string folder = Path.Combine(Paths.Cache, "workshop-client"); Directory.CreateDirectory(folder);
                string executable = Path.Combine(folder, "BrokenPhone.Workshop.exe");
                File.Copy(System.Reflection.Assembly.GetExecutingAssembly().Location, executable, true);
                File.Copy(Path.Combine(Paths.Base, "steam_api64.dll"), Path.Combine(folder, "steam_api64.dll"), true);
                File.WriteAllText(Path.Combine(folder, "steam_appid.txt"), WorkshopService.AppId);
                string result = null, failure = null;
                Action<string> output = line =>
                {
                    if (!line.StartsWith("{")) return;
                    Message message; try { message = Json.Read<Message>(line); } catch (ArgumentException) { return; }
                    if (message.kind == "workshop_result") result = message.text;
                    else if (message.kind == "workshop_error") failure = message.text;
                    else if (message.kind == "workshop_progress") progress(new PackProgress { Stage = message.text, Fraction = message.position, Received = message.bytes, Total = message.totalBytes });
                };
                string report = Path.Combine(folder, "result-" + Guid.NewGuid().ToString("N") + ".jsonl");
                try
                {
                    var process = Task.Run(() => Transfers.Process(executable, "--steam-workshop-worker " + id + " " + Tools.Quote(report), folder, 600000, cancellation, true), cancellation);
                    while (!process.IsCompleted)
                    {
                        ReadWorkshopReport(report, output); await Task.Delay(200).ConfigureAwait(false);
                    }
                    ReadWorkshopReport(report, output); await process.ConfigureAwait(false);
                }
                catch (Exception) { cancellation.ThrowIfCancellationRequested(); throw new IOException(failure ?? "Steam не завершил загрузку пака через текущий профиль."); }
                finally { if (File.Exists(report)) File.Delete(report); }
                if (String.IsNullOrEmpty(result) || !Directory.Exists(result)) throw new IOException(failure ?? "Steam не вернул папку загруженного пака.");
                return result;
            }
            finally { workshopHelper.Release(); }
        }

        static void ReadWorkshopReport(string report, Action<string> output)
        {
            if (!File.Exists(report)) return;
            try
            {
                using (var stream = new FileStream(report, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    string line, last = null; while ((line = reader.ReadLine()) != null) if (line.EndsWith("}")) last = line;
                    if (last != null) output(last);
                }
            }
            catch (IOException) { }
        }
        async Task<string> DownloadWorkshopNative(string id, Action<PackProgress> progress, CancellationToken cancellation)
        {
            ulong item; if (!UInt64.TryParse(id, out item) || item == 0) throw new InvalidDataException("Неверный ID пака.");
            var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously); IntPtr ugc;
            lock (Gate)
            {
                if (!Available || disposed) throw new IOException("Запусти клиент Steam с твоим профилем и подключи его в настройках игры. Отдельный вход для паков не нужен.");
                ugc = SteamNative.SteamAPI_SteamUGC_v021(); if (ugc == IntPtr.Zero) throw new IOException("Обнови клиент Steam: Workshop API недоступен.");
                if (workshopRequests.ContainsKey(item)) throw new IOException("Этот пак уже загружается через Steam.");
                workshopRequests.Add(item, completion);
            }
            try
            {
                cancellation.ThrowIfCancellationRequested();
                lock (Gate)
                {
                    uint state = SteamNative.SteamAPI_ISteamUGC_GetItemState(ugc, item);
                    if ((state & 4) != 0 && (state & (8 | 16 | 32)) == 0)
                    {
                        string installed = InstalledWorkshopItem(ugc, item); if (!String.IsNullOrEmpty(installed) && Directory.Exists(installed)) return installed;
                    }
                    if (!SteamNative.SteamAPI_ISteamUGC_DownloadItem(ugc, item, false)) throw new WorkshopAccessException("Steam не разрешил загрузку этого пака для текущей игры.");
                }
                DateTime deadline = DateTime.UtcNow.AddMinutes(10);
                while (true)
                {
                    cancellation.ThrowIfCancellationRequested();
                    lock (Gate)
                    {
                        if (!Available || disposed) throw new IOException("Steam отключился во время загрузки.");
                        if (completion.Task.IsCompleted)
                        {
                            int result = completion.Task.GetAwaiter().GetResult();
                            if (result == 9 || result == 15 || result == 24 || result == 42) throw new WorkshopAccessException("Steam отказал в доступе к паку (код " + result + ").");
                            if (result != 1) throw new IOException("Steam отказал в загрузке пака (код " + result + "). Проверь подключение и доступность пака в Steam.");
                            string folder = InstalledWorkshopItem(ugc, item);
                            if (!String.IsNullOrEmpty(folder) && Directory.Exists(folder)) return folder;
                        }
                        ulong downloaded, total;
                        if (SteamNative.SteamAPI_ISteamUGC_GetItemDownloadInfo(ugc, item, out downloaded, out total)) progress(PackProgress.Bytes("Клиент Steam загружает пак", (long)Math.Min((ulong)Int64.MaxValue, downloaded), (long)Math.Min((ulong)Int64.MaxValue, total)));
                    }
                    if (DateTime.UtcNow >= deadline) throw new TimeoutException("Steam не завершил загрузку пака за 10 минут. Проверь раздел загрузок клиента Steam.");
                    await Task.Delay(200, cancellation).ConfigureAwait(false);
                }
            }
            finally { lock (Gate) workshopRequests.Remove(item); }
        }
    }
}
