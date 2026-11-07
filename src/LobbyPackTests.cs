using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BrokenPhone
{
    public static partial class SelfTests
    {
        public static int LobbyPackRegression()
        {
            var report = new List<string>();
            try
            {
                var demo = PackLibrary.Load(Path.Combine(Paths.Base, "Assets", "Demo"), "");
                int steamCalls = 0, hostCalls = 0;
                demo.id = "lobby-steam-test"; demo.workshopId = "9900999"; demo.title = "Lobby test " + Guid.NewGuid().ToString("N"); PackContent.Describe(demo, CancellationToken.None);
                var remote = demo.PublicCopy();
                var started = new CountdownEvent(2); var release = new TaskCompletionSource<bool>();
                Func<WorkshopItem, Action<PackProgress>, CancellationToken, Task<Pack>> download = async (item, progress, token) =>
                {
                    Interlocked.Increment(ref steamCalls); started.Signal(); await release.Task;
                    Check(item.Id == remote.workshopId, "Steam получил другой ID."); return demo;
                };
                Func<string, string, Action<long, long>, CancellationToken, Task<string>> asset = (a, e, p, t) => { hostCalls++; return Task.FromResult(a == "video" ? demo.video : demo.background); };
                var first = PackContent.PrepareSelected(remote, download, asset, p => { }, CancellationToken.None);
                var second = PackContent.PrepareSelected(remote, download, asset, p => { }, CancellationToken.None);
                try { Check(started.Wait(3000), "Участники не начали загрузку Steam параллельно."); }
                finally { release.TrySetResult(true); }
                Task.WaitAll(first, second);
                Check(steamCalls == 2 && hostCalls == 0, "Участники получили пак от хоста вместо Steam.");
                using (var cancel = new CancellationTokenSource())
                {
                    cancel.Cancel(); bool cancelled = false;
                    try { PackContent.PrepareSelected(remote, download, asset, p => { }, cancel.Token).GetAwaiter().GetResult(); }
                    catch (OperationCanceledException) { cancelled = true; }
                    Check(cancelled && hostCalls == 0, "Отмена загрузки обратилась к хосту.");
                }
                report.Add("PASS: two peers start independent Steam downloads concurrently, no host transfer; cancellation stops preparation");
                // Selecting the disk copy below replaces the distinct initial metadata.
                using (var server = new RoomServer(demo, 0)) using (var host = new Inbox()) using (var guest = new Inbox())
                {
                    host.Client.Connect("127.0.0.1", server.Port, server.Code, "Host", server.Secret);
                    host.Wait(s => s.players.Count == 1);
                    guest.Client.Connect("127.0.0.1", server.Port, server.Code, "Guest", "");
                    guest.Wait(s => s.players.Count == 2);
                    guest.Client.Send(new Message { kind = "pack_download_progress", id = demo.contentKey, position = .4, text = "Steam" });
                    host.Wait(s => s.players.Any(p => !p.host && p.downloadProgress == .4), 3);
                    Check(host.State.phase == "lobby", "Предзагрузка запустила раунд.");
                    guest.Client.Send(new Message { kind = "pack_download_ready", id = demo.contentKey });
                    host.Wait(s => s.players.Any(p => !p.host && p.downloadProgress == 1), 3);
                    Check(host.State.players.All(p => !p.prepared), "Предзагрузка обошла проверку открытия видео.");
                    host.Client.Send(new Message { kind = "pack", pack = demo });
                    host.Wait(s => s.players.All(p => p.downloadProgress == 0), 3);
                    guest.Client.Send(new Message { kind = "pack_download_progress", id = demo.contentKey, position = .8 });
                    guest.Client.Send(new Message { kind = "chat", text = "sync" });
                    host.Wait(s => s.chat.Any(c => c.text == "sync"));
                    Check(host.State.players.All(p => p.downloadProgress == 0), "Принят прогресс старого пака.");
                    report.Add("PASS: lobby download progress and completion broadcast before start; media barrier retained; selection resets status and stale reports ignored");
                }
                return 0;
            }
            catch (Exception error) { report.Add("FAIL: " + error); return 1; }
            finally { File.WriteAllLines(Path.Combine(Paths.Base, "lobby-pack-test.txt"), report); }
        }
    }
}
