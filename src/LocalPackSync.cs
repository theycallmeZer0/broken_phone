using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace BrokenPhone
{
    public static class LocalPackSync
    {
        static readonly SemaphoreSlim install = new SemaphoreSlim(1, 1);
        sealed class Match { public PackSourceFile File; public int Index; public string Local; }
        public static List<string> Folders(Pack remote, string selected, CancellationToken token)
        {
            var result = new List<string>(); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string root in new[] { selected, Paths.Packs })
            {
                if (String.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;
                var pending = new Queue<KeyValuePair<string, int>>(); pending.Enqueue(new KeyValuePair<string, int>(Path.GetFullPath(root), 0)); int count = 0;
                while (pending.Count > 0 && ++count <= 4096)
                {
                    token.ThrowIfCancellationRequested(); var item = pending.Dequeue();
                    try
                    {
                        if ((File.GetAttributes(item.Key) & FileAttributes.ReparsePoint) != 0 || !seen.Add(item.Key)) continue;
                        result.Add(item.Key); if (item.Value >= 6 || File.Exists(Path.Combine(item.Key, "_pack_info.ini"))) continue;
                        foreach (string dir in Directory.GetDirectories(item.Key))
                        {
                            string name = Path.GetFileName(dir); if (name.StartsWith(".") || name.Contains("-import-")) continue;
                            pending.Enqueue(new KeyValuePair<string, int>(dir, item.Value + 1));
                        }
                    }
                    catch (UnauthorizedAccessException) { } catch (IOException) { }
                }
            }
            return result.OrderBy(dir => String.Equals(Path.GetFileName(dir), remote.workshopId, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ToList();
        }
        static string Find(IEnumerable<string> folders, PackSourceFile file, CancellationToken token)
        {
            foreach (string folder in folders)
            {
                token.ThrowIfCancellationRequested();
                try { string path = PackSources.SafeFile(folder, file.path); if (PackSources.Matches(path, file, token)) return path; }
                catch (UnauthorizedAccessException) { } catch (IOException) { }
            }
            return null;
        }
        public static string Media(IEnumerable<string> folders, string hash, long bytes, CancellationToken token)
        {
            if (bytes <= 0) return null;
            foreach (string folder in folders)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    foreach (string file in Directory.GetFiles(folder))
                        if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0 && new FileInfo(file).Length == bytes && PackContent.FileHash(file, token) == hash) return file;
                }
                catch (UnauthorizedAccessException) { } catch (IOException) { }
            }
            return null;
        }
        public static async Task<Pack> Prepare(Pack remote, string selected, Func<string, string, Action<long, long>, CancellationToken, Task<string>> asset, Action<PackProgress> progress, CancellationToken token)
        {
            PackSources.Validate(remote);
            await install.WaitAsync(token).ConfigureAwait(false);
            string target = Path.Combine(Paths.Packs, "room-" + remote.contentKey), stage = target + "-import-" + Guid.NewGuid().ToString("N");
            try
            {
                var cached = await Task.Run(() => PackContent.Cached(remote, token), token).ConfigureAwait(false);
                if (cached != null) { progress(new PackProgress { Stage = "Локальный пак готов — передача не нужна", Fraction = 1 }); return cached; }
                var plan = await Task.Run(() =>
                {
                    var folders = Folders(remote, selected, token); var matches = new List<Match>();
                    for (int i = 0; i < remote.sourceFiles.Count; i++)
                    {
                        token.ThrowIfCancellationRequested(); var file = remote.sourceFiles[i];
                        progress(PackProgress.Step("Сверяем файлы пака: " + (i + 1) + " / " + remote.sourceFiles.Count));
                        matches.Add(new Match { File = file, Index = i, Local = Find(folders, file, token) });
                    }
                    return matches;
                }, token).ConfigureAwait(false);
                Directory.CreateDirectory(stage); long missing = plan.Where(item => item.Local == null).Sum(item => item.File.bytes), received = 0;
                foreach (var item in plan)
                {
                    token.ThrowIfCancellationRequested(); string source = item.Local;
                    if (source == null)
                        source = await asset("packfile:" + item.Index, ".packpart", (bytes, size) => progress(PackProgress.Bytes("Догружаем " + item.File.path, received + bytes, missing)), token).ConfigureAwait(false);
                    if (!await Task.Run(() => PackSources.Matches(source, item.File, token), token).ConfigureAwait(false)) throw new InvalidDataException("Не совпала контрольная сумма: " + item.File.path);
                    string destination = PackSources.SafeFile(stage, item.File.path);
                    await Task.Run(() => PackSources.Copy(source, destination, token), token).ConfigureAwait(false);
                    if (!await Task.Run(() => PackSources.Matches(destination, item.File, token), token).ConfigureAwait(false)) throw new InvalidDataException("Файл изменился при копировании: " + item.File.path);
                    if (item.Local == null) received += item.File.bytes;
                }
                progress(PackProgress.Step(missing == 0 ? "Пак найден локально. Подготавливаем видео без передачи…" : "Недостающие файлы получены. Подготавливаем видео…"));
                var prepared = await Task.Run(() => PackLibrary.PrepareFolder(stage, remote.workshopId, token, remote), token).ConfigureAwait(false);
                if (prepared.contentKey != remote.contentKey) throw new InvalidDataException("Содержимое пака не совпало после подготовки.");
                token.ThrowIfCancellationRequested(); if (Directory.Exists(target)) Directory.Delete(target, true); Directory.Move(stage, target);
                File.WriteAllText(Path.Combine(target, ".source"), remote.workshopId ?? "");
                var result = await Task.Run(() => PackLibrary.Load(target, remote.workshopId, token), token).ConfigureAwait(false);
                progress(new PackProgress { Stage = missing == 0 ? "Готово — видео использовано из локального пака" : "Готово — загружены только недостающие файлы", Fraction = 1 }); return result;
            }
            finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); install.Release(); }
        }
    }
}
