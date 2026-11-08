using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace BrokenPhone
{
    public static partial class SelfTests
    {
        static Pack SwarmFixture(string root, int bytes)
        {
            Directory.CreateDirectory(root); var data = new byte[bytes]; new Random(42).NextBytes(data);
            string path = Path.Combine(root, "video.bin"); File.WriteAllBytes(path, data); string hash = PackContent.FileHash(path);
            return new Pack { id = "swarm-test", contentKey = hash, video = path, videoHash = hash, videoBytes = bytes, root = root, title = "Swarm", lines = new List<Line> { new Line { start = 0, end = 1, role = "Voice" } } };
        }
        static void ConnectSwarm(Inbox box, RoomServer server, bool host)
        {
            box.Client.Connect("127.0.0.1", server.Port, server.Code, host ? "Host" : "Peer", host ? server.Secret : "");
            box.Wait(s => s.players.Any(p => p.id == box.Client.Id));
        }
        static PackTransferPlan WaitForSeeds(Inbox receiver, Pack pack, int count)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            do { var plan = receiver.Client.PackSourcesFor(pack, "video", CancellationToken.None).GetAwaiter().GetResult(); if (plan.peers.Count(p => !p.host) >= count) return plan; Thread.Sleep(20); } while (DateTime.UtcNow < deadline);
            throw new Exception("Не объявлены источники пака.");
        }
        public static int PackSwarmRegression()
        {
            var report = new List<string>(); string root = Path.Combine(Paths.Cache, "swarm-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            try
            {
                var pack = SwarmFixture(Path.Combine(root, "full"), 8 * 1024 * 1024 + 513);
                using (var server = new RoomServer(pack, 0)) using (var host = new Inbox()) using (var seed1 = new Inbox()) using (var seed2 = new Inbox()) using (var receiver = new Inbox())
                {
                    ConnectSwarm(host, server, true); ConnectSwarm(seed1, server, false); ConnectSwarm(seed2, server, false); ConnectSwarm(receiver, server, false);
                    seed1.Client.SharePack(pack.PublicCopy(), pack); seed2.Client.SharePack(pack.PublicCopy(), pack); WaitForSeeds(receiver, pack, 2);
                    long previous = 0; string result = receiver.Client.PackAsset(pack.PublicCopy(), "video", ".bin", (received, total) => { Check(received >= previous && received <= total && total == pack.videoBytes, "Неверный прогресс частей."); previous = received; }, CancellationToken.None).GetAwaiter().GetResult();
                    Check(PackContent.FileHash(result) == pack.videoHash && previous == pack.videoBytes && seed1.Client.PackUploadBytes > 0 && seed2.Client.PackUploadBytes > 0 && server.PackUploadBytes == 0, "Несколько участников не передали файл напрямую без трафика через хоста.");
                    report.Add("PASS: four real room clients; two donors directly supply verified chunks, monotonic progress and exact final SHA256; host sends ZERO pack bytes");
                    bool rejected = false; try { receiver.Client.PackAsset(pack, "take:0", ".bin", null, CancellationToken.None).GetAwaiter().GetResult(); } catch (InvalidDataException) { rejected = true; }
                    Check(rejected, "Раздача открыла частные записи вне выбранного пака.");
                    var plan = receiver.Client.PackSourcesFor(pack, "video", CancellationToken.None).GetAwaiter().GetResult(); var source = plan.peers.First(p => !p.host);
                    using (var socket = new TcpClient())
                    {
                        socket.Connect(source.address, source.port); var stream = socket.GetStream(); Wire.Send(stream, socket, new Message { kind = "pack_chunk", code = "old-pack", secret = source.token, asset = "video", index = 0 });
                        Check(Wire.Read(stream).data == null, "Раздаётся файл от старого пака.");
                    }
                    report.Add("PASS: private recordings and obsolete pack keys cannot be fetched from peer sharing");
                }
                var small = SwarmFixture(Path.Combine(root, "partial"), 128 * 1024);
                using (var server = new RoomServer(small, 0)) using (var host = new Inbox()) using (var first = new Inbox()) using (var second = new Inbox()) using (var entered = new ManualResetEvent(false)) using (var release = new ManualResetEvent(false))
                {
                    ConnectSwarm(host, server, true); ConnectSwarm(first, server, false); ConnectSwarm(second, server, false);
                    var fetching = first.Client.PackAsset(small, "video", ".bin", (r, t) => { entered.Set(); release.WaitOne(10000); }, CancellationToken.None);
                    try
                    {
                        Check(entered.WaitOne(5000) && !fetching.IsCompleted, "Не воспроизведена незавершённая загрузка."); WaitForSeeds(second, small, 1);
                        long hostBytes = server.PackUploadBytes;
                        string copied = second.Client.PackAsset(small, "video", ".bin", null, CancellationToken.None).GetAwaiter().GetResult();
                        Check(PackContent.FileHash(copied) == small.videoHash && first.Client.PackUploadBytes == small.videoBytes && server.PackUploadBytes == hostBytes, "Проверенная часть не раздаётся до завершения загрузки.");
                    }
                    finally { release.Set(); fetching.GetAwaiter().GetResult(); }
                    report.Add("PASS: a still-downloading participant immediately seeds verified data to another downloader without a second host transfer");
                }
                using (var server = new RoomServer(pack, 0)) using (var host = new Inbox()) using (var donor = new Inbox()) using (var receiver = new Inbox())
                {
                    ConnectSwarm(host, server, true); ConnectSwarm(donor, server, false); ConnectSwarm(receiver, server, false);
                    var damaged = Json.Read<Pack>(Json.Write(pack)); damaged.video = Path.Combine(root, "corrupt.bin"); File.Copy(pack.video, damaged.video);
                    donor.Client.SharePack(pack, damaged); WaitForSeeds(receiver, pack, 1);
                    byte[] data = File.ReadAllBytes(damaged.video); for (int i = 0; i < data.Length; i += PackTransferFiles.ChunkSize) data[i] ^= 1; File.WriteAllBytes(damaged.video, data);
                    string result = receiver.Client.PackAsset(pack, "video", ".bin", null, CancellationToken.None).GetAwaiter().GetResult();
                    Check(PackContent.FileHash(result) == pack.videoHash && donor.Client.PackUploadBytes > 0 && server.PackUploadBytes > 0, "Повреждённые части не заменены из надёжного источника.");
                    report.Add("PASS: corrupt donor data fails individual chunk hashes and is replaced from the host; final file remains byte-for-byte identical");
                }
                using (var server = new RoomServer(pack, 0)) using (var host = new Inbox()) using (var donor = new Inbox()) using (var receiver = new Inbox())
                {
                    ConnectSwarm(host, server, true); ConnectSwarm(donor, server, false); ConnectSwarm(receiver, server, false);
                    donor.Client.SharePack(pack, pack); WaitForSeeds(receiver, pack, 1); int closed = 0;
                    string result = receiver.Client.PackAsset(pack, "video", ".bin", (r, t) => { if (Interlocked.Exchange(ref closed, 1) == 0) donor.Dispose(); }, CancellationToken.None).GetAwaiter().GetResult();
                    Check(PackContent.FileHash(result) == pack.videoHash && donor.Client.PackUploadBytes > 0 && server.PackUploadBytes > 0, "Выход помощника оборвал загрузку.");
                    var existing = new HashSet<string>(Directory.GetFiles(Paths.Cache, "swarm-*"));
                    using (var cancel = new CancellationTokenSource())
                    {
                        bool cancelled = false;
                        try { receiver.Client.PackAsset(pack, "video", ".bin", (r, t) => cancel.Cancel(), cancel.Token).GetAwaiter().GetResult(); } catch (OperationCanceledException) { cancelled = true; }
                        Check(cancelled && Directory.GetFiles(Paths.Cache, "swarm-*").All(existing.Contains), "Отмена оставила незавершённый файл раздачи.");
                    }
                    report.Add("PASS: donor disconnect retries missing chunks from the host; cancellation closes transfers and removes partial files");
                }
                var native = Json.Read<Pack>(Json.Write(pack)); native.sourceRoot = pack.root; native.sourceVideo = "video.bin";
                File.WriteAllText(Path.Combine(pack.root, "_pack_info.ini"), "[data]\ntitle=Swarm\n");
                native.sourceFiles = new List<PackSourceFile> {
                    new PackSourceFile { path = "_pack_info.ini", bytes = new FileInfo(Path.Combine(pack.root, "_pack_info.ini")).Length, hash = PackContent.FileHash(Path.Combine(pack.root, "_pack_info.ini")) },
                    new PackSourceFile { path = "video.bin", bytes = pack.videoBytes, hash = pack.videoHash } };
                native.sourceHash = PackSources.Fingerprint(native.sourceFiles);
                using (var server = new RoomServer(native, 0)) using (var host = new Inbox()) using (var donor = new Inbox()) using (var receiver = new Inbox())
                {
                    ConnectSwarm(host, server, true); ConnectSwarm(donor, server, false); ConnectSwarm(receiver, server, false); donor.Client.SharePack(native.PublicCopy(), native);
                    WaitForNativeSeed(receiver, native);
                    string result = receiver.Client.PackAsset(native.PublicCopy(), "packfile:1", ".bin", null, CancellationToken.None).GetAwaiter().GetResult();
                    Check(PackContent.FileHash(result) == native.sourceFiles[1].hash && donor.Client.PackUploadBytes == pack.videoBytes && server.PackUploadBytes == 0, "Исходные файлы Workshop не раздаются участниками.");
                    report.Add("PASS: native Workshop source files use the same swarm; original bytes are verified independently of local video encoding");
                }
                AssistedSteamRegression(report);
                return 0;
            }
            catch (Exception error) { report.Add("FAIL: " + error); return 1; }
            finally { File.WriteAllLines(Path.Combine(Paths.Base, "pack-swarm-test.txt"), report); if (Directory.Exists(root)) Directory.Delete(root, true); }
        }
        static void WaitForNativeSeed(Inbox receiver, Pack pack)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            do { if (receiver.Client.PackSourcesFor(pack, "packfile:1", CancellationToken.None).GetAwaiter().GetResult().peers.Any(p => !p.host)) return; Thread.Sleep(20); } while (DateTime.UtcNow < deadline);
            throw new Exception("Нет раздачи исходных файлов.");
        }
        static void AssistedSteamRegression(List<string> report)
        {
            string fixtureRoot = Path.Combine(Paths.Cache, "steam-swarm-" + Guid.NewGuid().ToString("N"));
            var source = PriorityFixture(fixtureRoot, "source"); source.workshopId = "9900888";
            var remote = source.PublicCopy(); string cache = Path.GetFullPath(Path.Combine(Paths.Packs, "room-" + source.contentKey));
            try
            {
                using (var server = new RoomServer(source, 0)) using (var host = new Inbox()) using (var donor = new Inbox()) using (var receiver = new Inbox())
                {
                    ConnectSwarm(host, server, true); ConnectSwarm(donor, server, false); ConnectSwarm(receiver, server, false); donor.Client.SharePack(remote, source); WaitForSeeds(receiver, source, 1);
                    var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var release = new TaskCompletionSource<Pack>(TaskCreationOptions.RunContinuationsAsynchronously);
                    int assetCalls = 0, helperCalls = 0;
                    var pending = PackContent.PrepareAssisted(remote, (item, progress, token) => { started.TrySetResult(true); return release.Task; }, (asset, ext, progress, token) => { Interlocked.Increment(ref assetCalls); return receiver.Client.PackAsset(remote, asset, ext, progress, token); }, p => { }, CancellationToken.None, null, token => { Interlocked.Increment(ref helperCalls); return receiver.Client.WaitForPackHelpers(remote, token); });
                    try
                    {
                        Check(started.Task.Wait(4000), "Steam не начал приоритетную загрузку."); Thread.Sleep(150);
                        Check(!pending.IsCompleted && assetCalls == 0 && helperCalls == 0 && donor.Client.PackUploadBytes == 0 && server.PackUploadBytes == 0, "Раздача конкурирует с работающим Steam.");
                    }
                    finally { release.TrySetException(new IOException("Steam download unavailable")); }
                    var prepared = pending.GetAwaiter().GetResult();
                    Check(prepared.contentKey == remote.contentKey && assetCalls == 1 && donor.Client.PackUploadBytes > 0 && server.PackUploadBytes == 0, "После отказа Steam пак не получен из раздачи участников.");
                    report.Add("PASS: healthy pending Steam performs no peer transfers; after Steam failure a real donor supplies the verified pack while the host uploads zero bytes");
                }
            }
            finally
            {
                if (cache.StartsWith(Path.GetFullPath(Paths.Packs).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && Directory.Exists(cache)) Directory.Delete(cache, true);
                if (Directory.Exists(fixtureRoot)) Directory.Delete(fixtureRoot, true);
            }
        }
    }
}
