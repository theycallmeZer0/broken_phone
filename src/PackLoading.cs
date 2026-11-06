using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace BrokenPhone
{
    public static class PackContent
    {
        static readonly object hashGate = new object();
        static readonly Dictionary<string, string> hashes = new Dictionary<string, string>();
        static readonly SemaphoreSlim installGate = new SemaphoreSlim(1, 1);
        public static string FileHash(string file, CancellationToken token = default(CancellationToken))
        {
            var info = new FileInfo(file); string key = info.FullName + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks;
            lock (hashGate) { string value; if (hashes.TryGetValue(key, out value)) return value; }
            using (var sha = SHA256.Create()) using (var input = File.OpenRead(file))
            {
                byte[] buffer = new byte[65536]; int count;
                while ((count = input.Read(buffer, 0, buffer.Length)) > 0) { token.ThrowIfCancellationRequested(); sha.TransformBlock(buffer, 0, count, buffer, 0); }
                sha.TransformFinalBlock(new byte[0], 0, 0); string value = Hex(sha.Hash); lock (hashGate) hashes[key] = value; return value;
            }
        }
        static string Hex(byte[] bytes) { return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant(); }
        public static void Describe(Pack pack, CancellationToken token)
        {
            PackSources.Describe(pack, token);
            pack.videoHash = FileHash(pack.video, token); pack.videoBytes = new FileInfo(pack.video).Length;
            pack.backgroundHash = String.IsNullOrEmpty(pack.background) ? "" : FileHash(pack.background, token); pack.backgroundBytes = String.IsNullOrEmpty(pack.background) ? 0 : new FileInfo(pack.background).Length;
            pack.contentKey = Key(pack);
        }
        static string Key(Pack pack)
        {
            string videoIdentity = PackSources.Native(pack) ? "source:" + pack.sourceHash + "|" + pack.sourceVideo + "|" + pack.sourceBackground : pack.videoHash;
            string metadata = Json.Write(new { videoHash = videoIdentity, backgroundHash = PackSources.Native(pack) ? "" : pack.backgroundHash, pack.title, pack.description, pack.language, lines = pack.lines.Select(l => new { l.start, l.end, l.text, l.role }).ToArray(), count = pack.DisplayLines, roles = pack.DisplayRoles });
            using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(metadata)));
        }
        public static Pack Cached(Pack remote, CancellationToken token = default(CancellationToken))
        {
            var folders = new List<string> { Path.Combine(Paths.Packs, "room-" + remote.contentKey) };
            if (Regex.IsMatch(remote.id ?? "", @"^[a-zA-Z0-9_-]{1,80}$")) folders.Add(Path.Combine(Paths.Packs, remote.id));
            if (Regex.IsMatch(remote.workshopId ?? "", @"^\d{6,20}$")) folders.Add(Path.Combine(Paths.Packs, remote.workshopId));
            if (remote.id == "Demo") folders.Add(Path.Combine(Paths.Base, "Assets", "Demo"));
            foreach (string folder in folders)
            {
                if (!File.Exists(Path.Combine(folder, "pack.json"))) continue;
                try { var local = PackLibrary.Load(folder, remote.workshopId, token); if (local.contentKey == remote.contentKey && (!PackSources.Native(remote) || Path.GetExtension(local.video).Equals(".mp4", StringComparison.OrdinalIgnoreCase) && local.preparedSourceHash == local.sourceHash && local.preparedVideoHash == local.videoHash)) return local; }
                catch (OperationCanceledException) { throw; } catch (Exception) { }
            }
            return null;
        }
        public static async Task<Pack> Prepare(Pack remote, Func<string, string, Action<long, long>, CancellationToken, Task<string>> asset, Action<PackProgress> progress, CancellationToken token, bool useCache = true, string sourceFolder = null)
        {
            PackSources.Validate(remote);
            if (Key(remote) != remote.contentKey || !Regex.IsMatch(remote.contentKey ?? "", @"^[a-f0-9]{64}$") || !Regex.IsMatch(remote.videoHash ?? "", @"^[a-f0-9]{64}$") || remote.videoBytes <= 0 || remote.videoBytes + remote.backgroundBytes > 2L * 1024 * 1024 * 1024) throw new InvalidDataException("Некорректные данные выбранного пака.");
            progress(PackProgress.Step("Проверяем локальные файлы…"));
            var cached = useCache ? await Task.Run(() => Cached(remote, token), token).ConfigureAwait(false) : null;
            if (cached != null) { progress(new PackProgress { Stage = "Пак уже загружен", Fraction = 1 }); return cached; }
            if (PackSources.Native(remote)) return await LocalPackSync.Prepare(remote, sourceFolder, asset, progress, token).ConfigureAwait(false);
            long total = remote.videoBytes + remote.backgroundBytes;
            var folders = useCache ? await Task.Run(() => LocalPackSync.Folders(remote, sourceFolder, token), token).ConfigureAwait(false) : new List<string>();
            string video = await Task.Run(() => LocalPackSync.Media(folders, remote.videoHash, remote.videoBytes, token), token).ConfigureAwait(false);
            string background = await Task.Run(() => LocalPackSync.Media(folders, remote.backgroundHash, remote.backgroundBytes, token), token).ConfigureAwait(false);
            if (video == null) video = await asset("video", ".mp4", (received, size) => progress(PackProgress.Bytes("Получаем недостающее видео", received, total)), token).ConfigureAwait(false);
            if (remote.backgroundBytes > 0 && background == null) background = await asset("background", ".audio", (received, size) => progress(PackProgress.Bytes("Получаем недостающий фоновый звук", remote.videoBytes + received, total)), token).ConfigureAwait(false);
            background = background ?? "";
            progress(PackProgress.Step("Проверяем целостность пака…"));
            if (await Task.Run(() => FileHash(video, token), token).ConfigureAwait(false) != remote.videoHash || remote.backgroundBytes > 0 && await Task.Run(() => FileHash(background, token), token).ConfigureAwait(false) != remote.backgroundHash) throw new InvalidDataException("Переданный пак повреждён. Попробуйте начать раунд заново.");
            await installGate.WaitAsync(token).ConfigureAwait(false);
            string target = Path.Combine(Paths.Packs, "room-" + remote.contentKey), stage = target + "-import-" + Guid.NewGuid().ToString("N");
            try
            {
                cached = Cached(remote, token); if (cached != null) return cached;
                Directory.CreateDirectory(stage); File.Copy(video, Path.Combine(stage, "video.mp4"));
                if (remote.backgroundBytes > 0) File.Copy(background, Path.Combine(stage, "background.audio"));
                File.WriteAllText(Path.Combine(stage, "pack.json"), Json.Write(new { internal_test = remote.isTestPack, title = remote.title, description = remote.description, language = remote.language, video = "video.mp4", background = remote.backgroundBytes > 0 ? "background.audio" : "", original = "", lines = remote.lines, line_count = remote.DisplayLines, role_count = remote.DisplayRoles, preview = remote.preview, thumbnail = remote.thumbnail, generated_timing = remote.generatedTiming, merged_timing = remote.mergedTiming }));
                File.WriteAllText(Path.Combine(stage, ".source"), remote.workshopId ?? ""); token.ThrowIfCancellationRequested();
                if (Directory.Exists(target)) Directory.Delete(target, true); Directory.Move(stage, target);
                var result = PackLibrary.Load(target, remote.workshopId, token); if (result.contentKey != remote.contentKey) throw new InvalidDataException("Метаданные пака не совпали.");
                progress(new PackProgress { Stage = "Пак загружен", Fraction = 1 }); return result;
            }
            finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); installGate.Release(); }
        }
    }
}
