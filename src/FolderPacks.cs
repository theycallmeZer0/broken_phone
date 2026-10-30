using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace BrokenPhone
{
    public sealed class FolderPackCatalog
    {
        public readonly List<Pack> Packs = new List<Pack>();
        public readonly List<string> Problems = new List<string>();
        public bool Limited;
    }

    public static class FolderPacks
    {
        public static string Normalize(string folder)
        {
            if (String.IsNullOrWhiteSpace(folder)) return Paths.Packs;
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(folder.Trim().Trim('"')));
        }

        public static bool SameFolder(string first, string second)
        {
            return String.Equals(Normalize(first).TrimEnd('\\', '/'), Normalize(second).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        }

        public static bool InLibrary(Pack pack)
        {
            string root = Normalize(pack.root);
            return root.StartsWith(Normalize(Paths.Packs).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)
                || SameFolder(root, Path.Combine(Paths.Base, "Assets", "Demo"));
        }

        // Read only: probing, thumbnails and imports write exclusively to the game's own folders.
        public static FolderPackCatalog Scan(string folder, CancellationToken cancellation = default(CancellationToken))
        {
            folder = Normalize(folder);
            var result = new FolderPackCatalog();
            if (!Directory.Exists(folder)) { result.Problems.Add("Папка недоступна. Выбери другую папку."); return result; }
            var pending = new Queue<KeyValuePair<string, int>>(); pending.Enqueue(new KeyValuePair<string, int>(folder, 0));
            int visited = 0;
            while (pending.Count > 0)
            {
                cancellation.ThrowIfCancellationRequested();
                if (++visited > 4096) { result.Limited = true; break; }
                var entry = pending.Dequeue();
                try
                {
                    if ((File.GetAttributes(entry.Key) & FileAttributes.ReparsePoint) != 0) continue;
                    if (File.Exists(Path.Combine(entry.Key, "pack.json")) || File.Exists(Path.Combine(entry.Key, "_pack_info.ini")))
                    {
                        string id = Path.GetFileName(entry.Key.TrimEnd('\\', '/'));
                        string marker = Path.Combine(entry.Key, ".source");
                        if (File.Exists(marker) && new FileInfo(marker).Length < 128) id = File.ReadAllText(marker).Trim();
                        if (!Regex.IsMatch(id, @"^\d{6,20}$")) id = "";
                        try { result.Packs.Add(PackLibrary.Load(entry.Key, id, cancellation)); }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception error) { result.Problems.Add(Path.GetFileName(entry.Key) + ": " + error.GetBaseException().Message); }
                        continue;
                    }
                    var children = Directory.GetDirectories(entry.Key).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
                    if (entry.Value >= 6) { if (children.Length > 0) result.Limited = true; continue; }
                    foreach (string child in children)
                    {
                        string name = Path.GetFileName(child);
                        if (name.StartsWith(".") || name.IndexOf("-import-", StringComparison.OrdinalIgnoreCase) >= 0 || name == "node_modules") continue;
                        pending.Enqueue(new KeyValuePair<string, int>(child, entry.Value + 1));
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception error) { result.Problems.Add(Path.GetFileName(entry.Key) + ": " + error.GetBaseException().Message); }
            }
            result.Packs.Sort((a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.title, b.title));
            return result;
        }

        public static Pack Prepare(Pack pack, CancellationToken cancellation)
        {
            if (InLibrary(pack) && String.Equals(Path.GetExtension(pack.video), ".mp4", StringComparison.OrdinalIgnoreCase)) return pack;
            // Stable storage key avoids a new copy on every selection, without overwriting Steam downloads.
            string key = "folder-" + pack.contentKey;
            return PackLibrary.Import(pack.root, pack.workshopId, cancellation, key);
        }
    }
}
