using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;

namespace BrokenPhone
{
    public static partial class SelfTests
    {
        internal static Pack PriorityFixture(string root, string name)
        {
            string folder = Path.Combine(root, name); Directory.CreateDirectory(folder);
            var source = PackLibrary.Load(Path.Combine(Paths.Base, "Assets", "Demo"), "9900666");
            string video = Path.Combine(folder, "video.mp4"); File.Copy(source.video, video);
            using (var output = new FileStream(video, FileMode.Append))
            {
                byte[] marker = Encoding.ASCII.GetBytes(Guid.NewGuid().ToString("N"));
                output.Write(new byte[] { 0, 0, 0, 40, 102, 114, 101, 101 }, 0, 8); output.Write(marker, 0, marker.Length);
            }
            source.id = "priority-" + Guid.NewGuid().ToString("N"); source.title = "Priority " + Guid.NewGuid().ToString("N");
            source.root = folder; source.video = video; source.background = ""; PackContent.Describe(source, CancellationToken.None); return source;
        }
        static void RemovePriorityCache(Pack source)
        {
            string path = Path.GetFullPath(Path.Combine(Paths.Packs, "room-" + source.contentKey));
            if (path.StartsWith(Path.GetFullPath(Paths.Packs).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && Directory.Exists(path)) Directory.Delete(path, true);
        }
        public static int DownloadPriorityRegression()
        {
            var report = new List<string>(); var fixtures = new List<Pack>();
            string root = Path.Combine(Paths.Cache, "download-priority-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            try
            {
                var source = PriorityFixture(root, "steam-success"); fixtures.Add(source); var remote = source.PublicCopy();
                int assets = 0, helpers = 0, steam = 0;
                var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource<Pack>(TaskCreationOptions.RunContinuationsAsynchronously);
                Func<WorkshopItem, Action<PackProgress>, CancellationToken, Task<Pack>> load = (item, progress, token) =>
                {
                    Interlocked.Increment(ref steam); started.TrySetResult(true); return release.Task;
                };
                var preparing = PackContent.PrepareAssisted(remote, load,
                    (asset, extension, progress, token) => { Interlocked.Increment(ref assets); return Task.FromResult(source.video); },
                    progress => { }, CancellationToken.None, null,
                    token => { Interlocked.Increment(ref helpers); return Task.FromResult(true); });
                try
                {
                    Check(started.Task.Wait(4000), "Загрузка через Steam не началась.");
                    Thread.Sleep(150);
                    Check(!preparing.IsCompleted && assets == 0 && helpers == 0, "Работающий Steam конкурирует с загрузкой от игроков.");
                }
                finally { release.TrySetResult(source); }
                var prepared = preparing.GetAwaiter().GetResult();
                Check(prepared.contentKey == remote.contentKey && steam == 1 && assets == 0 && helpers == 0, "Успешная загрузка Steam потребовала раздачу участников.");
                report.Add("PASS: a blocked healthy Steam download starts no helper discovery or peer asset requests; Steam success returns the selected pack with zero peer traffic");

                source = PriorityFixture(root, "steam-failure"); fixtures.Add(source); remote = source.PublicCopy(); assets = steam = 0;
                var order = new List<string>(); var failureSource = source;
                prepared = PackContent.PrepareAssisted(remote,
                    (item, progress, token) => { steam++; order.Add("steam"); throw new IOException("Steam refuses this item"); },
                    (asset, extension, progress, token) => { assets++; order.Add("peer"); return Task.FromResult(failureSource.video); },
                    progress => { }, CancellationToken.None, null, token => { throw new Exception("Не нужно ждать источников перед резервной загрузкой."); }).GetAwaiter().GetResult();
                Check(steam == 1 && assets == 1 && order.SequenceEqual(new[] { "steam", "peer" }) && prepared.contentKey == remote.contentKey && PackContent.FileHash(prepared.video) == source.videoHash, "Раздача началась до отказа Steam или передала другую версию.");
                report.Add("PASS: Steam failure precedes peer transfer; fallback receives and verifies exactly the selected pack");

                source = PriorityFixture(root, "steam-version"); fixtures.Add(source); remote = source.PublicCopy(); assets = 0;
                var selected = source; var other = PriorityFixture(root, "wrong-steam-version"); fixtures.Add(other);
                prepared = PackContent.PrepareSelected(remote,
                    (item, progress, token) => Task.FromResult(other),
                    (asset, extension, progress, token) => { assets++; return Task.FromResult(selected.video); },
                    progress => { }, CancellationToken.None).GetAwaiter().GetResult();
                Check(assets == 1 && prepared.contentKey == remote.contentKey && PackContent.FileHash(prepared.video) == source.videoHash && prepared.contentKey != other.contentKey, "Другая версия Steam подменила выбранную в лобби.");
                report.Add("PASS: a different Steam item version falls back to the selected descriptor and exact matching file bytes");

                source = PriorityFixture(root, "cancel-steam"); fixtures.Add(source); remote = source.PublicCopy(); assets = helpers = 0;
                using (var cancel = new CancellationTokenSource())
                {
                    started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var cancelling = PackContent.PrepareAssisted(remote, async (item, progress, token) => { started.TrySetResult(true); await Task.Delay(15000, token); return null; },
                        (asset, extension, progress, token) => { assets++; return Task.FromResult(source.video); }, progress => { }, cancel.Token, null,
                        token => { helpers++; return Task.FromResult(true); });
                    Check(started.Task.Wait(4000), "Отменяемый Steam не запустился."); cancel.Cancel();
                    bool cancelled = false; try { cancelling.GetAwaiter().GetResult(); } catch (OperationCanceledException) { cancelled = true; }
                    Check(cancelled && assets == 0 && helpers == 0, "Отмена Steam запустила резервную передачу.");
                }
                report.Add("PASS: cancelling Steam preparation stops the pipeline without starting swarm or room-channel fallback");

                source = PriorityFixture(root, "local-reuse"); fixtures.Add(source); remote = source.PublicCopy(); assets = steam = 0;
                prepared = PackContent.PrepareSelected(remote,
                    (item, progress, token) => { steam++; throw new Exception("Локальная копия не должна загружаться заново."); },
                    (asset, extension, progress, token) => { assets++; throw new Exception("Локальная копия не должна передаваться."); },
                    progress => { }, CancellationToken.None, source.root).GetAwaiter().GetResult();
                Check(steam == 0 && assets == 0 && prepared.contentKey == source.contentKey, "Полная проверенная локальная копия не использована до сети.");
                report.Add("PASS: complete verified local media are reused before Steam or peers, avoiding all network transfers");

                string nativeFolder = Path.Combine(root, "native-local", "9900667"); Directory.CreateDirectory(nativeFolder);
                File.Copy(source.video, Path.Combine(nativeFolder, "dub_video.ogv"));
                File.WriteAllText(Path.Combine(nativeFolder, "_pack_info.ini"), "[data]\ntitle=\"Native priority " + Guid.NewGuid().ToString("N") + "\"\nlanguage=\"RUS\"\n");
                File.WriteAllText(Path.Combine(nativeFolder, "01.ini"), "[data]\ndub_timestamps=[0,8]\ndub_ends=[4,12]\ndub_characters=[\"Первый\",\"Второй\"]\n");
                source = PackLibrary.PrepareFolder(nativeFolder, "9900667", CancellationToken.None); fixtures.Add(source); remote = source.PublicCopy(); assets = steam = 0;
                prepared = PackContent.PrepareSelected(remote,
                    (item, progress, token) => { steam++; throw new Exception("Локальные исходники не должны загружаться через Steam."); },
                    (asset, extension, progress, token) => { assets++; throw new Exception("Полные исходники не должны передаваться от игроков."); },
                    progress => { }, CancellationToken.None, nativeFolder).GetAwaiter().GetResult();
                Check(steam == 0 && assets == 0 && PackSources.Native(prepared) && prepared.contentKey == source.contentKey, "Полные нативные исходники не использованы до сети.");
                report.Add("PASS: a complete native Dub Together source folder is prepared locally before any Steam or peer request");
                return 0;
            }
            catch (Exception error) { report.Add("FAIL: " + error); return 1; }
            finally
            {
                foreach (var fixture in fixtures) RemovePriorityCache(fixture);
                if (Directory.Exists(root)) Directory.Delete(root, true);
                File.WriteAllLines(Path.Combine(Paths.Base, "download-priority-test.txt"), report);
            }
        }
    }

    public sealed partial class GameWindow
    {
        public async Task DownloadPrioritySmoke()
        {
            var report = new List<string>(); var guest = new RoomClient(); RoomState guestState = null;
            var release = new TaskCompletionSource<Pack>(TaskCreationOptions.RunContinuationsAsynchronously);
            string root = Path.Combine(Paths.Cache, "priority-ui-" + Guid.NewGuid().ToString("N")); Pack source = null;
            try
            {
                await Until(() => Me != null, "Нет лобби для проверки приоритета загрузки.");
                if (videoDownload != null) await videoDownload;
                source = await Task.Run(() => SelfTests.PriorityFixture(root, "donor")); source.workshopId = "9911229";
                int steamCalls = 0;
                pendingWorkshopLoader = (item, progress, token) =>
                {
                    if (!state.pack.pendingDownload || item.Id != source.workshopId) throw new Exception("Steam запущен до выбора пака.");
                    steamCalls++; return release.Task;
                };
                guest.Received += message => { if (message.state != null) guestState = message.state; };
                await Task.Run(() => guest.Connect("127.0.0.1", server.Port, server.Code, "Источник пака", ""));
                await Until(() => state.players.Count == 2 && guestState != null, "Источник пака не подключился.");
                SelectWorkshopPack(new WorkshopItem { Id = source.workshopId, Title = source.title, Lines = source.DisplayLines, Roles = source.DisplayRoles });
                await Until(() => state.pack.pendingDownload && state.pack.workshopId == source.workshopId && steamCalls == 1 && guestState.pack.pendingDownload && guestState.pack.workshopId == source.workshopId, "Не началась приоритетная загрузка Steam.");
                string key = state.pack.contentKey;
                await Task.Run(() => guest.OfferWorkshopPack(key, source));
                await Until(() => workshopOffers.Count > 0, "Предложение участника не попало в очередь.");
                await Task.Delay(150);
                if (!state.pack.pendingDownload || pendingWorkshopNeedsPeers == key || guest.PackUploadBytes != 0 || acceptingWorkshopKey == key || B("ReadyButton").IsEnabled) throw new Exception("Раздача участника началась при работающем Steam.");
                report.Add("PASS: the real lobby queues a donor offer while its Steam download is healthy; no donor bytes transfer and readiness remains blocked");

                release.TrySetException(new IOException("Steam download unavailable"));
                await Until(() => state.pack.Selected && !state.pack.pendingDownload && state.pack.contentKey == source.contentKey && guestState.pack.Selected && guestState.pack.contentKey == source.contentKey, "После отказа Steam не началась загрузка из раздачи.");
                if (videoDownload != null) await videoDownload;
                if (guest.PackUploadBytes < source.videoBytes || steamCalls != 1 || !B("ReadyButton").IsEnabled) throw new Exception("После отказа Steam не использована копия участника.");
                report.Add("PASS: after Steam fails the queued donor transfers exact verified bytes over TCP, the selected descriptor is committed, and readiness becomes available");
            }
            catch (Exception error) { report.Add("FAIL: " + error); }
            finally
            {
                pendingWorkshopLoader = null; release.TrySetCanceled(); guest.Dispose();
                File.WriteAllLines(Path.Combine(Paths.Base, "download-priority-ui-test.txt"), report); window.Close();
                if (source != null)
                {
                    string cache = Path.GetFullPath(Path.Combine(Paths.Packs, "room-" + source.contentKey));
                    if (cache.StartsWith(Path.GetFullPath(Paths.Packs).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && Directory.Exists(cache)) Directory.Delete(cache, true);
                }
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }
    }
}
