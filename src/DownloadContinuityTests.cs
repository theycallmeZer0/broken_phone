using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BrokenPhone
{
    public sealed partial class GameWindow
    {
        public async Task DownloadContinuitySmoke()
        {
            var report = new List<string>();
            var release = new TaskCompletionSource<Pack>(TaskCreationOptions.RunContinuationsAsynchronously);
            string root = Path.Combine(Paths.Cache, "continuity-test-" + Guid.NewGuid().ToString("N"));
            int calls = 0; CancellationToken steamToken = default(CancellationToken);
            try
            {
                await Until(() => Me != null, "Лобби не создано.");
                if (videoDownload != null) await videoDownload;
                Directory.CreateDirectory(root);
                foreach (string file in Directory.GetFiles(Path.Combine(Paths.Base, "Assets", "Demo"))) File.Copy(file, Path.Combine(root, Path.GetFileName(file)));
                string metadataFile = Path.Combine(root, "pack.json");
                var metadata = Json.Read<Dictionary<string, object>>(File.ReadAllText(metadataFile));
                metadata["title"] = "Continuous Steam " + Guid.NewGuid().ToString("N"); File.WriteAllText(metadataFile, Json.Write(metadata));
                using (var output = new FileStream(Path.Combine(root, "video.mp4"), FileMode.Append))
                {
                    byte[] tag = System.Text.Encoding.ASCII.GetBytes(Guid.NewGuid().ToString("N")); output.Write(tag, 0, tag.Length);
                }
                var source = await Task.Run(() => PackLibrary.Load(root, "9900513"));
                Disconnect();
                using (var room = new RoomServer(new Pack { title = "Пак не выбран" }, 0))
                using (var host = new RoomClient())
                {
                    RoomState hostState = null;
                    host.Received += message => { if (message.state != null) hostState = message.state; };
                    await Task.Run(() => host.Connect("127.0.0.1", room.Port, room.Code, "Host", room.Secret));
                    pendingWorkshopLoader = (item, progress, token) =>
                    {
                        Interlocked.Increment(ref calls); steamToken = token;
                        progress(PackProgress.Bytes("Steam загружает файлы", 1, 4)); return release.Task;
                    };
                    await Connect("127.0.0.1", room.Port, room.Code, "");
                    await Until(() => state != null && state.players.Count == 2 && hostState != null && hostState.players.Count == 2, "Гость не вошёл в комнату.");
                    host.Send(new Message { kind = "workshop_select", pack = new Pack { workshopId = source.workshopId, title = source.title } });
                    await Until(() => state.pack.pendingDownload && calls == 1 && hostState.pack.pendingDownload, "Steam-загрузка не началась после выбора.");
                    var initialTask = pendingWorkshopTask;
                    host.Send(new Message { kind = "pack", code = hostState.pack.contentKey, pack = source });
                    await Until(() => state.pack.Selected && !state.pack.pendingDownload && retainedWorkshopProgress != null, "Подготовка не продолжила текущую Steam-загрузку.");
                    if (steamToken.IsCancellationRequested || !Object.ReferenceEquals(initialTask, pendingWorkshopTask) || calls != 1 || videoDownload == null || videoDownload.IsCompleted || room.PackUploadBytes != 0)
                        throw new Exception("Публикация описания перезапустила Steam или начала раздачу до его завершения.");
                    var oldPreparation = videoDownload; preparationCancel.Cancel();
                    await Task.WhenAny(oldPreparation, Task.Delay(1000));
                    if (!oldPreparation.IsCanceled || steamToken.IsCancellationRequested)
                        throw new Exception("Отмена подготовки зависла на общей загрузке Steam или остановила её.");
                    videoDownload = null; EnsureSelectedPack();
                    release.TrySetResult(source);
                    string video = await videoDownload;
                    if (calls != 1 || steamToken.IsCancellationRequested || video != source.video || room.PackUploadBytes != 0)
                        throw new Exception("Игрок не использовал результат единственной продолжающейся загрузки Steam.");
                    report.Add("PASS: a real guest keeps its pending Steam task when the host publishes the full manifest; cancelling stale preparation returns promptly without cancelling/restarting shared Steam work or transferring peer bytes; the single completed download prepares the selected pack");
                }
            }
            catch (Exception error) { report.Add("FAIL: " + error); }
            finally
            {
                pendingWorkshopLoader = null; release.TrySetCanceled();
                File.WriteAllLines(Path.Combine(Paths.Base, "download-continuity-ui-test.txt"), report); window.Close();
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }
    }
}
