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
        public static int LocalPackReuse()
        {
            var report = new List<string>(); string root = Path.Combine(Paths.Cache, "local-reuse-" + Guid.NewGuid().ToString("N")), cached = null;
            Directory.CreateDirectory(root);
            try
            {
                string hostFolder = Path.Combine(root, "host", "9911001"), collection = Path.Combine(root, "Коллекция с пробелами"), playerFolder = Path.Combine(collection, "9911001"); Directory.CreateDirectory(hostFolder); Directory.CreateDirectory(playerFolder);
                File.Copy(Path.Combine(Paths.Base, "Assets", "Demo", "video.mp4"), Path.Combine(hostFolder, "dub_video.ogv"));
                File.Copy(Path.Combine(Paths.Base, "Assets", "Demo", "background.wav"), Path.Combine(hostFolder, "_backing_track.ogg"));
                File.Copy(Path.Combine(Paths.Base, "Assets", "Brand", "logo.png"), Path.Combine(hostFolder, "Icon.png"));
                File.WriteAllText(Path.Combine(hostFolder, "_pack_info.ini"), "[data]\ntitle=\"Локальная сверка " + Guid.NewGuid().ToString("N") + "\"\nlanguage=\"RUS\"\n");
                File.WriteAllText(Path.Combine(hostFolder, "01.ini"), "[data]\ndub_timestamps=[0,8]\ndub_ends=[4,12]\ndub_characters=[\"Первый\",\"Первый\"]\n");
                File.WriteAllText(Path.Combine(hostFolder, "02.ini"), "[data]\ndub_timestamps=[4,12]\ndub_ends=[8,16]\ndub_characters=[\"Второй\",\"Второй\"]\n");
                Pack hostPack = PackLibrary.PrepareFolder(hostFolder, "9911001", CancellationToken.None);
                string recoded = Path.Combine(root, "host-encoding.mp4");
                Tools.Run("ffmpeg", "-y -v error -i " + Tools.Quote(hostPack.video) + " -an -c:v libx264 -preset ultrafast -crf 30 " + Tools.Quote(recoded), 30000); File.Copy(recoded, hostPack.video, true);
                string metadataPath = Path.Combine(hostFolder, "pack.json"); var metadata = Json.Read<Dictionary<string, object>>(File.ReadAllText(metadataPath)); metadata["prepared_video_hash"] = PackContent.FileHash(hostPack.video); File.WriteAllText(metadataPath, Json.Write(metadata)); hostPack = PackLibrary.Load(hostFolder, "9911001");
                Pack remote = hostPack.PublicCopy(); cached = Path.Combine(Paths.Packs, "room-" + remote.contentKey);
                foreach (var file in remote.sourceFiles) { PackSources.Copy(PackSources.SafeFile(hostFolder, file.path), PackSources.SafeFile(playerFolder, file.path), CancellationToken.None); File.SetAttributes(PackSources.SafeFile(playerFolder, file.path), FileAttributes.ReadOnly); }
                var before = remote.sourceFiles.ToDictionary(file => file.path, file => PackContent.FileHash(PackSources.SafeFile(playerFolder, file.path)));
                using (var server = new RoomServer(hostPack, 0)) using (var host = new Inbox()) using (var guest = new Inbox())
                {
                    host.Client.Connect("127.0.0.1", server.Port, server.Code, "Ведущий", server.Secret); host.Wait(s => s.players.Count == 1); guest.Client.Connect("127.0.0.1", server.Port, server.Code, "Друг", ""); guest.Wait(s => s.players.Count == 2);
                    var requests = new List<string>();
                    Func<string, string, Action<long, long>, CancellationToken, Task<string>> download = (asset, ext, progress, token) => { requests.Add(asset); return guest.Client.Asset(asset, ext, progress, token, remote.contentKey); };
                    var prepared = PackContent.Prepare(remote, download, p => { }, CancellationToken.None, true, collection).GetAwaiter().GetResult();
                    Check(requests.Count == 0 && prepared.contentKey == remote.contentKey && prepared.videoHash != remote.videoHash && Path.GetExtension(prepared.video) == ".mp4", "Пак есть локально, но видео передаётся или сравнивается по результату кодирования.");
                    Check(before.All(pair => PackContent.FileHash(PackSources.SafeFile(playerFolder, pair.Key)) == pair.Value) && Directory.GetFiles(playerFolder).Length == before.Count, "Изменена исходная папка игрока.");
                    PackContent.Prepare(remote, (a, e, p, t) => { throw new Exception("Повторная передача готового пака."); }, p => { }, CancellationToken.None, true, collection).GetAwaiter().GetResult();
                    report.Add("PASS: identical native packs in the selected folder cause ZERO network asset requests; local conversion may differ from host encoding; read-only source stays unchanged; prepared cache is reused");
                    Directory.Delete(cached, true); string background = PackSources.SafeFile(playerFolder, remote.sourceBackground); File.SetAttributes(background, FileAttributes.Normal); File.Delete(background); requests.Clear();
                    PackContent.Prepare(remote, download, p => { }, CancellationToken.None, true, collection).GetAwaiter().GetResult();
                    int backgroundIndex = remote.sourceFiles.FindIndex(file => file.path == remote.sourceBackground);
                    Check(requests.SequenceEqual(new[] { "packfile:" + backgroundIndex }), "При отсутствии фона передаются другие файлы, включая видео.");
                    report.Add("PASS: a real TCP peer transfers only the missing background; existing video, metadata and icon never leave the host");
                    Directory.Delete(cached, true); PackSources.Copy(PackSources.SafeFile(hostFolder, remote.sourceBackground), background, CancellationToken.None);
                    string changed = PackSources.SafeFile(playerFolder, "01.ini"); File.SetAttributes(changed, FileAttributes.Normal); byte[] altered = File.ReadAllBytes(changed); altered[altered.Length - 2] ^= 1; File.WriteAllBytes(changed, altered); requests.Clear();
                    PackContent.Prepare(remote, download, p => { }, CancellationToken.None, true, collection).GetAwaiter().GetResult();
                    Check(requests.SequenceEqual(new[] { "packfile:" + remote.sourceFiles.FindIndex(file => file.path == "01.ini") }), "Файл с совпавшим именем и размером не проверяется по SHA256.");
                    report.Add("PASS: equal-size modified files are detected by checksum and only that file is repaired in the game's own cache");
                    Directory.Delete(cached, true); PackSources.Copy(PackSources.SafeFile(hostFolder, "01.ini"), changed, CancellationToken.None); File.Delete(background);
                    bool rejected = false; string invalid = Path.Combine(root, "bad.data"); File.WriteAllBytes(invalid, new byte[4]);
                    try { PackContent.Prepare(remote, (a, e, p, t) => Task.FromResult(invalid), p => { }, CancellationToken.None, true, collection).GetAwaiter().GetResult(); } catch (InvalidDataException) { rejected = true; }
                    Check(rejected && !Directory.Exists(cached), "Повреждённая догрузка оставила готовый пак.");
                    using (var cancellation = new CancellationTokenSource())
                    {
                        cancellation.CancelAfter(150); bool cancelled = false;
                        try { PackContent.Prepare(remote, async (a, e, p, t) => { await Task.Delay(5000, t); return invalid; }, p => { }, cancellation.Token, true, collection).GetAwaiter().GetResult(); } catch (OperationCanceledException) { cancelled = true; }
                        Check(cancelled && !Directory.Exists(cached), "Отмена синхронизации оставила неполный пак.");
                    }
                    var unsafePack = remote.PublicCopy(); unsafePack.sourceFiles[0].path = "../outside.txt"; bool unsafeRejected = false;
                    try { PackContent.Prepare(unsafePack, download, p => { }, CancellationToken.None, true, collection).GetAwaiter().GetResult(); } catch (InvalidDataException) { unsafeRejected = true; }
                    Check(unsafeRejected, "Путь из манифеста вышел за папку пака.");
                    report.Add("PASS: corrupt downloads, unsafe paths and cancellation are rejected without partial installed packs");
                }
                return 0;
            }
            catch (Exception error) { report.Add("FAIL: " + error); return 1; }
            finally
            {
                File.WriteAllLines(Path.Combine(Paths.Base, "pack-sync-test.txt"), report);
                if (cached != null && Directory.Exists(cached)) Directory.Delete(cached, true);
                foreach (string file in Directory.GetFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal); Directory.Delete(root, true);
            }
        }
    }
}
